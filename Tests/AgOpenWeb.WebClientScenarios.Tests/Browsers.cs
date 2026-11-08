// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;

namespace AgOpenWeb.WebClientScenarios.Tests;

/// <summary>A screen a farm uses for AgOpenWeb: size in CSS pixels, touch or mouse, phone or not.</summary>
public sealed record Device(string Code, string Name, int Width, int Height, bool Touch, bool Phone, float Scale = 1)
{
    public string Size => string.Create(CultureInfo.InvariantCulture, $"{Width}×{Height}");

    public static readonly Device Tablet10 = new("tablet10", "10\" tablet, landscape", 1280, 800, true, false);
    public static readonly Device Pc720 = new("pc720", "720p cab PC, mouse", 1280, 720, false, false);
    public static readonly Device Tablet7 = new("tablet7", "7\" tablet, landscape", 1024, 600, true, false);
    public static readonly Device Screen5 = new("screen5", "5\" cab screen", 800, 480, true, false);
    public static readonly Device PhoneLandscape = new("phone-landscape", "phone, landscape", 844, 390, true, true, 2);
    public static readonly Device PhonePortrait = new("phone-portrait", "phone, portrait", 390, 844, true, true, 2);
    public static readonly Device TabletPortrait = new("tablet-portrait", "10\" tablet, portrait", 800, 1280, true, false);
}

/// <summary>Headless Chromium for every screen of a run (Playwright).</summary>
public sealed class Browsers : IAsyncDisposable
{
    private IPlaywright _pw = null!;
    public IBrowser Browser { get; private set; } = null!;
    public string Version => Browser.Version;

    /// <summary>Chromium from Playwright's own install (`playwright.ps1 install chromium`), or the
    /// executable in AGOPENWEB_CHROMIUM.</summary>
    public static async Task<Browsers> StartAsync()
    {
        var b = new Browsers();
        b._pw = await Playwright.CreateAsync();
        var opts = new BrowserTypeLaunchOptions { Headless = true };
        var exe = Environment.GetEnvironmentVariable("AGOPENWEB_CHROMIUM");
        if (!string.IsNullOrEmpty(exe)) opts.ExecutablePath = exe;
        b.Browser = await b._pw.Chromium.LaunchAsync(opts);
        return b;
    }

    public async ValueTask DisposeAsync()
    {
        try { await Browser.CloseAsync(); } catch { }
        _pw?.Dispose();
    }
}

/// <summary>
/// One screen with the web client open: its own browser context (a separate device), its page,
/// and optionally its own Wi-Fi (<see cref="LinkRelay"/>). Taps are real taps (or mouse clicks
/// on a mouse screen) at the centre of the control, so a control covered by another, or off
/// the screen, is not reached.
/// </summary>
public sealed class Screen : IAsyncDisposable
{
    public Device Device { get; }
    public string Name { get; }
    public IBrowserContext Context { get; }
    public IPage Page { get; private set; }
    public LinkRelay? Wifi { get; }
    /// <summary>Script errors and console errors of the page, in order.</summary>
    public List<string> Errors { get; } = new();
    /// <summary>Requests the server answered with an error (status and path).</summary>
    public List<string> Missing { get; } = new();
    private readonly string _url;

    private Screen(Device device, string name, IBrowserContext ctx, IPage page, LinkRelay? wifi, string url)
    {
        Device = device; Name = name; Context = ctx; Page = page; Wifi = wifi; _url = url;
        Hook(page);
    }

    private void Hook(IPage page)
    {
        page.PageError += (_, e) => { lock (Errors) Errors.Add("page error: " + e); };
        // The browser's own network messages (a resource that failed, a WebSocket that could not
        // connect while the Wi-Fi is down) are not script errors; a failed resource is kept with
        // its address instead (Response below).
        page.Console += (_, m) =>
        {
            if (m.Type != "error" || m.Text.StartsWith("Failed to load resource", StringComparison.Ordinal)
                || (m.Text.StartsWith("WebSocket connection to", StringComparison.Ordinal) && m.Text.Contains("failed", StringComparison.Ordinal))) return;
            lock (Errors) Errors.Add("console: " + m.Text);
        };
        page.Response += (_, r) => { if (r.Status >= 400) lock (Errors) Missing.Add(r.Status.ToString(CultureInfo.InvariantCulture) + " " + new Uri(r.Url).PathAndQuery); };
    }

    public static async Task<Screen> OpenAsync(Browsers browsers, WebClientRig rig, Device device, string name,
        bool ownWifi = false, string locale = "en-US")
    {
        var ctx = await browsers.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = device.Width, Height = device.Height },
            HasTouch = device.Touch,
            IsMobile = device.Phone,
            DeviceScaleFactor = device.Scale,
            Locale = locale,
        });
        var page = await ctx.NewPageAsync();
        LinkRelay? wifi = ownWifi ? new LinkRelay(rig.Port) : null;
        string url = "http://127.0.0.1:" + (wifi?.Port ?? rig.Port).ToString(CultureInfo.InvariantCulture) + "/";
        var s = new Screen(device, name, ctx, page, wifi, url);
        await page.GotoAsync(url, new PageGotoOptions { Timeout = 30000 });
        return s;
    }

    /// <summary>Wait until the page is connected and has its first frames (id, control state, a tick).</summary>
    public async Task<bool> WaitReadyAsync(int timeoutMs = 20000)
    {
        try
        {
            await Page.WaitForFunctionAsync(
                "() => typeof connState !== 'undefined' && connState === 'connected' && typeof myClientId !== 'undefined' && !!myClientId && typeof tick !== 'undefined' && !!tick && typeof lastControl !== 'undefined'",
                null, new PageWaitForFunctionOptions { Timeout = timeoutMs, PollingInterval = 50 });
            return true;
        }
        catch (TimeoutException) { return false; }
    }

    /// <summary>Wait for a condition in the page (a JS expression), polling.</summary>
    public async Task<bool> WaitForAsync(string jsCondition, int timeoutMs)
    {
        try
        {
            await Page.WaitForFunctionAsync("() => { try { return !!(" + jsCondition + "); } catch (e) { return false; } }",
                null, new PageWaitForFunctionOptions { Timeout = timeoutMs, PollingInterval = 50 });
            return true;
        }
        catch (TimeoutException) { return false; }
        catch (PlaywrightException) { return false; }
    }

    public async Task<T> EvalAsync<T>(string js, object? arg = null) => await Page.EvaluateAsync<T>(js, arg);

    /// <summary>What this screen says about itself: the role line, the connection, whether it holds control.</summary>
    public async Task<View> ViewAsync()
    {
        try
        {
            var e = await Page.EvaluateAsync<JsonElement>(@"() => ({
                role: (document.getElementById('sb-roletext') || {}).textContent || '',
                conn: connState, hold: !!iHoldControl, held: !!(lastControl && lastControl.held),
                steer: !!(tick && tick.guidanceActive), as: (document.getElementById('rn-steer') || {}).className || '',
                field: (document.getElementById('sb-rot') || {}).textContent || '' })");
            return new View(e.GetProperty("role").GetString() ?? "", e.GetProperty("conn").GetString() ?? "",
                e.GetProperty("hold").GetBoolean(), e.GetProperty("held").GetBoolean(), e.GetProperty("as").GetString() ?? "");
        }
        catch (Exception ex) { return new View("(page not answering: " + ex.GetType().Name + ")", "", false, false, ""); }
    }

    /// <summary>A command as the page sends it (for actions with no button on the screen in use).</summary>
    public Task SendAsync(string cmd) => Page.EvaluateAsync("c => transport.send(c)", cmd);

    /// <summary>Tap a control where it is on the screen. Returns what was under the finger.</summary>
    public async Task<TapResult> TapAsync(string selector)
    {
        var e = await Page.EvaluateAsync<JsonElement>(@"sel => {
            const el = document.querySelector(sel);
            if (!el) return { found: false };
            const r = el.getBoundingClientRect();
            const x = r.left + r.width / 2, y = r.top + r.height / 2;
            const on = r.width > 0 && r.height > 0 && x >= 0 && y >= 0 && x < innerWidth && y < innerHeight;
            const h = on ? document.elementFromPoint(x, y) : null;
            return { found: true, on, x, y, hit: !!h && (h === el || el.contains(h)), by: h ? (h.id ? '#' + h.id : h.tagName.toLowerCase()) : '' };
        }", selector);
        if (!e.GetProperty("found").GetBoolean()) return new TapResult(false, false, "not in the page");
        if (!e.GetProperty("on").GetBoolean()) return new TapResult(true, false, "off the screen");
        float x = (float)e.GetProperty("x").GetDouble(), y = (float)e.GetProperty("y").GetDouble();
        if (Device.Touch) await Page.Touchscreen.TapAsync(x, y);
        else await Page.Mouse.ClickAsync(x, y);
        bool hit = e.GetProperty("hit").GetBoolean();
        return new TapResult(true, hit, hit ? "" : "covered by " + e.GetProperty("by").GetString());
    }

    /// <summary>Scroll a control into view the way a finger would (only inside a bar that scrolls).</summary>
    public Task<bool> ScrollToAsync(string selector) => Page.EvaluateAsync<bool>(@"sel => {
        const el = document.querySelector(sel); if (!el) return false;
        for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) {
            const cs = getComputedStyle(p);
            if ((/(auto|scroll)/.test(cs.overflowY) && p.scrollHeight > p.clientHeight + 1)
             || (/(auto|scroll)/.test(cs.overflowX) && p.scrollWidth > p.clientWidth + 1)) {
                const r = el.getBoundingClientRect(), pr = p.getBoundingClientRect();
                p.scrollTop += (r.top + r.height / 2) - (pr.top + pr.height / 2);
                p.scrollLeft += (r.left + r.width / 2) - (pr.left + pr.width / 2);
                return true;
            }
        }
        return false; }", selector);

    private ICDPSession? _sleep;

    /// <summary>The tablet's screen goes off or the app goes to the background: the page's script
    /// stops (no timers, no messages handled, no presence sent) while its socket stays open, as
    /// Android and iOS freeze a page that isn't shown. Done by pausing the page's script through
    /// the DevTools protocol (Debugger.pause): Page.setWebLifecycleState freezes only a hidden
    /// page, and a headless page is never hidden.</summary>
    public async Task FreezeAsync()
    {
        _sleep = await Context.NewCDPSessionAsync(Page);
        await _sleep.SendAsync("Debugger.enable");
        var paused = new TaskCompletionSource();
        _sleep.Event("Debugger.paused").OnEvent += (_, _) => paused.TrySetResult();
        await _sleep.SendAsync("Debugger.pause");
        await Task.WhenAny(paused.Task, Task.Delay(2000));
        if (!paused.Task.IsCompleted) throw new InvalidOperationException("the page's script did not pause");
    }

    public async Task ResumeAsync()
    {
        if (_sleep == null) return;
        await _sleep.SendAsync("Debugger.resume");
        await _sleep.SendAsync("Debugger.disable");
        await _sleep.DetachAsync();
        _sleep = null;
    }

    /// <summary>The page is reloaded (F5, or the app restarted its web view).</summary>
    public async Task ReloadAsync() => await Page.ReloadAsync(new PageReloadOptions { Timeout = 30000 });

    /// <summary>The tab is closed: the page and its socket go.</summary>
    public async Task CloseTabAsync() { try { await Page.CloseAsync(); } catch { } }

    /// <summary>A new tab on the same device, opening the web client again.</summary>
    public async Task NewTabAsync()
    {
        Page = await Context.NewPageAsync();
        Hook(Page);
        await Page.GotoAsync(_url, new PageGotoOptions { Timeout = 30000 });
    }

    public async Task ShotAsync(string path)
    {
        try { await Page.ScreenshotAsync(new PageScreenshotOptions { Path = path, Timeout = 15000 }); } catch { }
    }

    public List<string> TakeErrors() { lock (Errors) { var l = Errors.ToList(); Errors.Clear(); return l; } }

    public List<string> TakeMissing() { lock (Errors) { var l = Missing.ToList(); Missing.Clear(); return l; } }

    public async ValueTask DisposeAsync()
    {
        try { await Context.CloseAsync(); } catch { }
        Wifi?.Dispose();
    }
}

public sealed record View(string Role, string Conn, bool HoldsControl, bool SomeoneHolds, string SteerButtonClass)
{
    public override string ToString() => $"\"{Role}\" ({Conn}{(HoldsControl ? ", holds control" : "")})";
}

public sealed record TapResult(bool Found, bool Hit, string Why);
