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

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Views.InputMethods;
using Avalonia.Android;
using Microsoft.Extensions.DependencyInjection;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Android;

[Activity(
    Label = "AgOpenWeb",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    private const int ManageStorageRequestCode = 2001;

    /// <summary>The live activity, so the WebView's JS→native bridge can reach the IME.</summary>
    public static MainActivity? Instance { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Instance = this;

        // Must run before BackendService.Start, which is what actually reads
        // AppDataRoot.Documents to load fields/vehicles/config.
        SetupDataRoot();

        // Start the foreground service that owns the in-process guidance host so it survives
        // this Activity backgrounding. The WebView (built by App) waits on
        // BackendService.HostReady before navigating. The web app is the only UI.
        RequestNotificationPermissionIfNeeded();
        BackendService.Start(this);

        // Enable immersive full-screen mode
        EnableImmersiveMode();
    }

    /// <summary>
    /// Points AGOPENWEB_DATA at the public Documents folder so config lives at
    /// /storage/emulated/0/Documents/AgOpenWeb — visible to any file manager or PC over USB,
    /// no root needed, and it mirrors the Documents\AgOpenWeb layout used on desktop.
    /// Requires "All files access" (MANAGE_EXTERNAL_STORAGE); until granted, falls back to
    /// the app's own external files dir, which needs no permission and is adb-accessible
    /// without root.
    /// </summary>
    private void SetupDataRoot()
    {
        try
        {
            string? root;

            if (OperatingSystem.IsAndroidVersionAtLeast(30) && global::Android.OS.Environment.IsExternalStorageManager)
            {
                // Granted: use the public Documents folder.
                var storageRoot = global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
                root = storageRoot != null ? System.IO.Path.Combine(storageRoot, "Documents") : null;
            }
            else
            {
                // Not granted yet (or pre-Android 11 without the runtime check): fall back to
                // the app's own external files dir. No permission dialog required, and it's
                // reachable via `adb push` even without root.
                root = ApplicationContext?.GetExternalFilesDir(null)?.AbsolutePath;

                RequestManageStoragePermission();
            }

            if (!string.IsNullOrEmpty(root))
            {
                System.IO.Directory.CreateDirectory(root); // ensure it exists before AppDataRoot appends "AgOpenWeb"
                System.Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, root);
            }
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainActivity] SetupDataRoot failed: {ex.Message}");
        }
    }

    private void RequestManageStoragePermission()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30)) return;
        try
        {
            var intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission);
            intent.SetData(global::Android.Net.Uri.Parse($"package:{PackageName}"));
            StartActivityForResult(intent, ManageStorageRequestCode);
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainActivity] permission request failed: {ex.Message}");
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        // User came back from the "All files access" settings screen. Re-resolve the data
        // root now that permission may have changed. Note: if BackendService already started
        // against the external-files-dir fallback, this does NOT migrate any data written
        // there — the app should be restarted once after granting permission so
        // BackendService.Start sees the Documents path from the beginning.
        if (requestCode == ManageStorageRequestCode)
            SetupDataRoot();
    }

    /// <summary>Lower the soft keyboard via the IME. A WebView input's JS blur() does NOT
    /// dismiss the Android keyboard — only InputMethodManager can — so the web app posts a
    /// "hideKeyboard" message (App wires WebMessageReceived) which lands here.</summary>
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

    private void SaveAppState()
    {
        try
        {
            // Save panel positions from MainView
            // Panels are now anchored — no position save needed

            // Save settings to ConfigurationStore and disk
            if (App.Services != null)
            {
                var configService = App.Services.GetService<IConfigurationService>();
                configService?.SaveAppSettings();
                App.Services.GetService<IPersistentStateService>()?.Save();
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