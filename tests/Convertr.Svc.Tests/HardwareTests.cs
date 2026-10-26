// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using Convertr.Core;
using Convertr.Svc.Server;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Convertr.Svc.Tests;

/// <summary>The Intel encoder test explains a failure with ffmpeg's own words; the systemd unit allows the GPU device class.</summary>
public class HardwareTests
{
    private sealed class FailingRunner : IIntelProcessRunner
    {
        public Task<(int ExitCode, string Stderr)> RunAsync(string exe, IReadOnlyList<string> args, IReadOnlyList<string> envPairs, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult((234, string.Join("\n", Enumerable.Range(1, 8).Select(i => i == 1 ? "[AVHWDeviceContext @ 0x1] No VA display found for device /dev/dri/renderD128." : "detail line " + i))));
    }

    [SkippableFact]
    public async Task IntelTestFailure_ShowsTheFfmpegStderrHead_InProblemAndLog()
    {
        Skip.If(OperatingSystem.IsWindows());
        Skip.IfNot(TestApp.HasFfmpeg, "ffmpeg is not installed");
        using var t = new TestApp(services: s =>
        {
            foreach (var d in s.Where(x => x.ServiceType == typeof(IIntelProcessRunner)).ToList()) s.Remove(d);
            s.AddSingleton<IIntelProcessRunner, FailingRunner>();
        });
        await t.LoginAsync();
        t.Get<SettingsService>().Mutate(x => x.GpuDevice = "/dev/dri/renderD128");
        var r = await t.PostAsync("/api/hardware/detect");
        Assert.Equal(System.Net.HttpStatusCode.OK, r.StatusCode);
        var intel = (await TestApp.Json(r)).GetProperty("hardware").GetProperty("intel");
        var reason = intel.GetProperty("reason").GetString()!;
        Assert.Contains("ffmpeg said:", reason);
        Assert.Contains("No VA display found", reason);
        Assert.Contains("hevc_vaapi", reason);
        var log = string.Join("\n", intel.GetProperty("log").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("stderr (first 5 lines)", log);
        Assert.Contains("detail line 5", log);
        Assert.DoesNotContain("detail line 6", log);
    }

    [Fact]
    public void HeadLines_KeepsTheFirstLines_AndCutsLongOnes()
    {
        var h = HardwareService.HeadLines("\n a \n\n b\nc\nd\ne\nf\n" + new string('x', 400), 5);
        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, h);
        Assert.EndsWith("...", HardwareService.HeadLines(new string('x', 400))[0]);
        Assert.Empty(HardwareService.HeadLines(null));
    }

    private static string RepoFile(string rel)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "VERSION"))) d = d.Parent;
        Assert.NotNull(d);
        return File.ReadAllText(Path.Combine(d!.FullName, rel));
    }

    [Fact]
    public void SystemdUnit_AllowsTheDrmDeviceClass_NotTheDirectory()
    {
        var unit = RepoFile("deploy/convertr-svc.service");
        var lines = unit.Split('\n').Select(l => l.Trim()).ToList();
        Assert.Contains("DeviceAllow=char-drm rw", lines);
        Assert.Contains("DevicePolicy=closed", lines);
        Assert.Contains("PrivateDevices=false", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("DeviceAllow=/dev/dri"));
    }

    [Fact]
    public void Installer_RefreshesTheUnit_OnEveryRun_AndReloadsSystemd()
    {
        var sh = RepoFile("scripts/install.sh");
        Assert.Contains("install -m 0644 \"$HERE/deploy/convertr-svc.service\" \"$UNIT\"", sh);
        Assert.Contains("systemctl daemon-reload", sh);
    }
}
