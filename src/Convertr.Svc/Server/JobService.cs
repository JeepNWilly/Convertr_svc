// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Globalization;
using Convertr.Core;

namespace Convertr.Svc.Server;

/// <summary>Creates the ffmpeg / ffprobe runners for a conversion run (replaced in tests).</summary>
public interface IEngineBackend
{
    IFfmpegRunner CreateRunner(FfmpegLocation loc);
    IProbeRunner CreateProbe(FfmpegLocation loc);
}

public sealed class DefaultEngineBackend : IEngineBackend
{
    public IFfmpegRunner CreateRunner(FfmpegLocation loc) => new FfmpegProcessRunner(loc.FfmpegPath);
    public IProbeRunner CreateProbe(FfmpegLocation loc) => new FfprobeRunner(loc.FfprobePath);
}

/// <summary>
/// DESIGN section 5A.2 "remember": when ffmpeg dies with a crash (Bus error / exit 135 ...) while the bundled libraries are in use,
/// the system libva / iHD environment is applied, remembered in the settings, and the same file is encoded again once.
/// </summary>
public sealed class CrashFallbackRunner : IFfmpegRunner
{
    private readonly IFfmpegRunner _inner;
    private readonly Func<bool> _applySystemLibs;
    public CrashFallbackRunner(IFfmpegRunner inner, Func<bool> applySystemLibs) { _inner = inner; _applySystemLibs = applySystemLibs; }

    public async Task<FfmpegRunResult> RunAsync(IReadOnlyList<string> args, IProgress<ProgressSnapshot>? progress, ProcessControl? control, CancellationToken ct)
    {
        var r = await _inner.RunAsync(args, progress, control, ct).ConfigureAwait(false);
        if (!r.Cancelled && r.ExitCode != 0 && IntelProbe.IsCrash(r.ExitCode, r.StderrTail) && args.Contains("-init_hw_device") && _applySystemLibs())
            r = await _inner.RunAsync(args, progress, control, ct).ConfigureAwait(false);
        return r;
    }
}

public sealed record QueueSummaryView(string State, string RunId, string Folder, bool DryRun, int Total, int Pending, int Active, int Done, int NotWorthIt, int Failed, int Skipped,
    long SourceBytes, long SavedBytes, string Message, bool HasQueue, string Mode, string Storage);

/// <summary>Owns the conversion queue: builds it from the selected plan rows, persists it (queue.json), runs the Core engine with the scratch storage, and reports through SSE.</summary>
public sealed class JobService
{
    private readonly ServerOptions _o;
    private readonly SettingsService _settings;
    private readonly ScanService _scan;
    private readonly RootGuard _guard;
    private readonly EventHub _hub;
    private readonly IEngineBackend _backend;
    private readonly HardwareService _hw;
    private readonly ILogger<JobService> _log;
    private readonly object _lock = new();
    private QueueFile? _queue;
    private ConversionEngine? _engine;
    private Task? _run;
    private string _state = "idle";   // idle | running | paused | stopping | finished
    private string _message = "";
    private DateTime _lastPublish = DateTime.MinValue;
    private EngineProgress? _progress;
    private RunSummary? _summary;
    private ScratchStorage? _scratch;

    public JobService(ServerOptions o, SettingsService s, ScanService scan, RootGuard g, EventHub h, IEngineBackend b, HardwareService hw, ILogger<JobService> log)
    {
        _o = o; _settings = s; _scan = scan; _guard = g; _hub = h; _backend = b; _hw = hw; _log = log;
        _queue = QueueStore.Load(_o.QueuePath);
        if (_queue != null && _queue.Remaining == 0 && _queue.Jobs.Count > 0) _state = "finished";
        else if (_queue != null && _queue.Remaining > 0) { _state = "idle"; _message = "A queue from before the restart is waiting: " + _queue.Remaining + " file(s). Press Resume to continue or Discard."; }
    }

    public Task? Running => _run;
    public string LogsDir => Path.Combine(_o.DataDir, "logs");

    // ---------------- confirmation ----------------
    public sealed record Confirmation(int Files, long SourceBytes, long EstOutBytes, long EstSaveBytes, string Mode, string Backend, string Storage, long ScratchNeedBytes,
        long ScratchFreeBytes, long ScratchBudgetBytes, string ScratchFolder, int Workers, bool CanStart, List<string> Blockers, List<string> Warnings, string Folder, string Originals, bool Busy);

    public Confirmation Preview()
    {
        var s = _settings.Load();
        var sel = _scan.SelectedRows();
        var blockers = new List<string>(); var warnings = new List<string>();
        var folder = _scan.Status().Folder;
        var busy = IsBusy();
        if (busy) blockers.Add("a conversion run is already active");
        if (sel.Count == 0) blockers.Add("no files are selected");
        if (string.IsNullOrEmpty(folder)) blockers.Add("scan a folder first");
        var loc = FfmpegLocator.Find(s.FfmpegFolder);
        if (loc == null) blockers.Add("ffmpeg was not found");
        var src = sel.Sum(r => r.Entry.SizeBytes);
        var save = sel.Sum(r => r.Plan.EstSaveMidBytes);
        var workers = Math.Max(1, s.Mode == EncoderMode.Gpu ? s.ParallelGpuJobs : s.ParallelCpuJobs);
        long need = 0, free = 0, budget = 0;
        if (s.UseScratch)
        {
            var largest = sel.OrderByDescending(r => r.Entry.SizeBytes).Take(workers).Sum(r => ScratchStorage.NeededBytes(r.Entry.SizeBytes));
            need = largest;
            if (string.IsNullOrWhiteSpace(s.ScratchFolder)) blockers.Add("the scratch folder is not set (Settings)");
            else
            {
                free = FreeOf(s.ScratchFolder);
                budget = s.ScratchBudgetGb > 0 ? s.ScratchBudgetGb * 1073741824L : Math.Max(0, (long)(free * 0.9) - 2 * 1073741824L);
                if (!Directory.Exists(s.ScratchFolder) && !CanCreate(s.ScratchFolder)) blockers.Add("the scratch folder does not exist and cannot be created");
                if (need > budget) blockers.Add($"the scratch disk is too small: need about {need / 1073741824.0:0.0} GB at once, budget {budget / 1073741824.0:0.0} GB");
                var tooBig = sel.Where(r => ScratchStorage.NeededBytes(r.Entry.SizeBytes) > budget).ToList();
                if (tooBig.Count > 0 && need <= budget) warnings.Add($"{tooBig.Count} file(s) are too big for the scratch budget and will be stopped on");
            }
        }
        else warnings.Add("Scratch is off: ffmpeg writes next to the original on the library share (slower over a network, and not recommended for a NAS).");
        if (s.Mode == EncoderMode.Gpu)
        {
            var intel = _hw.Current?.Intel;
            if (s.GpuBackend is GpuBackend.Vaapi or GpuBackend.Qsv)
            {
                if (intel == null || !intel.Tested) warnings.Add("The Intel GPU has not been tested yet; it is tested automatically when the run starts.");
                else if (!intel.Any) blockers.Add("the Intel GPU encoder does not work: " + intel.Reason);
            }
        }
        if (s.Originals == OriginalsHandling.MoveToHolding && !string.IsNullOrEmpty(folder))
        {
            var parent = Path.GetDirectoryName(folder.TrimEnd('/'));
            if (string.IsNullOrEmpty(parent) || !_guard.CanWriteHolding(folder)) blockers.Add($"originals cannot be moved to '{HoldingArea.Root(folder, s.OriginalsFolderName)}' (its parent folder is not writable for the service); allow it in the service settings (ReadWritePaths) or choose another Media folder");
        }
        if (sel.Any(r => r.Plan.LikelyRefused)) warnings.Add("some files will probably be kept as they are (saving too small)");
        var est = Math.Max(0, src - save);
        return new Confirmation(sel.Count, src, est, save, s.Mode.ToString(), s.Mode == EncoderMode.Gpu ? s.GpuBackend.ToString() : "CPU (x265)", s.UseScratch ? "scratch" : "in place",
            need, free, budget, s.ScratchFolder, workers, blockers.Count == 0, blockers, warnings, folder, s.Originals.ToString(), busy);
    }

    private static long FreeOf(string path)
    {
        try
        {
            var p = path; while (!string.IsNullOrEmpty(p) && !Directory.Exists(p)) p = Path.GetDirectoryName(p) ?? "";
            return string.IsNullOrEmpty(p) ? 0 : new DriveInfo(p).AvailableFreeSpace;
        }
        catch { return 0; }
    }

    private static bool CanCreate(string path)
    {
        try { Directory.CreateDirectory(path); return true; } catch { return false; }
    }

    // ---------------- start / control ----------------
    public bool IsBusy() { lock (_lock) return _state is "running" or "paused" or "stopping"; }

    public async Task<(bool Ok, string? Error, int Status)> StartAsync(bool dryRun)
    {
        var s = _settings.Load();
        var conf = Preview();
        if (!conf.CanStart) return (false, string.Join("; ", conf.Blockers), conf.Busy ? 409 : 400);
        var loc = FfmpegLocator.Find(s.FfmpegFolder)!;
        var rows = _scan.SelectedRows();
        // the selected files must still be inside the allowed folders
        rows = rows.Where(r => _guard.IsInside(r.Entry.FullPath)).ToList();
        if (rows.Count == 0) return (false, "no selected file is inside an allowed folder", 400);

        if (s.Mode == EncoderMode.Gpu && s.GpuBackend is GpuBackend.Vaapi or GpuBackend.Qsv && !dryRun)
        {
            var hw = _hw.Current;
            if (hw?.Intel is not { Tested: true }) { hw = await _hw.DetectAsync(CancellationToken.None).ConfigureAwait(false); s = _settings.Load(); }
            if (hw.Intel is { Any: false }) return (false, "the Intel GPU encoder does not work: " + hw.Intel.Reason, 400);
        }
        FfmpegEnvironment.Current = FfmpegEnvironment.For(s);

        var runId = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        QueueFile q;
        lock (_lock)
        {
            if (_state is "running" or "paused" or "stopping") return (false, "a conversion run is already active", 409);
            q = QueueBuilder.Build(rows.Select(r => (r.Entry, r.Plan)), s, conf.Folder, runId);
            q.DryRun = dryRun;
            _queue = q; _summary = null; _progress = null; _message = "";
            QueueStore.Save(q, _o.QueuePath);
        }
        Launch(q, s, loc);
        return (true, null, 202);
    }

    public (bool Ok, string? Error, int Status) Resume()
    {
        var s = _settings.Load();
        var loc = FfmpegLocator.Find(s.FfmpegFolder);
        lock (_lock)
        {
            if (_state is "running" or "paused" or "stopping") return (false, "a conversion run is already active", 409);
            if (_queue == null || _queue.Remaining == 0) return (false, "there is no unfinished queue", 400);
            if (loc == null) return (false, "ffmpeg was not found", 503);
        }
        FfmpegEnvironment.Current = FfmpegEnvironment.For(s);
        _message = "";
        Launch(_queue!, s, loc);
        return (true, null, 202);
    }

    private void Launch(QueueFile q, Settings s, FfmpegLocation loc)
    {
        var logPath = RunLog.NewPath(LogsDir, DateTime.Now);
        var log = new RunLog(logPath) { Echo = PublishLogLine };
        var journal = new UndoJournal(UndoJournal.NewPath(LogsDir, DateTime.Now));
        var ff = new CrashFallbackRunner(_backend.CreateRunner(loc), ApplySystemLibs);
        var swapper = new Swapper(s, journal, log);
        IJobStorage storage = s.UseScratch ? (_scratch = new ScratchStorage(s, swapper, log)) : new InPlaceStorage(swapper);
        var engine = new ConversionEngine(s, ff, _backend.CreateProbe(loc), log, journal, _o.QueuePath, storage: storage);
        engine.JobChanged += j => { Publish("job", JobView(j)); PublishQueue(false); };
        engine.ProgressChanged += p => { _progress = p; PublishProgress(p); };
        engine.StatusChanged += m => { _message = m; PublishQueue(true); };
        lock (_lock) { _engine = engine; _state = "running"; }
        PublishQueue(true);
        _run = Task.Run(async () =>
        {
            try { _summary = await engine.RunAsync(q).ConfigureAwait(false); _message = _summary.Stopped ? "Stopped: " + _summary.StopReason : _summary.Failed > 0 ? "Finished with failures" : "Finished"; }
            catch (Exception ex) { _log.LogError(ex, "conversion run crashed"); _message = "The run stopped unexpectedly: " + ex.Message; }
            finally
            {
                lock (_lock) { _state = q.Remaining == 0 ? "finished" : "idle"; _engine = null; }
                PublishQueue(true);
                Publish("undo", new { changed = true });
            }
        });
    }

    private bool ApplySystemLibs()
    {
        var s = _settings.Load();
        if (s.FfmpegEnvironment.Count > 0) return false;   // already configured: a crash then is something else
        _settings.Mutate(x => x.FfmpegEnvironment = FfmpegEnvironment.SystemLibsPairs.ToList());
        FfmpegEnvironment.Current = FfmpegEnvironment.For(_settings.Load());
        _log.LogWarning("ffmpeg crashed with the bundled libraries; switching to the system libva / iHD environment and remembering it");
        return true;
    }

    public bool Pause() { ConversionEngine? e; lock (_lock) { e = _engine; if (e == null || _state != "running") return false; _state = "paused"; } e.Pause(); PublishQueue(true); return true; }
    public bool ResumePaused() { ConversionEngine? e; lock (_lock) { e = _engine; if (e == null || _state != "paused") return false; _state = "running"; } e.Resume(); PublishQueue(true); return true; }
    /// <summary>Stop now: the running ffmpeg is killed, its temporary files are deleted, the original is untouched; the rest stays in the queue.</summary>
    public bool Stop() { ConversionEngine? e; lock (_lock) { e = _engine; if (e == null) return false; _state = "stopping"; } e.Stop(); PublishQueue(true); return true; }
    public bool StopAfterCurrent() { ConversionEngine? e; lock (_lock) { e = _engine; if (e == null) return false; _state = "stopping"; } e.StopAfterCurrent(); PublishQueue(true); return true; }

    /// <summary>Removes a waiting job from the queue (a running job is cancelled with Stop).</summary>
    public (bool Ok, string? Error) RemoveJob(int id)
    {
        lock (_lock)
        {
            var j = _queue?.Jobs.FirstOrDefault(x => x.Id == id);
            if (j == null) return (false, "unknown job");
            if (j.State != JobState.Pending) return (false, "only a waiting job can be removed; use Stop for the running one");
            j.State = JobState.Skipped; j.Message = "removed from the queue by the user";
            try { QueueStore.Save(_queue!, _o.QueuePath); } catch { /* ignore */ }
        }
        Publish("job", JobView(_queue!.Jobs.First(x => x.Id == id)));
        PublishQueue(true);
        return (true, null);
    }

    public (bool Ok, string? Error) Discard()
    {
        lock (_lock)
        {
            if (_state is "running" or "paused" or "stopping") return (false, "stop the run first");
            _queue = null; _summary = null; _progress = null; _state = "idle"; _message = "";
            QueueStore.Delete(_o.QueuePath);
        }
        PublishQueue(true);
        return (true, null);
    }

    // ---------------- views ----------------
    public QueueSummaryView Status()
    {
        lock (_lock)
        {
            var q = _queue;
            var s = _settings.Load();
            if (q == null) return new QueueSummaryView(_state, "", "", false, 0, 0, 0, 0, 0, 0, 0, 0, 0, _message, false, s.Mode.ToString(), s.UseScratch ? "scratch" : "in place");
            var done = q.Jobs.Where(j => j.State == JobState.Done).ToList();
            return new QueueSummaryView(_state, q.RunId, q.MediaFolder, q.DryRun, q.Jobs.Count,
                q.Jobs.Count(j => j.State == JobState.Pending), q.Jobs.Count(j => j.State is JobState.CopyingIn or JobState.Encoding or JobState.Verifying or JobState.CopyingBack or JobState.Swapping),
                done.Count, q.Jobs.Count(j => j.State == JobState.NotWorthIt), q.Jobs.Count(j => j.State == JobState.Failed), q.Jobs.Count(j => j.State == JobState.Skipped),
                q.Jobs.Sum(j => j.Entry.SizeBytes), done.Sum(j => j.Entry.SizeBytes - j.OutputBytes), _message, true, s.Mode.ToString(), s.UseScratch ? "scratch" : "in place");
        }
    }

    public static object JobView(QueueJob j) => new
    {
        id = j.Id, state = j.State.ToString(), name = j.Entry.FileName, path = j.Entry.RelativePath, sizeBytes = j.Entry.SizeBytes, outputBytes = j.OutputBytes,
        savingPercent = Math.Round(j.SavingPercent, 1), message = j.Message, elapsedSec = Math.Round(j.ElapsedSeconds), speed = j.Speed is double sp ? Math.Round(sp, 2) : (double?)null,
        copyFraction = Math.Round(j.CopyFraction, 4), copyMBps = Math.Round(j.CopyMBps, 1), action = j.Plan.ActionLabel,
    };

    public object Jobs(int skip, int take)
    {
        List<QueueJob> jobs; lock (_lock) jobs = _queue?.Jobs.ToList() ?? new();
        // running first, then waiting, then finished (newest first)
        int Rank(QueueJob j) => j.IsFinal ? 2 : j.State == JobState.Pending ? 1 : 0;
        var ordered = jobs.OrderBy(Rank).ThenBy(j => Rank(j) == 2 ? -(j.FinishedUtc?.Ticks ?? 0) : j.Id).ToList();
        return new { total = ordered.Count, skip, jobs = ordered.Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 500)).Select(JobView).ToList() };
    }

    public object ProgressView()
    {
        var p = _progress;
        return p == null ? new { } : new
        {
            jobId = p.Job?.Id, jobFraction = Math.Round(p.JobFraction, 4), speed = p.Speed, etaSec = p.JobEta?.TotalSeconds, finished = p.FinishedJobs, total = p.TotalJobs,
            processedBytes = p.ProcessedBytes, totalBytes = p.TotalBytes, elapsedSec = p.Elapsed.TotalSeconds, overallEtaSec = p.OverallEta?.TotalSeconds,
        };
    }

    private void Publish(string type, object payload) => _hub.Publish(type, payload);

    /// <summary>Every run-log line (also written to the file under LogsDir) is pushed to the browsers as a "log" event (Queue page > Log) and to the journal at Info level.</summary>
    private void PublishLogLine(string line)
    {
        _log.LogInformation("{RunLogLine}", line);
        _hub.Publish("log", new { line });
    }

    // ---------------- run log (Queue page > Log) ----------------
    public const int MaxLogLines = 2000;

    /// <summary>The newest run log under <see cref="LogsDir"/> (Convertr_yyyyMMdd_HHmm.log), or null when there is none yet.</summary>
    public string? LatestLogPath()
    {
        try
        {
            if (!Directory.Exists(LogsDir)) return null;
            return Directory.GetFiles(LogsDir, "Convertr_*.log").OrderByDescending(f => File.GetLastWriteTimeUtc(f)).ThenByDescending(f => f, StringComparer.Ordinal).FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>The last <paramref name="take"/> lines of the newest run log (dry-run ffmpeg commands included). The log is a plain file, so it survives restarts.</summary>
    public object LogView(int take)
    {
        take = Math.Clamp(take, 1, MaxLogLines);
        var path = LatestLogPath();
        if (path == null) return new { file = "", path = "", logsDir = LogsDir, lines = Array.Empty<string>() };
        var q = new Queue<string>(take + 1);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var rd = new StreamReader(fs, System.Text.Encoding.UTF8);
            string? l;
            while ((l = rd.ReadLine()) != null) { q.Enqueue(l); if (q.Count > take) q.Dequeue(); }
        }
        catch { /* unreadable: show nothing */ }
        return new { file = Path.GetFileName(path), path, logsDir = LogsDir, lines = q.ToArray() };
    }

    private void PublishQueue(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - _lastPublish).TotalMilliseconds < 400) return;
        _lastPublish = now;
        _hub.Publish("queue", Status());
    }

    private DateTime _lastProgress = DateTime.MinValue;
    private void PublishProgress(EngineProgress p)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastProgress).TotalMilliseconds < 500) return;
        _lastProgress = now;
        _hub.Publish("progress", ProgressView());
    }

    // ---------------- undo ----------------
    public IReadOnlyList<UndoItem> UndoItems()
    {
        try
        {
            if (!Directory.Exists(LogsDir)) return Array.Empty<UndoItem>();
            return UndoService.Load(Directory.GetFiles(LogsDir, "undo_*.csv")).Where(i => _guard.IsInside(i.OriginalPath) || _guard.IsInside(i.NewPath)).ToList();
        }
        catch { return Array.Empty<UndoItem>(); }
    }

    public (bool Ok, string? Error, List<object> Results) Undo(IEnumerable<string> originalPaths)
    {
        if (IsBusy()) return (false, "stop the conversion run before undoing", new());
        var wanted = new HashSet<string>(originalPaths, PathCompare.Comparer);
        var items = UndoItems().Where(i => wanted.Contains(i.OriginalPath)).ToList();
        if (items.Count == 0) return (false, "nothing to undo for the given files", new());
        var s = _settings.Load();
        var log = new RunLog(RunLog.NewPath(LogsDir, DateTime.Now)) { Echo = PublishLogLine };
        var journal = new UndoJournal(UndoJournal.NewPath(LogsDir, DateTime.Now));
        var res = UndoService.Execute(items, journal, log, s.PreserveTimestamps);
        Publish("undo", new { changed = true });
        return (true, null, res.Select(r => (object)new { path = r.Item.OriginalPath, success = r.Success, message = r.Message }).ToList());
    }
}
