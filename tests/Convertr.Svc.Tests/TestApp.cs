// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Convertr.Svc.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Convertr.Svc.Tests;

/// <summary>A running service (in memory) with its own data dir and media tree. Cookies are handled by hand so tests control them.</summary>
public sealed class TestApp : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "convertr-svc-test-" + Guid.NewGuid().ToString("N")[..8]);
    public string Data => Path.Combine(Root, "data");
    public string Media => Path.Combine(Root, "media");
    public string Outside => Path.Combine(Root, "outside");
    public WebApplicationFactory<Program> Factory { get; }
    public HttpClient Http { get; }
    public string? Cookie { get; set; }
    public string? Csrf { get; set; }
    public const string Password = "correct horse battery";

    public TestApp(bool withMedia = false, Action<IServiceCollection>? services = null, IDictionary<string, string?>? config = null)
    {
        Directory.CreateDirectory(Data); Directory.CreateDirectory(Media); Directory.CreateDirectory(Outside);
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Convertr:DataDir"] = Data, ["Convertr:AllowedRoots:0"] = Media, ["Convertr:PasswordIterations"] = "2000",
            }));
            if (config != null) b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config));
            if (services != null) b.ConfigureTestServices(services);
        });
        Http = Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
        if (withMedia) MakeMedia();
    }

    public T Get<T>() where T : notnull => Factory.Services.GetRequiredService<T>();

    public static bool HasFfmpeg => File.Exists("/usr/bin/ffmpeg") || File.Exists("/usr/local/bin/ffmpeg");

    private void MakeMedia()
    {
        Directory.CreateDirectory(Path.Combine(Media, "Movies")); Directory.CreateDirectory(Path.Combine(Media, "Series"));
        Ffmpeg("-f lavfi -i testsrc=size=320x180:rate=10:duration=2 -c:v libx264 -pix_fmt yuv420p", Path.Combine(Media, "Movies", "a.mkv"));
        Ffmpeg("-f lavfi -i testsrc=size=320x180:rate=10:duration=2 -c:v mpeg4", Path.Combine(Media, "Series", "b.avi"));
        Ffmpeg("-f lavfi -i testsrc=size=320x180:rate=10:duration=2 -c:v libx264 -pix_fmt yuv420p", Path.Combine(Outside, "secret.mkv"));
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(Path.Combine(Media, "Movies", "linkdir"), Outside);              // folder link out of the root: never entered
            File.CreateSymbolicLink(Path.Combine(Media, "Movies", "linkfile.mkv"), Path.Combine(Outside, "secret.mkv"));   // file link out of the root: dropped
        }
    }

    private static void Ffmpeg(string args, string output)
    {
        var psi = new ProcessStartInfo("ffmpeg", "-v error -y " + args + " \"" + output + "\"") { RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, "ffmpeg failed: " + p.StandardError.ReadToEnd());
    }

    // ---------- requests ----------
    public HttpRequestMessage Req(HttpMethod m, string url, object? body = null, bool csrf = true, bool cookie = true)
    {
        var r = new HttpRequestMessage(m, url);
        if (cookie && Cookie != null) r.Headers.Add("Cookie", Cookie);
        if (csrf && Csrf != null) r.Headers.Add("X-CSRF-Token", Csrf);
        if (body != null) r.Content = JsonContent.Create(body);
        return r;
    }

    public Task<HttpResponseMessage> Send(HttpMethod m, string url, object? body = null, bool csrf = true, bool cookie = true) => Http.SendAsync(Req(m, url, body, csrf, cookie));
    public Task<HttpResponseMessage> GetAsync(string url) => Send(HttpMethod.Get, url);
    public Task<HttpResponseMessage> PostAsync(string url, object? body = null) => Send(HttpMethod.Post, url, body ?? new { });
    public Task<HttpResponseMessage> PutAsync(string url, object body) => Send(HttpMethod.Put, url, body);

    public static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>Completes the first-run setup and logs in; Cookie and Csrf are then set.</summary>
    public async Task LoginAsync()
    {
        var token = Get<AuthService>().EnsureSetupToken(forceNew: true)!;
        var s = await Send(HttpMethod.Post, "/api/auth/setup", new { token, password = Password }, csrf: false, cookie: false);
        Assert.Equal(HttpStatusCode.OK, s.StatusCode);
        await SignInAsync(Password);
    }

    public async Task<HttpResponseMessage> SignInAsync(string password)
    {
        var r = await Send(HttpMethod.Post, "/api/auth/login", new { password }, csrf: false, cookie: false);
        if (r.IsSuccessStatusCode)
        {
            var sc = r.Headers.GetValues("Set-Cookie").First(h => h.StartsWith(AuthService.CookieName + "="));
            Cookie = sc.Split(';')[0];
            Csrf = (await Json(r)).GetProperty("csrf").GetString();
        }
        return r;
    }

    public async Task<JsonElement> WaitScanAsync(int seconds = 60)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            var j = await Json(await GetAsync("/api/scan"));
            if (j.GetProperty("state").GetString() != "running") return j;
            await Task.Delay(100);
        }
        throw new TimeoutException("scan did not finish");
    }

    public void Dispose()
    {
        Http.Dispose(); Factory.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp */ }
    }
}
