// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Net;
using System.Text.Json;
using Convertr.Svc.Server;
using Xunit;

namespace Convertr.Svc.Tests;

public class AuthTests
{
    [Fact]
    public async Task Healthz_IsPublic_AndRevealsNothing()
    {
        using var t = new TestApp();
        var r = await t.Http.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("""{"status":"ok"}""", await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/settings")] [InlineData("/api/fs/roots")] [InlineData("/api/fs/list")] [InlineData("/api/scan")]
    [InlineData("/api/plan")] [InlineData("/api/plan/summary")] [InlineData("/api/events")] [InlineData("/api/version")]
    public async Task EveryApiEndpoint_NeedsALogin(string url)
    {
        using var t = new TestApp();
        Assert.Equal(HttpStatusCode.Unauthorized, (await t.Http.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task SecurityHeaders_AreOnEveryResponse()
    {
        using var t = new TestApp();
        foreach (var url in new[] { "/", "/healthz", "/api/auth/state", "/api/settings", "/nothing-here" })
        {
            var r = await t.Http.GetAsync(url);
            Assert.Contains("default-src 'self'", r.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Contains("frame-ancestors 'none'", r.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("DENY", r.Headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("no-referrer", r.Headers.GetValues("Referrer-Policy").Single());
        }
    }

    [Fact]
    public async Task Setup_NeedsTheToken_AndAStrongEnoughPassword()
    {
        using var t = new TestApp();
        Assert.True((await TestApp.Json(await t.Http.GetAsync("/api/auth/state"))).GetProperty("needsSetup").GetBoolean());
        var token = t.Get<AuthService>().EnsureSetupToken(forceNew: true)!;
        var bad = await t.Send(HttpMethod.Post, "/api/auth/setup", new { token = "wrong", password = "a long enough password" }, false, false);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var weak = await t.Send(HttpMethod.Post, "/api/auth/setup", new { token, password = "short" }, false, false);
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Contains("at least 10", await weak.Content.ReadAsStringAsync());
        var ok = await t.Send(HttpMethod.Post, "/api/auth/setup", new { token, password = TestApp.Password }, false, false);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        // the token is single use and setup is closed once a password exists
        var again = await t.Send(HttpMethod.Post, "/api/auth/setup", new { token, password = "another long password" }, false, false);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.False((await TestApp.Json(await t.Http.GetAsync("/api/auth/state"))).GetProperty("needsSetup").GetBoolean());
    }

    [Fact]
    public async Task Login_BeforeSetup_IsRefused()
    {
        using var t = new TestApp();
        Assert.Equal(HttpStatusCode.Conflict, (await t.SignInAsync("whatever it is")).StatusCode);
    }

    [Fact]
    public async Task Login_WrongPassword_Is401_RightPassword_SetsAHardenedCookie()
    {
        using var t = new TestApp();
        await t.LoginAsync();
        t.Cookie = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await t.SignInAsync("not the password")).StatusCode);
        var r = await t.SignInAsync(TestApp.Password);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var set = r.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith("convertr_session="));
        Assert.Contains("httponly", set, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", set, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await t.GetAsync("/api/settings")).StatusCode);
    }

    [Fact]
    public async Task Login_IsRateLimited_WithBackOff()
    {
        using var t = new TestApp();
        await t.LoginAsync();
        HttpResponseMessage last = null!;
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await t.SignInAsync("wrong password " + i)).StatusCode);   // five free tries
        await t.SignInAsync("wrong password 6");                                                                                           // the sixth starts the back-off
        last = await t.SignInAsync("wrong password 7");
        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
        Assert.True(last.Headers.RetryAfter?.Delta?.TotalSeconds >= 1);
        // even the right password is refused while locked out
        Assert.Equal(HttpStatusCode.TooManyRequests, (await t.SignInAsync(TestApp.Password)).StatusCode);
    }

    [Fact]
    public async Task Setup_TokenGuessing_IsRateLimited()
    {
        using var t = new TestApp();
        t.Get<AuthService>().EnsureSetupToken(forceNew: true);
        HttpResponseMessage last = null!;
        for (var i = 0; i < 8; i++) last = await t.Send(HttpMethod.Post, "/api/auth/setup", new { token = "guess" + i, password = "a long enough password" }, false, false);
        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
    }

    [Fact]
    public async Task Csrf_IsRequiredOnEveryStateChangingRequest()
    {
        using var t = new TestApp();
        await t.LoginAsync();
        var noToken = await t.Send(HttpMethod.Put, "/api/settings", new { Theme = "Dark" }, csrf: false);
        Assert.Equal(HttpStatusCode.Forbidden, noToken.StatusCode);
        var req = t.Req(HttpMethod.Put, "/api/settings", new { Theme = "Dark" }, csrf: false); req.Headers.Add("X-CSRF-Token", "not-the-token");
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Http.SendAsync(req)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Send(HttpMethod.Post, "/api/scan", new { folder = t.Media }, csrf: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Send(HttpMethod.Post, "/api/auth/logout", new { }, csrf: false)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t.PutAsync("/api/settings", new { Theme = "Dark" })).StatusCode);
    }

    [Fact]
    public async Task CrossOriginRequests_AreRefused_EvenWithAValidSession()
    {
        using var t = new TestApp();
        await t.LoginAsync();
        var r = t.Req(HttpMethod.Put, "/api/settings", new { Theme = "Dark" });
        r.Headers.Add("Origin", "http://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Http.SendAsync(r)).StatusCode);
        var r2 = t.Req(HttpMethod.Put, "/api/settings", new { Theme = "Dark" });
        r2.Headers.Add("Origin", "http://localhost");   // the test server's own host
        Assert.Equal(HttpStatusCode.OK, (await t.Http.SendAsync(r2)).StatusCode);
        var r3 = t.Req(HttpMethod.Post, "/api/auth/login", new { password = TestApp.Password }, false, false);
        r3.Headers.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Http.SendAsync(r3)).StatusCode);
    }

    [Fact]
    public async Task Logout_EndsTheSession()
    {
        using var t = new TestApp();
        await t.LoginAsync();
        Assert.Equal(HttpStatusCode.OK, (await t.PostAsync("/api/auth/logout")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await t.GetAsync("/api/settings")).StatusCode);   // the old cookie no longer works
    }

    [Fact]
    public async Task ResetPassword_EndsEverySession_AndReopensSetup_WithoutRestart()
    {
        using var t = new TestApp();
        await t.LoginAsync();
        Assert.Equal(HttpStatusCode.OK, (await t.GetAsync("/api/settings")).StatusCode);
        // what `convertr-svc --reset-password` does: a separate process edits auth.json
        var token = new AuthService(t.Get<ServerOptions>()).ResetPassword();
        Assert.Equal(HttpStatusCode.Unauthorized, (await t.GetAsync("/api/settings")).StatusCode);
        Assert.True((await TestApp.Json(await t.Http.GetAsync("/api/auth/state"))).GetProperty("needsSetup").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await t.Send(HttpMethod.Post, "/api/auth/setup", new { token, password = "a brand new password" }, false, false)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await t.GetAsync("/api/settings")).StatusCode);   // the old cookie stays dead
        t.Cookie = null; Assert.Equal(HttpStatusCode.OK, (await t.SignInAsync("a brand new password")).StatusCode);
    }

    [Fact]
    public async Task ChangePassword_NeedsTheCurrentOne_AndEndsOtherSessions()
    {
        using var t = new TestApp();
        await t.LoginAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await t.PostAsync("/api/auth/password", new { current = "wrong wrong wrong", next = "something else entirely" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t.PostAsync("/api/auth/password", new { current = TestApp.Password, next = "something else entirely" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await t.GetAsync("/api/settings")).StatusCode);
    }

    [Fact]
    public async Task AuthFile_HoldsAHashNeverThePassword_AndIsPrivate()
    {
        using var t = new TestApp();
        await t.LoginAsync();
        var text = File.ReadAllText(t.Get<ServerOptions>().AuthPath);
        Assert.DoesNotContain(TestApp.Password, text);
        Assert.Contains("pbkdf2-sha256$", text);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(t.Get<ServerOptions>().AuthPath));
    }

    [Fact]
    public void Cookie_TamperedOrExpired_IsRejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "convertr-auth-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var o = new ServerOptions { DataDir = dir, PasswordIterations = 2000, SessionHours = 1 };
            var clock = new FakeClock();
            var a = new AuthService(o, clock);
            var token = a.EnsureSetupToken(forceNew: true)!;
            Assert.True(a.Setup("1.2.3.4", token, "a long enough password").Ok);
            var (res, cookie, _) = a.Login("1.2.3.4", "a long enough password");
            Assert.Equal(LoginResult.Ok, res);
            Assert.NotNull(a.Validate(cookie));
            Assert.Null(a.Validate(cookie![..^2] + (cookie[^1] == 'A' ? "BB" : "AA")));                    // signature changed
            Assert.Null(a.Validate(cookie.Replace("v1.", "v1.x")));                                          // payload changed
            Assert.Null(a.Validate("garbage"));
            // a stolen cookie from another server (other key) does not work
            var o2 = new ServerOptions { DataDir = dir + "-b", PasswordIterations = 2000 };
            var b = new AuthService(o2); b.Setup("1.1.1.1", b.EnsureSetupToken(true)!, "a long enough password");
            Assert.Null(b.Validate(cookie));
            clock.Advance(TimeSpan.FromHours(2));
            Assert.Null(a.Validate(cookie));                                                                 // expired
        }
        finally { try { Directory.Delete(dir, true); Directory.Delete(dir + "-b", true); } catch { } }
    }

    [Fact]
    public void SetupToken_Expires()
    {
        var dir = Path.Combine(Path.GetTempPath(), "convertr-auth-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var clock = new FakeClock();
            var a = new AuthService(new ServerOptions { DataDir = dir, PasswordIterations = 2000 }, clock);
            var token = a.EnsureSetupToken(true)!;
            clock.Advance(TimeSpan.FromHours(25));
            Assert.False(a.Setup("9.9.9.9", token, "a long enough password").Ok);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void PasswordHash_IsSalted_AndVerifiesOnlyTheRightPassword()
    {
        var a = new AuthService(new ServerOptions { DataDir = Path.Combine(Path.GetTempPath(), "convertr-auth-" + Guid.NewGuid().ToString("N")[..8]), PasswordIterations = 2000 });
        var h1 = a.Hash("pw-one-pw-one"); var h2 = a.Hash("pw-one-pw-one");
        Assert.NotEqual(h1, h2);
        Assert.True(AuthService.Verify("pw-one-pw-one", h1));
        Assert.False(AuthService.Verify("pw-two-pw-two", h1));
        Assert.False(AuthService.Verify("x", "not a hash"));
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan t) => _now += t;
    }
}
