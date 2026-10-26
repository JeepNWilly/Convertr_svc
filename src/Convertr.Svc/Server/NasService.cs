// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Convertr.Core;

namespace Convertr.Svc.Server;

// =====================================================================================================================
// Network storage (NAS) on the Setup page, DESIGN.md section 7B.
// The service never mounts anything and never runs anything as root: it validates the input, lists exports (showmount, no shell),
// generates the exact commands for the administrator to run once with sudo, and checks the result from the inside.
// =====================================================================================================================

/// <summary>The saved form values (nas.json in the data dir). No secrets; only what the page shows.</summary>
public sealed record NasConfig(string Host, string Share, string MountPoint, string Vers, bool ReadOnly, string MediaFolder);

public sealed record NasExport(string Path, string Clients, bool? AllowsThisServer, bool Usable);
public sealed record NasStep(string Id, string Title, string Status, string Message, string[]? Commands = null);
public sealed record NasMountFacts(string Source, string FsType, string Options, string? NfsVersion, bool ReadOnly, bool Hard, long TotalBytes, long FreeBytes);
public sealed record NasCheckResult(bool Ok, string Summary, List<NasStep> Steps, NasMountFacts? Mount);
public delegate void NasAdd(string id, string title, string status, string message, string[]? commands = null);
public sealed record NasProcResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

/// <summary>Everything the NAS feature needs from the operating system, so tests can replace it.</summary>
public interface INasEnvironment
{
    string ReadMountInfo();
    string? FindExecutable(string name);
    Task<NasProcResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct);
    IReadOnlyList<string> LocalIPv4();
}

public sealed class SystemNasEnvironment : INasEnvironment
{
    private static readonly string[] Dirs = { "/usr/sbin", "/sbin", "/usr/bin", "/bin", "/usr/local/sbin", "/usr/local/bin" };

    public string ReadMountInfo() => File.Exists("/proc/self/mountinfo") ? File.ReadAllText("/proc/self/mountinfo") : "";

    public string? FindExecutable(string name)
    {
        foreach (var d in Dirs.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)))
        {
            try { var p = Path.Combine(d, name); if (File.Exists(p)) return p; } catch { /* odd PATH entry */ }
        }
        return null;
    }

    public async Task<NasProcResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        // no shell: the file name and every argument are passed as they are (the caller has validated them)
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["LC_ALL"] = "C";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + exe);
        p.StandardInput.Close();
        var so = ReadCapped(p.StandardOutput); var se = ReadCapped(p.StandardError);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* already gone */ }
            if (ct.IsCancellationRequested) throw;
            return new NasProcResult(-1, "", "", true);
        }
        return new NasProcResult(p.ExitCode, await so.ConfigureAwait(false), await se.ConfigureAwait(false), false);
    }

    private static async Task<string> ReadCapped(StreamReader r)
    {
        var sb = new StringBuilder(); var buf = new char[4096]; int n;
        while ((n = await r.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0) { if (sb.Length < 65536) sb.Append(buf, 0, n); }
        return sb.ToString();
    }

    public IReadOnlyList<string> LocalIPv4()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var s = ua.Address.ToString();
                    if (!s.StartsWith("169.254.", StringComparison.Ordinal) && !list.Contains(s)) list.Add(s);
                }
            }
        }
        catch { /* no network information */ }
        return list;
    }
}

/// <summary>Input checks. Everything that ends up in a command line or in /etc/fstab goes through here first.</summary>
public static class NasValidation
{
    private static readonly Regex HostName = new(@"^(?=.{1,253}$)[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$", RegexOptions.Compiled);
    private static readonly Regex PathRx = new(@"^/[A-Za-z0-9._@+-]+(/[A-Za-z0-9._@+-]+)*$", RegexOptions.Compiled);
    public static readonly string[] Versions = { "3", "4.1" };
    public static readonly string[] DefaultMountParents = { "/mnt", "/media", "/srv" };

    /// <summary>A host name (letters, digits, dots, hyphens) or a plain IPv4 address (a.b.c.d, no leading zeros).</summary>
    public static bool Host(string? host, out string value, out string error)
    {
        value = (host ?? "").Trim(); error = "";
        if (value.Length == 0) { error = "Enter the NAS host name or IPv4 address."; return false; }
        if (Regex.IsMatch(value, @"^[0-9.]+$"))
        {
            if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || ip.ToString() != value || value.StartsWith("0.", StringComparison.Ordinal) || value == "255.255.255.255")
            // 172.16.0.20 is a placeholder example address for the message only, not a real host.
            { error = "That is not a valid IPv4 address (use four numbers like 172.16.0.20)."; return false; }
            return true;
        }
        if (!HostName.IsMatch(value)) { error = "The host may only contain letters, digits, dots and hyphens (or be an IPv4 address)."; return false; }
        var last = value[(value.LastIndexOf('.') + 1)..];
        if (last.All(char.IsAsciiDigit) || Regex.IsMatch(last, "^0[xX][0-9a-fA-F]+$")) { error = "That looks like a number, not a host name. Use a name such as nas.local or a full IPv4 address (four numbers)."; return false; }
        return true;
    }

    /// <summary>The exported folder on the NAS, for example /volume1/library: absolute, letters digits . _ @ + - only, no ".." and no spaces.</summary>
    public static bool Share(string? share, out string value, out string error)
    {
        value = (share ?? "").Trim(); error = "";
        if (value.Length == 0) { error = "Enter the shared folder path on the NAS, for example /volume1/library."; return false; }
        if (!SafePath(value)) { error = "The shared folder path must start with / and may only use letters, digits and . _ @ + - (no spaces, no '..')."; return false; }
        return true;
    }

    /// <summary>The empty folder on this server where the share appears. Only below /mnt, /media or /srv so a typo cannot hide a system folder.</summary>
    public static bool MountPoint(string? mp, IEnumerable<string> parents, out string value, out string error)
    {
        value = (mp ?? "").Trim(); error = "";
        if (value.Length == 0) { error = "Enter the mount point on this server, for example /mnt/nas-media."; return false; }
        var v = value;
        if (!SafePath(v) || !parents.Any(p => v.StartsWith(p.TrimEnd('/') + "/", StringComparison.Ordinal)))
        { error = "The mount point must be a new folder below " + string.Join(", ", parents) + " (letters, digits and . _ @ + - only), for example /mnt/nas-media."; return false; }
        return true;
    }

    public static bool Version(string? v, out string value, out string error)
    {
        value = (v ?? "3").Trim(); error = "";
        if (!Versions.Contains(value)) { error = "NFS version must be 3 or 4.1."; return false; }
        return true;
    }

    private static bool SafePath(string p) =>
        p.Length <= 200 && PathRx.IsMatch(p) && !p.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(s => s.All(c => c == '.'));

    /// <summary>A media folder typed by the user: absolute, no NUL, no ".." (it may contain spaces; it is never put in a command).</summary>
    public static bool MediaFolder(string? f, out string value, out string error)
    {
        value = (f ?? "").Trim(); error = "";
        if (value.Length == 0) return true;
        if (value.Contains('\0') || !value.StartsWith('/') || value.Length > 400 || value.Split('/').Any(s => s == "..")) { error = "The media folder must be an absolute path without '..'."; return false; }
        value = value.Length > 1 ? value.TrimEnd('/') : value;
        return true;
    }
}

/// <summary>The exact text shown to the administrator. Pure functions: the same input always gives the same commands.</summary>
public static class NasCommands
{
    public static string Options(string vers, bool readOnly) =>
        $"vers={vers},{(readOnly ? "ro" : "rw")},hard,_netdev,nofail,x-systemd.automount,x-systemd.idle-timeout=0";

    public static string FstabLine(string host, string share, string mountPoint, string vers, bool readOnly) =>
        $"{host}:{share} {mountPoint} nfs {Options(vers, readOnly)} 0 0";

    public sealed record Cmd(string Id, string Title, string Command, string Note);

    public static List<Cmd> MountSteps(NasConfig c)
    {
        var line = FstabLine(c.Host, c.Share, c.MountPoint, c.Vers, c.ReadOnly);
        var testDir = c.MediaFolder.Length > 0 && (c.MediaFolder == c.MountPoint || c.MediaFolder.StartsWith(c.MountPoint + "/", StringComparison.Ordinal)) && !c.MediaFolder.Contains('\'') && !c.MediaFolder.Contains(' ') ? c.MediaFolder : c.MountPoint;
        var list = new List<Cmd>
        {
            new("client", "Install the NFS client (skip if it is already installed)", "sudo apt install -y nfs-common", "Provides mount.nfs and showmount."),
            new("export", "Check that the NAS offers the share to this server", $"showmount -e {c.Host}", $"The list must show {c.Share} and this server's address (or *)."),
            new("mkdir", "Create the mount point", $"sudo mkdir -p {c.MountPoint}", "An empty folder where the share will appear."),
            new("fstab", "Add the share to /etc/fstab (only if it is not there yet)",
                $"awk '$2==\"{c.MountPoint}\"{{f=1}} END{{exit !f}}' /etc/fstab && echo 'already in /etc/fstab' || echo '{line}' | sudo tee -a /etc/fstab",
                "Makes the mount come back after a reboot. Running it twice does not add a second line."),
            new("reload", "Let systemd read the new fstab line", "sudo systemctl daemon-reload", ""),
            new("mount", "Mount it", $"sudo mount {c.MountPoint} && ls {c.MountPoint}", "Should list the folders of the share."),
        };
        if (!c.ReadOnly)
            list.Add(new("write", "Test that writing works (creates and removes one empty hidden file)", $"touch {testDir}/.convertr-write-test && echo WRITE-OK && rm {testDir}/.convertr-write-test", "Prints WRITE-OK when the NAS accepts writes from this server."));
        return list;
    }

    public static string Script(IEnumerable<Cmd> steps) => string.Join("\n", steps.Select(s => s.Command));

    /// <summary>Command that adds a mount point to AllowedRoots. Different when the list is empty; the file is read only by root and the convertr group, so sudo is needed.</summary>
    public static string AddAllowedRoot(string mountPoint, bool listIsEmpty) => listIsEmpty
        ? $"sudo sed -i -E 's#(\"AllowedRoots\"[[:space:]]*:[[:space:]]*\\[)[[:space:]]*\\]#\\1\"{mountPoint}\"]#' /etc/convertr-svc/appsettings.json"
        : $"sudo sed -i -E 's#(\"AllowedRoots\"[[:space:]]*:[[:space:]]*\\[)#\\1\"{mountPoint}\", #' /etc/convertr-svc/appsettings.json";

    public const string ShowAllowedRoots = "sudo grep -A3 AllowedRoots /etc/convertr-svc/appsettings.json";

    /// <summary>The installer call that gives the service write access (ReadWritePaths) and makes it wait for the mount. It rewrites paths.conf, so every media root is repeated.</summary>
    public static string Install(string version, IEnumerable<string> roots) =>
        $"cd ~/convertr-svc-{version}-linux-x64 && sudo ./scripts/install.sh " + string.Join(" ", roots.Select(r => "--media-root " + r));

    /// <summary>The service stays on the existing paths when only AllowedRoots changed: a restart reads the file again.</summary>
    public const string Restart = "sudo systemctl restart convertr-svc";
}

/// <summary>One line of /proc/self/mountinfo.</summary>
public sealed record MountEntry(string MountPoint, string FsType, string Source, string MountOptions, string SuperOptions)
{
    public bool ReadOnly => Opts(MountOptions).Contains("ro");
    public bool Hard => Opts(SuperOptions).Contains("hard") && !Opts(SuperOptions).Contains("soft");
    public bool Soft => Opts(SuperOptions).Contains("soft");
    public string? NfsVersion
    {
        get
        {
            foreach (var o in SuperOptions.Split(',')) if (o.StartsWith("vers=", StringComparison.Ordinal)) return o[5..];
            foreach (var o in SuperOptions.Split(',')) if (o.StartsWith("nfsvers=", StringComparison.Ordinal)) return o[8..];
            return null;
        }
    }
    public bool IsNfs => FsType is "nfs" or "nfs4";
    private static HashSet<string> Opts(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
}

public sealed class MountTable
{
    private readonly List<MountEntry> _entries;
    private MountTable(List<MountEntry> e) { _entries = e; }

    public static MountTable Parse(string text)
    {
        var list = new List<MountEntry>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var sep = line.IndexOf(" - ", StringComparison.Ordinal);
            if (sep < 0) continue;
            var left = line[..sep].Split(' '); var right = line[(sep + 3)..].Split(' ');
            if (left.Length < 6 || right.Length < 2) continue;
            list.Add(new MountEntry(Unescape(left[4]), right[0], Unescape(right[1]), left[5], right.Length > 2 ? right[2] : ""));
        }
        return new MountTable(list);
    }

    private static string Unescape(string s) => Regex.Replace(s, @"\\([0-7]{3})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());

    /// <summary>The newest mount exactly at this path (a later mount hides an earlier one, which is how an automount shows up first and the NFS mount on top of it).</summary>
    public MountEntry? At(string path) => _entries.LastOrDefault(e => e.MountPoint == path.TrimEnd('/'));

    /// <summary>The mount that holds this path: the longest mount point that contains it (newest wins).</summary>
    public MountEntry? Holding(string path)
    {
        var p = path.TrimEnd('/');
        MountEntry? best = null;
        foreach (var e in _entries)
            if (e.MountPoint == "/" || p == e.MountPoint || p.StartsWith(e.MountPoint + "/", StringComparison.Ordinal))
                if (best == null || e.MountPoint.Length >= best.MountPoint.Length) best = e;
        return best;
    }
}

public sealed class NasService
{
    private readonly ServerOptions _o;
    private readonly RootGuard _guard;
    private readonly SettingsService _settings;
    private readonly INasEnvironment _env;
    private readonly ILogger<NasService> _log;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(45);
    public TimeSpan CheckTimeoutOverride { get; set; } = CheckTimeout;

    public NasService(ServerOptions o, RootGuard g, SettingsService s, INasEnvironment env, ILogger<NasService> log)
    { _o = o; _guard = g; _settings = s; _env = env; _log = log; }

    private string ConfigPath => Path.Combine(_o.DataDir, "nas.json");

    public NasConfig LoadConfig()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(ConfigPath) && JsonSerializer.Deserialize<NasConfig>(File.ReadAllText(ConfigPath), new JsonSerializerOptions(JsonSerializerDefaults.Web)) is { } c)
                    return c;
            }
            catch { /* a damaged file is the same as none */ }
            return new NasConfig("", "", "/mnt/nas-media", "3", false, "");
        }
    }

    private void SaveConfig(NasConfig c)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_o.DataDir);
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(c, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            File.Move(tmp, ConfigPath, true);
        }
    }

    // ---------- info ----------
    public object Info()
    {
        var mountNfs = _env.FindExecutable("mount.nfs");
        var showmount = _env.FindExecutable("showmount");
        var cfg = LoadConfig();
        var media = _settings.Load().MediaFolder ?? "";
        return new
        {
            config = cfg,
            clientInstalled = mountNfs != null,
            showmountAvailable = showmount != null,
            installHint = mountNfs == null || showmount == null ? "The NFS client tools are not installed on this server. Run once: sudo apt install -y nfs-common" : null,
            serverAddresses = _env.LocalIPv4(),
            allowedRoots = _guard.Roots,
            configuredRoots = _o.AllowedRoots,
            currentMediaFolder = media,
            version = Program.Version,
            mountParents = _o.NasMountParents,
        };
    }

    // ---------- exports (showmount) ----------
    public async Task<(object Body, int Status)> ExportsAsync(string? host, CancellationToken ct)
    {
        if (!NasValidation.Host(host, out var h, out var err)) return (new { error = err }, 400);
        var exe = _env.FindExecutable("showmount");
        if (exe == null)
            return (new { available = false, ok = false, error = "The NFS client tools are not installed on this server, so the NAS cannot be asked for its shares.", hint = "sudo apt install -y nfs-common", exports = Array.Empty<NasExport>() }, 200);
        NasProcResult r;
        try { r = await _env.RunAsync(exe, new[] { "-e", h }, TimeSpan.FromSeconds(12), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (new { available = true, ok = false, error = "showmount could not be started: " + ex.Message, exports = Array.Empty<NasExport>() }, 200); }
        if (r.TimedOut) return (new { available = true, ok = false, error = $"{h} did not answer within 12 seconds. Is the NAS on, on the same network, and is NFS enabled (DSM: Control Panel, File Services, NFS)?", exports = Array.Empty<NasExport>() }, 200);
        if (r.ExitCode != 0) return (new { available = true, ok = false, error = ExplainShowmount(r.Stderr, h), exports = Array.Empty<NasExport>() }, 200);
        var list = ParseExports(r.Stdout, _env.LocalIPv4());
        return (new { available = true, ok = true, host = h, exports = list, note = list.Count == 0 ? "The NAS answered but offers no NFS shares. In DSM, open the shared folder, Edit, NFS Permissions and add a rule for this server." : null }, 200);
    }

    public static string ExplainShowmount(string stderr, string host)
    {
        var first = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        var low = first.ToLowerInvariant();
        string plain =
            low.Contains("name or service not known") || low.Contains("unknown host") ? $"The name {host} cannot be found. Check the spelling or use the IPv4 address."
            : low.Contains("no route to host") || low.Contains("network is unreachable") ? $"There is no network route to {host}."
            : low.Contains("program not registered") || low.Contains("connection refused") || low.Contains("portmap") || low.Contains("rpc") ? $"{host} does not answer NFS. In DSM turn NFS on (Control Panel, File Services, NFS tab, Enable NFS service)."
            : low.Contains("timed out") ? $"{host} did not answer in time. Is the NAS on and on the same network?"
            : "showmount failed.";
        return first.Length > 0 ? plain + " (showmount said: " + (first.Length > 200 ? first[..200] + "..." : first) + ")" : plain;
    }

    /// <summary>Parses "showmount -e" output (header line, then "path   client,client").</summary>
    public static List<NasExport> ParseExports(string text, IReadOnlyList<string> localAddresses)
    {
        var list = new List<NasExport>();
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith("Export list for", StringComparison.OrdinalIgnoreCase) || !raw.StartsWith('/')) continue;
            var parts = raw.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var path = parts[0]; var clients = parts.Length > 1 ? parts[1] : "";
            list.Add(new NasExport(path, clients, AllowsAny(clients, localAddresses), NasValidation.Share(path, out _, out _)));
        }
        return list;
    }

    /// <summary>True when the client list names this server (or everyone), false when it clearly does not, null when it cannot be told (host names, wildcards).</summary>
    public static bool? AllowsAny(string clients, IReadOnlyList<string> localAddresses)
    {
        var unknown = false;
        foreach (var tok in clients.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (tok is "*" or "(everyone)") return true;
            var slash = tok.IndexOf('/');
            var addr = slash >= 0 ? tok[..slash] : tok;
            if (!IPAddress.TryParse(addr, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) { unknown = true; continue; }
            if (slash < 0) { if (localAddresses.Contains(tok)) return true; continue; }
            if (!int.TryParse(tok[(slash + 1)..], out var bits) || bits is < 0 or > 32) { unknown = true; continue; }
            var mask = bits == 0 ? 0u : uint.MaxValue << (32 - bits);
            foreach (var l in localAddresses)
                if (IPAddress.TryParse(l, out var lip) && (ToUInt(lip) & mask) == (ToUInt(ip) & mask)) return true;
        }
        return unknown ? null : false;
    }

    private static uint ToUInt(IPAddress ip) { var b = ip.GetAddressBytes(); return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3]; }

    // ---------- commands ----------
    public (object Body, int Status) Commands(NasConfig input)
    {
        if (!NasValidation.Host(input.Host, out var host, out var e1)) return (new { error = e1 }, 400);
        if (!NasValidation.Share(input.Share, out var share, out var e2)) return (new { error = e2 }, 400);
        if (!NasValidation.MountPoint(input.MountPoint, _o.NasMountParents, out var mp, out var e3)) return (new { error = e3 }, 400);
        if (!NasValidation.Version(input.Vers, out var vers, out var e4)) return (new { error = e4 }, 400);
        if (!NasValidation.MediaFolder(input.MediaFolder, out var media, out var e5)) return (new { error = e5 }, 400);
        var cfg = new NasConfig(host, share, mp, vers, input.ReadOnly, media);
        try { SaveConfig(cfg); } catch (Exception ex) { _log.LogWarning("Could not save nas.json: {Message}", ex.Message); }
        var steps = NasCommands.MountSteps(cfg);
        var line = NasCommands.FstabLine(host, share, mp, vers, cfg.ReadOnly);
        var folder = media.Length > 0 ? media : mp;
        var roots = RootsAdvice(mp, folder);
        return (new
        {
            config = cfg,
            fstabLine = line,
            options = NasCommands.Options(vers, cfg.ReadOnly),
            steps = steps.Select(s => new { s.Id, s.Title, s.Command, s.Note }).ToList(),
            script = NasCommands.Script(steps),
            allowedRoots = roots,
        }, 200);
    }

    /// <summary>Whether the folder is inside AllowedRoots, and the exact commands to change that. The web page can never edit AllowedRoots itself (DESIGN.md 6.4).</summary>
    public object RootsAdvice(string mountPoint, string folder)
    {
        var state = RootState(folder);
        var mpState = RootState(mountPoint);
        var empty = _o.AllowedRoots.Length == 0;
        var roots = _o.AllowedRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.TrimEnd('/')).Distinct().ToList();
        if (!roots.Contains(mountPoint)) roots.Add(mountPoint);
        var needAdd = state == "outside" && mpState == "outside";
        var commands = new List<string>();
        string message;
        switch (state)
        {
            case "inside": message = "The folder is inside the allowed folders. Nothing to change."; break;
            case "configured-not-loaded":
                message = "The folder is listed in AllowedRoots but was not there when the service started (the share was not mounted yet), so it was ignored. Restart the service now that the share is mounted.";
                commands.Add(NasCommands.Restart); break;
            default:
                message = "The folder is outside the allowed folders, so the web page refuses it. The web page cannot change that setting (a safety rule); run these once on the server. The installer also gives the service write access to the share and makes it wait for the mount at boot, and it restarts the service.";
                if (needAdd) commands.Add(NasCommands.AddAllowedRoot(mountPoint, empty));
                if (needAdd) commands.Add(NasCommands.ShowAllowedRoots);
                commands.Add(NasCommands.Install(Program.Version, roots));
                break;
        }
        return new { state, folder, mountPointState = mpState, message, commands, rewritesPathsConf = state == "outside" };
    }

    public string RootState(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch { return "outside"; }
        var real = RootGuard.RealPath(full) ?? full;
        if (_guard.IsInside(real)) return "inside";
        foreach (var r in _o.AllowedRoots)
        {
            var t = r.TrimEnd('/');
            if (t.Length > 0 && (PathCompare.Equal(full, t) || PathCompare.StartsWith(full, t + "/"))) return "configured-not-loaded";
        }
        return "outside";
    }

    // ---------- check ----------
    public async Task<(object Body, int Status)> CheckAsync(string? mountPoint, string? mediaFolder, CancellationToken ct)
    {
        if (!NasValidation.MountPoint(mountPoint, _o.NasMountParents, out var mp, out var e1)) return (new { error = e1 }, 400);
        if (!NasValidation.MediaFolder(mediaFolder, out var media, out var e2)) return (new { error = e2 }, 400);
        if (!await _checkGate.WaitAsync(0, ct).ConfigureAwait(false))
            return (new { error = "The previous check is still waiting for the NAS (a hard mount waits until the NAS answers). Wait for it or check that the NAS is on." }, 409);
        var original = _settings.Load().OriginalsFolderName;
        if (string.IsNullOrWhiteSpace(original)) original = "_Convertr_Originals";
        var work = Task.Run(() => RunChecks(mp, media, original), CancellationToken.None);
        // the gate is held until the file work really ends, even when the page stopped waiting
        _ = work.ContinueWith(_ => _checkGate.Release(), TaskScheduler.Default);
        try
        {
            var result = await work.WaitAsync(CheckTimeoutOverride, ct).ConfigureAwait(false);
            return (result, 200);
        }
        catch (TimeoutException)
        {
            return (new NasCheckResult(false, "The share did not answer within " + (int)CheckTimeoutOverride.TotalSeconds + " seconds. Is the NAS on and reachable? (The mount waits for it: 'hard'.)",
                new List<NasStep> { new("timeout", "Answer from the NAS", "fail", "No answer in time. Nothing was left behind; try again when the NAS is reachable.") }, null), 200);
        }
    }

    private NasCheckResult RunChecks(string mp, string media, string originalsName)
    {
        var steps = new List<NasStep>();
        NasMountFacts? facts = null;
        NasCheckResult Done()
        {
            var fails = steps.Count(s => s.Status == "fail"); var warns = steps.Count(s => s.Status == "warn");
            var summary = fails > 0 ? $"{fails} problem{(fails == 1 ? "" : "s")} found. Fix the first red item and check again."
                : warns > 0 ? "The share works, with " + warns + " warning" + (warns == 1 ? "" : "s") + " to read."
                : "All checks passed. The share is ready for conversion.";
            return new NasCheckResult(fails == 0, summary, steps, facts);
        }
        void Add(string id, string title, string status, string msg, string[]? cmds = null) => steps.Add(new NasStep(id, title, status, msg, cmds));

        // 1. the folder (listing it also wakes an automount)
        try
        {
            if (!Directory.Exists(mp)) { Add("folder", "Mount point folder", "fail", $"{mp} does not exist on this server. Create it and mount the share (the commands above).", new[] { $"sudo mkdir -p {mp}" }); return Done(); }
            using var en = Directory.EnumerateFileSystemEntries(mp).GetEnumerator(); en.MoveNext();
            Add("folder", "Mount point folder", "ok", $"{mp} exists and the service can open it.");
        }
        catch (UnauthorizedAccessException) { Add("folder", "Mount point folder", "fail", $"The service is not allowed to open {mp}. On the NAS, the NFS rule for this server must use Squash 'Map all users to admin' (or the folder must be readable by user convertr)."); return Done(); }
        catch (IOException ex) { Add("folder", "Mount point folder", "fail", $"{mp} cannot be read: {ex.Message}"); return Done(); }

        // 2. really a mount point (never write into a plain folder: it would fill the server's own disk)
        var full = Path.GetFullPath(mp);
        var real = RootGuard.RealPath(full) ?? full;
        var table = MountTable.Parse(_env.ReadMountInfo());
        var entry = table.At(real);
        if (entry == null)
        {
            Add("mounted", "Is a mounted share", "fail", $"{mp} is only a normal folder on this server's own disk, nothing is mounted there. Nothing was written. Mount the share first (the commands above).", new[] { $"sudo mount {mp}" });
            return Done();
        }
        if (entry.FsType == "autofs")
        {
            Add("mounted", "Is a mounted share", "fail", $"{mp} is set up to mount on first use, but the mount did not happen when it was opened. The NAS may be unreachable or may not allow this server. Try: sudo mount {mp} and read its message.", new[] { $"sudo mount {mp}" });
            return Done();
        }
        Add("mounted", "Is a mounted share", "ok", $"{mp} is a mount point (source {entry.Source}).");

        // 3. NFS
        if (entry.IsNfs)
        {
            var hardSoft = entry.Soft ? "soft" : entry.Hard ? "hard" : "unknown";
            Add("nfs", "Type of share", entry.Soft ? "warn" : "ok",
                $"NFS{(entry.NfsVersion != null ? " version " + entry.NfsVersion : "")}, {(entry.ReadOnly ? "read-only" : "read/write")}, {hardSoft} mount." +
                (entry.Soft ? " A 'soft' mount can cut a file short when the NAS hiccups; use 'hard' (the fstab line above)." : ""));
        }
        else Add("nfs", "Type of share", "warn", $"This is {entry.FsType}, not NFS. It can work, but this page and the design are tested with NFS (renames and timestamps may behave differently).");

        // 4. size
        try
        {
            var di = new DriveInfo(real);
            facts = new NasMountFacts(entry.Source, entry.FsType, entry.SuperOptions, entry.NfsVersion, entry.ReadOnly, entry.Hard, di.TotalSize, di.AvailableFreeSpace);
            var free = di.AvailableFreeSpace;
            Add("space", "Size and free space", free < 20L * 1024 * 1024 * 1024 ? "warn" : "ok", $"{Gb(di.TotalSize)} in total, {Gb(free)} free." + (free < 20L * 1024 * 1024 * 1024 ? " Little space is left." : ""));
        }
        catch (Exception ex) { Add("space", "Size and free space", "warn", "Could not read the size: " + ex.Message); }

        // where the tests run: the media folder, or the share's top folder when none is given
        string testDir = real;
        if (media.Length > 0)
        {
            var mf = RootGuard.RealPath(Path.GetFullPath(media));
            if (mf == null || !Directory.Exists(mf)) { Add("media", "Media folder", "fail", $"{media} does not exist. Check the name (it is case sensitive)."); testDir = ""; }
            else if (!(mf == real || mf.StartsWith(real.TrimEnd('/') + "/", StringComparison.Ordinal)) || table.Holding(mf)?.MountPoint != entry.MountPoint)
            { Add("media", "Media folder", "fail", $"{media} is not on this share ({mp}). Choose a folder inside the mount."); testDir = ""; }
            else { testDir = mf; Add("media", "Media folder", "ok", $"{mf} is on this share."); }
        }
        else Add("media", "Media folder", "skip", "No media folder given; the write tests use the top folder of the share. Enter your movies folder to also test the originals folder.");

        if (testDir.Length > 0)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            var f1 = Path.Combine(testDir, $".convertr-check-{id}.tmp");
            var f2 = Path.Combine(testDir, $".convertr-check-{id}.renamed");
            var created = new List<string>();
            try
            {
                // 5. write and read back
                if (entry.ReadOnly) Add("write", "Writing", "fail", "The share is mounted read-only (ro). Conversion needs read/write. Change 'ro' to 'rw' in /etc/fstab and mount again.");
                else
                {
                    try
                    {
                        var data = new byte[4096]; Random.Shared.NextBytes(data);
                        File.WriteAllBytes(f1, data); created.Add(f1);
                        var back = File.ReadAllBytes(f1);
                        if (!back.AsSpan().SequenceEqual(data)) Add("write", "Writing", "fail", "A test file was written but read back different. The NAS or the network is not reliable.");
                        else Add("write", "Writing", "ok", $"Created a hidden test file in {testDir}, read it back and it matched.");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Add("write", "Writing", "fail", ExplainWrite(ex, entry)); }
                }
                // 6. rename within the share
                if (steps.Last().Status == "ok")
                {
                    try { File.Move(f1, f2); created.Remove(f1); created.Add(f2); Add("rename", "Renaming", "ok", "Renaming a file inside the share works (this is how a converted file replaces the original)."); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Add("rename", "Renaming", "fail", "Renaming inside the share failed: " + ex.Message); }
                }
                else Add("rename", "Renaming", "skip", "Skipped because writing did not work.");

                // 7. the originals folder next to the media folder, on the same share
                if (media.Length == 0) Add("originals", $"{originalsName} folder", "skip", "Skipped: no media folder given.");
                else if (!steps.Any(s => s.Id == "write" && s.Status == "ok") || !created.Contains(f2)) Add("originals", $"{originalsName} folder", "skip", "Skipped because writing or renaming did not work.");
                else CheckOriginals(testDir, originalsName, entry, table, f2, created, Add);
            }
            finally
            {
                foreach (var f in created) { try { File.Delete(f); } catch { /* left behind: reported below */ } }
                foreach (var f in new[] { f1, f2 }) if (File.Exists(f)) Add("cleanup", "Cleaning up", "warn", $"Could not remove the test file {f}. Delete it by hand.");
            }
        }

        // 8. AllowedRoots
        AddRootSteps(mp, media, Add);
        // 9. systemd write access
        AddUnitStep(mp, media, Add);
        return Done();
    }

    private void CheckOriginals(string mediaReal, string originalsName, MountEntry entry, MountTable table, string movedFrom, List<string> created, NasAdd add)
    {
        var title = $"{originalsName} folder";
        var holding = HoldingArea.Root(mediaReal, originalsName);
        var p = holding;
        while (!string.IsNullOrEmpty(p) && !Directory.Exists(p)) p = Path.GetDirectoryName(p) ?? "";
        var mp = entry.MountPoint;
        if (string.IsNullOrEmpty(p) || !(p == mp || p.StartsWith(mp.TrimEnd('/') + "/", StringComparison.Ordinal)) || table.Holding(p)?.MountPoint != mp)
        {
            add("originals", title, "fail", $"The originals would go to {holding}, which is not on this share. The converted file could not be swapped in with a fast rename. Choose a media folder that is a sub folder of the share, such as {mp}/Movies, not the top folder.", null);
            return;
        }
        var dir = Path.Combine(p, $".convertr-check-{Guid.NewGuid().ToString("N")[..8]}");
        var target = Path.Combine(dir, "moved.tmp");
        try
        {
            Directory.CreateDirectory(dir);
            File.Move(movedFrom, target); created.Remove(movedFrom); created.Add(target);
            add("originals", title, "ok", $"Can create folders next to the media folder ({p}) and move a file there with a fast rename. The originals will go to {holding}.", null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { add("originals", title, "fail", $"Could not create or move into {p}: {ex.Message}. The service needs write access in the folder that holds your media folder, not only inside it.", null); }
        finally
        {
            try { if (File.Exists(target)) File.Delete(target); created.Remove(target); } catch { /* reported by the caller */ }
            try { if (Directory.Exists(dir)) Directory.Delete(dir); } catch { /* the test folder stays: harmless, hidden */ }
        }
    }

    private static string ExplainWrite(Exception ex, MountEntry entry)
    {
        if (ex.Message.Contains("Read-only file system", StringComparison.OrdinalIgnoreCase))
            return entry.ReadOnly
                ? "The share is mounted read-only."
                : "The share is mounted read/write, but the service is not allowed to write there: its systemd protection only allows the folders the installer lists. Run the installer once with --media-root for this folder (see the last items below).";
        return "The NAS refused the write (" + ex.Message + "). In DSM, open the shared folder, Edit, NFS Permissions, and for this server's address set Privilege 'Read/Write' and Squash 'Map all users to admin'.";
    }

    private void AddRootSteps(string mp, string media, NasAdd add)
    {
        var folder = media.Length > 0 ? media : mp;
        var adv = RootsAdvice(mp, folder);
        var json = JsonSerializer.SerializeToElement(adv, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var state = json.GetProperty("state").GetString()!;
        var cmds = json.GetProperty("commands").EnumerateArray().Select(x => x.GetString()!).ToArray();
        add("roots", "Allowed folders (AllowedRoots)", state == "inside" ? "ok" : state == "configured-not-loaded" ? "warn" : "fail", json.GetProperty("message").GetString()!, cmds.Length > 0 ? cmds : null);
    }

    private void AddUnitStep(string mp, string media, NasAdd add)
    {
        const string title = "Service write access (systemd)";
        try
        {
            if (!File.Exists(_o.PathsDropInPath)) { add("unit", title, "skip", $"{_o.PathsDropInPath} was not found (the service is not running from the installer's unit). Nothing to check."); return; }
            var paths = new List<string>();
            foreach (var line in File.ReadAllLines(_o.PathsDropInPath))
                if (line.StartsWith("ReadWritePaths=", StringComparison.Ordinal))
                    paths.AddRange(line["ReadWritePaths=".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => x.TrimStart('-', '+', '!').TrimEnd('/')));
            var folder = (media.Length > 0 ? media : mp).TrimEnd('/');
            var covered = paths.Any(p => folder == p || folder.StartsWith(p + "/", StringComparison.Ordinal));
            if (covered) add("unit", title, "ok", "The installer's drop-in lists this folder as writable for the service.");
            else
            {
                var roots = _o.AllowedRoots.Select(r => r.TrimEnd('/')).Distinct().ToList(); if (!roots.Contains(mp)) roots.Add(mp);
                add("unit", title, "warn", $"The service's writable list ({_o.PathsDropInPath}) does not include this folder yet. Writing may fail with 'Read-only file system' after a restart of the machine or service. Run the installer once; it keeps every media root you list.", new[] { NasCommands.Install(Program.Version, roots) });
            }
        }
        catch (Exception ex) { add("unit", title, "skip", "Could not read the drop-in: " + ex.Message); }
    }

    private static string Gb(long b) => b >= 1L << 40 ? (b / (double)(1L << 40)).ToString("0.0") + " TB" : (b / (double)(1L << 30)).ToString("0.0") + " GB";
}
