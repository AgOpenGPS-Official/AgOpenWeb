// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Android;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Android;

/// <summary>
/// The Android head is an all-in-one thin launcher: there is no native UI. The foreground
/// <see cref="BackendService"/> (started by <see cref="MainActivity"/>) owns the in-process
/// guidance host (<see cref="AndroidBackendHost"/>) on its own host-loop thread so it survives
/// the Activity backgrounding; this Application just shows a full-screen WebView pointed at the
/// local web app once the host is bound.
/// </summary>
public partial class App : Avalonia.Application
{
    // internal set: the foreground BackendService's AndroidBackendHost owns the DI provider and
    // publishes it here so App.Services lookups (e.g. MainActivity's save-on-background) resolve.
    public static IServiceProvider? Services { get; internal set; }

    public override void Initialize()
    {
        Console.WriteLine("[App] Initializing...");
        AvaloniaXamlLoader.Load(this);
        Console.WriteLine("[App] XAML loaded.");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Console.WriteLine("[App] WebView launcher (no native UI).");
        if (ApplicationLifetime is IActivityApplicationLifetime launcherActivity)
            launcherActivity.MainViewFactory = BuildWebViewLauncherView;
        else if (ApplicationLifetime is ISingleViewApplicationLifetime launcherSingleView)
            launcherSingleView.MainView = BuildWebViewLauncherView();

        base.OnFrameworkInitializationCompleted();
    }

    // The launcher view: a full-screen NativeWebView with a splash on top until the page loads.
    // The in-process host binds :5174 from the foreground BackendService, which may still be
    // starting (cold start) or restarting (the Activity can outlive a stopped host, leaving the
    // static HostReady signal stale). So we don't navigate on a timer and we don't trust the
    // WebView to tell us the truth — we PROBE the port until it accepts a connection, and only
    // then navigate. See BuildWebViewLauncherView for why (issue #73).
    private const int LauncherPort = 5174;

    // How long to wait for the host to start accepting before giving up and showing an error.
    // A freshly booted device with cold caches took ~21 s to bind in the #73 repro, so this is
    // deliberately generous — waiting is always better than parking on a dead error page.
    private const int HostWaitSeconds = 120;
    private const int ProbeIntervalMs = 250;
    private const int ProbeTimeoutMs = 1000;
    // How long one navigation gets to finish before we give up on it and retry. Generous on
    // purpose: a short fixed watchdog re-navigates on top of a load that is merely slow, which on
    // a slow device thrashes (each retry restarts the load) instead of converging.
    private const int NavTimeoutMs = 15000;
    private const int RetryDelayMs = 1000;
    private const int MaxAttempts = 10;

    /// <summary>
    /// Ground truth for "is the guidance host serving?": can we open a TCP connection to it.
    /// Unlike <see cref="BackendService.HostReady"/> this can't be stale (that TCS is static and
    /// survives a host restart within the same process) and unlike the WebView's own load result
    /// it can't be faked by an error page.
    /// </summary>
    private static async Task<bool> IsHostAcceptingAsync()
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var cts = new System.Threading.CancellationTokenSource(ProbeTimeoutMs);
            await client.ConnectAsync(System.Net.IPAddress.Loopback, LauncherPort, cts.Token)
                .ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            // Connection refused (host not bound yet) / timeout / cancellation — all mean "not up".
            return false;
        }
    }

    private static Control BuildWebViewLauncherView()
    {
        var web = new Avalonia.Controls.NativeWebView
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        var splash = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#0b1020")),
            Child = new TextBlock
            {
                Text = "Starting AgOpenWeb…",
                Foreground = Brushes.White,
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var uri = new Uri($"http://localhost:{LauncherPort}/");
        var loaded = false;
        var hostUp = false;
        var attempts = 0;

        // A NavigationCompleted with IsSuccess=true is NOT proof our UI loaded: Android's WebView
        // reports its own "webpage not available" page as a SUCCESSFUL navigation. That was
        // issue #73 — on a cold start the host needed longer than the old 8 s wait, we navigated
        // early, got ERR_CONNECTION_REFUSED, and the error page's "success" latched `loaded`,
        // which permanently stopped the retry watchdog. The host came up seconds later and
        // nothing ever navigated again, so the app sat on a dead error page (which users
        // reasonably report as "crashes on startup").
        //
        // So a success only counts once the port probe has actually seen the host accepting.
        //
        // `pending` lets the drive loop await THIS navigation's outcome instead of guessing on a
        // timer; it's swapped per attempt so a late completion from a superseded navigation can't
        // satisfy the current one.
        TaskCompletionSource<bool>? pending = null;
        web.NavigationCompleted += (_, e) =>
        {
            Console.WriteLine($"[App] webview completed IsSuccess={e.IsSuccess} hostUp={hostUp} attempt={attempts}");
            if (e.IsSuccess && hostUp)
            {
                loaded = true;
                splash.IsVisible = false;
            }
            pending?.TrySetResult(e.IsSuccess);
        };

        async Task DriveAsync()
        {
            // 1. Wait for the host to actually accept connections. Probing the port sidesteps
            //    the stale/pending HostReady problem entirely — it doesn't matter whether that
            //    signal is left over from a previous run, still pending, or faulted.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(HostWaitSeconds))
            {
                if (await IsHostAcceptingAsync().ConfigureAwait(false)) { hostUp = true; break; }
                if (BackendService.HostReady.Task.IsFaulted) break;   // host start blew up; report it
                await Task.Delay(ProbeIntervalMs).ConfigureAwait(false);
            }

            if (!hostUp)
            {
                var reason = BackendService.HostReady.Task.IsFaulted
                    ? BackendService.HostReady.Task.Exception?.GetBaseException().Message
                    : $"the guidance host did not start within {HostWaitSeconds} s";
                Console.WriteLine($"[App] host never came up: {reason}");
                await Dispatcher.UIThread.InvokeAsync(() => ShowSplashError(splash, reason));
                return;
            }

            Console.WriteLine($"[App] host accepting on :{LauncherPort} after {sw.Elapsed.TotalSeconds:F1}s");

            // 2. Navigate, retrying until a load is confirmed. The JS→native bridge must be
            //    attached BEFORE the first load (addJavascriptInterface only applies to the NEXT
            //    one), so the attach and the navigate share a single UI-thread lambda — splitting
            //    them across two dispatcher hops would let the navigate race ahead of the attach.
            //    Re-probe between attempts so a host that restarted under us flips `hostUp` back
            //    off and we don't latch on a fresh error page.
            var bridgeAttached = false;
            while (!loaded && attempts < MaxAttempts)
            {
                attempts++;
                pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                Console.WriteLine($"[App] webview navigate attempt {attempts} → {uri}");
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    if (!bridgeAttached)
                    {
                        await AttachKeyboardBridgeAsync(web);
                        bridgeAttached = true;
                    }
                    try { web.Navigate(uri); }
                    catch (Exception ex) { Console.WriteLine($"[App] navigate threw: {ex.Message}"); }
                });

                // Wait for this navigation to actually finish (or to stall past the timeout).
                await Task.WhenAny(pending.Task, Task.Delay(NavTimeoutMs)).ConfigureAwait(false);
                if (loaded) return;

                // Failed or stalled: re-probe (the host may have gone away or restarted) and retry.
                hostUp = await IsHostAcceptingAsync().ConfigureAwait(false);
                await Task.Delay(RetryDelayMs).ConfigureAwait(false);
            }

            if (!loaded)
                await Dispatcher.UIThread.InvokeAsync(() =>
                    ShowSplashError(splash, $"the web UI did not load after {MaxAttempts} attempts"));
        }

        _ = DriveAsync();
        return new Grid { Children = { web, splash } };
    }

    // Replace the splash text with a real message. Without this a startup failure leaves either a
    // frozen "Starting AgOpenWeb…" or the WebView's own error page, neither of which tells the
    // user (or a bug report) anything actionable.
    private static void ShowSplashError(Border splash, string? reason)
    {
        splash.IsVisible = true;
        splash.Child = new TextBlock
        {
            Text = $"AgOpenWeb could not start.\n\n{reason}\n\nClose the app and open it again.",
            Foreground = Brushes.White,
            FontSize = 16,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    // Attach our own JS→native bridge to the underlying android.webkit.WebView (Avalonia's
    // NativeWebView injects none, and a WebView input's JS blur() can't lower the Android IME —
    // only InputMethodManager can). addJavascriptInterface takes effect on the NEXT load, so we
    // attach BEFORE the first Navigate. The native control realizes asynchronously after attach,
    // so poll TryGetPlatformHandle until it's available. JS calls window.agnative.hideKeyboard().
    private static async Task AttachKeyboardBridgeAsync(NativeWebView web)
    {
        for (int i = 0; i < 25; i++)
        {
            if (web.TryGetPlatformHandle() is Avalonia.Platform.IAndroidWebViewPlatformHandle ah
                && ah.WebKitWebView != IntPtr.Zero)
            {
                try
                {
                    var native = Java.Lang.Object.GetObject<global::Android.Webkit.WebView>(
                        ah.WebKitWebView, global::Android.Runtime.JniHandleOwnership.DoNotTransfer);
                    if (native != null)
                    {
                        native.AddJavascriptInterface(new WebKeyboardBridge(), "agnative");
                        Console.WriteLine("[App] keyboard bridge attached (window.agnative)");
                        return;
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[App] keyboard bridge attach failed: {ex}"); return; }
            }
            await Task.Delay(150).ConfigureAwait(false);
        }
        Console.WriteLine("[App] keyboard bridge NOT attached (no Android platform handle)");
    }

}
