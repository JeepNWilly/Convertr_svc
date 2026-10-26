// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Reflection;
using Convertr.Core;
using Convertr.Svc.Server;

namespace Convertr.Svc;

public sealed class Program
{
    public static string Version => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split(' ')[0] ?? "0.0.0";

    public const string Usage = """
        convertr-svc - Convertr web service

        Usage: convertr-svc [options]
          --config-dir <dir>     folder with appsettings.json        (env CONVERTR_CONFIG_DIR, default /etc/convertr-svc)
          --data-dir <dir>       settings, queue, cache, auth        (env CONVERTR_DATA_DIR,   default /var/lib/convertr-svc)
          --listen <address>     address to listen on                (env CONVERTR_LISTEN,     default 127.0.0.1)
          --port <number>        TCP port                            (env CONVERTR_PORT,       default 8484)
          --allowed-root <dir>   media root the web UI may use; repeatable   (env CONVERTR_ALLOWED_ROOTS, ':' separated)
          --reset-password       remove the admin password and print a new setup token, then exit
          --version              print the version and exit
          --help                 this text
        """;

    public static int Main(string[] args)
    {
        var cli = Cli.Parse(args);
        if (cli.Error != null) { Console.Error.WriteLine(cli.Error); Console.Error.WriteLine(Usage); return 2; }
        if (cli.Help) { Console.WriteLine(Usage); return 0; }
        if (cli.Version) { Console.WriteLine($"convertr-svc {Version} (Core {typeof(Settings).Assembly.GetName().Version})"); return 0; }
        if (cli.ResetPassword) return ResetPassword(cli);
        var app = ServerHost.Build(cli, args);
        app.Run();
        return 0;
    }

    private static int ResetPassword(Cli cli)
    {
        var cfg = ServerHost.LoadConfiguration(cli, Array.Empty<string>());
        var o = ServerOptions.From(cfg);
        try
        {
            var token = new AuthService(o).ResetPassword();
            Console.WriteLine("The admin password was removed and all sessions were ended.");
            Console.WriteLine("Open the web page and set a new password with this one-time setup token (valid 24 hours):");
            Console.WriteLine();
            Console.WriteLine("    " + token);
            Console.WriteLine();
            Console.WriteLine("Data folder: " + o.DataDir);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("Could not reset the password in " + o.DataDir + ": " + ex.Message); return 1; }
    }
}

public sealed class Cli
{
    public string? ConfigDir, DataDir, Listen, Port, Error;
    public List<string> AllowedRoots { get; } = new();
    public bool ResetPassword, Version, Help;

    public static Cli Parse(string[] args)
    {
        var c = new Cli();
        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--config-dir": c.ConfigDir = Next(); if (c.ConfigDir == null) c.Error = "--config-dir needs a value"; break;
                case "--data-dir": c.DataDir = Next(); if (c.DataDir == null) c.Error = "--data-dir needs a value"; break;
                case "--listen": c.Listen = Next(); if (c.Listen == null) c.Error = "--listen needs a value"; break;
                case "--port": c.Port = Next(); if (c.Port == null) c.Error = "--port needs a value"; break;
                case "--allowed-root": { var v = Next(); if (v == null) c.Error = "--allowed-root needs a value"; else c.AllowedRoots.Add(v); break; }
                case "--reset-password": c.ResetPassword = true; break;
                case "--version": c.Version = true; break;
                case "--help": case "-h": c.Help = true; break;
                // host options the ASP.NET test host passes (--contentRoot=..., --applicationName=..., --environment=...)
                case var a when a.StartsWith("--contentRoot", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--applicationName", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--environment", StringComparison.OrdinalIgnoreCase): break;
                default: c.Error = "Unknown option: " + args[i]; break;
            }
            if (c.Error != null) break;
        }
        return c;
    }
}
