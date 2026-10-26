// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Diagnostics;
using Xunit;

namespace Convertr.Svc.Tests;

/// <summary>The drop-in paths.conf that install.sh writes: RequiresMountsFor= belongs in [Unit] (in [Service] systemd logs "Unknown key name"), and an old wrong file is repaired on upgrade.</summary>
public class InstallerTests
{
    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "VERSION"))) d = d.Parent;
        Assert.NotNull(d);
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

    // the two helper functions are defined in install.sh; load only them
    private static string Funcs() => "source <(sed -n '/^render_paths_conf()/,/^}/p;/^paths_conf_is_old()/,/^}/p' \"" + Path.Combine(Repo(), "scripts/install.sh") + "\");";

    [Fact]
    public void PathsConf_PutsRequiresMountsFor_InUnit_AndReadWritePaths_InService()
    {
        Skip.If(OperatingSystem.IsWindows());
        var (code, o) = Bash(Funcs() + " render_paths_conf '/a /b'");
        Assert.Equal(0, code);
        Assert.Equal("[Unit]\nRequiresMountsFor=/a /b\n\n[Service]\nReadWritePaths=/a /b\n", o);
        var lines = o.Split('\n');
        Assert.True(Array.IndexOf(lines, "RequiresMountsFor=/a /b") < Array.IndexOf(lines, "[Service]"));
    }

    [Fact]
    public void PathsConf_OldWrongFile_IsDetected_NewFileIsNot()
    {
        Skip.If(OperatingSystem.IsWindows());
        var d = Path.Combine(Path.GetTempPath(), "convertr-inst-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d);
        try
        {
            File.WriteAllText(Path.Combine(d, "old.conf"), "[Service]\nReadWritePaths=/x\nRequiresMountsFor=/x\n");
            File.WriteAllText(Path.Combine(d, "new.conf"), "[Unit]\nRequiresMountsFor=/x\n\n[Service]\nReadWritePaths=/x\n");
            Assert.Equal(0, Bash(Funcs() + $" paths_conf_is_old '{d}/old.conf'").Code);
            Assert.NotEqual(0, Bash(Funcs() + $" paths_conf_is_old '{d}/new.conf'").Code);
            Assert.NotEqual(0, Bash(Funcs() + $" paths_conf_is_old '{d}/missing.conf'").Code);
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void Installer_WritesPathsConfThroughTheHelper_AndRepairsAnOldFileOnUpgrade()
    {
        var sh = File.ReadAllText(Path.Combine(Repo(), "scripts/install.sh"));
        Assert.Contains("render_paths_conf \"${rw# }\" > \"$DROPIN_DIR/paths.conf\"", sh);
        Assert.Contains("elif paths_conf_is_old \"$DROPIN_DIR/paths.conf\"", sh);          // upgrade without --media-root repairs it too
        Assert.DoesNotContain(">> \"$DROPIN_DIR/paths.conf\"", sh);                          // the old append under [Service]
        Assert.Contains("bash", Bash("bash -n '" + Path.Combine(Repo(), "scripts/install.sh") + "' && echo bash").Out);
    }
}
