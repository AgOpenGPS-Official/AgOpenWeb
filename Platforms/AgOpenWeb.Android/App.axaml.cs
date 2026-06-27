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
    // static HostReady signal stale). So we navigate as soon as the host SIGNALS ready (bounded
    // wait, so a stale/pending signal can't block us), then RETRY on any load failure until the
    // page actually comes up — that bridges the startup gap instead of leaving a dead
    // "Webpage not available" on the first miss.
    private const int LauncherPort = 5174;

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
        int attempts = 0;
        const int maxAttempts = 20;       // ~50 s total at the 2.5 s watchdog cadence
        const int watchdogMs = 2500;

        // Watchdog-driven retry (event-independent): re-navigate until a load is CONFIRMED.
        // We can't rely on NavigationCompleted(IsSuccess=false) to fire — Android's WebView may
        // surface a connection-refused (host not listening yet) as a silent failure with no
        // completion event — so instead, after each attempt, if no success arrived within the
        // window, navigate again. Stops the moment a successful load flips `loaded`.
        void TryNavigate()
        {
            if (loaded) return;
            attempts++;
            Console.WriteLine($"[App] webview navigate attempt {attempts} → {uri}");
            try { web.Navigate(uri); } catch (Exception ex) { Console.WriteLine($"[App] navigate threw: {ex.Message}"); }
            if (attempts < maxAttempts)
                DelayThenPost(watchdogMs, () => { if (!loaded) TryNavigate(); });
        }

        web.NavigationCompleted += (_, e) =>
        {
            Console.WriteLine($"[App] webview completed IsSuccess={e.IsSuccess} attempt={attempts}");
            if (e.IsSuccess) { loaded = true; splash.IsVisible = false; }
        };

        _ = StartNavigationAsync(web, TryNavigate);
        return new Grid { Children = { web, splash } };
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

    // Navigate as soon as the host signals ready, but wait no longer than a few seconds for that
    // signal — the retry-on-failure loop covers any remaining gap (and the case where HostReady is
    // a stale leftover from a previous run in the same process).
    private static async Task StartNavigationAsync(NativeWebView web, Action tryNavigate)
    {
        try { await Task.WhenAny(BackendService.HostReady.Task, Task.Delay(8000)).ConfigureAwait(false); }
        catch { /* host start may have faulted; try anyway — the server can still come up */ }
        // Attach the keyboard bridge BEFORE the first navigation (addJavascriptInterface applies
        // to the next load), then navigate — all on the UI thread.
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await AttachKeyboardBridgeAsync(web);
            tryNavigate();
        });
    }

    private static void DelayThenPost(int milliseconds, Action action) =>
        _ = Task.Delay(milliseconds).ContinueWith(
            _ => Dispatcher.UIThread.Post(action), TaskScheduler.Default);
}
