// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using Convertr.Core;
using Microsoft.AspNetCore.Builder;

namespace Convertr.Svc.Server;

public static class ServerHost
{
    /// <summary>Configuration order (later wins): appsettings.json in the config dir, environment (CONVERTR_*), command line.</summary>
    public static IConfigurationRoot LoadConfiguration(Cli cli, string[] args)
    {
        Func<string, string?> env = Environment.GetEnvironmentVariable;
        var configDir = cli.ConfigDir ?? env("CONVERTR_CONFIG_DIR") ?? ServerOptions.DefaultConfigDir;
        var b = new ConfigurationBuilder();
        var file = Path.Combine(configDir, "appsettings.json");
        if (File.Exists(file)) b.AddJsonFile(file, optional: true, reloadOnChange: false);
        var m = new Dictionary<string, string?> { ["Convertr:ConfigDir"] = configDir };
        void Set(string key, string? envName, string? cliValue)
        {
            var v = cliValue ?? (envName != null ? env(envName) : null);
            if (!string.IsNullOrWhiteSpace(v)) m["Convertr:" + key] = v;
        }
        Set("DataDir", "CONVERTR_DATA_DIR", cli.DataDir);
        Set("Listen", "CONVERTR_LISTEN", cli.Listen);
        Set("Port", "CONVERTR_PORT", cli.Port);
        var roots = cli.AllowedRoots.Count > 0 ? cli.AllowedRoots
            : (env("CONVERTR_ALLOWED_ROOTS") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (roots.Count > 0)
        {
            // an explicit list (command line / environment) replaces the one from the file
            for (var i = 0; i < roots.Count; i++) m[$"Convertr:AllowedRootsOverride:{i}"] = roots[i];
        }
        b.AddInMemoryCollection(m);
        return b.Build();
    }

    public static WebApplication Build(Cli cli, string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
        builder.Configuration.AddConfiguration(LoadConfiguration(cli, args));
        builder.Host.UseSystemd();
        builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = null; });

        // the listen address is needed now (Kestrel); everything else is read lazily so tests can override it
        var early = ServerOptions.From(builder.Configuration);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            var ip = System.Net.IPAddress.TryParse(early.Listen, out var a) ? a : early.Listen is "*" or "0.0.0.0" ? System.Net.IPAddress.Any : System.Net.IPAddress.Loopback;
            k.Listen(ip, early.Port);
        });
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");

        var s = builder.Services;
        s.AddSingleton(sp => ServerOptions.From(sp.GetRequiredService<IConfiguration>()));
        s.AddSingleton<RootGuard>();
        s.AddSingleton<AuthService>();
        s.AddSingleton<EventHub>();
        s.AddSingleton<SettingsService>();
        s.AddSingleton<IProbeRunnerFactory, DefaultProbeRunnerFactory>();
        s.AddSingleton<ScanService>();
        s.AddSingleton<IIntelProcessRunner, IntelProcessRunner>();
        s.AddSingleton<HardwareService>();
        s.AddSingleton<IEngineBackend, DefaultEngineBackend>();
        s.AddSingleton<JobService>();
        s.AddSingleton<INasEnvironment, SystemNasEnvironment>();
        s.AddSingleton<NasService>();
        s.AddHostedService<StartupBanner>();

        var app = builder.Build();
        Pipeline.Configure(app);
        return app;
    }
}

/// <summary>Logs the listen address, folders and (when no password exists yet) the one-time setup token.</summary>
public sealed class StartupBanner : IHostedService
{
    private readonly ServerOptions _o; private readonly AuthService _auth; private readonly RootGuard _guard; private readonly ILogger<StartupBanner> _log;
    public StartupBanner(ServerOptions o, AuthService a, RootGuard g, ILogger<StartupBanner> log) { _o = o; _auth = a; _guard = g; _log = log; }

    public Task StartAsync(CancellationToken ct)
    {
        _log.LogInformation("Convertr_svc {Version} (Core {Core}) listening on {Listen}:{Port}; data folder {Data}", Program.Version, typeof(Settings).Assembly.GetName().Version, _o.Listen, _o.Port, _o.DataDir);
        if (_guard.Roots.Count == 0) _log.LogWarning("No AllowedRoots are configured (or none exist): the folder browser and scans are disabled. Edit {Dir}/appsettings.json.", _o.ConfigDir);
        else _log.LogInformation("Allowed folders: {Roots}", string.Join(", ", _guard.Roots));
        foreach (var sk in _guard.Skipped) _log.LogWarning("Ignored allowed root: {Root}", sk);
        if (!IsLoopback(_o.Listen)) _log.LogWarning("Listening on {Listen}: plain HTTP for the home LAN only. Do not expose this service to the internet; the login is protected, but the traffic is not encrypted.", _o.Listen);
        var token = _auth.NewSetupTokenForStartup();
        if (token != null)
            _log.LogWarning("FIRST-RUN SETUP: open the web page and enter this one-time setup token to choose the admin password: {Token}   (valid 24 hours; a new one is printed at every start until a password exists)", token);
        return Task.CompletedTask;
    }

    private static bool IsLoopback(string l) => l is "127.0.0.1" or "::1" or "localhost";
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
