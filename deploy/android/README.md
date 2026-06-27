# AgOpenWeb — Android

The Android APK is an **all-in-one launcher**: the in-process guidance host (the same one the
desktop/iOS launchers run) kept alive by a **foreground service**, with the UI shown in a
full-screen Android System WebView at `http://localhost:5174`. The host binds `0.0.0.0`, so cab
tablets/phones can connect over the LAN alongside the on-screen UI. There is no native UI — the
web app is the only interface.

The signed APK is built + released by `.github/workflows/build-deploy-bundles.yml` (the `android`
job).

## Foreground service behavior

- The host runs inside a **foreground service** (type `specialUse`) with a persistent
  notification, so the 100 Hz control loop, UDP to the AiO hardware, and the LAN web feed keep
  running while the app is **backgrounded** (home / app-switch) or the screen is off.
- The backend runs on its own host-loop thread (not the Avalonia UI thread, which Android pauses
  when backgrounded) — the same model as the headless daemon.
- The host runs **until you force-stop the app** — backgrounding and even swiping the app away
  from Recents leave it serving, because LAN clients may still be connected (like a navigation or
  music app). Config + field state are saved every time the Activity is backgrounded. To stop the
  host, force-stop AgOpenWeb from the app info screen (or Recents → app info).
- Android 13+ shows the notification only after the user grants `POST_NOTIFICATIONS` (requested
  on first launch); the service runs regardless.

## Requirements

- Android 6.0 (API 23)+ to install; the foreground-service type and notification-permission
  behavior apply on newer releases.
- The embedded host serves plain HTTP on the LAN, so the app declares
  `usesCleartextTraffic="true"`.
