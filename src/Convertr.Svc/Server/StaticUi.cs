// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Reflection;
using Microsoft.AspNetCore.StaticFiles;

namespace Convertr.Svc.Server;

/// <summary>Serves the embedded web UI (wwwroot). CONVERTR_WWWROOT=/path serves from disk instead (development).</summary>
public static class StaticUi
{
    private static readonly FileExtensionContentTypeProvider Types = new();
    private static readonly Assembly Asm = typeof(StaticUi).Assembly;

    public static void Map(WebApplication app)
    {
        var disk = Environment.GetEnvironmentVariable("CONVERTR_WWWROOT");
        app.MapGet("/{**path}", async (HttpContext ctx, string? path) =>
        {
            path = string.IsNullOrEmpty(path) ? "index.html" : path;
            if (path.Contains("..") || path.Contains('\\') || path.StartsWith('/') || path.StartsWith("api/", StringComparison.Ordinal))
                return Results.NotFound();
            if (!Types.TryGetContentType(path, out var ct)) ct = "application/octet-stream";
            Stream? s;
            if (!string.IsNullOrEmpty(disk))
            {
                var f = Path.GetFullPath(Path.Combine(disk, path));
                s = f.StartsWith(Path.GetFullPath(disk)) && File.Exists(f) ? File.OpenRead(f) : null;
            }
            else s = Asm.GetManifestResourceStream("wwwroot/" + path);
            if (s == null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "no-cache";
            await using (s) { var ms = new MemoryStream(); await s.CopyToAsync(ms); return Results.Bytes(ms.ToArray(), ct); }
        });
    }
}
