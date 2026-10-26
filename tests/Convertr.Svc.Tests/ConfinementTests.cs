// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Net;
using System.Text.Json;
using Convertr.Svc.Server;
using Xunit;

namespace Convertr.Svc.Tests;

/// <summary>Folder confinement (DESIGN.md 6.4): text checks, symlink resolution, prefix tricks, and the HTTP endpoints that use them.</summary>
public class ConfinementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "convertr-guard-" + Guid.NewGuid().ToString("N")[..8]);
    private string Media => Path.Combine(_root, "media");
    private string Outside => Path.Combine(_root, "outside");

    public ConfinementTests()
    {
        Directory.CreateDirectory(Path.Combine(Media, "Movies", "Action")); Directory.CreateDirectory(Path.Combine(Media, ".hidden"));
        Directory.CreateDirectory(Path.Combine(Media, "@eaDir")); Directory.CreateDirectory(Path.Combine(_root, "media2")); Directory.CreateDirectory(Outside);
        File.WriteAllText(Path.Combine(Media, "file.txt"), "x");
    }

    private RootGuard Guard() => new(new ServerOptions { AllowedRoots = new[] { Media } });
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [SkippableFact]
    public void AcceptsFoldersInsideTheRoot()
    {
        Skip.If(OperatingSystem.IsWindows());
        var g = Guard();
        Assert.True(g.TryResolveDirectory(Media, out var real, out _)); Assert.Equal(Path.GetFullPath(Media), real);
        Assert.True(g.TryResolveDirectory(Path.Combine(Media, "Movies", "Action"), out _, out _));
        Assert.True(g.TryResolveDirectory(Path.Combine(Media, "Movies") + "/", out _, out _));
    }

    [SkippableTheory]
    [InlineData("")] [InlineData("   ")] [InlineData("relative/path")] [InlineData("Movies")]
    public void RejectsEmptyAndRelativePaths(string p)
    {
        Skip.If(OperatingSystem.IsWindows());
        Assert.False(Guard().TryResolve(p, out _, out _));
    }

    [SkippableFact]
    public void RejectsParentReferences_EvenWhenTheyStayInside()
    {
        Skip.If(OperatingSystem.IsWindows());
        var g = Guard();
        Assert.False(g.TryResolve(Media + "/Movies/../Movies", out _, out var err)); Assert.Contains("..", err);
        Assert.False(g.TryResolve(Media + "/../outside", out _, out _));
        Assert.False(g.TryResolve(Media + "/Movies/%2e%2e/..", out _, out _));   // does not exist
        Assert.False(g.TryResolve(Media + "/Movies/..\\..", out _, out _));
    }

    [SkippableFact]
    public void RejectsOutsidePaths_AndSiblingNamesThatOnlyShareAPrefix()
    {
        Skip.If(OperatingSystem.IsWindows());
        var g = Guard();
        Assert.False(g.TryResolve("/etc", out _, out _));
        Assert.False(g.TryResolve("/", out _, out _));
        Assert.False(g.TryResolve(Outside, out _, out _));
        Assert.False(g.TryResolve(Path.Combine(_root, "media2"), out _, out _));   // /x/media2 starts with /x/media
        Assert.False(g.TryResolve(Media + "\0/Movies", out _, out _));
    }

    [SkippableFact]
    public void SymlinkThatEscapesTheRoot_IsRejected_AtAnyDepth()
    {
        Skip.If(OperatingSystem.IsWindows());
        Directory.CreateSymbolicLink(Path.Combine(Media, "escape"), Outside);
        Directory.CreateSymbolicLink(Path.Combine(Media, "Movies", "Action", "deep"), "/etc");
        Directory.CreateSymbolicLink(Path.Combine(Media, "relative-escape"), "../outside");
        var g = Guard();
        Assert.False(g.TryResolve(Path.Combine(Media, "escape"), out _, out var err)); Assert.Contains("outside", err);
        Assert.False(g.TryResolve(Path.Combine(Media, "escape", "sub"), out _, out _));
        Assert.False(g.TryResolve(Path.Combine(Media, "Movies", "Action", "deep"), out _, out _));
        Assert.False(g.TryResolve(Path.Combine(Media, "relative-escape"), out _, out _));
    }

    [SkippableFact]
    public void ChainedSymlinks_AreFollowedToTheEnd()
    {
        Skip.If(OperatingSystem.IsWindows());
        Directory.CreateSymbolicLink(Path.Combine(Outside, "hop"), Outside);
        Directory.CreateSymbolicLink(Path.Combine(Media, "chain"), Path.Combine(Outside, "hop"));
        Assert.False(Guard().TryResolve(Path.Combine(Media, "chain"), out _, out _));
    }

    [SkippableFact]
    public void SymlinkThatStaysInsideTheRoot_IsAllowed_AndResolvedToItsRealPath()
    {
        Skip.If(OperatingSystem.IsWindows());
        Directory.CreateSymbolicLink(Path.Combine(Media, "shortcut"), Path.Combine(Media, "Movies"));
        var g = Guard();
        Assert.True(g.TryResolveDirectory(Path.Combine(Media, "shortcut"), out var real, out _));
        Assert.Equal(Path.Combine(Path.GetFullPath(Media), "Movies"), real);
    }

    [SkippableFact]
    public void SymlinkLoop_IsRejected_NotHung()
    {
        Skip.If(OperatingSystem.IsWindows());
        Directory.CreateSymbolicLink(Path.Combine(Media, "loopA"), Path.Combine(Media, "loopB"));
        Directory.CreateSymbolicLink(Path.Combine(Media, "loopB"), Path.Combine(Media, "loopA"));
        Assert.False(Guard().TryResolve(Path.Combine(Media, "loopA"), out _, out _));
    }

    [SkippableFact]
    public void Roots_ThatAreSymlinks_AreResolved_AndAFileIsNotAFolder()
    {
        Skip.If(OperatingSystem.IsWindows());
        var link = Path.Combine(_root, "rootlink"); Directory.CreateSymbolicLink(link, Media);
        var g = new RootGuard(new ServerOptions { AllowedRoots = new[] { link, "/does/not/exist" } });
        Assert.Single(g.Roots); Assert.Equal(Path.GetFullPath(Media), g.Roots[0]); Assert.Single(g.Skipped);
        Assert.True(g.TryResolveDirectory(Path.Combine(Media, "Movies"), out _, out _));
        Assert.False(g.TryResolveDirectory(Path.Combine(Media, "file.txt"), out _, out var err)); Assert.Contains("not a folder", err);
    }

    [SkippableFact]
    public void NoRootsConfigured_MeansNothingIsAllowed()
    {
        Skip.If(OperatingSystem.IsWindows());
        var g = new RootGuard(new ServerOptions());
        Assert.False(g.TryResolve(Media, out _, out var err)); Assert.Contains("AllowedRoots", err);
    }

    [SkippableFact]
    public void List_ShowsFoldersOnly_HidesHousekeepingAndEscapingLinks()
    {
        Skip.If(OperatingSystem.IsWindows());
        Directory.CreateSymbolicLink(Path.Combine(Media, "escape"), Outside);
        Directory.CreateSymbolicLink(Path.Combine(Media, "inside-link"), Path.Combine(Media, "Movies"));
        var g = Guard();
        g.TryResolveDirectory(Media, out var real, out _);
        var l = g.List(real);
        var names = l.Dirs.Select(d => d.Name).ToList();
        Assert.Contains("Movies", names); Assert.Contains("inside-link", names);
        Assert.DoesNotContain("file.txt", names); Assert.DoesNotContain(".hidden", names); Assert.DoesNotContain("@eaDir", names);
        Assert.DoesNotContain("escape", names);
        Assert.Null(l.Parent); Assert.True(l.IsRoot);
        g.TryResolveDirectory(Path.Combine(Media, "Movies"), out var movies, out _);
        Assert.Equal(real, g.List(movies).Parent);
    }

    // ---------- over HTTP ----------
    [SkippableFact]
    public async Task Endpoints_RefuseEscapes()
    {
        Skip.If(OperatingSystem.IsWindows());
        using var t = new TestApp();
        Directory.CreateSymbolicLink(Path.Combine(t.Media, "escape"), t.Outside);
        await t.LoginAsync();
        foreach (var p in new[] { "/etc", "/", t.Outside, t.Media + "/../outside", t.Media + "/escape", t.Media + "/escape/x", "relative" })
        {
            var r = await t.GetAsync("/api/fs/list?path=" + Uri.EscapeDataString(p));
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            var s = await t.PostAsync("/api/scan", new { folder = p });
            Assert.Equal(HttpStatusCode.BadRequest, s.StatusCode);
            var m = await t.PutAsync("/api/settings", new { MediaFolder = p });
            Assert.Equal(HttpStatusCode.BadRequest, m.StatusCode);
        }
        var ok = await t.GetAsync("/api/fs/list?path=" + Uri.EscapeDataString(t.Media));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var def = await TestApp.Json(await t.GetAsync("/api/fs/list"));   // single root: lists it
        Assert.True(def.GetProperty("isRoot").GetBoolean());
    }

    [SkippableFact]
    public async Task Settings_OnlyTheListedFieldsCanBeChanged()
    {
        Skip.If(OperatingSystem.IsWindows());
        using var t = new TestApp();
        await t.LoginAsync();
        foreach (var field in new[] { "FfmpegFolder", "FfmpegEnvironment", "GpuEncoderArgs", "Originals", "Hardware", "NoSuchThing" })
        {
            var r = await t.PutAsync("/api/settings", JsonDocument.Parse($"{{\"{field}\": \"/tmp/x\"}}").RootElement);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await t.PutAsync("/api/settings", new { Kbps1080 = 5 })).StatusCode);          // out of range
        Assert.Equal(HttpStatusCode.BadRequest, (await t.PutAsync("/api/settings", new { Theme = "Neon" })).StatusCode);        // not an enum value
        var good = await t.PutAsync("/api/settings", new { Theme = "Dark", Kbps1080 = 3500, MediaFolder = t.Media });
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        var j = await TestApp.Json(await t.GetAsync("/api/settings"));
        Assert.Equal("Dark", j.GetProperty("values").GetProperty("Theme").GetString());
        Assert.Equal(3500, j.GetProperty("values").GetProperty("Kbps1080").GetInt32());
    }
}
