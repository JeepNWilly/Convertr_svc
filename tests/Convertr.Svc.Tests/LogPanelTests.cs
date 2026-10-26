// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Convertr.Core;
using Convertr.Svc.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Convertr.Svc.Tests;

/// <summary>Queue page > Log: the newest run log (dry-run ffmpeg commands, per-job lines) is readable in the UI, arrives by SSE, lives under DataDir/logs and goes to the journal at Info.</summary>
public class LogPanelTests
{
    private sealed class CaptureProvider : ILoggerProvider
    {
        public readonly ConcurrentQueue<(LogLevel Level, string Category, string Text)> Entries = new();
        public ILogger CreateLogger(string categoryName) => new L(this, categoryName);
        public void Dispose() { }
        private sealed class L(CaptureProvider p, string c) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel l) => true;
            public void Log<TState>(LogLevel l, EventId e, TState s, Exception? x, Func<TState, Exception?, string> f) => p.Entries.Enqueue((l, c, f(s, x)));
        }
    }

    private static async Task<JsonElement> WaitQueueAsync(TestApp t, int seconds = 60)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            var j = await TestApp.Json(await t.GetAsync("/api/queue"));
            if (j.GetProperty("state").GetString() is "finished" or "idle") return j;
            await Task.Delay(100);
        }
        throw new TimeoutException();
    }

    [Fact]
    public async Task LogEndpoint_NeedsLogin_AndIsEmptyBeforeTheFirstRun()
    {
        using var t = new TestApp();
        Assert.Equal(HttpStatusCode.Unauthorized, (await t.Send(HttpMethod.Get, "/api/queue/log", null, csrf: false, cookie: false)).StatusCode);
        await t.LoginAsync();
        var r = await TestApp.Json(await t.GetAsync("/api/queue/log"));
        Assert.Equal(0, r.GetProperty("lines").GetArrayLength());
        Assert.Equal("", r.GetProperty("file").GetString());
        Assert.Equal(Path.Combine(t.Data, "logs"), r.GetProperty("logsDir").GetString());
    }

    [Fact]
    public async Task LogEndpoint_ShowsTheTailOfTheNewestLogFile()
    {
        using var t = new TestApp();
        var logs = Path.Combine(t.Data, "logs"); Directory.CreateDirectory(logs);
        File.WriteAllLines(Path.Combine(logs, "Convertr_20260101_0900.log"), new[] { "old run" });
        File.WriteAllLines(Path.Combine(logs, "Convertr_20260102_1000.log"), Enumerable.Range(1, 50).Select(i => "line " + i));
        File.SetLastWriteTimeUtc(Path.Combine(logs, "Convertr_20260101_0900.log"), new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(logs, "Convertr_20260102_1000.log"), new DateTime(2026, 1, 2, 10, 0, 0, DateTimeKind.Utc));
        File.WriteAllLines(Path.Combine(logs, "undo_20260103_1100.csv"), new[] { "not a run log" });
        await t.LoginAsync();
        var r = await TestApp.Json(await t.GetAsync("/api/queue/log?lines=5"));
        Assert.Equal("Convertr_20260102_1000.log", r.GetProperty("file").GetString());
        var lines = r.GetProperty("lines").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Equal(new[] { "line 46", "line 47", "line 48", "line 49", "line 50" }, lines);
        var all = await TestApp.Json(await t.GetAsync("/api/queue/log?lines=999999"));
        Assert.Equal(50, all.GetProperty("lines").GetArrayLength());   // clamped, never more than the file has
    }

    [SkippableFact]
    public async Task DryRun_WritesTheFfmpegCommands_ToTheFile_TheLogEndpoint_TheSseHub_AndTheJournal()
    {
        Skip.IfNot(TestApp.HasFfmpeg, "ffmpeg is not installed");
        Skip.If(OperatingSystem.IsWindows());
        var cap = new CaptureProvider();
        using var t = new TestApp(services: sv => sv.AddSingleton<ILoggerProvider>(cap));
        Directory.CreateDirectory(Path.Combine(t.Media, "Movies"));
        var src = Path.Combine(t.Media, "Movies", "Test (2020).mkv");
        var psi = new ProcessStartInfo("ffmpeg", "-v error -y -f lavfi -i testsrc=size=640x360:rate=25:duration=3 -c:v libx264 -b:v 3M -pix_fmt yuv420p \"" + src + "\"") { UseShellExecute = false };
        using (var p = Process.Start(psi)!) { p.WaitForExit(); Assert.Equal(0, p.ExitCode); }
        SettingsStore.Save(new Settings
        {
            Mode = EncoderMode.Cpu, X265Preset = "ultrafast", KeepIfSmallerPercent = 0, KeepIfSmallerMb = 0, UseScratch = true,
            ScratchFolder = Path.Combine(t.Root, "scratch"), Kbps1080 = 100, Kbps720 = 100, KbpsSd = 100, KbpsBelowSd = 100,
            MediaFolder = t.Media, Container = OutputContainer.Mkv, ParallelCpuJobs = 1,
        }, Path.Combine(t.Data, "settings.json"));
        await t.LoginAsync();
        await t.PostAsync("/api/scan", new { folder = t.Media });
        await t.WaitScanAsync();
        await t.PutAsync("/api/plan/selection", new { selected = true, allMatching = "convert" });

        var reader = t.Get<EventHub>().Subscribe(out var unsub);
        Assert.Equal(HttpStatusCode.Accepted, (await t.PostAsync("/api/convert/start", new { dryRun = true })).StatusCode);
        await WaitQueueAsync(t);

        // 1. the file under DataDir/logs (survives a restart)
        var files = Directory.GetFiles(Path.Combine(t.Data, "logs"), "Convertr_*.log");
        Assert.Single(files);
        var text = File.ReadAllText(files[0]);
        Assert.Contains("[dry run] Movies/Test (2020).mkv", text.Replace('\\', '/'));
        Assert.Contains("ffmpeg", text); Assert.Contains("libx265", text);
        Assert.Contains("dry run finished: no file was touched", text);

        // 2. the endpoint behind the Log panel
        var r = await TestApp.Json(await t.GetAsync("/api/queue/log"));
        Assert.Equal(files[0], r.GetProperty("path").GetString());
        var lines = r.GetProperty("lines").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Contains(lines, l => l.Contains("[dry run]") && l.Contains("libx265"));

        // 3. the live "log" events (SSE)
        var events = new List<string>();
        while (reader.TryRead(out var m)) if (m.Type == "log") events.Add(JsonDocument.Parse(m.Json).RootElement.GetProperty("line").GetString()!);
        unsub();
        Assert.Contains(events, l => l.Contains("[dry run]") && l.Contains("libx265"));
        Assert.Contains(events, l => l.Contains("dry run finished"));

        // 4. the journal (Info)
        Assert.Contains(cap.Entries, e => e.Level == LogLevel.Information && e.Category.EndsWith("JobService") && e.Text.Contains("[dry run]") && e.Text.Contains("libx265"));
        Assert.True(File.Exists(src));   // a dry run changes nothing
    }

    [Fact]
    public async Task QueuePage_HasTheLogPanel_AndTheScriptFollowsTheLogEvent()
    {
        using var t = new TestApp();
        string Page(string url) => t.Send(HttpMethod.Get, url, csrf: false, cookie: false).Result.Content.ReadAsStringAsync().Result;
        var html = Page("/");
        Assert.Contains("id=\"q-log-box\"", html); Assert.Contains("id=\"q-log\"", html); Assert.Contains("id=\"q-log-file\"", html);
        Assert.Contains("dry run", html);
        var q = Page("/js/queue.js");
        Assert.Contains("/api/queue/log", q); Assert.Contains("type === 'log'", q);
        Assert.Contains("'log'", Page("/js/app.js"));      // subscribed to the SSE event
        await Task.CompletedTask;
    }
}
