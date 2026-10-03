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
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using UIKit;
using WebKit;
using AgOpenWeb.iOS.DependencyInjection;

namespace AgOpenWeb.iOS;

/// <summary>
/// The only view: a full-screen <see cref="WKWebView"/> on the local web UI, with a splash on
/// top until the page has loaded. Starts the in-process backend, then navigates; a failed load
/// (the host not up yet, a transient error) is retried.
/// </summary>
public sealed class LauncherViewController : UIViewController
{
    private const int MaxAttempts = 10;
    private const int RetryDelayMs = 1000;

    private WKWebView? _web;
    private UILabel? _splash;
    private AgOpenWeb.RemoteWiring.WebBackend? _backend;
    private int _port;
    private int _attempts;
    private bool _loaded;
    private NSObject? _becameActiveObserver;

    public override bool PrefersStatusBarHidden() => true;
    public override bool PrefersHomeIndicatorAutoHidden => true;
    // Edge swipes reach the map first; the system gesture needs a second swipe.
    public override UIRectEdge PreferredScreenEdgesDeferringSystemGestures => UIRectEdge.All;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        var view = View!;
        view.BackgroundColor = Splash;

        var config = new WKWebViewConfiguration
        {
            AllowsInlineMediaPlayback = true,
            // Alarms must sound before the first tap.
            MediaTypesRequiringUserActionForPlayback = WKAudiovisualMediaTypes.None,
        };
        var web = new WKWebView(view.Bounds, config)
        {
            AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
            Opaque = false,
            BackgroundColor = Splash,
            NavigationDelegate = new NavigationDelegate(this),
            UIDelegate = new UiDelegate(),
        };
        // The page is an app, not a document: no rubber-banding, no safe-area insets added
        // (the page reads env(safe-area-inset-*) itself).
        web.ScrollView.Bounces = false;
        web.ScrollView.ContentInsetAdjustmentBehavior = UIScrollViewContentInsetAdjustmentBehavior.Never;
#if DEBUG
        if (OperatingSystem.IsIOSVersionAtLeast(16, 4)) web.Inspectable = true; // Safari → Develop
#endif
        view.AddSubview(web);

        var splash = new UILabel(view.Bounds)
        {
            AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
            BackgroundColor = Splash,
            TextColor = UIColor.White,
            Font = UIFont.SystemFontOfSize(16)!,
            TextAlignment = UITextAlignment.Center,
            Lines = 0,
            Text = "Starting AgOpenWeb…",
        };
        view.AddSubview(splash);

        _web = web;
        _splash = splash;

        _ = StartBackendThenNavigateAsync();
    }

    private static UIColor Splash => UIColor.FromRGB(0x0b, 0x10, 0x20);

    private async Task StartBackendThenNavigateAsync()
    {
        try
        {
            // Build DI. The platform extension registers a HostLoopDispatcher as the UI-thread
            // stand-in and NullMapService (the browser/CanvasKit client renders the map).
            var services = new ServiceCollection();
            services.AddAgOpenWebServices();
            var provider = services.BuildServiceProvider();
            AppDelegate.Services = provider;

            // Start the backend off the UI thread; point the WebView at it once it's bound.
            _backend = await Task.Run(() =>
                AgOpenWeb.RemoteWiring.WebBackend.StartAsync(provider, new IosImageryCapture()));
            _port = _backend.Server.Port;
            Console.WriteLine($"[App] Backend up; WebView -> http://localhost:{_port}/ (all-in-one).");
            BeginInvokeOnMainThread(Navigate);
        }
        catch (Exception ex)
        {
            // Console (not Debug) so it shows in a Release device console (devicectl --console).
            Console.WriteLine($"[App] Backend start FAILED: {ex}");
            BeginInvokeOnMainThread(() => ShowSplashError(ex.GetBaseException().Message));
        }
    }

    // Main thread.
    private void Navigate()
    {
        if (_web == null || _loaded) return;
        _attempts++;
        Console.WriteLine($"[App] navigate attempt {_attempts} → http://localhost:{_port}/");
        _web.LoadRequest(new NSUrlRequest(new NSUrl($"http://localhost:{_port}/")));
    }

    private void OnLoadFinished()
    {
        _loaded = true;
        if (_splash != null) _splash.Hidden = true;
        KeepScreenOn();
    }

    private void OnLoadFailed(string reason)
    {
        Console.WriteLine($"[App] load failed (attempt {_attempts}): {reason}");
        if (_attempts >= MaxAttempts)
        {
            ShowSplashError($"the web UI did not load after {MaxAttempts} attempts ({reason})");
            return;
        }
        Task.Delay(RetryDelayMs).ContinueWith(_ => BeginInvokeOnMainThread(Navigate));
    }

    // Replace the splash text with a real message, so a startup failure tells the user (and a
    // bug report) something actionable instead of a frozen "Starting…".
    private void ShowSplashError(string? reason)
    {
        if (_splash == null) return;
        _splash.Text = $"AgOpenWeb could not start.\n\n{reason}\n\nClose the app and open it again.";
        _splash.Hidden = false;
    }

    // A guidance screen must never sleep mid-pass, so hold it awake once the web UI is up. iOS
    // only honours this in the foreground and can drop it across a background/foreground cycle,
    // so re-assert it each time the app becomes active.
    private void KeepScreenOn()
    {
        UIApplication.SharedApplication.IdleTimerDisabled = true;
        _becameActiveObserver ??= UIApplication.Notifications.ObserveDidBecomeActive(
            (_, _) => UIApplication.SharedApplication.IdleTimerDisabled = true);
    }

    private static bool IsLocalApp(NSUrl? url, int port) =>
        url != null && (url.Host == "localhost" || url.Host == "127.0.0.1") && url.Port == port;

    private static void OpenExternally(NSUrl url)
    {
        UIApplication.SharedApplication.OpenUrl(url, new UIApplicationOpenUrlOptions(), null);
    }

    /// <summary>Keeps the local app in the view, sends other links to the system browser, and
    /// reports main-frame load results for the retry loop.</summary>
    private sealed class NavigationDelegate : WKNavigationDelegate
    {
        private readonly LauncherViewController _owner;
        public NavigationDelegate(LauncherViewController owner) => _owner = owner;

        public override void DecidePolicy(WKWebView webView, WKNavigationAction navigationAction, Action<WKNavigationActionPolicy> decisionHandler)
        {
            var url = navigationAction.Request.Url;
            if (navigationAction.TargetFrame?.MainFrame == true && !IsLocalApp(url, _owner._port) && url != null)
            {
                OpenExternally(url);
                decisionHandler(WKNavigationActionPolicy.Cancel);
                return;
            }
            decisionHandler(WKNavigationActionPolicy.Allow);
        }

        public override void DidFinishNavigation(WKWebView webView, WKNavigation navigation) => _owner.OnLoadFinished();

        public override void DidFailProvisionalNavigation(WKWebView webView, WKNavigation navigation, NSError error)
            => _owner.OnLoadFailed(error.LocalizedDescription);

        public override void DidFailNavigation(WKWebView webView, WKNavigation navigation, NSError error)
            => _owner.OnLoadFailed(error.LocalizedDescription);

        // The web content process died (memory pressure): reload rather than show a blank view.
        public override void ContentProcessDidTerminate(WKWebView webView)
        {
            Console.WriteLine("[App] web content process terminated — reloading");
            webView.Reload();
        }
    }

    /// <summary><c>window.open(url, '_blank')</c> goes to the system browser.</summary>
    private sealed class UiDelegate : WKUIDelegate
    {
        public override WKWebView? CreateWebView(WKWebView webView, WKWebViewConfiguration configuration, WKNavigationAction navigationAction, WKWindowFeatures windowFeatures)
        {
            var url = navigationAction.Request.Url;
            if (url != null) OpenExternally(url);
            return null;
        }
    }
}
