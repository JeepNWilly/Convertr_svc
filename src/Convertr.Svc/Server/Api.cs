// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Text;
using System.Text.Json;
using Convertr.Core;

namespace Convertr.Svc.Server;

public sealed record SetupBody(string? Token, string? Password);
public sealed record LoginBody(string? Password);
public sealed record PasswordBody(string? Current, string? Next);
public sealed record ScanBody(string? Folder);
public sealed record StartBody(bool DryRun);
public sealed record UndoBody(string[]? Paths);
public sealed record NasExportsBody(string? Host);
public sealed record NasCommandsBody(string? Host, string? Share, string? MountPoint, string? Vers, bool ReadOnly, string? MediaFolder);
public sealed record NasCheckBody(string? MountPoint, string? MediaFolder);
public sealed record SelectBody(int[]? Ids, bool Selected, string? AllMatching, string? Q);

/// <summary>All /api endpoints. Authentication and CSRF are enforced by the pipeline (Pipeline.cs); only login, setup and state are public.</summary>
public static class Api
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api");

        // ---------- auth ----------
        g.MapGet("/auth/state", (HttpContext ctx, AuthService auth) =>
        {
            var s = ctx.Items["session"] as Session;
            return Results.Json(new { needsSetup = auth.NeedsSetup, authenticated = s != null, csrf = s != null ? auth.CsrfFor(s) : null, version = Program.Version });
        });

        g.MapPost("/auth/setup", (HttpContext ctx, AuthService auth, SetupBody b) =>
        {
            var (ok, err, retry) = auth.Setup(Ip(ctx), b.Token ?? "", b.Password ?? "");
            if (!ok)
            {
                if (retry > 0) ctx.Response.Headers.RetryAfter = retry.ToString();
                return Results.Json(new { error = err }, statusCode: retry > 0 ? 429 : 400);
            }
            return Results.Json(new { ok = true });
        });

        g.MapPost("/auth/login", (HttpContext ctx, AuthService auth, LoginBody b) =>
        {
            var (result, cookie, retry) = auth.Login(Ip(ctx), b.Password ?? "");
            switch (result)
            {
                case LoginResult.Ok:
                    var session = auth.Validate(cookie)!;
                    Pipeline.SetCookie(ctx, cookie!, auth, session.ExpiresUtc);
                    return Results.Json(new { ok = true, csrf = auth.CsrfFor(session) });
                case LoginResult.NeedsSetup: return Results.Json(new { error = "the first-run setup is not finished", needsSetup = true }, statusCode: 409);
                case LoginResult.LockedOut:
                    ctx.Response.Headers.RetryAfter = retry.ToString();
                    return Results.Json(new { error = $"too many attempts; try again in {retry} seconds", retryAfter = retry }, statusCode: 429);
                default: return Results.Json(new { error = "wrong password" }, statusCode: 401);
            }
        });

        g.MapPost("/auth/logout", (HttpContext ctx, AuthService auth) =>
        {
            if (ctx.Items["session"] is Session s) auth.Logout(s);
            ctx.Response.Cookies.Delete(AuthService.CookieName, new CookieOptions { Path = "/", HttpOnly = true, SameSite = SameSiteMode.Strict });
            return Results.Json(new { ok = true });
        });

        g.MapPost("/auth/password", (AuthService auth, PasswordBody b) =>
        {
            var (ok, err) = auth.ChangePassword(b.Current ?? "", b.Next ?? "");
            return ok ? Results.Json(new { ok = true, relogin = true }) : Results.Json(new { error = err }, statusCode: 400);
        });

        // ---------- info ----------
        g.MapGet("/version", (SettingsService settings, IProbeRunnerFactory probe, RootGuard guard) =>
        {
            var s = settings.Load();
            var runner = probe.Create(s, out var ffprobe);
            return Results.Json(new
            {
                service = "convertr-svc", version = Program.Version, core = typeof(Settings).Assembly.GetName().Version?.ToString(),
                platform = PlatformServices.Current.Name, ffprobe = runner != null ? ffprobe : null, roots = guard.Roots,
            });
        });

        // ---------- settings ----------
        g.MapGet("/settings", (SettingsService svc, RootGuard guard) => Results.Json(SettingsView(svc.Load(), guard)));
        g.MapPut("/settings", (JsonElement body, SettingsService svc, ScanService scan, RootGuard guard) =>
        {
            var before = svc.Load();
            var (s, err) = svc.Update(body);
            if (s == null) return Results.Json(new { error = err }, statusCode: 400);
            if (PlanningChanged(before, s)) scan.Replan();
            return Results.Json(SettingsView(s, guard));
        });

        // ---------- folder browser ----------
        g.MapGet("/fs/roots", (RootGuard guard) => Results.Json(new { roots = guard.Roots.Select(r => new DirEntry(r, r)).ToList(), skipped = guard.Skipped }));
        g.MapGet("/fs/list", (string? path, RootGuard guard) =>
        {
            if (string.IsNullOrEmpty(path))
            {
                if (guard.Roots.Count == 1) path = guard.Roots[0];
                else return Results.Json(new DirListing("", null, true, guard.Roots.Select(r => new DirEntry(r, r)).ToList(), 0));
            }
            if (!guard.TryResolveDirectory(path, out var real, out var err)) return Results.Json(new { error = err }, statusCode: 400);
            return Results.Json(guard.List(real));
        });

        // ---------- scan ----------
        g.MapPost("/scan", (ScanBody b, ScanService scan, SettingsService settings) =>
        {
            var (ok, err, code) = scan.Start(b.Folder ?? "");
            if (!ok) return Results.Json(new { error = err }, statusCode: code);
            return Results.Json(scan.Status(), statusCode: 202);
        });
        g.MapGet("/scan", (ScanService scan) => Results.Json(scan.Status()));
        g.MapDelete("/scan", (ScanService scan) => Results.Json(new { cancelled = scan.Cancel() }));

        // ---------- plan ----------
        g.MapGet("/plan", (ScanService scan, string? filter, string? q, string? sort, string? dir, int? skip, int? take) =>
            Results.Json(scan.Query(new PlanQuery
            {
                Filter = filter is "convert" or "skip" or "attention" ? filter : "all", Q = q, Sort = sort ?? "name",
                Desc = string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase), Skip = skip ?? 0, Take = take ?? 100,
            })));
        g.MapGet("/plan/summary", (ScanService scan) => Results.Json(scan.Summary()));
        g.MapPut("/plan/selection", (SelectBody b, ScanService scan) =>
        {
            var n = scan.Select(b.Ids, b.Selected, b.AllMatching is "all" or "convert" or "skip" or "attention" ? b.AllMatching : null, b.Q);
            return Results.Json(new { selected = n });
        });
        g.MapPost("/plan/export", (ScanService scan) => Results.Text(scan.ExportCsv(), "text/csv", Encoding.UTF8));

        // ---------- hardware ----------
        g.MapGet("/hardware", (HardwareService hw) => Results.Json(new { detecting = hw.Detecting, hardware = hw.View(hw.Current) }));
        g.MapPost("/hardware/detect", async (HardwareService hw, CancellationToken ct) =>
        {
            if (hw.Detecting) return Results.Json(new { error = "detection is already running" }, statusCode: 409);
            var p = await hw.DetectAsync(ct);
            return Results.Json(new { detecting = false, hardware = hw.View(p) });
        });

        // ---------- network storage (NAS): show-only commands, never a mount or a root action (DESIGN.md 7B) ----------
        g.MapGet("/nas", (NasService nas) => Results.Json(nas.Info()));
        g.MapPost("/nas/exports", async (NasExportsBody b, NasService nas, CancellationToken ct) =>
        {
            var (body, status) = await nas.ExportsAsync(b.Host, ct);
            return Results.Json(body, statusCode: status);
        });
        g.MapPost("/nas/commands", (NasCommandsBody b, NasService nas) =>
        {
            var (body, status) = nas.Commands(new NasConfig(b.Host ?? "", b.Share ?? "", b.MountPoint ?? "", b.Vers ?? "3", b.ReadOnly, b.MediaFolder ?? ""));
            return Results.Json(body, statusCode: status);
        });
        g.MapPost("/nas/check", async (NasCheckBody b, NasService nas, CancellationToken ct) =>
        {
            var (body, status) = await nas.CheckAsync(b.MountPoint, b.MediaFolder, ct);
            return Results.Json(body, statusCode: status);
        });

        // ---------- conversion queue ----------
        g.MapGet("/convert/preview", (JobService jobs) => Results.Json(jobs.Preview()));
        g.MapPost("/convert/start", async (StartBody b, JobService jobs, ScanService scan) =>
        {
            var (ok, err, code) = await jobs.StartAsync(b.DryRun);
            if (!ok) return Results.Json(new { error = err }, statusCode: code);
            if (!b.DryRun) scan.ClearSelection();
            return Results.Json(jobs.Status(), statusCode: 202);
        });
        g.MapPost("/convert/resume-queue", (JobService jobs) =>
        {
            var (ok, err, code) = jobs.Resume();
            return ok ? Results.Json(jobs.Status(), statusCode: 202) : Results.Json(new { error = err }, statusCode: code);
        });
        g.MapGet("/queue", (JobService jobs) => Results.Json(jobs.Status()));
        g.MapGet("/queue/jobs", (JobService jobs, int? skip, int? take) => Results.Json(jobs.Jobs(skip ?? 0, take ?? 200)));
        g.MapGet("/queue/log", (JobService jobs, int? lines) => Results.Json(jobs.LogView(lines ?? 300)));
        g.MapGet("/queue/progress", (JobService jobs) => Results.Json(jobs.ProgressView()));
        g.MapPost("/queue/pause", (JobService jobs) => jobs.Pause() ? Results.Json(jobs.Status()) : Results.Json(new { error = "nothing is running" }, statusCode: 409));
        g.MapPost("/queue/resume", (JobService jobs) => jobs.ResumePaused() ? Results.Json(jobs.Status()) : Results.Json(new { error = "the run is not paused" }, statusCode: 409));
        g.MapPost("/queue/stop", (JobService jobs) => jobs.Stop() ? Results.Json(jobs.Status()) : Results.Json(new { error = "nothing is running" }, statusCode: 409));
        g.MapPost("/queue/stop-after-current", (JobService jobs) => jobs.StopAfterCurrent() ? Results.Json(jobs.Status()) : Results.Json(new { error = "nothing is running" }, statusCode: 409));
        g.MapDelete("/queue/jobs/{id:int}", (int id, JobService jobs) =>
        {
            var (ok, err) = jobs.RemoveJob(id);
            return ok ? Results.Json(jobs.Status()) : Results.Json(new { error = err }, statusCode: 400);
        });
        g.MapDelete("/queue", (JobService jobs) =>
        {
            var (ok, err) = jobs.Discard();
            return ok ? Results.Json(jobs.Status()) : Results.Json(new { error = err }, statusCode: 409);
        });

        // ---------- undo ----------
        g.MapGet("/undo", (JobService jobs) => Results.Json(new
        {
            items = jobs.UndoItems().Select(i => new { path = i.OriginalPath, name = Path.GetFileName(i.OriginalPath), newPath = i.NewPath, origSize = i.OrigSize, newSize = i.NewSize, canUndo = i.CanUndo, status = i.Status, runId = i.RunId }).ToList(),
        }));
        g.MapPost("/undo", (UndoBody b, JobService jobs) =>
        {
            var (ok, err, results) = jobs.Undo(b.Paths ?? Array.Empty<string>());
            return ok ? Results.Json(new { results }) : Results.Json(new { error = err }, statusCode: 409);
        });

        // ---------- live events (Server-Sent Events) ----------
        g.MapGet("/events", async (HttpContext ctx, EventHub hub, ScanService scan, JobService jobs) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            var reader = hub.Subscribe(out var unsub);
            var ct = ctx.RequestAborted;
            try
            {
                await ctx.Response.WriteAsync("retry: 3000\n\n", ct);
                var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                await Write(ctx, "scan", JsonSerializer.Serialize(scan.Status(), web), ct);
                await Write(ctx, "queue", JsonSerializer.Serialize(jobs.Status(), web), ct);
                await Write(ctx, "progress", JsonSerializer.Serialize(jobs.ProgressView(), web), ct);
                while (!ct.IsCancellationRequested)
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(15));
                    try
                    {
                        if (!await reader.WaitToReadAsync(cts.Token)) break;
                        while (reader.TryRead(out var m)) await Write(ctx, m.Type, m.Json, ct);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        await ctx.Response.WriteAsync(": ping\n\n", ct); await ctx.Response.Body.FlushAsync(ct);
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally { unsub(); }
        });

        g.MapFallback(() => Results.Json(new { error = "not found" }, statusCode: 404));
    }

    private static async Task Write(HttpContext ctx, string type, string json, CancellationToken ct)
    {
        await ctx.Response.WriteAsync($"event: {type}\ndata: {json}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    private static string Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static bool PlanningChanged(Settings a, Settings b) =>
        JsonSerializer.Serialize(a.ToPlannerOptions()) != JsonSerializer.Serialize(b.ToPlannerOptions()) || a.Mode != b.Mode;

    private static object SettingsView(Settings s, RootGuard guard)
    {
        var values = new Dictionary<string, object?>();
        foreach (var n in SettingsService.Editable)
        {
            var v = typeof(Settings).GetProperty(n)!.GetValue(s);
            values[n] = v is Enum ? v.ToString() : v;
        }
        return new
        {
            values,
            readOnly = new Dictionary<string, object?>
            {
                ["FfmpegFolder"] = s.FfmpegFolder, ["FfmpegEnvironment"] = s.FfmpegEnvironment, ["GpuEncoderArgs"] = s.GpuEncoderArgs,
                ["X265Preset"] = s.X265Preset, ["Originals"] = s.Originals.ToString(), ["OriginalsFolderName"] = s.OriginalsFolderName,
            },
            roots = guard.Roots,
        };
    }
}
