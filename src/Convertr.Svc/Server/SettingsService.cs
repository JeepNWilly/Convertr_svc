// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Reflection;
using System.Text.Json;
using Convertr.Core;

namespace Convertr.Svc.Server;

/// <summary>
/// Reads and writes settings.json in the data dir. The web UI may change only the fields in <see cref="Editable"/>;
/// everything else (ffmpeg folder, environment, scratch, GPU backend ...) keeps its stored value until the later milestones add safe editors.
/// </summary>
public sealed class SettingsService
{
    private readonly ServerOptions _o;
    private readonly RootGuard _guard;
    private readonly object _lock = new();

    public static readonly string[] Editable =
    {
        "Theme", "MediaFolder", "Container", "Mode", "Kbps1080", "Kbps720", "KbpsSd", "KbpsBelowSd", "KeepIfSmallerPercent", "KeepIfSmallerMb",
        "ProcessInterlaced", "CleanJunkMetadata", "PreserveTimestamps", "IncludePlexVersions", "ProbeThreads", "ParallelGpuJobs", "ParallelCpuJobs",
        "UseScratch", "ScratchFolder", "ScratchBudgetGb", "MaxScratchPerJobGb", "MaxConcurrentTransfers", "CopyBackVerify", "KeepScratchOnFailure",
        "GpuBackend", "GpuCodec", "GpuDevice", "IntelCq", "IntelQsvCq", "IntelAv1Cq", "CpuCrf", "GpuCq",
    };

    public SettingsService(ServerOptions o, RootGuard g) { _o = o; _guard = g; }

    /// <summary>Changes any setting from inside the service (hardware detection, remembered ffmpeg environment) and saves.</summary>
    public Settings Mutate(Action<Settings> change)
    {
        lock (_lock)
        {
            var s = SettingsStore.Load(_o.SettingsPath);
            change(s);
            SettingsStore.Save(s, _o.SettingsPath);
            return s;
        }
    }

    /// <summary>The settings as the rest of the service sees them. The service has no NVIDIA encoder path: a stored NVENC (the desktop default) or Auto backend means "Intel VAAPI" until the hardware test picks the backend.</summary>
    public Settings Load()
    {
        lock (_lock)
        {
            return Effective(SettingsStore.Load(_o.SettingsPath));
        }
    }

    private static Settings Effective(Settings s)
    {
        if (s.GpuBackend is GpuBackend.Nvenc or GpuBackend.Auto) s.GpuBackend = GpuBackend.Vaapi;
        return s;
    }

    /// <summary>Applies a partial update. Returns the new settings or an error text (nothing is saved on error).</summary>
    public (Settings? Settings, string? Error) Update(JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object) return (null, "an object is required");
        lock (_lock)
        {
            var s = SettingsStore.Load(_o.SettingsPath);
            foreach (var prop in patch.EnumerateObject())
            {
                var name = Editable.FirstOrDefault(n => n.Equals(prop.Name, StringComparison.OrdinalIgnoreCase));
                if (name == null) return (null, $"'{prop.Name}' cannot be changed here");
                var pi = typeof(Settings).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!;
                object? value;
                try { value = JsonSerializer.Deserialize(prop.Value.GetRawText(), pi.PropertyType, SettingsStore.JsonOptions); }
                catch { return (null, $"'{name}' has an invalid value"); }
                if (value == null) return (null, $"'{name}' has an invalid value");
                if (name == "MediaFolder")
                {
                    var folder = ((string)value).Trim();
                    if (folder.Length > 0)
                    {
                        if (!_guard.TryResolveDirectory(folder, out var real, out var err)) return (null, "Media folder: " + err);
                        folder = real;
                    }
                    value = folder;
                }
                if (name == "ScratchFolder")
                {
                    var f = ((string)value).Trim();
                    if (f.Length > 0)
                    {
                        if (!Path.IsPathRooted(f) || f.Contains("..")) return (null, "Scratch folder: an absolute path without '..' is required");
                        f = Path.GetFullPath(f).TrimEnd('/');
                        if (f.Length == 0) return (null, "Scratch folder: the root folder cannot be used");
                        if (_guard.IsInside(f) || _guard.Roots.Any(r => PathCompare.StartsWith(r, f + "/"))) return (null, "Scratch folder: it must be on a local disk outside the media folders");
                    }
                    value = f;
                }
                if (name == "GpuDevice")
                {
                    var d = ((string)value).Trim();
                    if (d.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(d, @"^/dev/dri/renderD\d{3}$")) return (null, "GPU device: use /dev/dri/renderD128 (or leave empty)");
                    value = d;
                }
                if (value is int i && !InRange(name, i)) return (null, $"'{name}' is out of range");
                pi.SetValue(s, value);
            }
            SettingsStore.Save(s, _o.SettingsPath);
            return (Effective(s), null);
        }
    }

    private static bool InRange(string name, int v) => name switch
    {
        "KeepIfSmallerPercent" => v is >= 0 and <= 90,
        "KeepIfSmallerMb" => v is >= 0 and <= 100000,
        "ProbeThreads" => v is >= 1 and <= 16,
        "ParallelGpuJobs" or "ParallelCpuJobs" => v is >= 1 and <= 8,
        "ScratchBudgetGb" or "MaxScratchPerJobGb" => v is >= 0 and <= 100000,
        "MaxConcurrentTransfers" => v is >= 1 and <= 8,
        "IntelCq" or "IntelQsvCq" or "GpuCq" => v is >= 10 and <= 45,
        "IntelAv1Cq" => v is >= 10 and <= 200,
        "CpuCrf" => v is >= 10 and <= 35,
        _ => v is >= 100 and <= 100000,   // the kbps thresholds
    };
}
