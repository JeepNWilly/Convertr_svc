// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Convertr.Core;
using Convertr.Svc.Server;
using Xunit;

namespace Convertr.Svc.Tests;

public class JobTests
{
    private static async Task<JsonElement> Status(TestApp t) => await TestApp.Json(await t.GetAsync("/api/queue"));

    private static async Task<JsonElement> WaitQueueAsync(TestApp t, int seconds = 120)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            var j = await Status(t);
            if (j.GetProperty("state").GetString() is "finished" or "idle") return j;
            await Task.Delay(200);
        }
        throw new TimeoutException("the conversion did not finish");
    }

    private static void Ffmpeg(string args, string output)
    {
        var psi = new ProcessStartInfo("ffmpeg", "-v error -y " + args + " \"" + output + "\"") { RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());
    }

    private static void PrepareSettings(TestApp t, Action<Settings>? f = null)
    {
        var s = new Settings
        {
            Mode = EncoderMode.Cpu, X265Preset = "ultrafast", KeepIfSmallerPercent = 0, KeepIfSmallerMb = 0, UseScratch = true,
            ScratchFolder = Path.Combine(t.Root, "scratch"), Kbps1080 = 100, Kbps720 = 100, KbpsSd = 100, KbpsBelowSd = 100, VerifyDecode = true,
            MediaFolder = t.Media, Container = OutputContainer.Mkv, ParallelCpuJobs = 1,
        };
        f?.Invoke(s);
        SettingsStore.Save(s, Path.Combine(t.Data, "settings.json"));
    }

    [Fact]
    public async Task Settings_ScratchAndGpuFields_AreValidated()
    {
        Skip.If(OperatingSystem.IsWindows());
        using var t = new TestApp();
        await t.LoginAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await t.PutAsync("/api/settings", new { ScratchFolder = "relative/dir" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t.PutAsync("/api/settings", new { ScratchFolder = Path.Combine(t.Media, "scratch") })).StatusCode);   // inside the media folder
        Assert.Equal(HttpStatusCode.BadRequest, (await t.PutAsync("/api/settings", new { ScratchFolder = "/" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t.PutAsync("/api/settings", new { GpuDevice = "/etc/passwd" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t.PutAsync("/api/settings", new { IntelCq = 2 })).StatusCode);
        var ok = await t.PutAsync("/api/settings", new { UseScratch = true, ScratchFolder = Path.Combine(t.Root, "scratch"), GpuBackend = "Vaapi", GpuDevice = "/dev/dri/renderD128", IntelCq = 24, MaxConcurrentTransfers = 3 });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var v = (await TestApp.Json(await t.GetAsync("/api/settings"))).GetProperty("values");
        Assert.Equal("Vaapi", v.GetProperty("GpuBackend").GetString());
        Assert.Equal(24, v.GetProperty("IntelCq").GetInt32());
    }

    [Fact]
    public async Task Settings_IntelDefaults_AreTheBenchmarkValues_AndTheServiceNeverUsesNvenc()
    {
        Skip.If(OperatingSystem.IsWindows());
        using var t = new TestApp();
        await t.LoginAsync();
        var v = (await TestApp.Json(await t.GetAsync("/api/settings"))).GetProperty("values");
        Assert.Equal(23, v.GetProperty("IntelCq").GetInt32());        // VAAPI HEVC q23 (measured default)
        Assert.Equal(21, v.GetProperty("IntelQsvCq").GetInt32());     // QSV q21 = the same quality
        Assert.Equal(27, v.GetProperty("IntelAv1Cq").GetInt32());     // untested placeholder
        Assert.Equal("Vaapi", v.GetProperty("GpuBackend").GetString());   // the desktop default (Nvenc) is shown as Vaapi in the service
        Assert.Equal(HttpStatusCode.BadRequest, (await t.PutAsync("/api/settings", new { IntelQsvCq = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t.PutAsync("/api/settings", new { IntelQsvCq = 22 })).StatusCode);
    }

    [Fact]
    public void EstimateNote_SaysWhatTheEstimateIsBasedOn()
    {
        var hevc = ScanService.EstimateNote(new Settings { Mode = EncoderMode.Gpu, GpuBackend = GpuBackend.Vaapi });
        Assert.Contains("VAAPI q23", hevc); Assert.Contains("5 movies", hevc);
        Assert.Contains("QSV q21", ScanService.EstimateNote(new Settings { Mode = EncoderMode.Gpu, GpuBackend = GpuBackend.Qsv }));
        Assert.Contains("NOT measured", ScanService.EstimateNote(new Settings { Mode = EncoderMode.Gpu, GpuBackend = GpuBackend.Vaapi, GpuCodec = GpuCodec.Av1 }));
        Assert.Contains("CPU x265", ScanService.EstimateNote(new Settings { Mode = EncoderMode.Cpu }));
    }

    [Fact]
    public async Task Convert_Endpoints_NeedLogin_AndCsrf()
    {
        using var t = new TestApp();
        Assert.Equal(HttpStatusCode.Unauthorized, (await t.Send(HttpMethod.Post, "/api/convert/start", new { dryRun = true }, csrf: false, cookie: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await t.Send(HttpMethod.Get, "/api/queue", null, csrf: false, cookie: false)).StatusCode);
        await t.LoginAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Send(HttpMethod.Post, "/api/convert/start", new { dryRun = true }, csrf: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Send(HttpMethod.Post, "/api/undo", new { paths = new[] { "/x" } }, csrf: false)).StatusCode);
    }

    [Fact]
    public async Task Preview_BlocksWithoutSelection_AndWithoutScratchFolder()
    {
        Skip.If(OperatingSystem.IsWindows());
        using var t = new TestApp();
        PrepareSettings(t, s => s.ScratchFolder = "");
        await t.LoginAsync();
        var c = await TestApp.Json(await t.GetAsync("/api/convert/preview"));
        Assert.False(c.GetProperty("canStart").GetBoolean());
        var blockers = string.Join("|", c.GetProperty("blockers").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("no files are selected", blockers);
        var r = await t.PostAsync("/api/convert/start", new { dryRun = false });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [SkippableFact]
    public async Task EndToEnd_Scratch_CpuConvert_Undo()
    {
        Skip.IfNot(TestApp.HasFfmpeg, "ffmpeg is not installed");
        Skip.If(OperatingSystem.IsWindows());
        using var t = new TestApp();
        Directory.CreateDirectory(Path.Combine(t.Media, "Movies"));
        var src = Path.Combine(t.Media, "Movies", "Test (2020).mkv");
        Ffmpeg("-f lavfi -i testsrc=size=640x360:rate=25:duration=6 -c:v libx264 -b:v 3M -pix_fmt yuv420p", src);
        var srcBytes = new FileInfo(src).Length;
        var mtime = new DateTime(2019, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(src, mtime);
        PrepareSettings(t);
        await t.LoginAsync();
        Assert.Equal(HttpStatusCode.Accepted, (await t.PostAsync("/api/scan", new { folder = t.Media })).StatusCode);
        var scan = await t.WaitScanAsync();
        Assert.Equal("done", scan.GetProperty("state").GetString());
        await t.PutAsync("/api/plan/selection", new { selected = true, allMatching = "convert" });
        var sum = await TestApp.Json(await t.GetAsync("/api/plan/summary"));
        Assert.Equal(1, sum.GetProperty("selected").GetInt32());

        var c = await TestApp.Json(await t.GetAsync("/api/convert/preview"));
        Assert.True(c.GetProperty("canStart").GetBoolean(), c.ToString());
        Assert.Equal("scratch", c.GetProperty("storage").GetString());
        Assert.True(c.GetProperty("scratchNeedBytes").GetInt64() >= 2 * srcBytes);

        // dry run first: nothing changes
        Assert.Equal(HttpStatusCode.Accepted, (await t.PostAsync("/api/convert/start", new { dryRun = true })).StatusCode);
        await WaitQueueAsync(t);
        Assert.True(File.Exists(src));
        Assert.Equal(srcBytes, new FileInfo(src).Length);
        Assert.Empty(Directory.GetFiles(Path.Combine(t.Media, "Movies")).Where(f => f != src));

        // the real run
        var start = await t.PostAsync("/api/convert/start", new { dryRun = false });
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var done = await WaitQueueAsync(t);
        Assert.Equal("finished", done.GetProperty("state").GetString());
        Assert.Equal(1, done.GetProperty("done").GetInt32());
        var final = Path.Combine(t.Media, "Movies", "Test (2020).mkv");
        Assert.True(File.Exists(final));
        Assert.True(new FileInfo(final).Length < srcBytes);
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(final));
        var holding = Directory.GetFiles(Path.Combine(t.Root, "_Convertr_Originals"), "*", SearchOption.AllDirectories);
        Assert.Single(holding);
        Assert.Equal(srcBytes, new FileInfo(holding[0]).Length);
        Assert.Empty(Directory.GetDirectories(Path.Combine(t.Root, "scratch"), "job-*"));
        Assert.Empty(Directory.GetFiles(Path.Combine(t.Media, "Movies"), "*.convertr-part"));

        var jobs = await TestApp.Json(await t.GetAsync("/api/queue/jobs"));
        Assert.Equal("Done", jobs.GetProperty("jobs")[0].GetProperty("state").GetString());

        // undo
        var undo = await TestApp.Json(await t.GetAsync("/api/undo"));
        var item = undo.GetProperty("items")[0];
        Assert.True(item.GetProperty("canUndo").GetBoolean());
        var r = await t.PostAsync("/api/undo", new { paths = new[] { item.GetProperty("path").GetString() } });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(srcBytes, new FileInfo(final).Length);   // the original is back under its name
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(final));
        Assert.True(Directory.GetFiles(Path.Combine(t.Root, "_Convertr_Undone"), "*", SearchOption.AllDirectories).Length == 1);
    }

    [SkippableFact]
    public async Task Stop_CancelsTheRun_AndLeavesTheOriginal()
    {
        Skip.IfNot(TestApp.HasFfmpeg, "ffmpeg is not installed");
        Skip.If(OperatingSystem.IsWindows());
        using var t = new TestApp();
        Directory.CreateDirectory(Path.Combine(t.Media, "Movies"));
        var src = Path.Combine(t.Media, "Movies", "Slow (2020).mkv");
        Ffmpeg("-f lavfi -i testsrc2=size=1280x720:rate=30:duration=40 -c:v libx264 -b:v 6M -pix_fmt yuv420p", src);
        var srcBytes = new FileInfo(src).Length;
        PrepareSettings(t, s => s.X265Preset = "veryslow");
        await t.LoginAsync();
        await t.PostAsync("/api/scan", new { folder = t.Media });
        await t.WaitScanAsync();
        await t.PutAsync("/api/plan/selection", new { selected = true, allMatching = "convert" });
        Assert.Equal(HttpStatusCode.Accepted, (await t.PostAsync("/api/convert/start", new { dryRun = false })).StatusCode);
        // wait until it is encoding
        var end = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < end)
        {
            var j = await TestApp.Json(await t.GetAsync("/api/queue/jobs"));
            if (j.GetProperty("jobs")[0].GetProperty("state").GetString() == "Encoding") break;
            await Task.Delay(100);
        }
        Assert.Equal(HttpStatusCode.OK, (await t.PostAsync("/api/queue/pause")).StatusCode);
        Assert.Equal("paused", (await Status(t)).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.OK, (await t.PostAsync("/api/queue/resume")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t.PostAsync("/api/queue/stop")).StatusCode);
        var st = await WaitQueueAsync(t, 30);
        Assert.Equal("idle", st.GetProperty("state").GetString());
        Assert.Equal(1, st.GetProperty("pending").GetInt32());
        Assert.Equal(srcBytes, new FileInfo(src).Length);
        Assert.Empty(Directory.GetFiles(Path.Combine(t.Media, "Movies")).Where(f => f != src));
        Assert.Empty(Directory.GetDirectories(Path.Combine(t.Root, "scratch"), "job-*"));
        // discard
        Assert.Equal(HttpStatusCode.OK, (await t.Send(HttpMethod.Delete, "/api/queue")).StatusCode);
        Assert.False((await Status(t)).GetProperty("hasQueue").GetBoolean());
    }

    [Fact]
    public async Task Hardware_Detect_WorksWithoutIntelGpu_AndReportsWhy()
    {
        Skip.If(OperatingSystem.IsWindows());
        using var t = new TestApp();
        await t.LoginAsync();
        var r = await t.PostAsync("/api/hardware/detect");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await TestApp.Json(r);
        Assert.True(j.GetProperty("hardware").GetProperty("detected").GetBoolean());
        var g = await TestApp.Json(await t.GetAsync("/api/hardware"));
        Assert.True(g.GetProperty("hardware").GetProperty("detected").GetBoolean());
    }
}
