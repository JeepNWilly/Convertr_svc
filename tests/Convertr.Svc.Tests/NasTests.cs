// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Convertr.Svc.Server;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Convertr.Svc.Tests;

/// <summary>A pretend operating system for the NAS feature: a mount table, tools that may or may not be installed, a canned showmount.</summary>
public sealed class FakeNas : INasEnvironment
{
    public string MountInfo = "";
    public HashSet<string> Tools = new() { "mount.nfs", "showmount" };
    public Func<IReadOnlyList<string>, NasProcResult> Show = _ => new NasProcResult(0, "", "", false);
    public List<(string Exe, string[] Args)> Calls = new();
    public string[] Addresses = { "172.16.0.12" };
    public ManualResetEventSlim? BlockMountInfo;

    public string ReadMountInfo() { BlockMountInfo?.Wait(); return MountInfo; }
    public string? FindExecutable(string name) => Tools.Contains(name) ? "/usr/sbin/" + name : null;
    public Task<NasProcResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) { Calls.Add((exe, args.ToArray())); return Task.FromResult(Show(args)); }
    public IReadOnlyList<string> LocalIPv4() => Addresses;

    public static string Line(string mountPoint, string type, string source, string mopts = "rw,relatime", string sopts = "rw,vers=3,hard,proto=tcp") =>
        $"100 30 0:50 / {mountPoint} {mopts} shared:1 - {type} {source} {sopts}\n";
}

public class NasValidationTests
{
    [Theory]
    [InlineData("172.16.0.35")] [InlineData("nas")] [InlineData("nas.local")] [InlineData("my-nas.example.com")] [InlineData("NAS01")]
    public void Host_AcceptsNamesAndPlainIPv4(string h) => Assert.True(NasValidation.Host(h, out var v, out _) && v == h);

    [Theory]
    [InlineData("")] [InlineData("   ")] [InlineData("a;rm -rf /")] [InlineData("$(id)")] [InlineData("`id`")] [InlineData("nas && ls")] [InlineData("-oProxyCommand=x")]
    [InlineData("nas name")] [InlineData("nas\nname")] [InlineData("172.16.0.256")] [InlineData("172.16.000.35")] [InlineData("172.16.0")] [InlineData("0.0.0.0")]
    [InlineData("::1")] [InlineData("[fe80::1]")] [InlineData("nas|cat")] [InlineData("nas'x")] [InlineData("nas\"x")] [InlineData("-nas")] [InlineData("nas-")] [InlineData(".nas")]
    [InlineData("nas..local")] [InlineData("näs")] [InlineData("1.2.3.4.5")] [InlineData("0x7f.1")]
    public void Host_RefusesAnythingElse(string h) => Assert.False(NasValidation.Host(h, out _, out var err) || err.Length == 0, h);

    [Theory]
    [InlineData("/volume1/library")] [InlineData("/Media")] [InlineData("/volume1/media.v2/a-b_c@d+e")]
    public void Share_Accepts(string s) => Assert.True(NasValidation.Share(s, out _, out _));

    [Theory]
    [InlineData("")] [InlineData("volume1/library")] [InlineData("/volume1/../etc")] [InlineData("/volume 1/library")] [InlineData("/a;b")] [InlineData("/a$(x)")] [InlineData("/a'b")]
    [InlineData("/a//b")] [InlineData("/a/")] [InlineData("/.")] [InlineData("/a/./b")] [InlineData("/a\nb")] [InlineData("/a b")] [InlineData("/a\\b")] [InlineData("/ä")]
    public void Share_Refuses(string s) => Assert.False(NasValidation.Share(s, out _, out _), s);

    [Theory]
    [InlineData("/mnt/nas-media")] [InlineData("/media/nas")] [InlineData("/srv/nas/media")] [InlineData("/mnt/nas.media_1")]
    public void MountPoint_AcceptsNewFoldersBelowMntMediaSrv(string m) => Assert.True(NasValidation.MountPoint(m, NasValidation.DefaultMountParents, out _, out _));

    [Theory]
    [InlineData("")] [InlineData("/")] [InlineData("/mnt")] [InlineData("/mnt/")] [InlineData("/etc")] [InlineData("/etc/convertr-svc")] [InlineData("/usr/bin")] [InlineData("/var/lib/convertr-svc")]
    [InlineData("/home/x")] [InlineData("/mnt/../etc")] [InlineData("/mnt/a b")] [InlineData("/mnt/a'b")] [InlineData("/mnt/a;b")] [InlineData("mnt/a")] [InlineData("/mntx/a")] [InlineData("/mnt/$(id)")]
    public void MountPoint_Refuses(string m) => Assert.False(NasValidation.MountPoint(m, NasValidation.DefaultMountParents, out _, out _), m);

    [Fact]
    public void Version_OnlyThreeOrFourOne()
    {
        Assert.True(NasValidation.Version("3", out _, out _)); Assert.True(NasValidation.Version("4.1", out _, out _));
        Assert.False(NasValidation.Version("4.1,rw", out _, out _)); Assert.False(NasValidation.Version("2", out _, out _));
    }

    [Theory]
    [InlineData("/mnt/nas/Movies", true)] [InlineData("", true)] [InlineData("/mnt/nas/../etc", false)] [InlineData("relative", false)] [InlineData("/mnt/a\0b", false)]
    public void MediaFolder(string f, bool ok) => Assert.Equal(ok, NasValidation.MediaFolder(f, out _, out _));
}

public class NasCommandTests
{
    private static readonly NasConfig Sample = new("172.16.0.35", "/volume1/library", "/mnt/nas-media", "3", false, "/mnt/nas-media/Movies");

    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "VERSION"))) d = d.Parent;
        return d!.FullName;
    }

    private static (int Code, string Out) Bash(string script)
    {
        var psi = new ProcessStartInfo("bash", new[] { "-c", script }) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o);
    }

    [Fact]
    public void FstabLine_IsExactlyTheOneThatWorkedOnTheServer()
    {
        Assert.Equal("172.16.0.35:/volume1/library /mnt/nas-media nfs vers=3,rw,hard,_netdev,nofail,x-systemd.automount,x-systemd.idle-timeout=0 0 0",
            NasCommands.FstabLine("172.16.0.35", "/volume1/library", "/mnt/nas-media", "3", false));
        Assert.Contains("vers=4.1,ro,hard", NasCommands.FstabLine("nas", "/volume1/library", "/mnt/n", "4.1", true));
    }

    [Fact]
    public void Steps_AreTheOneTimeSudoSequence_InOrder()
    {
        var ids = NasCommands.MountSteps(Sample).Select(s => s.Id).ToArray();
        Assert.Equal(new[] { "client", "export", "mkdir", "fstab", "reload", "mount", "write" }, ids);
        var cmds = NasCommands.MountSteps(Sample).ToDictionary(s => s.Id, s => s.Command);
        Assert.Equal("sudo apt install -y nfs-common", cmds["client"]);
        Assert.Equal("showmount -e 172.16.0.35", cmds["export"]);
        Assert.Equal("sudo mkdir -p /mnt/nas-media", cmds["mkdir"]);
        Assert.Contains("sudo tee -a /etc/fstab", cmds["fstab"]);
        Assert.Equal("sudo systemctl daemon-reload", cmds["reload"]);
        Assert.StartsWith("sudo mount /mnt/nas-media", cmds["mount"]);
        Assert.Equal("touch /mnt/nas-media/Movies/.convertr-write-test && echo WRITE-OK && rm /mnt/nas-media/Movies/.convertr-write-test", cmds["write"]);
    }

    [Fact]
    public void ReadOnlyMount_HasNoWriteTest() =>
        Assert.DoesNotContain(NasCommands.MountSteps(Sample with { ReadOnly = true }), s => s.Id == "write");

    [Fact]
    public void WriteTest_FallsBackToTheMountPoint_WhenTheMediaFolderIsElsewhere() =>
        Assert.Contains("/mnt/nas-media/.convertr-write-test", NasCommands.MountSteps(Sample with { MediaFolder = "/somewhere/else" }).Single(s => s.Id == "write").Command);

    [Fact]
    public void FstabStep_IsIdempotent_AndTheScriptIsValidBash()
    {
        Skip.If(OperatingSystem.IsWindows());
        var dir = Path.Combine(Path.GetTempPath(), "convertr-nas-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(dir);
        try
        {
            var fstab = Path.Combine(dir, "fstab"); File.WriteAllText(fstab, "UUID=x / ext4 defaults 0 1\n");
            var cmd = NasCommands.MountSteps(Sample).Single(s => s.Id == "fstab").Command.Replace("sudo ", "").Replace("/etc/fstab", fstab);
            Assert.Equal(0, Bash(cmd).Code);
            var second = Bash(cmd);
            Assert.Contains("already in", second.Out);
            var lines = File.ReadAllLines(fstab);
            Assert.Equal(2, lines.Length);
            Assert.Equal(NasCommands.FstabLine("172.16.0.35", "/volume1/library", "/mnt/nas-media", "3", false), lines[1]);
            File.WriteAllText(Path.Combine(dir, "all.sh"), NasCommands.Script(NasCommands.MountSteps(Sample)) + "\n");
            Assert.Equal(0, Bash($"bash -n '{dir}/all.sh'").Code);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("{\n  \"Convertr\": {\n    \"AllowedRoots\": [\"/srv/testmedia\" ],\n    \"DataDir\": \"/var/lib/convertr-svc\"\n  }\n}", false)]
    [InlineData("{\n  \"Convertr\": {\n    \"AllowedRoots\": [ ],\n    \"DataDir\": \"/var/lib/convertr-svc\"\n  }\n}", true)]
    [InlineData("{ \"Convertr\": { \"AllowedRoots\": [], \"Port\": 8484 } }", true)]
    [InlineData("{ \"Convertr\": { \"AllowedRoots\": [\"/a\", \"/b\"] } }", false)]
    public void AddAllowedRootCommand_ProducesValidJsonWithTheRoot_ForEmptyAndFilledLists(string json, bool empty)
    {
        Skip.If(OperatingSystem.IsWindows());
        var dir = Path.Combine(Path.GetTempPath(), "convertr-nas-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "appsettings.json"); File.WriteAllText(f, json);
            var cmd = NasCommands.AddAllowedRoot("/mnt/nas-media", empty).Replace("sudo ", "").Replace("/etc/convertr-svc/appsettings.json", f);
            Assert.Equal(0, Bash(cmd).Code);
            using var doc = JsonDocument.Parse(File.ReadAllText(f), new JsonDocumentOptions { AllowTrailingCommas = true });
            var roots = doc.RootElement.GetProperty("Convertr").GetProperty("AllowedRoots").EnumerateArray().Select(x => x.GetString()).ToList();
            Assert.Equal("/mnt/nas-media", roots[0]);
            Assert.Equal(JsonDocument.Parse(json).RootElement.GetProperty("Convertr").GetProperty("AllowedRoots").GetArrayLength() + 1, roots.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void InstallCommand_RepeatsEveryRoot_AndNeverPassesScratch() =>
        Assert.Equal("cd ~/convertr-svc-0.3.4-linux-x64 && sudo ./scripts/install.sh --media-root /a --media-root /mnt/nas-media", NasCommands.Install("0.3.4", new[] { "/a", "/mnt/nas-media" }));

    [Fact]
    public void ShowOnlyCommands_NeverRunAnythingFromTheService()
    {
        // the service code that serves the NAS page must not start a shell, sudo, mount or any writer of /etc
        var src = File.ReadAllText(Path.Combine(Repo(), "src/Convertr.Svc/Server/NasService.cs"));
        Assert.DoesNotContain("\"sudo\"", src.Replace("sudo ", ""));
        Assert.DoesNotContain("/bin/sh", src); Assert.DoesNotContain("/bin/bash", src); Assert.DoesNotContain("\"bash\"", src);
        Assert.DoesNotContain("UseShellExecute = true", src);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(src, @"new ProcessStartInfo\(").Count);   // the one place a program is started
        var started = System.Text.RegularExpressions.Regex.Matches(src, "FindExecutable\\(\"([^\"]+)\"\\)").Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "mount.nfs", "showmount" }, started);       // only the lookups of the NFS client; showmount is the only one that is run
        Assert.Contains("psi.ArgumentList.Add(a)", src);                 // arguments are passed one by one, never as one command line
        Assert.DoesNotContain("File.WriteAllText(\"/etc", src);
    }
}

public class NasParsingTests
{
    [Fact]
    public void ParseExports_SampleShowmountOutput()
    {
        const string text = "Export list for 172.16.0.35:\n/volume1/library  172.16.0.12,172.16.0.101\n/volume1/share1   172.16.0.102,172.16.0.103\n/volume1/share2   172.16.0.102\n/volume1/share3   172.16.0.103\n";
        var l = NasService.ParseExports(text, new[] { "172.16.0.12" });
        Assert.Equal(4, l.Count);
        Assert.Equal("/volume1/library", l[0].Path); Assert.Equal("172.16.0.12,172.16.0.101", l[0].Clients);
        Assert.True(l[0].AllowsThisServer); Assert.False(l[1].AllowsThisServer); Assert.All(l, x => Assert.True(x.Usable));
    }

    [Theory]
    [InlineData("*", true)] [InlineData("(everyone)", true)] [InlineData("172.16.0.0/24", true)] [InlineData("10.0.0.0/8", false)] [InlineData("172.16.0.12", true)]
    [InlineData("172.16.0.101", false)] [InlineData("nas.local", null)] [InlineData("172.16.0.99,nas.local", null)] [InlineData("172.16.0.99,172.16.0.12", true)] [InlineData("172.16.0.0/33", null)]
    public void AllowsAny(string clients, bool? expected) => Assert.Equal(expected, NasService.AllowsAny(clients, new[] { "172.16.0.12" }));

    [Fact]
    public void ParseExports_MarksUnsafeSharePathsAsNotUsable() =>
        Assert.False(NasService.ParseExports("/volume 1/x  *\n/volume1/a;b  *\n", Array.Empty<string>()).Where(e => e.Path == "/volume1/a;b").Single().Usable);

    [Fact]
    public void MountInfo_AutomountThenNfsOnTop_NewestWins_AndFacts()
    {
        var text = "50 30 0:45 / /mnt/nas-media rw,relatime shared:5 - autofs systemd-1 rw,fd=70\n" +
                   "60 50 0:60 / /mnt/nas-media rw,relatime shared:9 - nfs 172.16.0.35:/volume1/library rw,vers=3,rsize=131072,hard,proto=tcp,timeo=600\n" +
                   "70 30 8:1 / / rw,relatime - ext4 /dev/sda1 rw\n";
        var t = MountTable.Parse(text);
        var e = t.At("/mnt/nas-media")!;
        Assert.Equal("nfs", e.FsType); Assert.Equal("172.16.0.35:/volume1/library", e.Source); Assert.Equal("3", e.NfsVersion);
        Assert.True(e.IsNfs && e.Hard && !e.Soft && !e.ReadOnly);
        Assert.Equal("/mnt/nas-media", t.Holding("/mnt/nas-media/Movies/x")!.MountPoint);
        Assert.Equal("/", t.Holding("/home/x")!.MountPoint);
        Assert.Null(t.At("/mnt/other"));
    }

    [Fact]
    public void MountInfo_ReadOnlySoftAndEscapedSpaces()
    {
        var t = MountTable.Parse("1 2 0:1 / /mnt/a\\040b ro,relatime - nfs4 h:/x ro,vers=4.1,soft\n");
        var e = t.At("/mnt/a b")!;
        Assert.True(e.ReadOnly && e.Soft && !e.Hard); Assert.Equal("4.1", e.NfsVersion); Assert.Equal("nfs4", e.FsType);
        Assert.Null(MountTable.Parse("garbage\n\n").At("/"));
    }

    [Fact]
    public void ExplainShowmount_GivesPlainReasons()
    {
        Assert.Contains("turn NFS on", NasService.ExplainShowmount("clnt_create: RPC: Program not registered", "nas"));
        Assert.Contains("cannot be found", NasService.ExplainShowmount("showmount: Name or service not known", "nas"));
        Assert.Contains("no network route", NasService.ExplainShowmount("No route to host", "nas"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("showmount failed.", NasService.ExplainShowmount("", "nas"));
    }

    [Fact]
    public async Task SystemEnvironment_PassesArgumentsLiterally_AndTimesOut()
    {
        Skip.If(OperatingSystem.IsWindows());
        var env = new SystemNasEnvironment();
        var echo = env.FindExecutable("echo");
        Assert.NotNull(echo);
        var r = await env.RunAsync(echo!, new[] { "-e", "a;touch /tmp/convertr-should-not-exist$(id)" }, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("a;touch /tmp/convertr-should-not-exist$(id)", r.Stdout);
        Assert.False(File.Exists("/tmp/convertr-should-not-exist"));
        var sleep = env.FindExecutable("sleep")!;
        var t = await env.RunAsync(sleep, new[] { "5" }, TimeSpan.FromMilliseconds(300), CancellationToken.None);
        Assert.True(t.TimedOut);
        Assert.NotEmpty(env.ReadMountInfo());
    }
}

public class NasApiTests
{
    private static (TestApp App, FakeNas Fake) Make(Dictionary<string, string?>? cfg = null, string? rootDir = null)
    {
        var fake = new FakeNas();
        TestApp? app = null;
        var c = cfg ?? new();
        var root = rootDir ?? Path.Combine(Path.GetTempPath(), "convertr-svc-nas-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "mnt")); lock (TempRoots) TempRoots.Add(root);
        c["Convertr:NasMountParents:0"] = Path.Combine(root, "mnt");
        c["Convertr:NasMountParents:1"] = "/mnt";
        c["Convertr:PathsDropInPath"] = Path.Combine(root, "paths.conf");
        app = new TestApp(false, s => s.AddSingleton<INasEnvironment>(fake), c);
        app.Cookie = null;
        NasRoots.Add(app, root);
        return (app, fake);
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TestApp, string> NasRoots = new();
    private static readonly List<string> TempRoots = new();
    static NasApiTests() => AppDomain.CurrentDomain.ProcessExit += (_, _) => { foreach (var r in TempRoots.ToArray()) { try { Directory.Delete(r, true); } catch { /* temp */ } } };
    private static string R(TestApp a) => NasRoots.TryGetValue(a, out var r) ? r : "";

    private static object Body(string? host = "172.16.0.35", string? share = "/volume1/library", string? mp = "/mnt/nas-media", string vers = "3", bool ro = false, string media = "") =>
        new { host, share, mountPoint = mp, vers, readOnly = ro, mediaFolder = media };

    [Fact]
    public async Task EveryNasEndpoint_NeedsLogin_AndCsrf()
    {
        var (t, _) = Make();
        using var _t = t;
        foreach (var (m, u) in new[] { (HttpMethod.Get, "/api/nas"), (HttpMethod.Post, "/api/nas/exports"), (HttpMethod.Post, "/api/nas/commands"), (HttpMethod.Post, "/api/nas/check") })
            Assert.Equal(HttpStatusCode.Unauthorized, (await t.Send(m, u, new { }, csrf: false, cookie: false)).StatusCode);
        await t.LoginAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Send(HttpMethod.Post, "/api/nas/commands", Body(), csrf: false)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t.GetAsync("/api/nas")).StatusCode);
    }

    [Fact]
    public async Task Info_ShowsDefaults_ClientState_ServerAddresses_AndTheHintWhenNfsCommonIsMissing()
    {
        var (t, fake) = Make(); using var _t = t; await t.LoginAsync();
        var j = await TestApp.Json(await t.GetAsync("/api/nas"));
        Assert.True(j.GetProperty("clientInstalled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, j.GetProperty("installHint").ValueKind);
        Assert.Equal("172.16.0.12", j.GetProperty("serverAddresses")[0].GetString());
        Assert.Equal("/mnt/nas-media", j.GetProperty("config").GetProperty("mountPoint").GetString());
        fake.Tools.Clear();
        j = await TestApp.Json(await t.GetAsync("/api/nas"));
        Assert.False(j.GetProperty("clientInstalled").GetBoolean());
        Assert.Contains("sudo apt install -y nfs-common", j.GetProperty("installHint").GetString());
    }

    [Fact]
    public async Task Commands_ReturnTheExactFstabLine_Steps_AndSaveTheForm()
    {
        var (t, _) = Make(); using var _t = t; await t.LoginAsync();
        var mp = Path.Combine(R(t), "mnt", "nas");
        var r = await t.PostAsync("/api/nas/commands", Body(mp: mp, media: mp + "/Movies"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await TestApp.Json(r);
        Assert.Equal($"172.16.0.35:/volume1/library {mp} nfs vers=3,rw,hard,_netdev,nofail,x-systemd.automount,x-systemd.idle-timeout=0 0 0", j.GetProperty("fstabLine").GetString());
        Assert.Equal(7, j.GetProperty("steps").GetArrayLength());
        Assert.Contains("sudo mkdir -p " + mp, j.GetProperty("script").GetString());
        var cfg = (await TestApp.Json(await t.GetAsync("/api/nas"))).GetProperty("config");
        Assert.Equal("172.16.0.35", cfg.GetProperty("host").GetString()); Assert.Equal(mp + "/Movies", cfg.GetProperty("mediaFolder").GetString());
        Assert.True(File.Exists(Path.Combine(t.Data, "nas.json")));
    }

    [Theory]
    [InlineData("a;reboot", "/volume1/library", "/mnt/nas-media", "3")]
    [InlineData("nas", "/volume1/library; reboot", "/mnt/nas-media", "3")]
    [InlineData("nas", "/volume1/library", "/etc/cron.d", "3")]
    [InlineData("nas", "/volume1/library", "/mnt/nas-media", "3,rw,no_root_squash")]
    [InlineData("", "/volume1/library", "/mnt/nas-media", "3")]
    public async Task Commands_RefuseBadInput_WithoutSavingAnything(string host, string share, string mp, string vers)
    {
        var (t, _) = Make(); using var _t = t; await t.LoginAsync();
        var r = await t.PostAsync("/api/nas/commands", Body(host, share, mp, vers));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.False(string.IsNullOrEmpty((await TestApp.Json(r)).GetProperty("error").GetString()));
        Assert.False(File.Exists(Path.Combine(t.Data, "nas.json")));
    }

    [Fact]
    public async Task Commands_AllowedRoots_OutsideGivesSedAndInstall_InsideSaysNothingToChange_ConfiguredButNotLoadedSaysRestart()
    {
        var (t, _) = Make(); using var _t = t; await t.LoginAsync();
        var mp = Path.Combine(R(t), "mnt", "nas");
        var j = await TestApp.Json(await t.PostAsync("/api/nas/commands", Body(mp: mp)));
        var a = j.GetProperty("allowedRoots");
        Assert.Equal("outside", a.GetProperty("state").GetString());
        var cmds = a.GetProperty("commands").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Contains(cmds, c => c.StartsWith("sudo sed -i -E") && c.Contains(mp) && c.EndsWith("/etc/convertr-svc/appsettings.json"));
        Assert.Contains(cmds, c => c.StartsWith("cd ~/convertr-svc-") && c.Contains("--media-root " + t.Media) && c.Contains("--media-root " + mp));
        Assert.True(a.GetProperty("rewritesPathsConf").GetBoolean());
        // the web page has no way to change AllowedRoots
        Assert.Equal(HttpStatusCode.BadRequest, (await t.PutAsync("/api/settings", new { AllowedRoots = new[] { mp } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t.PutAsync("/api/nas/roots", new { roots = new[] { mp } })).StatusCode);
    }

    [Fact]
    public async Task Commands_AllowedRoots_Inside_And_ConfiguredButNotMounted()
    {
        var root = Path.Combine(Path.GetTempPath(), "convertr-svc-nas-" + Guid.NewGuid().ToString("N")[..8]);
        var mpIn = Path.Combine(root, "mnt", "in"); Directory.CreateDirectory(mpIn);
        var mpLate = Path.Combine(root, "mnt", "late");   // configured, but missing when the service starts
        var (t, _) = Make(new() { ["Convertr:AllowedRoots:1"] = mpIn, ["Convertr:AllowedRoots:2"] = mpLate }, root);
        using var _t = t; await t.LoginAsync();
        var inside = (await TestApp.Json(await t.PostAsync("/api/nas/commands", Body(mp: mpIn)))).GetProperty("allowedRoots");
        Assert.Equal("inside", inside.GetProperty("state").GetString()); Assert.Equal(0, inside.GetProperty("commands").GetArrayLength());
        var late = (await TestApp.Json(await t.PostAsync("/api/nas/commands", Body(mp: mpLate)))).GetProperty("allowedRoots");
        Assert.Equal("configured-not-loaded", late.GetProperty("state").GetString());
        Assert.Equal("sudo systemctl restart convertr-svc", late.GetProperty("commands")[0].GetString());
        try { Directory.Delete(root, true); } catch { }
    }

    // ---------- exports ----------
    [Fact]
    public async Task Exports_RunShowmountWithoutAShell_AndMarkWhetherThisServerIsAllowed()
    {
        var (t, fake) = Make(); using var _t = t; await t.LoginAsync();
        fake.Show = _ => new NasProcResult(0, "Export list for 172.16.0.35:\n/volume1/library  172.16.0.12,172.16.0.101\n/volume1/share1   172.16.0.102\n", "", false);
        var j = await TestApp.Json(await t.PostAsync("/api/nas/exports", new { host = "172.16.0.35" }));
        Assert.True(j.GetProperty("ok").GetBoolean());
        Assert.Equal(2, j.GetProperty("exports").GetArrayLength());
        Assert.True(j.GetProperty("exports")[0].GetProperty("allowsThisServer").GetBoolean());
        Assert.False(j.GetProperty("exports")[1].GetProperty("allowsThisServer").GetBoolean());
        var call = Assert.Single(fake.Calls);
        Assert.Equal("/usr/sbin/showmount", call.Exe); Assert.Equal(new[] { "-e", "172.16.0.35" }, call.Args);
    }

    [Fact]
    public async Task Exports_WithoutNfsCommon_SaysInstallIt_AndRunsNothing()
    {
        var (t, fake) = Make(); using var _t = t; await t.LoginAsync();
        fake.Tools.Clear();
        var j = await TestApp.Json(await t.PostAsync("/api/nas/exports", new { host = "nas" }));
        Assert.False(j.GetProperty("available").GetBoolean()); Assert.False(j.GetProperty("ok").GetBoolean());
        Assert.Equal("sudo apt install -y nfs-common", j.GetProperty("hint").GetString());
        Assert.Contains("not installed", j.GetProperty("error").GetString());
        Assert.Empty(fake.Calls);
    }

    [Theory]
    [InlineData("nas;id")] [InlineData("$(id)")] [InlineData("-o x")] [InlineData("")] [InlineData("a b")] [InlineData("999.1.1.1")]
    public async Task Exports_RefuseABadHost_BeforeAnythingRuns(string host)
    {
        var (t, fake) = Make(); using var _t = t; await t.LoginAsync();
        var r = await t.PostAsync("/api/nas/exports", new { host });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Exports_ShowmountFailureAndTimeout_AreExplained()
    {
        var (t, fake) = Make(); using var _t = t; await t.LoginAsync();
        fake.Show = _ => new NasProcResult(1, "", "clnt_create: RPC: Program not registered\n", false);
        var j = await TestApp.Json(await t.PostAsync("/api/nas/exports", new { host = "nas" }));
        Assert.False(j.GetProperty("ok").GetBoolean()); Assert.Contains("turn NFS on", j.GetProperty("error").GetString());
        fake.Show = _ => new NasProcResult(-1, "", "", true);
        j = await TestApp.Json(await t.PostAsync("/api/nas/exports", new { host = "nas" }));
        Assert.Contains("did not answer", j.GetProperty("error").GetString());
        fake.Show = _ => new NasProcResult(0, "Export list for nas:\n", "", false);
        j = await TestApp.Json(await t.PostAsync("/api/nas/exports", new { host = "nas" }));
        Assert.True(j.GetProperty("ok").GetBoolean()); Assert.Equal(0, j.GetProperty("exports").GetArrayLength()); Assert.Contains("NFS Permissions", j.GetProperty("note").GetString());
    }

    // ---------- check ----------
    private static async Task<JsonElement> Check(TestApp t, string mp, string media = "")
    {
        var r = await t.PostAsync("/api/nas/check", new { mountPoint = mp, mediaFolder = media });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await TestApp.Json(r);
    }
    private static string Status(JsonElement j, string id) => j.GetProperty("steps").EnumerateArray().First(s => s.GetProperty("id").GetString() == id).GetProperty("status").GetString()!;
    private static string Msg(JsonElement j, string id) => j.GetProperty("steps").EnumerateArray().First(s => s.GetProperty("id").GetString() == id).GetProperty("message").GetString()!;
    private static string[] Hidden(string dir) => Directory.Exists(dir) ? Directory.GetFileSystemEntries(dir, ".convertr-check-*", SearchOption.AllDirectories) : Array.Empty<string>();

    private static (string Mp, string Media) Share(TestApp t, FakeNas fake, string type = "nfs", string sopts = "rw,vers=3,hard,proto=tcp", string mopts = "rw,relatime")
    {
        var mp = Path.Combine(R(t), "mnt", "nas"); var media = Path.Combine(mp, "Movies");
        Directory.CreateDirectory(media);
        var real = Convertr.Svc.Server.RootGuard.RealPath(mp)!;
        fake.MountInfo = FakeNas.Line(real, type, "172.16.0.35:/volume1/library", mopts, sopts);
        return (mp, media);
    }

    [Fact]
    public async Task Check_AMountedShare_PassesEveryStep_AndLeavesNothingBehind()
    {
        var (t, fake) = Make(); using var _t = t; await t.LoginAsync();
        var (mp, media) = Share(t, fake);
        var j = await Check(t, mp, media);
        foreach (var id in new[] { "folder", "mounted", "nfs", "space", "media", "write", "rename", "originals" }) Assert.Equal("ok", Status(j, id));
        Assert.Contains("NFS version 3", Msg(j, "nfs")); Assert.Contains("read/write", Msg(j, "nfs")); Assert.Contains("hard", Msg(j, "nfs"));
        Assert.Contains("172.16.0.35:/volume1/library", Msg(j, "mounted"));
        Assert.True(j.GetProperty("mount").GetProperty("freeBytes").GetInt64() > 0);
        Assert.Empty(Hidden(mp));
        Assert.False(Directory.Exists(Path.Combine(mp, "_Convertr_Originals")), "the check must not create the originals folder itself");
        // not allowed folder: that is the only problem left, and it gives the commands
        Assert.Equal("fail", Status(j, "roots")); Assert.False(j.GetProperty("ok").GetBoolean());
        Assert.Contains("--media-root", string.Join("\n", j.GetProperty("steps").EnumerateArray().First(s => s.GetProperty("id").GetString() == "roots").GetProperty("commands").EnumerateArray().Select(x => x.GetString())));
    }

    [Fact]
    public async Task Check_Everything_Ok_WhenTheFolderIsAllowed_AndTheDropInListsIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "convertr-svc-nas-" + Guid.NewGuid().ToString("N")[..8]);
        var (t, fake) = Make(); using var _t = t; await t.LoginAsync();
        var (mp, media) = Share(t, fake);
        // restart the app with the mount point allowed
        t.Dispose();
        var fake2 = new FakeNas();
        var dropIn = Path.Combine(root, "paths.conf"); Directory.CreateDirectory(root);
        var mpDir = Path.Combine(root, "mnt", "nas"); Directory.CreateDirectory(Path.Combine(mpDir, "Movies"));
        File.WriteAllText(dropIn, $"[Unit]\nRequiresMountsFor={mpDir}\n\n[Service]\nReadWritePaths={mpDir}\n");
        using var t2 = new TestApp(false, s => s.AddSingleton<INasEnvironment>(fake2), new Dictionary<string, string?>
        {
            ["Convertr:AllowedRoots:1"] = mpDir, ["Convertr:NasMountParents:0"] = Path.Combine(root, "mnt"), ["Convertr:PathsDropInPath"] = dropIn,
        });
        await t2.LoginAsync();
        fake2.MountInfo = FakeNas.Line(Convertr.Svc.Server.RootGuard.RealPath(mpDir)!, "nfs4", "nas:/volume1/library", sopts: "rw,vers=4.1,hard");
        var j = await Check(t2, mpDir, mpDir + "/Movies");
        Assert.True(j.GetProperty("ok").GetBoolean(), j.ToString());
        Assert.Equal("ok", Status(j, "roots")); Assert.Equal("ok", Status(j, "unit"));
        Assert.Contains("All checks passed", j.GetProperty("summary").GetString());
        Assert.Contains("version 4.1", Msg(j, "nfs"));
        try { Directory.Delete(root, true); } catch { }
    }

    [Fact]
    public async Task Check_APlainFolder_IsNotAMount_AndNothingIsWritten()
    {
        var (t, fake) = Make(); using var _t = t; await t.LoginAsync();
        var mp = Path.Combine(R(t), "mnt", "nas"); Directory.CreateDirectory(Path.Combine(mp, "Movies"));
        fake.MountInfo = "70 30 8:1 / / rw,relatime - ext4 /dev/sda1 rw\n";
        var j = await Check(t, mp, mp + "/Movies");
        Assert.False(j.GetProperty("ok").GetBoolean());
        Assert.Equal("fail", Status(j, "mounted")); Assert.Contains("only a normal folder", Msg(j, "mounted"));
        Assert.DoesNotContain(j.GetProperty("steps").EnumerateArray(), s => s.GetProperty("id").GetString() is "write" or "rename" or "originals");
        Assert.Empty(Hidden(mp));
        Assert.Empty(Directory.GetFiles(Path.Combine(mp, "Movies")));
    }

    [Fact]
    public async Task Check_MissingFolder_And_AutomountThatDidNotStart()
    {
        var (t, fake) = Make(); using var _t = t; await t.LoginAsync();
        var mp = Path.Combine(R(t), "mnt", "gone");
        var j = await Check(t, mp);
        Assert.Equal("fail", Status(j, "folder")); Assert.Contains("does not exist", Msg(j, "folder"));
        Directory.CreateDirectory(mp);
        fake.MountInfo = FakeNas.Line(Convertr.Svc.Server.RootGuard.RealPath(mp)!, "autofs", "systemd-1", sopts: "rw,fd=70");
        j = await Check(t, mp);
        Assert.Equal("fail", Status(j, "mounted")); Assert.Contains("did not happen", Msg(j, "mounted"));
    }

    [Fact]
    public async Task Check_ReadOnlyMount_FailsWritingInPlainWords_AndWritesNothing()
    {
        var (t, fake) = Make(); using var _t = t; await t.LoginAsync();
        var (mp, media) = Share(t, fake, mopts: "ro,relatime", sopts: "ro,vers=3,hard");
        var j = await Check(t, mp, media);
        Assert.Equal("fail", Status(j, "write")); Assert.Contains("read-only", Msg(j, "write"));
        Assert.Equal("skip", Status(j, "rename")); Assert.Equal("skip", Status(j, "originals"));
        Assert.Empty(Hidden(mp));
    }

    [Fact]
    public async Task Check_SoftMountAndOtherFilesystems_AreWarnings()
    {
        var (t, fake) = Make(); using var _t = t; await t.LoginAsync();
        var (mp, media) = Share(t, fake, sopts: "rw,vers=3,soft");
        Assert.Equal("warn", Status(await Check(t, mp, media), "nfs"));
        (mp, media) = Share(t, fake, type: "cifs");
        var j = await Check(t, mp, media);
        Assert.Equal("warn", Status(j, "nfs")); Assert.Contains("not NFS", Msg(j, "nfs"));
    }

    [Fact]
    public async Task Check_MediaFolderProblems_AreReported()
    {
        var (t, fake) = Make(); using var _t = t; await t.LoginAsync();
        var (mp, media) = Share(t, fake);
        var j = await Check(t, mp, mp + "/DoesNotExist");
        Assert.Equal("fail", Status(j, "media")); Assert.Contains("does not exist", Msg(j, "media"));
        Assert.DoesNotContain(j.GetProperty("steps").EnumerateArray(), s => s.GetProperty("id").GetString() == "write");
        j = await Check(t, mp, t.Media);   // exists but is on another disk
        Assert.Equal("fail", Status(j, "media")); Assert.Contains("not on this share", Msg(j, "media"));
        // no media folder: tests run at the top of the share, originals is skipped
        j = await Check(t, mp);
        Assert.Equal("skip", Status(j, "media")); Assert.Equal("ok", Status(j, "write")); Assert.Equal("skip", Status(j, "originals"));
        Assert.Empty(Hidden(mp));
    }

    [Fact]
    public async Task Check_MediaFolderAtTheTopOfTheShare_CannotHaveItsOriginalsOnTheShare()
    {
        var (t, fake) = Make(); using var _t = t; await t.LoginAsync();
        var (mp, _) = Share(t, fake);
        var j = await Check(t, mp, mp);
        Assert.Equal("ok", Status(j, "write"));
        Assert.Equal("fail", Status(j, "originals")); Assert.Contains("not on this share", Msg(j, "originals"));
        Assert.Empty(Hidden(mp));
    }

    [Fact]
    public async Task Check_WriteRefused_ExplainsDsmSquash_AndTheSystemdCase()
    {
        Skip.If(OperatingSystem.IsWindows() || Environment.UserName == "root");
        var (t, fake) = Make(); using var _t = t; await t.LoginAsync();
        var (mp, media) = Share(t, fake);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(media, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var j = await Check(t, mp, media);
            Assert.Equal("fail", Status(j, "write")); Assert.Contains("Squash", Msg(j, "write"));
            Assert.Equal("skip", Status(j, "rename"));
        }
        finally { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(media, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        Assert.Empty(Hidden(mp));
    }

    [Fact]
    public async Task Check_NasThatDoesNotAnswer_TimesOut_ThenRefusesAPileUp_ThenRecovers()
    {
        var (t, fake) = Make(); using var _t = t; await t.LoginAsync();
        var (mp, media) = Share(t, fake);
        t.Get<NasService>().CheckTimeoutOverride = TimeSpan.FromMilliseconds(400);
        fake.BlockMountInfo = new ManualResetEventSlim(false);
        var j = await Check(t, mp, media);
        Assert.False(j.GetProperty("ok").GetBoolean()); Assert.Equal("fail", Status(j, "timeout"));
        var again = await t.PostAsync("/api/nas/check", new { mountPoint = mp, mediaFolder = media });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        fake.BlockMountInfo.Set();
        await Task.Delay(1200);
        t.Get<NasService>().CheckTimeoutOverride = TimeSpan.FromSeconds(30);
        j = await Check(t, mp, media);
        Assert.Equal("ok", Status(j, "write"));
    }

    [Theory]
    [InlineData("/etc")] [InlineData("/mnt/../etc")] [InlineData("")] [InlineData("/tmp/x;y")]
    public async Task Check_RefusesMountPointsOutsideTheAllowedParents(string mp)
    {
        var (t, _) = Make(); using var _t = t; await t.LoginAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await t.PostAsync("/api/nas/check", new { mountPoint = mp, mediaFolder = "" })).StatusCode);
    }

    [Fact]
    public async Task Check_WithTheRealSystem_AFolderThatIsNotMounted_IsReportedAndLeftAlone()
    {
        Skip.If(OperatingSystem.IsWindows());
        var root = Path.Combine(Path.GetTempPath(), "convertr-svc-nas-" + Guid.NewGuid().ToString("N")[..8]);
        var mp = Path.Combine(root, "mnt", "plain"); Directory.CreateDirectory(mp);
        using var t = new TestApp(false, null, new Dictionary<string, string?> { ["Convertr:NasMountParents:0"] = Path.Combine(root, "mnt"), ["Convertr:PathsDropInPath"] = Path.Combine(root, "none.conf") });
        await t.LoginAsync();
        var j = await Check(t, mp);
        Assert.Equal("fail", Status(j, "mounted"));
        Assert.Empty(Directory.GetFileSystemEntries(mp));
        try { Directory.Delete(root, true); } catch { }
    }

    [Fact]
    public async Task Check_UnitStep_WarnsWhenTheDropInDoesNotListTheFolder_AndSkipsWhenThereIsNone()
    {
        var (t, fake) = Make(); using var _t = t; await t.LoginAsync();
        var (mp, media) = Share(t, fake);
        var j = await Check(t, mp, media);
        Assert.Equal("skip", Status(j, "unit"));
        File.WriteAllText(Path.Combine(R(t), "paths.conf"), "[Unit]\nRequiresMountsFor=/srv/testmedia\n\n[Service]\nReadWritePaths=/srv/testmedia\n");
        j = await Check(t, mp, media);
        Assert.Equal("warn", Status(j, "unit")); Assert.Contains("install.sh", string.Join(" ", j.GetProperty("steps").EnumerateArray().First(s => s.GetProperty("id").GetString() == "unit").GetProperty("commands").EnumerateArray().Select(x => x.GetString())));
    }
}

public class NasUiTests
{
    [Fact]
    public async Task SetupPage_HasTheNasSection_WiredToTheApi_WithoutInnerHtml()
    {
        using var t = new TestApp();
        async Task<string> Fetch(string u) => await (await t.Send(HttpMethod.Get, u, csrf: false, cookie: false)).Content.ReadAsStringAsync();
        var html = await Fetch("/"); var app = await Fetch("/js/app.js"); var nas = await Fetch("/js/nas.js");
        Assert.Contains("Network storage (NAS)", html);
        foreach (var id in new[] { "nas-host", "nas-share", "nas-mp", "nas-vers", "nas-media", "nas-ro", "nas-list", "nas-gen", "nas-check", "nas-fstab", "nas-steps", "nas-copy-all", "nas-roots-cmds", "nas-results", "nas-summary" })
            Assert.Contains($"id=\"{id}\"", html);
        Assert.Contains("import * as nas from './nas.js'", app); Assert.Contains("nas.load()", app);
        foreach (var path in new[] { "/api/nas/exports", "/api/nas/commands", "/api/nas/check", "/api/nas" }) Assert.Contains(path, nas);
        Assert.DoesNotContain("innerHTML", nas); Assert.DoesNotContain("eval(", nas);
        Assert.Contains("execCommand('copy')", nas);   // plain http on a LAN address has no clipboard API
        Assert.Contains("Copy all commands", html);
    }
}
