// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using Convertr.Core;

namespace Convertr.Svc.Server;

/// <summary>Hardware detection for the web UI: CPU, GPUs, ffmpeg, and the Intel (VAAPI / QSV) test encodes. The result is stored in settings.json.</summary>
public sealed class HardwareService
{
    private readonly SettingsService _settings;
    private readonly EventHub _hub;
    private readonly IIntelProcessRunner _runner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<HardwareService> _log;

    public HardwareService(SettingsService settings, EventHub hub, IIntelProcessRunner runner, ILogger<HardwareService> log)
    { _settings = settings; _hub = hub; _runner = runner; _log = log; }

    public HardwareProfile? Current => _settings.Load().Hardware;

    public bool Detecting => _gate.CurrentCount == 0;

    public async Task<HardwareProfile> DetectAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var s = _settings.Load();
            FfmpegEnvironment.Current = FfmpegEnvironment.For(s);
            var loc = FfmpegLocator.Find(s.FfmpegFolder);
            var hw = await HardwareDetector.DetectAsync(loc, null, ct).ConfigureAwait(false);
            var wantIntel = hw.Gpus.Any(g => g.Vendor == "Intel") || IntelHw.FirstRenderNode() != null || !string.IsNullOrWhiteSpace(s.GpuDevice);
            var capture = new FailureCapturingRunner(_runner);
            if (wantIntel && loc != null) hw.Intel = await IntelProbe.DetectAsync(loc, s, capture, null, ct).ConfigureAwait(false);
            else if (loc != null) hw.Intel = new IntelCapabilities { Tested = true, Reason = "no Intel GPU / render node found" };
            if (hw.Intel is { Tested: true, Any: false } && capture.Failures.Count > 0) ExplainFailure(hw.Intel, capture.Failures);
            hw.DefaultsSummary = hw.Intel is { Any: true } ? "Intel GPU: " + hw.Intel.Summary : (hw.Intel?.Reason ?? "");
            _settings.Mutate(x =>
            {
                x.Hardware = hw;
                if (hw.Intel is { Any: true }) IntelProbe.Apply(x, hw.Intel);
            });
            FfmpegEnvironment.Current = FfmpegEnvironment.For(_settings.Load());
            _log.LogInformation("Hardware detected: {Cpu}; GPUs: {Gpus}; Intel: {Intel}", hw.Cpu, string.Join(", ", hw.Gpus.Select(g => g.Name)), hw.Intel?.Summary);
            _hub.Publish("hardware", View(hw));
            return hw;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Puts the real ffmpeg error text (first lines of stderr of each failed test encode) into the job log and the problem text, and logs it at Warning.</summary>
    private void ExplainFailure(IntelCapabilities intel, IReadOnlyList<ProbeFailure> failures)
    {
        var parts = new List<string>();
        foreach (var f in failures)
        {
            var head = HeadLines(f.Stderr, 5);
            intel.Log.Add($"ffmpeg {f.Encoder} failed, exit {f.ExitCode}; stderr (first {head.Count} line{(head.Count == 1 ? "" : "s")}):");
            if (head.Count == 0) intel.Log.Add("    (ffmpeg printed nothing)");
            foreach (var l in head) intel.Log.Add("    " + l);
            parts.Add($"{f.Encoder}: exit {f.ExitCode}" + (head.Count > 0 ? " - " + string.Join(" | ", head) : ""));
        }
        var detail = string.Join(" ;; ", parts);
        if (!intel.Reason.Contains("ffmpeg said:", StringComparison.Ordinal)) intel.Reason = (intel.Reason + " ffmpeg said: " + detail).Trim();
        _log.LogWarning("Intel GPU encoder test failed ({Reason}); ffmpeg stderr: {Detail}", intel.Reason, detail);
    }

    /// <summary>The first <paramref name="max"/> non-empty lines of a text, each cut to 300 characters.</summary>
    public static List<string> HeadLines(string? text, int max = 5) =>
        (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Length > 300 ? l[..300] + "..." : l).Take(max).ToList();

    public sealed record ProbeFailure(string Encoder, int ExitCode, string Stderr);

    /// <summary>Wraps the runner and remembers the first failed ffmpeg test per encoder (the Intel probe itself only keeps one line).</summary>
    private sealed class FailureCapturingRunner : IIntelProcessRunner
    {
        private readonly IIntelProcessRunner _inner;
        public List<ProbeFailure> Failures { get; } = new();
        public FailureCapturingRunner(IIntelProcessRunner inner) { _inner = inner; }

        public async Task<(int ExitCode, string Stderr)> RunAsync(string exe, IReadOnlyList<string> args, IReadOnlyList<string> envPairs, TimeSpan timeout, CancellationToken ct)
        {
            var r = await _inner.RunAsync(exe, args, envPairs, timeout, ct).ConfigureAwait(false);
            if (r.ExitCode != 0 && args.Contains("-c:v"))
            {
                var i = args.ToList().IndexOf("-c:v");
                var enc = i >= 0 && i + 1 < args.Count ? args[i + 1] : "encoder";
                if (!Failures.Any(f => f.Encoder == enc)) Failures.Add(new ProbeFailure(enc, r.ExitCode, r.Stderr));
            }
            return r;
        }
    }

    public object View(HardwareProfile? hw)
    {
        var s = _settings.Load();
        return new
        {
            detected = hw != null, whenUtc = hw?.DetectedUtc, cpu = hw?.Cpu.ToString(), gpus = hw?.Gpus.Select(g => g.ToString()).ToList() ?? new(),
            ffmpeg = hw?.FfmpegVersion, ffmpegPath = hw?.FfmpegPath,
            intel = hw?.Intel == null ? null : new
            {
                hw.Intel.Tested, hw.Intel.Device, hw.Intel.Driver, hw.Intel.VaapiHevc, hw.Intel.QsvHevc, hw.Intel.VaapiAv1, hw.Intel.QsvAv1,
                hw.Intel.SystemLibsNeeded, hw.Intel.Reason, summary = hw.Intel.Summary, log = hw.Intel.Log,
            },
            effective = new { backend = s.GpuBackend.ToString(), codec = s.GpuCodec.ToString(), mode = s.Mode.ToString(), device = s.GpuDevice, encoderArgs = s.GpuEncoderArgs, environment = s.FfmpegEnvironment },
        };
    }
}
