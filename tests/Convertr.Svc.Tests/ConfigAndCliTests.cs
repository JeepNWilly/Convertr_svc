// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using Convertr.Svc;
using Convertr.Svc.Server;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Convertr.Svc.Tests;

public class ConfigAndCliTests
{
    [Fact]
    public void Cli_ParsesOptions_AndRejectsUnknownOnes()
    {
        var c = Cli.Parse(new[] { "--data-dir", "/d", "--config-dir", "/c", "--port", "9000", "--listen", "0.0.0.0", "--allowed-root", "/a", "--allowed-root", "/b" });
        Assert.Null(c.Error); Assert.Equal("/d", c.DataDir); Assert.Equal("/c", c.ConfigDir); Assert.Equal("9000", c.Port); Assert.Equal(new[] { "/a", "/b" }, c.AllowedRoots);
        Assert.True(Cli.Parse(new[] { "--reset-password" }).ResetPassword);
        Assert.NotNull(Cli.Parse(new[] { "--bogus" }).Error);
        Assert.NotNull(Cli.Parse(new[] { "--port" }).Error);
        Assert.Null(Cli.Parse(new[] { "--contentRoot=/x", "--applicationName=y" }).Error);   // host options from the test host
    }

    [Fact]
    public void Defaults_AreTheServiceLayout_AndLoopbackOnly()
    {
        var o = ServerOptions.From(new ConfigurationBuilder().Build());
        Assert.Equal("/etc/convertr-svc", o.ConfigDir); Assert.Equal("/var/lib/convertr-svc", o.DataDir);
        Assert.Equal("127.0.0.1", o.Listen); Assert.Equal(8484, o.Port); Assert.Empty(o.AllowedRoots);
        Assert.Equal(10, o.MinPasswordLength);
    }

    [Fact]
    public void ConfigFile_ThenEnvironment_ThenCommandLine_LaterWins()
    {
        var dir = Path.Combine(Path.GetTempPath(), "convertr-cfg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "appsettings.json"), """{"Convertr":{"Port":9100,"Listen":"10.0.0.5","AllowedRoots":["/mnt/nas/a","/mnt/nas/b"],"DataDir":"/data/x"}}""");
        try
        {
            var fromFile = ServerOptions.From(ServerHost.LoadConfiguration(Cli.Parse(new[] { "--config-dir", dir }), Array.Empty<string>()));
            Assert.Equal(9100, fromFile.Port); Assert.Equal("10.0.0.5", fromFile.Listen); Assert.Equal(new[] { "/mnt/nas/a", "/mnt/nas/b" }, fromFile.AllowedRoots); Assert.Equal("/data/x", fromFile.DataDir);
            var cli = ServerOptions.From(ServerHost.LoadConfiguration(Cli.Parse(new[] { "--config-dir", dir, "--port", "9200", "--data-dir", "/tmp/y", "--allowed-root", "/only" }), Array.Empty<string>()));
            Assert.Equal(9200, cli.Port); Assert.Equal("/tmp/y", cli.DataDir); Assert.Equal(new[] { "/only" }, cli.AllowedRoots);   // an explicit root list replaces the file's list
            Assert.Equal("10.0.0.5", cli.Listen);
        }
        finally { Directory.Delete(dir, true); }
    }
}
