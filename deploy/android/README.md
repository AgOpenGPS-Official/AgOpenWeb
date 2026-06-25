# AgOpenWeb — Android

The Android APK ships two modes from one build:

- **Native app** (default) — the full Avalonia guidance UI, as before.
- **All-in-one launcher** — the in-process guidance host (the same one the desktop/iOS
  launchers run) kept alive by a **foreground service**, with the UI shown in a full-screen
  Android System WebView at `http://localhost:5174`. The host binds `0.0.0.0`, so cab
  tablets/phones can connect over the LAN alongside the on-screen UI.

The APK itself is built by `.github/workflows/build-and-release.yml` (the `build-android` job);
there is no separate launcher artifact — the mode is a runtime marker.

## Enabling launcher mode

Launcher mode is gated by the same marker-file mechanism as iOS
([`DiagFlags.WebViewLauncher`](../../Shared/AgOpenWeb.Models/Diagnostics/DiagFlags.cs)): the app
runs as the all-in-one launcher when a file named `.use_webview_launcher` exists in the app's
`Documents/AgOpenWeb/` directory. Push it with adb, then relaunch:

```bash
adb shell run-as com.agopenweb.android \
  sh -c 'mkdir -p files/Documents/AgOpenWeb && touch files/Documents/AgOpenWeb/.use_webview_launcher'
# remove it to go back to the native UI:
adb shell run-as com.agopenweb.android rm files/Documents/AgOpenWeb/.use_webview_launcher
```

(Flags are read once at process start — force-stop and relaunch the app to pick up a change.)

## Foreground service behavior

- The host runs inside a **foreground service** (type `specialUse`) with a persistent
  notification, so the 100 Hz control loop, UDP to the AiO hardware, and the LAN web feed keep
  running while the app is **backgrounded** (home / app-switch) or the screen is off.
- The backend runs on its own host-loop thread (not the Avalonia UI thread, which Android pauses
  when backgrounded) — the same model as the headless daemon.
- **Swiping the app away from Recents** is treated as "quit": the service stops and saves config
  + field state first (the Android parallel to closing the desktop launcher window). Merely
  backgrounding does not stop it.
- Android 13+ shows the notification only after the user grants `POST_NOTIFICATIONS` (requested
  on first launch); the service runs regardless.

## Requirements

- Android 6.0 (API 23)+ to install; the foreground-service type and notification-permission
  behavior apply on newer releases.
- The embedded host serves plain HTTP on the LAN, so the app declares
  `usesCleartextTraffic="true"`.
