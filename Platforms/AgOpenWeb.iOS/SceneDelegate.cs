// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using Foundation;
using UIKit;

namespace AgOpenWeb.iOS;

/// <summary>
/// The app's one window scene (Info.plist's <c>UIApplicationSceneManifest</c>, no multiple
/// windows). Apps built with the iOS 27 SDK must use the scene lifecycle or they crash at launch
/// on iOS 27, so the window is made here, on the scene, not in <see cref="AppDelegate"/>; and
/// going to the background is a scene event, so the save that <see cref="AppDelegate"/> made on
/// the app's <c>DidEnterBackground</c> is made here.
/// </summary>
[Register("SceneDelegate")]
public class SceneDelegate : UIWindowSceneDelegate
{
    public override UIWindow? Window { get; set; }

    public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene) return;
        Window = new UIWindow(windowScene)
        {
            RootViewController = new LauncherViewController(),
        };
        Window.MakeKeyAndVisible();
    }

    public override void DidEnterBackground(UIScene scene) => AppDelegate.SaveAppState();
}
