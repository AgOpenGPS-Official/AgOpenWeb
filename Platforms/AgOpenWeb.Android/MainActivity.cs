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
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Views.InputMethods;
using Android.Webkit;
using Android.Widget;
using Android.Window;
using Microsoft.Extensions.DependencyInjection;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Android;

/// <summary>
/// The Android head is an all-in-one thin launcher: there is no native UI. The foreground
/// <see cref="BackendService"/> owns the in-process guidance host (<see cref="AndroidBackendHost"/>)
/// on its own host-loop thread so it survives this Activity backgrounding; the Activity just
/// shows a full-screen <see cref="WebView"/> pointed at the local web app once the host is bound.
/// </summary>
[Activity(
    Label = "AgOpenWeb",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    // Handled here, so none of these restarts the Activity (and reloads the page).
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize
        | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation)]
public class MainActivity : Activity
{
    /// <summary>The live activity, so the WebView's JS→native bridge can reach the IME.</summary>
    public static MainActivity? Instance { get; private set; }

    // The in-process host binds :5174 from the foreground BackendService, which may still be
    // starting (cold start) or restarting (the Activity can outlive a stopped host, leaving the
    // static HostReady signal stale). So we don't navigate on a timer: we PROBE the port until
    // it accepts a connection, and only then navigate (issue #73).
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

    private WebView? _web;
    private TextView? _splash;
    private bool _destroyed;
    private BackCallback? _backCallback;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Instance = this;

        BuildContent();

        // Start the foreground service that owns the in-process guidance host so it survives
        // this Activity backgrounding. The web app is the only UI.
        RequestNotificationPermissionIfNeeded();
        BackendService.Start(this);

        // Enable immersive full-screen mode
        EnableImmersiveMode();

        // A guidance screen must never sleep mid-pass. FLAG_KEEP_SCREEN_ON only holds while this
        // Activity's window is visible, so backgrounding the app releases it.
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);

        // Back asks before exiting. From Android 13 the system delivers Back through this
        // callback (OnBackPressed is no longer called once predictive back is on).
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            _backCallback = new BackCallback(ConfirmExit);
            OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(0 /* PRIORITY_DEFAULT */, _backCallback);
        }

        _ = DriveAsync();
    }

    // A full-screen WebView with a splash on top until the page has loaded.
    private void BuildContent()
    {
#if DEBUG
        WebView.SetWebContentsDebuggingEnabled(true); // chrome://inspect
#endif
        var web = new WebView(this);
        var s = web.Settings;
        s.JavaScriptEnabled = true;
        s.DomStorageEnabled = true;
        s.MediaPlaybackRequiresUserGesture = false; // alarms must sound before the first tap
        s.LoadWithOverviewMode = true;
        s.UseWideViewPort = true;
        s.TextZoom = 100; // the system font-size setting must not scale the UI
        s.SetSupportMultipleWindows(true); // window.open → LauncherWebChromeClient.OnCreateWindow
        s.JavaScriptCanOpenWindowsAutomatically = true;
        web.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#0b1020"));
        web.SetWebViewClient(new LauncherWebViewClient(LauncherPort, OnLoadFinished));
        web.SetWebChromeClient(new LauncherWebChromeClient());
        // window.agnative.hideKeyboard(). Must be added before the first load.
        web.AddJavascriptInterface(new WebKeyboardBridge(), "agnative");

        var splash = new TextView(this)
        {
            Text = "Starting AgOpenWeb…",
            Gravity = GravityFlags.Center,
        };
        splash.SetTextColor(global::Android.Graphics.Color.White);
        splash.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 16);
        splash.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#0b1020"));
        splash.SetPadding(48, 48, 48, 48);
        splash.Clickable = true; // taps must not fall through to the page behind

        var match = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
        var root = new FrameLayout(this);
        root.AddView(web, match);
        root.AddView(splash, match);
        SetContentView(root);

        _web = web;
        _splash = splash;
    }

    // ---- startup: wait for the host, then load the page --------------------------------------

    private bool _loaded;
    private bool _hostUp;
    private int _attempts;
    // Lets the drive loop await THIS navigation's outcome instead of guessing on a timer; swapped
    // per attempt so a late result from a superseded navigation can't satisfy the current one.
    private TaskCompletionSource<bool>? _pending;

    // UI thread (WebViewClient callback). A finished load only counts once the port probe has
    // actually seen the host accepting.
    private void OnLoadFinished(bool success)
    {
        Console.WriteLine($"[Launcher] load finished success={success} hostUp={_hostUp} attempt={_attempts}");
        if (success && _hostUp && !_loaded)
        {
            _loaded = true;
            if (_splash != null) _splash.Visibility = ViewStates.Gone;
        }
        _pending?.TrySetResult(success);
    }

    /// <summary>
    /// Ground truth for "is the guidance host serving?": can we open a TCP connection to it.
    /// Unlike <see cref="BackendService.HostReady"/> this can't be stale (that TCS is static and
    /// survives a host restart within the same process).
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

    private async Task DriveAsync()
    {
        // 1. Wait for the host to actually accept connections.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!_destroyed && sw.Elapsed < TimeSpan.FromSeconds(HostWaitSeconds))
        {
            if (await IsHostAcceptingAsync().ConfigureAwait(false)) { _hostUp = true; break; }
            if (BackendService.HostReady.Task.IsFaulted) break;   // host start blew up; report it
            await Task.Delay(ProbeIntervalMs).ConfigureAwait(false);
        }
        if (_destroyed) return;

        if (!_hostUp)
        {
            var reason = BackendService.HostReady.Task.IsFaulted
                ? BackendService.HostReady.Task.Exception?.GetBaseException().Message
                : $"the guidance host did not start within {HostWaitSeconds} s";
            Console.WriteLine($"[Launcher] host never came up: {reason}");
            RunOnUiThread(() => ShowSplashError(reason));
            return;
        }

        Console.WriteLine($"[Launcher] host accepting on :{LauncherPort} after {sw.Elapsed.TotalSeconds:F1}s");

        // 2. Navigate, retrying until a load is confirmed. Re-probe between attempts so a host
        //    that restarted under us flips _hostUp back off and we don't latch on an error page.
        var url = $"http://localhost:{LauncherPort}/";
        while (!_loaded && !_destroyed && _attempts < MaxAttempts)
        {
            _attempts++;
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = pending;

            Console.WriteLine($"[Launcher] navigate attempt {_attempts} → {url}");
            RunOnUiThread(() =>
            {
                try { _web?.LoadUrl(url); }
                catch (Exception ex) { Console.WriteLine($"[Launcher] navigate threw: {ex.Message}"); }
            });

            // Wait for this navigation to actually finish (or to stall past the timeout).
            await Task.WhenAny(pending.Task, Task.Delay(NavTimeoutMs)).ConfigureAwait(false);
            if (_loaded) return;

            // Failed or stalled: re-probe (the host may have gone away or restarted) and retry.
            _hostUp = await IsHostAcceptingAsync().ConfigureAwait(false);
            await Task.Delay(RetryDelayMs).ConfigureAwait(false);
        }

        if (!_loaded && !_destroyed)
            RunOnUiThread(() => ShowSplashError($"the web UI did not load after {MaxAttempts} attempts"));
    }

    // Replace the splash text with a real message. Without this a startup failure leaves either a
    // frozen "Starting AgOpenWeb…" or the WebView's own error page, neither of which tells the
    // user (or a bug report) anything actionable.
    private void ShowSplashError(string? reason)
    {
        if (_splash == null) return;
        _splash.Text = $"AgOpenWeb could not start.\n\n{reason}\n\nClose the app and open it again.";
        _splash.Visibility = ViewStates.Visible;
    }

    // ---- Back: ask before exiting -------------------------------------------------------------

#pragma warning disable CS0672, CA1422 // OnBackPressed is the only hook before Android 13
    public override void OnBackPressed() => ConfirmExit();
#pragma warning restore CS0672, CA1422

    private void ConfirmExit()
    {
        new AlertDialog.Builder(this)
            .SetTitle("Exit AgOpenWeb?")!
            .SetMessage("Guidance stops, and other devices lose the web view.")!
            .SetPositiveButton("Exit", (_, _) => Exit())!
            .SetNegativeButton("Cancel", (_, _) => EnableImmersiveMode())!
            .SetOnCancelListener(new CancelListener(EnableImmersiveMode))!
            .Show();
    }

    // Stops the guidance host as well (BackendService.OnDestroy saves config, state and
    // coverage). Dismissing the app from Recents still leaves the host running.
    private void Exit()
    {
        SaveAppState();
        BackendService.Stop(this);
        FinishAndRemoveTask();
    }

    private sealed class BackCallback(Action onBack) : Java.Lang.Object, IOnBackInvokedCallback
    {
        public void OnBackInvoked() => onBack();
    }

    private sealed class CancelListener(Action onCancel) : Java.Lang.Object, IDialogInterfaceOnCancelListener
    {
        public void OnCancel(IDialogInterface? dialog) => onCancel();
    }

    // ---- keyboard, permissions, lifecycle -----------------------------------------------------

    /// <summary>Lower the soft keyboard via the IME. A WebView input's JS blur() does NOT
    /// dismiss the Android keyboard — only InputMethodManager can — so the web app calls
    /// <c>window.agnative.hideKeyboard()</c> (<see cref="WebKeyboardBridge"/>), which lands here.</summary>
    public static void HideSoftKeyboard()
    {
        var act = Instance;
        if (act == null) return;
        act.RunOnUiThread(() =>
        {
            try
            {
                var imm = (InputMethodManager?)act.GetSystemService(Context.InputMethodService);
                var view = act.CurrentFocus ?? act.Window?.DecorView;
                if (imm != null && view != null)
                    imm.HideSoftInputFromWindow(view.WindowToken, HideSoftInputFlags.None);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainActivity] HideSoftKeyboard failed: {ex.Message}");
            }
        });
    }

    // Android 13+ (API 33) gates notification display behind a runtime permission; without it
    // the foreground-service notification is suppressed (the service still runs). Best-effort.
    private void RequestNotificationPermissionIfNeeded()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        try
        {
            const string perm = global::Android.Manifest.Permission.PostNotifications;
            if (CheckSelfPermission(perm) != global::Android.Content.PM.Permission.Granted)
                RequestPermissions(new[] { perm }, 1001);
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainActivity] notif permission request failed: {ex.Message}");
        }
    }

    protected override void OnResume()
    {
        base.OnResume();

        // Re-enable immersive mode when returning to the app
        EnableImmersiveMode();
    }

    protected override void OnPause()
    {
        base.OnPause();

        // Save app state when going to background
        SaveAppState();
    }

    protected override void OnStop()
    {
        base.OnStop();

        // Also save on stop in case OnPause wasn't enough
        SaveAppState();
    }

    protected override void OnDestroy()
    {
        _destroyed = true;
        if (_backCallback != null && OperatingSystem.IsAndroidVersionAtLeast(33))
            OnBackInvokedDispatcher.UnregisterOnBackInvokedCallback(_backCallback);
        if (Instance == this) Instance = null;
        _web?.Destroy();
        _web = null;
        base.OnDestroy();
    }

    private static void SaveAppState()
    {
        try
        {
            // Save settings to ConfigurationStore and disk
            var services = AndroidApp.Services;
            if (services != null)
            {
                services.GetService<IConfigurationService>()?.SaveAppSettings();
                services.GetService<IPersistentStateService>()?.Save();
            }
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainActivity] Error saving app state: {ex.Message}");
        }
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);

        // Re-enable immersive mode when window gains focus
        if (hasFocus)
        {
            EnableImmersiveMode();
        }
    }

    private void EnableImmersiveMode()
    {
        if (Window == null) return;

        // Enable immersive full-screen mode (requires Android 11+ / API 30+)
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            Window.SetDecorFitsSystemWindows(false);
            var controller = Window.InsetsController;
            if (controller != null)
            {
                controller.Hide(WindowInsets.Type.StatusBars() | WindowInsets.Type.NavigationBars());
                controller.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
            }
        }
    }
}
