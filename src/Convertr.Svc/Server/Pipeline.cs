// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Text.Json;
using Convertr.Core;

namespace Convertr.Svc.Server;

public static class Pipeline
{
    public const string CsrfHeader = "X-CSRF-Token";
    private static readonly HashSet<string> PublicApi = new(StringComparer.Ordinal) { "/api/auth/login", "/api/auth/setup", "/api/auth/state" };

    public static void Configure(WebApplication app)
    {
        // ---- security headers on everything ----
        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            if (ctx.Request.Path.StartsWithSegments("/api")) h.CacheControl = "no-store";
            await next();
        });

        // ---- authentication, CSRF, same-origin check for /api ----
        app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Path.StartsWithSegments("/api")) { await next(); return; }
            var unsafeMethod = !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && !HttpMethods.IsOptions(ctx.Request.Method);
            if (unsafeMethod && !SameOrigin(ctx.Request)) { await Fail(ctx, 403, "cross-origin request refused"); return; }
            var pub = PublicApi.Contains(ctx.Request.Path.Value ?? "");
            var auth = ctx.RequestServices.GetRequiredService<AuthService>();
            ctx.Request.Cookies.TryGetValue(AuthService.CookieName, out var cookie);
            var session = auth.Validate(cookie);
            if (session != null)
            {
                ctx.Items["session"] = session;
                if (auth.Renew(session) is { } fresh) SetCookie(ctx, fresh, auth, session.ExpiresUtc);
            }
            if (!pub)
            {
                if (session == null) { await Fail(ctx, 401, "login required"); return; }
                if (unsafeMethod && !auth.CsrfOk(session, ctx.Request.Headers[CsrfHeader])) { await Fail(ctx, 403, "missing or wrong CSRF token"); return; }
            }
            await next();
        });

        app.MapGet("/healthz", () => Results.Json(new { status = "ok" }));
        Api.Map(app);
        StaticUi.Map(app);
    }

    public static bool SameOrigin(HttpRequest r)
    {
        if (r.Headers.TryGetValue("Sec-Fetch-Site", out var sfs) && sfs.Count > 0 && sfs[0] is not ("same-origin" or "none")) return false;
        if (r.Headers.TryGetValue("Origin", out var o) && o.Count > 0 && !string.IsNullOrEmpty(o[0]))
        {
            if (!Uri.TryCreate(o[0], UriKind.Absolute, out var u)) return false;
            var host = r.Host.Value ?? "";
            var origin = u.IsDefaultPort ? u.Host : u.Host + ":" + u.Port;
            if (!string.Equals(origin, host, StringComparison.OrdinalIgnoreCase) && !string.Equals(u.Authority, host, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    public static void SetCookie(HttpContext ctx, string value, AuthService auth, DateTime expiresUtc)
    {
        ctx.Response.Cookies.Append(AuthService.CookieName, value, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = ctx.Request.IsHttps, Path = "/", Expires = expiresUtc,
        });
    }

    public static async Task Fail(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = message }));
    }
}
