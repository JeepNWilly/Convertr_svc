// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using Microsoft.Extensions.Configuration;

namespace Convertr.Svc.Server;

/// <summary>Server settings (config file + environment + command line). Read lazily from IConfiguration section "Convertr".</summary>
public sealed class ServerOptions
{
    public const string DefaultConfigDir = "/etc/convertr-svc";
    public const string DefaultDataDir = "/var/lib/convertr-svc";

    public string ConfigDir { get; init; } = DefaultConfigDir;
    public string DataDir { get; init; } = DefaultDataDir;
    public string Listen { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 8484;
    public string[] AllowedRoots { get; init; } = Array.Empty<string>();
    /// <summary>PBKDF2 iterations for new password hashes (tests lower it).</summary>
    public int PasswordIterations { get; init; } = 600_000;
    public int MinPasswordLength { get; init; } = 10;
    public int SessionHours { get; init; } = 12;
    /// <summary>Folders below which a NAS mount point may be chosen on the Setup page (config key NasMountParents; tests use a temp folder).</summary>
    public string[] NasMountParents { get; init; } = NasValidation.DefaultMountParents;
    /// <summary>The drop-in install.sh writes (ReadWritePaths= and RequiresMountsFor=). Read only, to tell the user whether a folder is writable for the service.</summary>
    public string PathsDropInPath { get; init; } = "/etc/systemd/system/convertr-svc.service.d/paths.conf";

    public string SettingsPath => Path.Combine(DataDir, "settings.json");
    public string ProbeCachePath => Path.Combine(DataDir, "probe-cache.json");
    public string QueuePath => Path.Combine(DataDir, "queue.json");
    public string AuthPath => Path.Combine(DataDir, "auth.json");

    public static ServerOptions From(IConfiguration c)
    {
        var s = c.GetSection("Convertr");
        var over = s.GetSection("AllowedRootsOverride").GetChildren().ToList();
        var roots = (over.Count > 0 ? over : s.GetSection("AllowedRoots").GetChildren().ToList()).Select(x => x.Value ?? "").Where(x => x.Length > 0).ToArray();
        return new ServerOptions
        {
            ConfigDir = s["ConfigDir"] is { Length: > 0 } cd ? cd : DefaultConfigDir,
            DataDir = s["DataDir"] is { Length: > 0 } dd ? dd : DefaultDataDir,
            Listen = s["Listen"] is { Length: > 0 } l ? l : "127.0.0.1",
            Port = int.TryParse(s["Port"], out var p) && p is > 0 and < 65536 ? p : 8484,
            AllowedRoots = roots,
            PasswordIterations = int.TryParse(s["PasswordIterations"], out var it) && it >= 1000 ? it : 600_000,
            MinPasswordLength = int.TryParse(s["MinPasswordLength"], out var mp) && mp >= 1 ? mp : 10,
            NasMountParents = s.GetSection("NasMountParents").GetChildren().Select(x => x.Value ?? "").Where(x => x.StartsWith('/') && x.Length > 1).ToArray() is { Length: > 0 } nmp ? nmp : NasValidation.DefaultMountParents,
            PathsDropInPath = s["PathsDropInPath"] is { Length: > 0 } pd ? pd : "/etc/systemd/system/convertr-svc.service.d/paths.conf",
            SessionHours = int.TryParse(s["SessionHours"], out var sh) && sh is > 0 and <= 720 ? sh : 12,
        };
    }
}
