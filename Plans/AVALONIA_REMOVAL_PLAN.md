# Avalonia Removal Plan

**Status:** proposed (2026-10-01). **Stop gap:** PR #196 (Android accessibility crash) is merged as-is. This plan replaces the code it touches.

## Why

The native Avalonia UI was removed in v26.6.41. Since then, Avalonia's only job on each head is to show one full-screen web view pointed at `http://localhost:5174/`. That thin layer keeps causing problems the system web views don't have:

- **#196:** Android crashed on launch whenever any accessibility service was on (Lenovo Pen is on by default). Avalonia's web view puts an `InteropAutomationPeer` in the accessibility tree whose members throw `NotImplementedException`, and Avalonia's Android bridge doesn't special-case it (still true in 12.0.5). Linux's AT-SPI bridge doesn't either, so the Linux launcher may have the same crash with a screen reader running.
- **Keyboard bridge:** Avalonia's `NativeWebView` has no JS→native bridge on Android. `App.axaml.cs` already digs out the underlying `android.webkit.WebView` (`TryGetPlatformHandle` polling) to call `AddJavascriptInterface` for `window.agnative.hideKeyboard()`.
- **Error reporting:** Android's WebView reports its own "webpage not available" page as a *successful* navigation through Avalonia's `NavigationCompleted` (#73). A native `WebViewClient.OnReceivedError` reports the real failure.
- **Linux VM:** the launcher reparents a WebKitGTK X11 child into an Avalonia window, which renders black on virtualized GPUs.
- **Weight:** every head ships Avalonia, Skia (as a **preview**, 3.119.4-preview.1.1, pulled in by Avalonia), HarfBuzz, the Fluent theme and fonts, all to host one control.

The system web views are reachable directly from .NET: `Android.Webkit.WebView` (.NET for Android), `WKWebView` (.NET for iOS), and WebView2 / WKWebView / WebKitGTK on desktop.

## What Avalonia does today

| Where | Avalonia use | Replacement |
|---|---|---|
| `Platforms/AgOpenWeb.Android` | `AndroidApp : AvaloniaAndroidApplication<App>`, `MainActivity : AvaloniaMainActivity`, `App.axaml.cs` builds the `NativeWebView` + splash and drives the port probe and retries (#73) | Plain `Application`/`Activity` hosting `Android.Webkit.WebView` |
| `Platforms/AgOpenWeb.iOS` | `AppDelegate : AvaloniaAppDelegate<App>`, `App.axaml.cs` starts `WebBackend` and shows a `NativeWebView` | `UIApplicationDelegate` + `UIViewController` hosting `WKWebView` |
| `Platforms/AgOpenWeb.Desktop` launcher (`--launcher`, the default on Windows/macOS) | `LauncherEntry` (`AppBuilder`), `LauncherApplication`, `WebViewLauncherWindow` (`NativeWebView` + splash), `LauncherWindow` (`--console` supervisor with start/stop + LAN URL) | Photino.NET window (see Phase 3) |
| `Platforms/AgOpenWeb.Desktop` headless (`--headless`, the default on Linux) | **None.** `HeadlessHost` is already plain .NET | — |
| `Shared/AgOpenWeb.ViewModels` | `PackageReference Avalonia`. Only real use: `MainViewModel.ApplyThemeVariant` sets `Application.Current.RequestedThemeVariant`, which is dead with no native UI. Plus stale `using Avalonia.Threading;` in 9 files | Delete |
| `Shared/AgOpenWeb.RemoteServer` | Nothing directly, but its `SkiaSharp` reference relies on Avalonia.Skia to bring the native `libSkiaSharp` (comment in the csproj). Used by `BoundaryImageryCapture` (imagery downscale for `/backpic.png`) and `RemoteServerHost` | Explicit `SkiaSharp.NativeAssets.*` per head |
| Backend threading | **None.** Every head already runs the VM on `HostLoopDispatcher` behind `IUiDispatcher`/`IUiTimerFactory` | — |
| Tests | **None.** The test projects are already Avalonia-free | — |

So everything below `Platforms/*/App.axaml.cs` is already Avalonia-free. The work is replacing three thin launchers, plus a little shared cleanup.

## What the web UI needs from the host web view

Each replacement must provide these (checked in `wwwroot/app.js`):

- **JavaScript + WebAssembly** (CanvasKit, `vendor/canvaskit.wasm`), plus DOM storage (`localStorage`).
- **Audio without a user gesture.** Alarms use `AudioContext` and `new Audio('/sounds/*.wav')`. Android needs `MediaPlaybackRequiresUserGesture = false`; iOS needs `MediaTypesRequiringUserActionForPlayback = None`. Without these the alarms are silent until the first tap.
- **`window.open(url, '_blank')`** for external links (`app.js:3240`). These open in the system browser, not inside the app.
- **Fullscreen API** (`requestFullscreen`), harmless if the host is already full screen.
- **`window.agnative.hideKeyboard()`** on Android (the soft keyboard can only be lowered natively).
- **Keep the screen on while the app is in front, always, with no setting** (decided on #198: a guidance screen must not dim mid-pass, and a toggle would show on LAN browser clients it can't affect).
  - **Android:** `FLAG_KEEP_SCREEN_ON` on the Activity window.
  - **iOS:** `IdleTimerDisabled`, re-asserted on `DidBecomeActive`.
  - **Desktop:** `SetThreadExecutionState` on Windows, `caffeinate -d -i -w <pid>` on macOS, `xdg-screensaver suspend <xid>` on Linux X11.
  - **Browser clients:** follow their device's settings.
- **Cleartext HTTP to localhost:** Android `UsesCleartextTraffic` (already set), iOS `NSAllowsLocalNetworking` (already set).
- There are no file inputs, downloads or clipboard use, so no file-chooser or download plumbing is needed.

## Phases

One PR per phase, each verified on hardware before the next. The web UI and backend don't change.

### Phase 0: Shared cleanup (no behaviour change)

1. Delete `MainViewModel.ApplyThemeVariant` and its callers (the web client does its own day/night theme from `IsDayMode`), and the stale `using Avalonia*` lines. Remove `PackageReference Avalonia` from `AgOpenWeb.ViewModels.csproj`.
2. Pin SkiaSharp explicitly. `RemoteServer` asks for 3.119.1, but the build actually resolves Avalonia's 3.119.4-preview. Choose one stable version and add the matching `SkiaSharp.NativeAssets.{Android,iOS,macOS,Win32,Linux}` to each head (Linux: the `.NoDependencies` variant is enough for the daemon's imagery child process, if it renders no text; it doesn't today). HarfBuzz isn't needed: the server code draws no text.
3. Remove the unused `Avalonia.Controls.DataGrid` and `AvaloniaUI.DiagnosticsSupport` packages from the Desktop csproj.
4. Fix stale comments that describe an Avalonia UI thread (`IUiDispatcher`, `ConnectionState`, `RemoteServerWiring`, `RemoteServer.csproj`).

Verify: all four test projects pass, and every head builds and runs. Check the imagery capture (`/backpic.png`) still works on Desktop, Android and iOS, since the native Skia now comes from our own reference.

### Phase 1: Android (first, because it fixes #196 properly)

- `AndroidApp`: a plain `Android.App.Application`. Keep `AndroidDataRoot.Initialize(this)` in `OnCreate`, and drop the Skia GPU cache override (Avalonia-only).
- `MainActivity`: a plain `Activity` whose content is a `FrameLayout` containing:
  - **`WebView` settings:** `JavaScriptEnabled`, `DomStorageEnabled`, `MediaPlaybackRequiresUserGesture = false`, and `LoadWithOverviewMode`/`UseWideViewPort` as the PWA expects. Text zoom is fixed at 100 % so the system font-size setting doesn't scale the UI.
  - **`WebViewClient`:** `ShouldOverrideUrlLoading` keeps `localhost:5174` in the view and sends everything else to the system browser. `OnReceivedError` / `OnReceivedHttpError` (main frame) drive the retry loop.
  - **`WebChromeClient`:** `OnCreateWindow` turns `window.open` into an external `ACTION_VIEW` intent. Also console logging to logcat.
  - `AddJavascriptInterface(new WebKeyboardBridge(), "agnative")` before the first load. This is direct now: no platform-handle polling.
  - A native splash `TextView` over the web view (same colours and text).
- Port the startup driver from `App.axaml.cs` unchanged in substance: probe `:5174` until it accepts (120 s), navigate, confirm the load, re-probe and retry (10 attempts), and show the error text on the splash. The #73 rule still holds: a load counts only once the probe has seen the host.
- Keep as-is: `BackendService` (the foreground service owning `AndroidBackendHost`), immersive mode, save on `OnPause`/`OnStop`, the notification-permission request, and `ConfigChanges` (no Activity restart on rotate/resize).
- Back button: check what it does in today's build first. Then either swallow it (there's no web history to go back through) or ask "Exit AgOpenWeb?".
- Remove `App.axaml`/`App.axaml.cs` and the Avalonia packages. Remove the `LauncherWebView` workaround from #196.

Verify on a Lenovo tablet with Lenovo Pen on (the #196 repro) and on one other Android device:
- cold start, and start after the service was killed (#73)
- keyboard hide (`agnative`), alarms audible before any tap, external links
- background/foreground with guidance live
- immersive mode, rotation
- with TalkBack on: no crash

### Phase 2: iOS

- `Main.cs`: `UIApplication.Main(args, null, typeof(AppDelegate))`.
- `AppDelegate : UIResponder, IUIApplicationDelegate`: in `FinishedLaunching`, create the `UIWindow`, build DI and start `WebBackend` (the code in `App.axaml.cs` today), and show a `UIViewController` with a `WKWebView`. Keep the landscape lock, the save on background/terminate, and the imagery capture as they are.
- **`WKWebViewConfiguration`:**
  - `MediaTypesRequiringUserActionForPlayback = None` and `AllowsInlineMediaPlayback`.
  - **`ScrollView`:** `Bounces = false`, `ContentInsetAdjustmentBehavior = Never`.
  - `Inspectable = true` in Debug.
- **The view controller:** `PrefersStatusBarHidden`, `PrefersHomeIndicatorAutoHidden`, and `PreferredScreenEdgesDeferringSystemGestures = All` so edge swipes reach the map.
- **`WKUIDelegate.CreateWebView`** (for `window.open`) → `UIApplication.OpenUrl`. **`WKNavigationDelegate`** gives real load failures for the same probe/retry driver as Android.
- Remove `App.axaml*` and the Avalonia packages.

Verify on the test iPad (`ios-local-build-env` memory: Xcode 27 needs `ValidateXcodeVersion=false`):
- launch, background/foreground saves
- landscape lock, keyboard, alarms
- **frame rate:** ProMotion 120 Hz. It's the same WKWebView, so no change is expected, but measure it, since the #21409 history makes this a sensitive area.
- a TestFlight build

### Phase 3: Desktop launcher

**Recommendation: Photino.NET** (`Photino.NET` 4.0.16 / `Photino.Native` 4.0.22). One small API over WebView2 (Windows), WKWebView (macOS) and WebKitGTK (Linux), with native binaries for win-x64/arm64, osx-x64/arm64 and linux-x64/arm64. The Linux binary links `libwebkit2gtk-4.1`, the same library the Linux launcher already requires (`deploy/linux/launcher/README.md`), so packaging dependencies don't change.

Caveat: its last release was January 2025 (the repo is active, but releases are slow). If that becomes a problem, the fallbacks are per-OS hosts (WebView2 WinForms on Windows; a `net10.0-macos` head with WKWebView) or browser app mode (below).

- `LauncherEntry.Run`: start `BackendHost` as today and open a `PhotinoWindow`:
  - **Window:** maximized, titled "AgOpenWeb", with the app icon.
  - **Splash:** `LoadRawString` of a small embedded splash page.
  - **Startup:** the same probe/retry driver, then `Load("http://localhost:5174/")`.
  - **On close:** `BackendHost.StopAsync()` (saves config, state and coverage), as `WebViewLauncherWindow` does.
- External links: handle `window.open` through Photino's web-message channel, or have the page post a message and `Process.Start` the URL.
- **`--console` supervisor window: decision needed.** Options:
  - (a) drop it (the headless daemon plus a browser covers the server-only case)
  - (b) rebuild it as a small embedded HTML page in a Photino window with start/stop/LAN-URL buttons over the web-message channel
  - (c) keep `--console` on Avalonia temporarily

  Recommendation: (a) or (b).
- Remove `LauncherApplication`, `WebViewLauncherWindow`, `LauncherWindow`, the `WarnIfVirtualMachine` hint (if the VM black window is gone; see Verify), and the Avalonia packages.
- Packaging: `deploy/{windows,macos,linux}/package.sh` need the Photino native libs in the publish output (they come with the NuGet runtimes). Check that the macOS `.app` bundle still launches with ad-hoc signing.

**Fallback with no embedded web view:** start the backend and open the default browser in app mode (`msedge`/`chrome --app=http://localhost:5174 --start-maximized`, or Safari as a normal window on macOS). It's the smallest code, but it depends on the user's browser, a closed browser window doesn't stop the backend, and macOS has no clean app mode.

Verify:
- **Windows 10/11 and macOS arm64:** launch, splash → UI, close saves state.
- **Linux x64 and arm64 on real hardware:** the same checks. Also in a VM, to see whether the black-window limitation is gone.
- **Accessibility:** with a screen reader on (Narrator, VoiceOver, Orca), no crash.
- **Headless mode:** unchanged.

### Phase 4: Finish

- Remove all remaining Avalonia packages and `*.axaml` files, and check `dotnet list package --include-transitive | grep -i avalonia` is empty for every head.
- Update `CLAUDE.md` (Technology Stack, platform sections, Key Files), `CONTRIBUTING.md`, and the About page (`index.html:2427` still says "Avalonia UI").
- CI (`build-and-release.yml`, `build-deploy-bundles.yml`): any Avalonia-specific steps.
- The VehicleSimulator (`Simulators/AgOpenWeb.VehicleSimulator`) is a separate Avalonia desktop tool. It's out of scope unless we want it gone too.

## Risks

- **Android WebView quirks we got for free:** Avalonia's control may set things we rely on without knowing it (focus, IME resize, hardware acceleration, insets). Compare against the current build on the same device before removing anything.
- **Keyboard and insets:** with immersive mode and `SetDecorFitsSystemWindows(false)`, check the soft keyboard doesn't cover the input being edited (`adjustResize` / `WindowInsets` handling).
- **SkiaSharp native assets:** a mismatch between the managed and native versions crashes at the first imagery capture, not at startup. Phase 0 must exercise `/backpic.png` on each head.
- **Photino maintenance:** see the caveat in Phase 3. The design keeps the launcher small enough that swapping hosts later is cheap.

## Decisions needed

1. Desktop host: Photino.NET (recommended), per-OS native hosts, or browser app mode.
2. The `--console` supervisor window: drop, rebuild in HTML, or keep on Avalonia for now.
3. Android back button: swallow, or confirm-to-exit.
4. Whether the VehicleSimulator follows (it's a dev tool, not shipped to users).
