// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using Convertr.Core;
using Xunit;

namespace Convertr.Svc.Tests;

/// <summary>Proves the shared Convertr.Core is linked and behaves as the Linux service expects. The full shared suite is run by scripts/test-core.sh.</summary>
public class SmokeTests
{
    [Fact]
    public void SharedCore_IsLinked_AndPlatformIsChosenByOs()
    {
        var p = PlatformServices.Current;
        Assert.Equal(OperatingSystem.IsWindows() ? "Windows" : "Linux", p.Name);
        Assert.Equal(!OperatingSystem.IsWindows(), p.PathsCaseSensitive);
        Assert.False(string.IsNullOrWhiteSpace(Convertr.Svc.Program.Version));
    }

    [Fact]
    public void LinuxDefaults_AreTheServiceLayout()
    {
        var p = new LinuxPlatform();
        Assert.Equal(Path.Combine("/var/lib/convertr-svc", "queue.json"), new LinuxAppPaths("/var/lib/convertr-svc").QueuePath);
        Assert.Equal(GpuBackend.Nvenc, new Settings().GpuBackend);   // service-specific defaults are applied by the service host (S1+), the shared default stays the desktop one
        Assert.True(p.PathsCaseSensitive);
    }

    [Fact]
    public void Planner_AndCommandBuilder_WorkOnLinux()
    {
        var e = new ScanEntry
        {
            Library = "Movies", RelativePath = "a.mkv", FullPath = "/mnt/nas/media/a.mkv", SizeBytes = 4_000_000_000,
            Probe = new ProbeInfo { FormatName = "matroska,webm", DurationSec = 3600, Video = new VideoInfo { Codec = "h264", Width = 1920, Height = 1080, BitDepth = 8, Kbps = 8000, KbpsSource = "stream" }, Audio = { new AudioInfo { Language = "eng", Codec = "aac", Channels = 2 } } },
        };
        var s = new Settings { Mode = EncoderMode.Cpu };
        var plan = Planner.Plan(e, s.ToPlannerOptions());
        Assert.True(plan.WritesFile);
        var cmd = FfmpegCommandBuilder.Build(e, plan, s, "/mnt/nas/media/a.mp4.convertr-part");
        Assert.Contains("libx265", cmd.Args);
    }
}
