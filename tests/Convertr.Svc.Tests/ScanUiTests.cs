// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using Xunit;

namespace Convertr.Svc.Tests;

/// <summary>The scan progress bar must go away when the scan is over, also when a very fast scan finishes before the POST answer (a stale "running" snapshot) reaches the page.</summary>
public class ScanUiTests
{
    [Fact]
    public async Task ScanStatus_CarriesAnIncreasingSeq()
    {
        using var t = new TestApp();
        await t.LoginAsync();
        var a = (await TestApp.Json(await t.GetAsync("/api/scan"))).GetProperty("seq").GetInt64();
        var b = (await TestApp.Json(await t.GetAsync("/api/scan"))).GetProperty("seq").GetInt64();
        Assert.True(a > 0 && b > a);
    }

    [Fact]
    public async Task AppJs_HidesTheProgressBox_ForEveryStateButRunning_AndDropsStaleSnapshots()
    {
        using var t = new TestApp();
        var js = await (await t.Send(HttpMethod.Get, "/js/app.js", csrf: false, cookie: false)).Content.ReadAsStringAsync();
        Assert.Contains("$('progress-box').hidden = !running;", js);       // done, cancelled, error and idle all hide it
        Assert.Contains("s.seq < scanSeq", js);                              // an older snapshot never replaces a newer one
    }
}
