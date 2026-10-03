# CLAUDE.md - AgOpenWeb3

This file provides guidance to Claude Code when working with this repository.

**Key documentation:**
- **[Plans/ARCHITECTURE.md](Plans/ARCHITECTURE.md)** - Full architecture: services, state management, data flow
- **[CONTRIBUTING.md](CONTRIBUTING.md)** - Contributor guide with cross-platform parity rules
- **[PGN.md](PGN.md)** - UDP packet protocol for hardware communication
- **[Docs/TRANSLATIONS.md](Docs/TRANSLATIONS.md)** - UI strings and Weblate

## Project Overview

AgOpenWeb3 is a cross-platform agricultural GPS guidance application: a .NET guidance host
with a web UI. It's a clean rewrite of AgOpenGPS with the backend shared across every
platform and the UI a single web client.

**What it does:**
- Real-time GPS guidance for agricultural equipment
- Field boundary management and recording
- Unified track guidance (AB lines and curves use same system)
- U-turn path generation and following
- Section control for sprayers/planters
- NTRIP RTK corrections support
- Configurable keyboard hotkeys
- Integration with AgOpenGPS ecosystem via UDP

## Architecture

**The web client is the only UI.** `AgOpenWeb.RemoteServer` embeds a web server and serves
the `wwwroot` CanvasKit PWA (`index.html`, `app.js`, `transport.js`, `i18n.js`). Every head
boots the same guidance backend (`AgOpenWeb.RemoteWiring.WebBackend`: the `MainViewModel`
"brain" on a `HostLoopDispatcher`, plus the embedded `RemoteServerHost`) and shows that page
in the platform's own web view; any browser on the LAN can open `http://<host>:5174` too.
There is no native UI framework in the heads (Avalonia was removed in October 2026; the
`VehicleSimulator` dev tool is the one Avalonia app left).

```
AgOpenWeb3/
├── Shared/                              # Platform-agnostic code
│   ├── AgOpenWeb.Models/            # Data models, geometry, configuration, DTOs
│   ├── AgOpenWeb.Services/          # Business logic, GPS, NTRIP, UDP, audio (.wav embedded)
│   ├── AgOpenWeb.ViewModels/        # MainViewModel: the headless control brain; no native UI
│   ├── AgOpenWeb.RemoteServer/      # Embedded web server + wwwroot web client (the UI)
│   └── AgOpenWeb.RemoteWiring/      # WebBackend host core + RemoteServer↔VM command/projector wiring
│
├── Platforms/                           # Thin web-view shells + platform services
│   ├── AgOpenWeb.Desktop/           # Windows/macOS/Linux: Photino.NET launcher window, or headless daemon
│   ├── AgOpenWeb.iOS/              # iOS/iPadOS: UIApplicationDelegate + WKWebView
│   └── AgOpenWeb.Android/          # Android: Activity + android.webkit.WebView, foreground BackendService
│
├── Simulators/
│   ├── AgOpenWeb.VirtualModules/    # Virtual GPS/steer/machine UDP modules (tests + bench)
│   └── AgOpenWeb.VehicleSimulator/  # Standalone Avalonia dev tool (not shipped)
│
├── Tests/                              # NUnit test projects
│   ├── AgOpenWeb.Models.Tests/     # Geometry, coordinate conversion
│   ├── AgOpenWeb.Services.Tests/   # NMEA parsing, guidance, coverage, pipeline
│   ├── AgOpenWeb.ViewModels.Tests/ # ViewModel/control-brain logic
│   └── AgOpenWeb.IntegrationTests/ # Test-support lib: fake settings etc. (used by Services.Tests)
│
├── Tools/                              # Dev scripts (i18n-extract.py, unpack-dump.sh, ...)
├── deploy/                             # Packaging per OS (package.sh, installers, READMEs)
├── TestRunner/                         # Legacy test harness for guidance algorithms
└── AgOpenWeb.sln                    # Solution file
```

### Platform Support

| Platform | Project | Notes |
|----------|---------|-------|
| Windows | AgOpenWeb.Desktop | Launcher window (WebView2) by default, or Windows Service (headless) |
| macOS | AgOpenWeb.Desktop | Launcher window (WKWebView) by default |
| Linux | AgOpenWeb.Desktop | Headless systemd daemon by default; `--launcher` window (WebKitGTK) on real hardware |
| iOS/iPadOS | AgOpenWeb.iOS | Requires Xcode 26.3+; TestFlight |
| Android | AgOpenWeb.Android | APK build, sideload install |

### Desktop modes

`Program.cs` picks the mode: `--headless` (display-less daemon: `HeadlessHost`), `--launcher`
(the Photino.NET window: `Launcher/LauncherEntry`), no flag = launcher on Windows and macOS,
headless on Linux. `--launcher --console` shows the supervisor page (start/stop, LAN URL)
instead of the UI, for a box that only serves other devices. The backend is identical in
every mode (`BackendHost`).

## Build Commands

```bash
# Build and run Desktop (works on Windows, macOS, Linux)
dotnet build Platforms/AgOpenWeb.Desktop/AgOpenWeb.Desktop.csproj
dotnet run --project Platforms/AgOpenWeb.Desktop/AgOpenWeb.Desktop.csproj            # launcher window
dotnet run --project Platforms/AgOpenWeb.Desktop/AgOpenWeb.Desktop.csproj -- --headless   # daemon; UI at http://localhost:5174

# Build iOS (requires macOS with Xcode 26.3+)
dotnet build Platforms/AgOpenWeb.iOS/AgOpenWeb.iOS.csproj -c Debug -f net10.0-ios -r iossimulator-arm64

# iOS on a device: Release signs for the App Store, so for a directly installable optimized build use
dotnet build Platforms/AgOpenWeb.iOS/AgOpenWeb.iOS.csproj -c Release -f net10.0-ios -r ios-arm64 \
  -p:MtouchUseLlvm=false "-p:CodesignKey=Apple Development" -p:CodesignProvision=Automatic
# (LLVM off builds in ~3 min instead of ~15; delete bin/obj for ios-arm64 after switching branches)
xcrun devicectl device install app --device <udid> Platforms/AgOpenWeb.iOS/bin/Release/net10.0-ios/ios-arm64/AgOpenWeb.iOS.app

# Build Android APK
dotnet build Platforms/AgOpenWeb.Android/AgOpenWeb.Android.csproj

# Build entire solution
dotnet build AgOpenWeb.sln

# Run tests
dotnet test Tests/AgOpenWeb.Models.Tests/ ; dotnet test Tests/AgOpenWeb.Services.Tests/ ; dotnet test Tests/AgOpenWeb.ViewModels.Tests/
```

## Key Design Decisions

### Rendering: the web client
The map is drawn by the web client with CanvasKit (Skia compiled to WebAssembly, bundled in
`wwwroot/vendor` for offline cab use). The host sends state over a WebSocket as compact binary
frames (`WireCodec` on the host, `transport.js` on the client): a 10 Hz `Tick` (pose, cross-track,
sections), the `Scene` (tracks, boundary, imagery extent), `Config`, `Status`, coverage as an
init + incremental cell deltas, and smaller frames for dialogs, prompts, sounds and toasts.
Position between ticks is extrapolated client-side (`RENDER_DELAY`); the pose estimator on the
host does the same for the 100 Hz control loop.

### Control authority
Several browsers may be connected; only one holds control (`ControlAuthority`, deadman model).
Commands that actuate (autosteer, sections, U-turn, simulator) are Tier-2 and dropped unless
the sender holds fresh control; everything else is Tier-1. A client can't take control from a
live operator, only fill an empty seat.

### Host ↔ client commands
The client sends `id|arg` strings (`transport.send('track.select|0')`). `RemoteServerWiring`
(in `RemoteWiring`) switches on the id and calls the VM; `RemoteServerWiring.Helpers.cs` holds
the `config.set|key:value` table. Projectors (`SceneProjector`, `CoverageProjector`) build the
DTOs in `Contracts.cs`, `WireCodec` encodes them, `MapBroadcaster` schedules the sends.

### MainViewModel is the control brain
`MainViewModel` has no view. It runs on the single `HostLoopDispatcher` thread (the "UI
thread" stand-in, `IUiDispatcher`), coordinates services, and is driven only through
`RemoteServerWiring`. It is a large partial class split by domain:

| File | Domain |
|------|--------|
| `MainViewModel.cs` | Core state, constructor, DI, properties |
| `MainViewModel.Commands.Track.cs` | Track/AB line commands |
| `MainViewModel.Commands.Boundary.cs` | Boundary, headland, AgShare commands |
| `MainViewModel.Commands.Fields.cs` | Field open/close/create, jobs |
| `MainViewModel.Commands.Ntrip.cs` | NTRIP profile management |
| `MainViewModel.Commands.Navigation.cs` | View settings, camera, day/night |
| `MainViewModel.Commands.Hotkeys.cs` | Hotkey configuration and dispatch |
| `MainViewModel.Commands.Settings.cs` | App directories, language, reset settings |
| `MainViewModel.Commands.Simulator.cs` | Simulator controls |
| `MainViewModel.Commands.Configuration.cs` | Vehicle/tool configuration |
| `MainViewModel.Commands.Wizards.cs` | Setup wizards |
| `MainViewModel.YouTurn.cs` | U-turn path generation and following |
| `MainViewModel.Guidance.cs` | Guidance algorithm orchestration |
| `MainViewModel.GpsHandling.cs` | GPS data processing |
| `MainViewModel.SectionControl.cs` | Section on/off logic |
| `MainViewModel.BoundaryRecording.cs` | Boundary recording state |
| `MainViewModel.Ntrip.cs` | NTRIP connection management |
| `MainViewModel.Simulator.cs` | GPS simulator state |
| `MainViewModel.ViewSettings.cs` | Display/view settings |
| `MainViewModel.*.Remote.cs` | Web-client-only flows (field builder edits, headland, KML, tram) |

### ConfigurationStore
`ConfigurationStore` is a reactive singleton holding all runtime configuration (vehicle, tool, guidance, hotkeys, etc.). It syncs to/from `AppSettings` JSON via `ConfigurationService`.

```csharp
ConfigStore.Vehicle.AntennaHeight    // Vehicle config
ConfigStore.Tool.ToolWidth           // Tool/implement config
ConfigStore.Hotkeys.GetActionForKey("A")  // Hotkey lookup
```

### Unified Track Architecture
**Key insight from AgOpenGPS creator Brian:** "An AB line is just a curve with 2 points."

All guidance track types use a single `Track` model (`Shared/AgOpenWeb.Models/Track/Track.cs`):

```csharp
public class Track
{
    public string Name { get; set; }
    public List<Vec3> Points { get; set; }  // AB lines have 2 points, curves have N
    public TrackMode Mode { get; set; }
    public bool IsVisible { get; set; }
    public double NudgeDistance { get; set; }

    // Computed properties
    public bool IsABLine => Points.Count == 2;
    public bool IsCurve => Points.Count > 2;
}
```

**Single guidance service** (`TrackGuidanceService`) handles both Pure Pursuit and Stanley algorithms for all track types. This replaced 4 separate guidance services and reduced ~2,100 lines of duplicated code.

**Shared utilities** in `GeometryMath.cs`:
- `Distance()`, `DistanceSquared()` - various overloads for Vec2/Vec3
- `ToDegrees()`, `ToRadians()` - angle conversion
- `IsPointInPolygon()` - boundary checks
- `PIBy2`, `twoPI` - common constants

### File Format Philosophy
AgOpenWeb may use different/improved formats from AgOpenGPS when it benefits code simplicity or features. Provide **one-way import** from AgOpenGPS formats rather than maintaining full backwards compatibility.

- **Formats**: a field is `field.geojson` (origin, boundaries, headland, tracks, flags, headland lines, background image placement) beside `background.png`, `contours.geojson`, `recorded-paths.geojson` and `elevation.csv`; a job's coverage is world-anchored tiles under `jobs/<task>/coverage/`. Profiles are JSON.
- **Migration**: AgOpenGPS files found in a field folder are imported once (on open) and **deleted**; AgOpenWeb never writes AgOpenGPS formats. No compatibility code for files from older AgOpenWeb builds (no installed base yet). See `Plans/Completed/FILE_FORMAT_MODERNIZATION_PLAN.md`.

## Technology Stack

- **.NET 10.0** - Target framework
- **Web client** - HTML/JS, CanvasKit (Skia WASM); no framework
- **Photino.NET 4** - Desktop launcher window (WebView2 / WKWebView / WebKitGTK)
- **WKWebView / android.webkit.WebView** - the iOS and Android shells
- **CommunityToolkit.Mvvm** - ObservableObject/RelayCommand in the control brain
- **Microsoft.Extensions.DependencyInjection / Hosting** - DI, daemon lifetime (systemd, Windows Service)
- **SkiaSharp 3.119.4** - server-side imagery work (`BoundaryImageryCapture`, `/backpic.png`); each head references its native package
- **NUnit 4 + NSubstitute** - Testing

## Key Files Reference

| File | Purpose |
|------|---------|
| `Shared/AgOpenWeb.ViewModels/MainViewModel.cs` | Control brain, constructor, DI |
| `Shared/AgOpenWeb.RemoteWiring/WebBackend.cs` | Boots the backend every head uses |
| `Shared/AgOpenWeb.RemoteWiring/RemoteServerWiring.cs` | Web command → VM routing |
| `Shared/AgOpenWeb.RemoteServer/RemoteServerHost.cs` | Embedded web server, routes, static assets |
| `Shared/AgOpenWeb.RemoteServer/SceneProjector.cs` | Host state → DTOs |
| `Shared/AgOpenWeb.RemoteServer/WireCodec.cs` | Binary wire encoding (pair with `wwwroot/transport.js`) |
| `Shared/AgOpenWeb.RemoteServer/wwwroot/app.js` | The web client |
| `Shared/AgOpenWeb.RemoteServer/wwwroot/i18n.js` | Client-side translation |
| `Shared/AgOpenWeb.Models/Track/Track.cs` | Unified track model (AB lines + curves) |
| `Shared/AgOpenWeb.Models/Base/GeometryMath.cs` | Shared geometry utilities |
| `Shared/AgOpenWeb.Models/Configuration/ConfigurationStore.cs` | Reactive config singleton |
| `Shared/AgOpenWeb.Services/Track/TrackGuidanceService.cs` | Pure Pursuit + Stanley guidance |
| `Shared/AgOpenWeb.Services/YouTurn/YouTurnStateMachine.cs` | U-turn planning and following |
| `Shared/AgOpenWeb.Services/Coverage/CoverageMapService.cs` | Coverage detection + display grids, tiles |
| `Shared/AgOpenWeb.Services/NtripClientService.cs` | NTRIP RTK corrections |
| `Shared/AgOpenWeb.Services/ConfigurationService.cs` | AppSettings ↔ ConfigurationStore sync |
| `Platforms/AgOpenWeb.Desktop/Program.cs` | Desktop mode selection |
| `Platforms/AgOpenWeb.Desktop/Launcher/LauncherEntry.cs` | Photino window: all-in-one app or `--console` supervisor |
| `Platforms/AgOpenWeb.iOS/LauncherViewController.cs` | iOS head: WKWebView over WebBackend |
| `Platforms/AgOpenWeb.Android/MainActivity.cs` | Android head: WebView, Back/Exit, startup probe |

## Service Interfaces

Services use interface-based design in `Shared/AgOpenWeb.Services/Interfaces/`:
- `ITrackGuidanceService` - Unified guidance (Pure Pursuit + Stanley) for all track types
- `IGpsService` - GPS data processing and position updates
- `IUdpCommunicationService` - UDP communication with AgOpenGPS modules
- `INtripClientService` - NTRIP caster connections for RTK
- `IFieldService` - Field loading/saving/management
- `IBoundaryRecordingService` - Recording field boundaries
- `IMapService` - Historical map hook; every head registers `NullMapService` (the web client renders)
- `IConfigurationService` - AppSettings ↔ ConfigurationStore sync, vehicle profiles
- `IVehicleProfileService` - Vehicle profile CRUD
- `INtripProfileService` - NTRIP profile CRUD
- `IAutoSteerService` - Zero-copy GPS→steering pipeline
- `ISettingsService` - AppSettings JSON persistence
- `ICoverageMapService` - Worked area tracking
- `ISectionControlService` - Automatic section on/off based on coverage/boundaries
- `IUiDispatcher` / `IUiTimerFactory` - the host-loop thread the VM runs on (`HostLoopDispatcher`)

## Platform-Specific Code

Platform projects contain only what **must** differ per platform: the process entry, the web
view shell, DI setup, and platform services (battery, data root, imagery capture process).
Everything else lives in `Shared/`.

### Desktop
- `Program.cs` - mode selection (see above); the imagery-capture child process entry
- `HeadlessHost.cs` / `BackendHost.cs` - daemon lifetime; the backend used by both modes
- `Launcher/` - `LauncherEntry` (Photino window), `WebViewLauncher` (all-in-one), `ConsolePage` (`--console` HTML supervisor), `ScreenAwake`
- `ImageryCaptureProcess.cs` - Skia imagery compositing in a throwaway child process (crash isolation)
- `DependencyInjection/ServiceCollectionExtensions.cs` - DI setup

### iOS
- `AppDelegate.cs` - window, landscape lock, saves on background/terminate
- `LauncherViewController.cs` - WKWebView, startup + retry, external links to Safari, keep screen on
- `IosImageryCapture.cs` - imagery capture in-process
- `Services/IOSBatteryService.cs`, `DependencyInjection/`, `Info.plist`

### Android
- `AndroidApp.cs` - `Application`; `AndroidDataRoot` init; holds `Services`
- `MainActivity.cs` - WebView, probe-and-retry startup (#73), Back → "Exit AgOpenWeb?", immersive mode, keep screen on
- `LauncherWebClients.cs` - navigation policy, external links, console → logcat
- `BackendService.cs` - foreground service owning the backend (survives backgrounding)
- `WebKeyboardBridge.cs` - `window.agnative.hideKeyboard()`

What each shell must provide to the web UI (JS + WASM, audio without a gesture, `window.open`
to the system browser, keep the screen awake, localhost HTTP) is listed in
`Plans/AVALONIA_REMOVAL_PLAN.md`.

## Common Tasks

### Adding a web command
1. In `app.js`, send it: `transport.send('mything.do|' + arg)`.
2. Route it in `RemoteServerWiring.cs` (or a `config.set` key in `RemoteServerWiring.Helpers.cs`) to a VM method or command.
3. If it actuates (steering, sections, turns), add it to the Tier-2 list so it needs control.

### Showing new state in the client
1. Add it to the DTO in `Contracts.cs`, fill it in `SceneProjector` (or the relevant projector).
2. Encode it in `WireCodec.cs` and decode it in the same order in `transport.js`.
3. Render it in `app.js`. Frames are versioned by content hash where that matters (`SceneProjector`).

### Adding UI text
Write it in English; `index.html` needs nothing more, and `app.js` strings use `tr('…')`
(`tr('Pass {n}', { n })` for values). Then run `Tools/i18n-extract.py` and commit `en.json`.
See `Docs/TRANSLATIONS.md`.

### Verifying a change in the running app
Run the Desktop head `--headless` with `AGOPENWEB_DATA=<dir>` and drive it from a browser or
Playwright: wait for `iHoldControl === true`, then `transport.send(...)`; read `tick`,
`lastTick`, `scene`, `statusBar`, `config`. Kill any older instance first, or you test the
old build on `:5174`.

## NTRIP Connection Format
The NTRIP client uses HTTP/1.1 format:
```
GET /mountpoint HTTP/1.1
Host: caster.example.com
Ntrip-Version: Ntrip/2.0
Authorization: Basic base64(username:password)
User-Agent: NTRIP AgOpenWeb
```

## Debugging Tips

1. **iOS simulator issues**: Use `xcrun simctl` commands directly if `dotnet build -t:Run` fails
2. **iOS device**: `xcrun devicectl device process launch --console --terminate-existing com.agopenweb.ios` streams the app's console; its web server is reachable from the Mac over the USB link
3. **Android**: Debug builds enable WebView debugging (`chrome://inspect`); `adb forward tcp:5599 tcp:5174` reaches the host
4. **A client shows something a fresh connection doesn't**: compare the incremental stream with the full snapshot (coverage deltas vs `Snapshot()`)
5. **Bug Report Dump** (main menu) zips settings, state, the field and the last 20 s of the GPS log
6. **iOS Release builds hang in CI**: Use Debug configuration (Release triggers AOT compilation that hangs on runners)

## Code Style

- **Cross-platform parity is mandatory.** All code MUST go in `Shared/` unless it requires platform-specific APIs. Platform projects contain only the shell, DI setup and platform services. See `CONTRIBUTING.md`.
- The web client is plain HTML/JS: no framework, no build step. Keep `app.js` idioms (DOM overlays for text, CanvasKit for the map).
- Use dependency injection for services
- Use shared `GeometryMath` utilities instead of duplicating distance/angle calculations
- Anything written to disk or a wire uses `CultureInfo.InvariantCulture` (see `CONTRIBUTING.md`)

## Testing

```bash
dotnet test Tests/AgOpenWeb.Models.Tests/
dotnet test Tests/AgOpenWeb.Services.Tests/
dotnet test Tests/AgOpenWeb.ViewModels.Tests/

# Legacy guidance algorithm test harness
dotnet run --project TestRunner/TestRunner.csproj
```

**Test projects:**
- `AgOpenWeb.Models.Tests` - GeometryMath, GeoConversion, boundary/curve utilities
- `AgOpenWeb.Services.Tests` - NMEA parsing, TrackGuidanceService, coverage, pipeline, U-turn (uses the virtual UDP modules in `Simulators/AgOpenWeb.VirtualModules`)
- `AgOpenWeb.ViewModels.Tests` - ViewModel / control-brain logic
- `AgOpenWeb.IntegrationTests` - test-support library (fake settings etc.); not a standalone test run

CI also runs `Tools/i18n-extract.py --check` so `en.json` stays current.

## U-Turn System

U-turns are planned by `YouTurnStateMachine` (automatic turns at the headland, and manual
turns from the on-screen buttons) with paths built by `YouTurnCreationService`:
- Automatic: entry leg into the headland, arc, exit leg back to the next track; triggered when the tractor reaches the turn start, with an approach alarm 20 m out.
- Manual: a Dubins path at the configured `UTurnRadius` from 4 m ahead of the tractor to the point abreast on the target pass (an omega when the radius exceeds half the pass offset), laid out along the track's local heading (the curve's heading at the nearest point); the path's first point is the tractor itself. Like AgOpenGPS `BuildManualYouTurn`.
- The turn hands back to line guidance with 4 m of arc remaining (`EarlyCompletionLookahead`).

Key parameters:
- `HeadlandDistance` - width of headland zone (green to yellow line)
- `turnRadius` - half of track offset (based on implement width x row skip)
- Arc positioning: `headlandLegLength = max(HeadlandDistance - turnRadius, 2.0)`

The arc must fit between the headland boundary (green line) and outer boundary (yellow line). If the headland is too narrow for the turn radius, the arc will extend past the outer boundary.

## CI/CD

Two GitHub Actions workflows, split by purpose:

- **`build-and-release.yml`** ("CI") — on every push / PR to `main`: runs the test suite plus a
  compile-check of each platform head (Desktop, Android, iOS). No packaging, no releases.
- **`build-deploy-bundles.yml`** — the single packaging + release publisher. Runs the
  `deploy/{linux,windows,macos}/package.sh` scripts + builds the signed Android APK on clean runners:
  - on a **`v*` tag** (or manual dispatch with a tag) → publishes one complete Release with every
    artifact: Linux daemon (x64/arm64) + desktop launcher (x64/arm64) tarballs, Windows zip
    (launcher + service installer), macOS `.dmg`, and the Android APK;
  - on a **daily schedule** → refreshes a rolling `nightly` prerelease with the same artifacts;
  - on a manual dispatch, by its `publish` input: `nightly` (default) refreshes the rolling
    nightly from `develop` right away (title carries build time + commit); `release` publishes
    the Release named by `release_tag`; `none` builds + uploads artifacts only (dry run).

To cut a release: bump `sys/version.h`, then push a tag — `git tag v26.6.x && git push origin v26.6.x`.

## Legacy Code

- `ABLine.cs` - Marked `[Obsolete]`, retained only for AgOpenGPS file I/O compatibility
- Use `Track` model for all new guidance code
- `TestRunner/` - Legacy console test harness, superseded by NUnit test projects in `Tests/`
- `Simulators/AgOpenWeb.VehicleSimulator` - Avalonia desktop dev tool, kept as is
