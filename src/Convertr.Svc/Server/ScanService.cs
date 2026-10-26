// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Diagnostics;
using Convertr.Core;

namespace Convertr.Svc.Server;

public interface IProbeRunnerFactory
{
    /// <summary>Null when ffprobe cannot be found.</summary>
    IProbeRunner? Create(Settings settings, out string? description);
}

public sealed class DefaultProbeRunnerFactory : IProbeRunnerFactory
{
    public IProbeRunner? Create(Settings settings, out string? description)
    {
        FfmpegEnvironment.Current = FfmpegEnvironment.For(settings);
        var loc = FfmpegLocator.Find(settings.FfmpegFolder);
        description = loc?.FfprobePath;
        return loc == null ? null : new FfprobeRunner(loc.FfprobePath);
    }
}

public sealed record PlanRow(int Id, ScanEntry Entry, PlanItem Plan);

public sealed class ScanStatus
{
    public string State { get; set; } = "idle";   // idle | running | done | cancelled | error
    public string Folder { get; set; } = "";
    public string Phase { get; set; } = "";
    public int Done { get; set; }
    public int Total { get; set; }
    public int FromCache { get; set; }
    public int Failed { get; set; }
    public string? Current { get; set; }
    public double ElapsedSec { get; set; }
    public string? Message { get; set; }
    public int Rows { get; set; }
    public int Selected { get; set; }
    /// <summary>Increases with every snapshot (and across restarts), so the web UI can drop a stale one that arrives after a newer one.</summary>
    public long Seq { get; set; }
}

public sealed class PlanQuery
{
    public string Filter { get; set; } = "all";   // all | convert | skip | attention
    public string? Q { get; set; }
    public string Sort { get; set; } = "name";    // name | size | codec | res | action | saving | container
    public bool Desc { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 100;
}

/// <summary>Runs one scan at a time (Core scanner + planner), keeps the resulting plan rows in memory and the user's row selection (empty by default).</summary>
public sealed class ScanService
{
    private readonly ServerOptions _o;
    private readonly SettingsService _settings;
    private readonly RootGuard _guard;
    private readonly IProbeRunnerFactory _probe;
    private readonly EventHub _hub;
    private readonly ILogger<ScanService> _log;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _task;
    private readonly ScanStatus _status = new();
    private List<ScanEntry> _entries = new();
    private List<PlanRow> _rows = new();
    private readonly HashSet<int> _selected = new();
    private DateTime _started;
    private long _seq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public ScanService(ServerOptions o, SettingsService s, RootGuard g, IProbeRunnerFactory p, EventHub h, ILogger<ScanService> log)
    { _o = o; _settings = s; _guard = g; _probe = p; _hub = h; _log = log; }

    public Task? Running => _task;

    public ScanStatus Status()
    {
        lock (_lock)
        {
            var c = new ScanStatus
            {
                State = _status.State, Folder = _status.Folder, Phase = _status.Phase, Done = _status.Done, Total = _status.Total,
                FromCache = _status.FromCache, Failed = _status.Failed, Current = _status.Current, Message = _status.Message,
                Rows = _rows.Count, Selected = _selected.Count, Seq = ++_seq,
                ElapsedSec = _status.State == "running" ? (DateTime.UtcNow - _started).TotalSeconds : _status.ElapsedSec,
            };
            return c;
        }
    }

    public (bool Ok, string? Error, int HttpStatus) Start(string folder)
    {
        if (!_guard.TryResolveDirectory(folder, out var real, out var err)) return (false, err, 400);
        var settings = _settings.Load();
        var runner = _probe.Create(settings, out _);
        if (runner == null) return (false, "ffprobe was not found. Install ffmpeg (for example the jellyfin-ffmpeg7 package) or set the ffmpeg folder.", 503);
        lock (_lock)
        {
            if (_status.State == "running") return (false, "a scan is already running", 409);
            _cts = new CancellationTokenSource();
            _status.State = "running"; _status.Folder = real; _status.Phase = "Enumerating"; _status.Done = _status.Total = _status.FromCache = _status.Failed = 0;
            _status.Current = null; _status.Message = null; _started = DateTime.UtcNow;
            var ct = _cts.Token;
            _task = Task.Run(() => RunAsync(real, settings, runner, ct));
        }
        _hub.Publish("scan", Status());
        return (true, null, 202);
    }

    public bool Cancel()
    {
        lock (_lock) { if (_status.State != "running") return false; _cts?.Cancel(); return true; }
    }

    private async Task RunAsync(string folder, Settings settings, IProbeRunner runner, CancellationToken ct)
    {
        var last = DateTime.MinValue;
        try
        {
            var cache = ProbeCache.Load(_o.ProbeCachePath);
            var scanner = new Scanner(runner, cache, settings.ProbeThreads, settings.IncludePlexVersions);
            var progress = new Progress<ScanProgress>(p =>
            {
                lock (_lock)
                {
                    _status.Phase = p.Phase.ToString(); _status.Done = p.Done; _status.Total = p.Total;
                    _status.FromCache = p.FromCache; _status.Failed = p.Failed; _status.Current = p.Current;
                }
                if ((DateTime.UtcNow - last).TotalMilliseconds < 200 && p.Phase != ScanPhase.Done) return;
                last = DateTime.UtcNow;
                _hub.Publish("scan", Status());
            });
            var result = await scanner.ScanAsync(new[] { new LibraryRoot(Path.GetFileName(folder.TrimEnd('/', '\\')) is { Length: > 0 } n ? n : folder, folder) }, progress, ct);
            if (result.Cancelled || ct.IsCancellationRequested) { Finish("cancelled", "Scan cancelled", result.Elapsed); return; }
            var entries = result.Entries.Where(e => !EscapesRoot(e.FullPath)).ToList();
            try { cache.Prune(new[] { folder }, new HashSet<string>(entries.Select(e => e.FullPath), PathCompare.Comparer)); cache.Save(); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not save the probe cache"); }
            Replace(entries, settings);
            var msg = result.MissingRoots.Count > 0 ? "the folder is not available" : null;
            Finish("done", msg, result.Elapsed);
        }
        catch (OperationCanceledException) { Finish("cancelled", "Scan cancelled", TimeSpan.Zero); }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scan failed");
            Finish("error", "The scan failed: " + ex.Message, TimeSpan.Zero);
        }
    }

    /// <summary>A symlinked file whose real location is outside the allowed roots is dropped from the results.</summary>
    private bool EscapesRoot(string path)
    {
        try
        {
            if (new FileInfo(path).LinkTarget == null) return false;
            var rp = RootGuard.RealPath(path);
            return rp == null || !_guard.IsInside(rp);
        }
        catch { return true; }
    }

    private void Finish(string state, string? message, TimeSpan elapsed)
    {
        lock (_lock) { _status.State = state; _status.Message = message; _status.ElapsedSec = elapsed.TotalSeconds > 0 ? elapsed.TotalSeconds : (DateTime.UtcNow - _started).TotalSeconds; _status.Current = null; }
        _hub.Publish("scan", Status());
        if (state == "done") _hub.Publish("plan", new { rows = Status().Rows });
    }

    private void Replace(List<ScanEntry> entries, Settings settings)
    {
        var opts = settings.ToPlannerOptions();
        var rows = entries.Select((e, i) => new PlanRow(i, e, Planner.Plan(e, opts))).ToList();
        lock (_lock) { _entries = entries; _rows = rows; _selected.Clear(); }
    }

    /// <summary>Re-plans the current scan with the current settings (after a threshold change). The selection is cleared.</summary>
    public void Replan()
    {
        List<ScanEntry> entries;
        lock (_lock) { if (_entries.Count == 0) return; entries = _entries; }
        Replace(entries, _settings.Load());
        _hub.Publish("plan", new { rows = Status().Rows });
    }

    // ---------- plan queries ----------
    private static bool Matches(PlanRow r, string filter) => filter switch
    {
        "convert" => r.Plan.WritesFile,
        "skip" => r.Plan.Action == PlanAction.Skip,
        "attention" => r.Plan.Action is PlanAction.Error or PlanAction.Review || r.Plan.HasWarnings,
        _ => true,
    };

    public object Query(PlanQuery q)
    {
        List<PlanRow> all; HashSet<int> sel;
        lock (_lock) { all = _rows; sel = new HashSet<int>(_selected); }
        IEnumerable<PlanRow> f = all.Where(r => Matches(r, q.Filter));
        if (!string.IsNullOrWhiteSpace(q.Q)) { var t = q.Q.Trim(); f = f.Where(r => r.Entry.RelativePath.Contains(t, StringComparison.OrdinalIgnoreCase)); }
        Func<PlanRow, object> key = q.Sort switch
        {
            "size" => r => r.Entry.SizeBytes,
            "codec" => r => r.Entry.Probe.Video?.Codec ?? "",
            "res" => r => (r.Entry.Probe.Video?.Height ?? 0),
            "action" => r => r.Plan.ActionLabel,
            "saving" => r => r.Plan.EstSaveMidBytes,
            "container" => r => r.Entry.Extension,
            _ => r => r.Entry.RelativePath.ToLowerInvariant(),
        };
        var list = (q.Desc ? f.OrderByDescending(key) : f.OrderBy(key)).ThenBy(r => r.Id).ToList();
        var take = Math.Clamp(q.Take, 1, 500);
        var page = list.Skip(Math.Max(0, q.Skip)).Take(take).Select(r => Row(r, sel.Contains(r.Id))).ToList();
        return new { total = list.Count, skip = Math.Max(0, q.Skip), rows = page };
    }

    private static object Row(PlanRow r, bool selected)
    {
        var v = r.Entry.Probe.Video;
        return new
        {
            id = r.Id, path = r.Entry.RelativePath, name = r.Entry.FileName, sizeBytes = r.Entry.SizeBytes,
            container = r.Entry.Extension, codec = v?.Codec ?? "", width = v?.Width ?? 0, height = v?.Height ?? 0,
            resolution = r.Plan.Resolution.ToString(), kbps = r.Entry.Probe.OverallKbps ?? 0,
            action = r.Plan.Action.ToString(), actionLabel = r.Plan.ActionLabel, reason = r.Plan.Reason,
            estSaveBytes = r.Plan.EstSaveMidBytes, likelyRefused = r.Plan.LikelyRefused, warnings = r.Plan.HasWarnings,
            writes = r.Plan.WritesFile, selected,
        };
    }

    /// <summary>What the savings estimate is based on (shown under the summary cards).</summary>
    public static string EstimateNote(Settings s)
    {
        var o = s.ToPlannerOptions();
        if (!SavingsModel.UsesIntelModel(o)) return s.Mode == EncoderMode.Cpu ? "Estimate for CPU x265 (CRF " + s.CpuCrf + ")." : "";
        if (!SavingsModel.IntelEstimateMeasured(o)) return "Estimate for Intel AV1: NOT measured yet (a guess based on the HEVC numbers). Treat the saving as unknown until AV1 is benchmarked.";
        var q = s.GpuBackend == GpuBackend.Qsv ? "QSV q" + s.IntelQsvCq : "VAAPI q" + s.IntelCq;
        return "Estimate for Intel HEVC (" + q + "), based on benchmark measurements on the Arc Pro B60 (5 movies, 30 s clips): about 30 % of the source size at the default quality. Real files vary.";
    }

    public object Summary()
    {
        List<PlanRow> all; int sel; long selBytes, selSave;
        lock (_lock)
        {
            all = _rows; sel = _selected.Count;
            selBytes = all.Where(r => _selected.Contains(r.Id)).Sum(r => r.Entry.SizeBytes);
            selSave = all.Where(r => _selected.Contains(r.Id)).Sum(r => r.Plan.EstSaveMidBytes);
        }
        var s = PlanSummary.From(all.Select(r => (r.Entry, r.Plan)));
        return new
        {
            s.Total, s.TotalBytes, s.Skip, s.Remux, s.ReencodeSize, s.ReencodeCompat, s.FixAudio, s.Error, s.Review, s.Warnings,
            s.LikelyRefused, s.EstSaveMidBytes, s.EstSaveLowBytes, s.EstSaveHighBytes, s.AudioConversions,
            selected = sel, selectedBytes = selBytes, selectedEstSaveBytes = selSave,
            estimateNote = EstimateNote(_settings.Load()),
        };
    }

    /// <summary>Selects or clears rows. ids = explicit rows; all = every row that matches the filter.</summary>
    public int Select(IEnumerable<int>? ids, bool selected, string? allMatching, string? q)
    {
        lock (_lock)
        {
            IEnumerable<int> target = ids ?? Enumerable.Empty<int>();
            if (allMatching != null)
            {
                IEnumerable<PlanRow> f = _rows.Where(r => Matches(r, allMatching) && (selected ? r.Plan.WritesFile : true));
                if (!string.IsNullOrWhiteSpace(q)) f = f.Where(r => r.Entry.RelativePath.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase));
                target = target.Concat(f.Select(r => r.Id));
            }
            var valid = _rows.Count;
            foreach (var id in target)
            {
                if (id < 0 || id >= valid) continue;
                if (selected) { if (_rows[id].Plan.WritesFile) _selected.Add(id); } else _selected.Remove(id);
            }
            return _selected.Count;
        }
    }

    /// <summary>The selected rows (in plan order) that still write a file.</summary>
    public List<PlanRow> SelectedRows()
    {
        lock (_lock) return _rows.Where(r => _selected.Contains(r.Id) && r.Plan.WritesFile).ToList();
    }

    /// <summary>Selected files that were queued: they leave the selection.</summary>
    public void ClearSelection() { lock (_lock) _selected.Clear(); _hub.Publish("plan", new { rows = Status().Rows }); }

    public string ExportCsv()
    {
        List<PlanRow> all; lock (_lock) all = _rows;
        return PlanCsv.Build(all.Select(r => (r.Entry, r.Plan)));
    }
}
