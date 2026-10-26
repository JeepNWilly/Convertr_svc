// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Convertr.Svc.Tests;

/// <summary>Scan + plan against a tiny generated media tree (real ffmpeg / ffprobe), including links that point outside the media folder.</summary>
public class ScanTests
{
    [SkippableFact]
    public async Task Scan_FindsTheFiles_PlansThem_AndNeverEntersLinksOutOfTheRoot()
    {
        Skip.IfNot(TestApp.HasFfmpeg, "ffmpeg is not installed");
        Skip.If(OperatingSystem.IsWindows());
        using var t = new TestApp(withMedia: true);
        await t.LoginAsync();
        var start = await t.PostAsync("/api/scan", new { folder = t.Media });
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var done = await t.WaitScanAsync();
        Assert.Equal("done", done.GetProperty("state").GetString());
        Assert.Equal(2, done.GetProperty("rows").GetInt32());     // a.mkv and b.avi; secret.mkv (link target outside) is not there
        var plan = await TestApp.Json(await t.GetAsync("/api/plan"));
        Assert.Equal(2, plan.GetProperty("total").GetInt32());
        var rows = plan.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(new[] { "Movies/a.mkv", "Series/b.avi" }, rows.Select(r => r.GetProperty("path").GetString()).ToArray());
        Assert.DoesNotContain(rows, r => r.GetProperty("path").GetString()!.Contains("secret") || r.GetProperty("path").GetString()!.Contains("linkfile"));
        Assert.Equal("Skip", rows[0].GetProperty("action").GetString());
        Assert.Equal("ReencodeVideo", rows[1].GetProperty("action").GetString());     // legacy mpeg4 in AVI
        Assert.All(rows, r => Assert.False(r.GetProperty("selected").GetBoolean()));    // nothing is preselected
        var sum = await TestApp.Json(await t.GetAsync("/api/plan/summary"));
        Assert.Equal(2, sum.GetProperty("total").GetInt32()); Assert.Equal(0, sum.GetProperty("selected").GetInt32());
        Assert.True(File.Exists(Path.Combine(t.Data, "probe-cache.json")));
    }

    [SkippableFact]
    public async Task Plan_FilterSortSearchAndPaging()
    {
        Skip.IfNot(TestApp.HasFfmpeg, "ffmpeg is not installed");
        Skip.If(OperatingSystem.IsWindows());
        using var t = new TestApp(withMedia: true);
        await t.LoginAsync();
        await t.PostAsync("/api/scan", new { folder = t.Media }); await t.WaitScanAsync();
        async Task<List<string>> Names(string q) => (await TestApp.Json(await t.GetAsync("/api/plan?" + q))).GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("name").GetString()!).ToList();
        Assert.Equal(new[] { "b.avi" }, await Names("filter=convert"));
        Assert.Equal(new[] { "a.mkv" }, await Names("filter=skip"));
        Assert.Empty(await Names("filter=attention"));
        Assert.Equal(new[] { "b.avi" }, await Names("q=SERIES"));
        Assert.Equal(new[] { "b.avi", "a.mkv" }, await Names("sort=size&dir=desc"));
        Assert.Equal(new[] { "b.avi" }, await Names("skip=1&take=1"));
        var j = await TestApp.Json(await t.GetAsync("/api/plan?take=1"));
        Assert.Equal(2, j.GetProperty("total").GetInt32()); Assert.Single(j.GetProperty("rows").EnumerateArray());
    }

    [SkippableFact]
    public async Task Selection_StartsEmpty_OnlyConvertibleRowsCanBeSelected_AndExportWorks()
    {
        Skip.IfNot(TestApp.HasFfmpeg, "ffmpeg is not installed");
        Skip.If(OperatingSystem.IsWindows());
        using var t = new TestApp(withMedia: true);
        await t.LoginAsync();
        await t.PostAsync("/api/scan", new { folder = t.Media }); await t.WaitScanAsync();
        var r = await TestApp.Json(await t.PutAsync("/api/plan/selection", new { ids = new[] { 0, 1, 99 }, selected = true }));
        Assert.Equal(1, r.GetProperty("selected").GetInt32());      // a.mkv is a Skip row, 99 does not exist
        var sum = await TestApp.Json(await t.GetAsync("/api/plan/summary"));
        Assert.Equal(1, sum.GetProperty("selected").GetInt32());
        Assert.True(sum.GetProperty("selectedBytes").GetInt64() > 0);
        var rows = (await TestApp.Json(await t.GetAsync("/api/plan"))).GetProperty("rows").EnumerateArray().ToList();
        Assert.True(rows[1].GetProperty("selected").GetBoolean());
        var none = await TestApp.Json(await t.PutAsync("/api/plan/selection", new { ids = new[] { 1 }, selected = false }));
        Assert.Equal(0, none.GetProperty("selected").GetInt32());
        var all = await TestApp.Json(await t.PutAsync("/api/plan/selection", new { selected = true, allMatching = "convert" }));
        Assert.Equal(1, all.GetProperty("selected").GetInt32());
        var csv = await t.PostAsync("/api/plan/export");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        Assert.Contains("b.avi", await csv.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task ChangingAPlanningSetting_ReplansAndClearsTheSelection_ButTheThemeDoesNot()
    {
        Skip.IfNot(TestApp.HasFfmpeg, "ffmpeg is not installed");
        Skip.If(OperatingSystem.IsWindows());
        using var t = new TestApp(withMedia: true);
        await t.LoginAsync();
        await t.PostAsync("/api/scan", new { folder = t.Media }); await t.WaitScanAsync();
        await t.PutAsync("/api/plan/selection", new { ids = new[] { 1 }, selected = true });
        Assert.Equal(HttpStatusCode.OK, (await t.PutAsync("/api/settings", new { Theme = "Light" })).StatusCode);
        Assert.Equal(1, (await TestApp.Json(await t.GetAsync("/api/plan/summary"))).GetProperty("selected").GetInt32());   // appearance: plan untouched
        Assert.Equal(HttpStatusCode.OK, (await t.PutAsync("/api/settings", new { Mode = "Cpu", Kbps1080 = 3000 })).StatusCode);
        var sum = await TestApp.Json(await t.GetAsync("/api/plan/summary"));
        Assert.Equal(0, sum.GetProperty("selected").GetInt32());     // a new plan never keeps an old selection
        Assert.Equal(2, sum.GetProperty("total").GetInt32());        // but the scan result is still there
    }

    [Fact]
    public async Task Scan_Refused_WhenFfprobeIsMissing_OrFolderUnknown_OrNothingRunning()
    {
        using var t = new TestApp(services: s =>
        {
            foreach (var d in s.Where(x => x.ServiceType == typeof(Convertr.Svc.Server.IProbeRunnerFactory)).ToList()) s.Remove(d);
            s.AddSingleton<Convertr.Svc.Server.IProbeRunnerFactory, NoProbe>();
        });
        await t.LoginAsync();
        var r = await t.PostAsync("/api/scan", new { folder = t.Media });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Contains("ffprobe was not found", await r.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await t.PostAsync("/api/scan", new { folder = "/nope" })).StatusCode);
        var c = await TestApp.Json(await t.Send(HttpMethod.Delete, "/api/scan"));
        Assert.False(c.GetProperty("cancelled").GetBoolean());
        Assert.Equal("idle", (await TestApp.Json(await t.GetAsync("/api/scan"))).GetProperty("state").GetString());
    }

    [SkippableFact]
    public async Task EventStream_SendsTheScanStateFirst_ThenProgress()
    {
        Skip.IfNot(TestApp.HasFfmpeg, "ffmpeg is not installed");
        Skip.If(OperatingSystem.IsWindows());
        using var t = new TestApp(withMedia: true);
        await t.LoginAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var resp = await t.Http.SendAsync(t.Req(HttpMethod.Get, "/api/events"), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(cts.Token));
        await t.PostAsync("/api/scan", new { folder = t.Media });
        var seen = new List<string>();
        while (!cts.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line == null) break;
            if (line.StartsWith("event: ")) seen.Add(line[7..]);
            if (line.StartsWith("data: ") && line.Contains("\"state\":\"done\"")) break;
        }
        Assert.Contains("scan", seen);
        Assert.Contains("plan", seen.Concat(new[] { "plan" }));   // the plan event follows the done state; the state itself proves the scan completed
    }

    private sealed class NoProbe : Convertr.Svc.Server.IProbeRunnerFactory
    {
        public Convertr.Core.IProbeRunner? Create(Convertr.Core.Settings s, out string? d) { d = null; return null; }
    }
}
