// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using Xunit;

namespace Convertr.Svc.Tests;

/// <summary>The web UI carries the Windows app's icon: every icon link in index.html resolves to an embedded, same-origin image (the CSP allows nothing else).</summary>
public class IconTests
{
    [Theory]
    [InlineData("/favicon.ico", "image/x-icon")]
    [InlineData("/img/favicon-32.png", "image/png")]
    [InlineData("/img/apple-touch-icon.png", "image/png")]
    [InlineData("/img/logo-128.png", "image/png")]
    public async Task IconAssets_AreServed_WithoutSignIn(string url, string type)
    {
        using var app = new TestApp();
        var r = await app.Send(HttpMethod.Get, url, csrf: false, cookie: false);
        Assert.Equal(System.Net.HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(type, r.Content.Headers.ContentType?.MediaType);
        Assert.True((await r.Content.ReadAsByteArrayAsync()).Length > 100);
    }

    [Fact]
    public async Task IndexHtml_LinksTheIcons_AndShowsTheLogo()
    {
        using var app = new TestApp();
        var html = await (await app.Send(HttpMethod.Get, "/", csrf: false, cookie: false)).Content.ReadAsStringAsync();
        Assert.Contains("rel=\"icon\" href=\"/favicon.ico\"", html);
        Assert.Contains("rel=\"apple-touch-icon\"", html);
        Assert.Contains("class=\"logo\"", html);
        Assert.DoesNotContain("href=\"data:,\"", html);
        Assert.DoesNotContain("http://", html.Replace("<!--", "").Split("-->")[^1].Replace("xmlns", ""));   // no external requests
    }
}
