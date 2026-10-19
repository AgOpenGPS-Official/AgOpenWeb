// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using UIKit;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.iOS;

/// <summary>
/// The iOS head is an all-in-one thin launcher: it boots the platform-agnostic guidance
/// backend (<see cref="AgOpenWeb.RemoteWiring.WebBackend"/>) in-process and fills the screen
/// with a <c>WKWebView</c> pointed at the local web UI (<see cref="LauncherViewController"/>).
/// There is no native UI. The host also binds 0.0.0.0, so other devices on the LAN can connect.
/// </summary>
[Register("AppDelegate")]
public class AppDelegate : UIApplicationDelegate
{
    /// <summary>The DI provider — read to save config + state when the app is backgrounded
    /// or terminated. Set by <see cref="LauncherViewController"/> before the backend starts.</summary>
    public static IServiceProvider? Services { get; internal set; }

    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        Window = new UIWindow(UIScreen.MainScreen.Bounds)
        {
            RootViewController = new LauncherViewController(),
        };
        Window.MakeKeyAndVisible();
        return true;
    }

    // Landscape only: a guidance screen is used sideways in the cab.
    public override UIInterfaceOrientationMask GetSupportedInterfaceOrientations(UIApplication application, UIWindow? forWindow)
        => UIInterfaceOrientationMask.Landscape;

    public override void DidEnterBackground(UIApplication application) => SaveAppState();

    public override void WillTerminate(UIApplication application) => SaveAppState();

    private static void SaveAppState()
    {
        try
        {
            var services = Services;
            if (services == null) return;

            services.GetRequiredService<IConfigurationService>().SaveAppSettings();
            services.GetRequiredService<IPersistentStateService>().Save();
            Console.WriteLine("[AppDelegate] Saved configuration on app background/terminate");

            // Save coverage to the active field
            var fieldService = services.GetRequiredService<IFieldService>();
            var coverageService = services.GetRequiredService<ICoverageMapService>();
            if (fieldService.ActiveField != null && !string.IsNullOrEmpty(fieldService.ActiveField.DirectoryPath))
            {
                coverageService.SaveToFile(fieldService.ActiveField.DirectoryPath);
                Console.WriteLine($"[Coverage] Saved coverage on app background/terminate to {fieldService.ActiveField.DirectoryPath}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AppDelegate] Error saving app state: {ex.Message}");
        }
    }
}
