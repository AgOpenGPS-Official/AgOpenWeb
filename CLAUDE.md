# CLAUDE.md - AgOpenWeb3

This file provides guidance to Claude Code when working with this repository.

**Key documentation:**
- **[Docs/ARCHITECTURE.md](Docs/ARCHITECTURE.md)** - Full architecture: services, state management, data flow
- **[CONTRIBUTING.md](CONTRIBUTING.md)** - Contributor guide with cross-platform parity rules
- **[PGN.md](PGN.md)** - UDP packet protocol for hardware communication
- **[Docs/TRANSLATIONS.md](Docs/TRANSLATIONS.md)** - UI strings and Weblate
- **[Docs/GPS_RECEIVERS.md](Docs/GPS_RECEIVERS.md)** - Receivers, sentence families, the bridge contract, per-receiver setup
- **[Docs/WEB_CLIENT.md](Docs/WEB_CLIENT.md)** - The web client's files (`js/`, `css/`), how the scripts load, adding a panel
- **[Docs/ADD_ON_CARDS.md](Docs/ADD_ON_CARDS.md)** - For add-on developers: web cards and VT cards, gateways, discovery
- **[Docs/FIELD_FILE_FORMATS.md](Docs/FIELD_FILE_FORMATS.md)** - `field.geojson`, sidecars and coverage tiles: the spec for tools that read or write fields

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
the `wwwroot` CanvasKit PWA (`index.html`, the client in `js/`, its styles in `css/`, `transport.js`,
`i18n.js`; map in `Docs/WEB_CLIENT.md`). Every head
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
│   ├── AgOpenWeb.RemoteServer.Tests/ # Web host: satellite tiles, imagery capture
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
dotnet test Tests/AgOpenWeb.Models.Tests/ ; dotnet test Tests/AgOpenWeb.Services.Tests/ ; dotnet test Tests/AgOpenWeb.ViewModels.Tests/ ; dotnet test Tests/AgOpenWeb.RemoteServer.Tests/
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
Long lines cost the most to draw: Skia strokes a bent, anti-aliased path through a CPU mask the
size of the path on the screen, so a curve that goes round the field costs a screen-sized mask.
Lines from the `Scene` are drawn with `strokeFixedPtsSk` (`js/map-draw.js`: a copy without the
points that add nothing, made once per array; the host replaces its arrays, the page never edits
them); a line the page draws or edits uses `strokePtsSk`. Lines in one style share one path
(`strokeCmdsSk`), as the extra guidelines do.

### Control authority
Several browsers may be connected; only one holds control (`ControlAuthority`, deadman model).
Every command is dropped unless the sender holds fresh control: the others are observers, who
see everything and change nothing (#397). `ObserverIds` lists the few requests an observer may
send. A client can't take control from a live operator, only fill an empty seat.
The deadman (1.5 s without the holder's presence) runs on a timer of its own. The page sends
presence on a timer and on every frame from the host (`sendPresence` in `js/control.js`): a
minimized window or a background tab gets its timers throttled but still hears the host (#766). When control
ends, `MainViewModel.OnBrowserControlEnded` turns off AutoSteer, Free Drive, the sections (a
section On by hand too) and the steer wizard, and the next screen to take control is told why.
`WebSocketHub` gives each browser its own outgoing queue: a broadcast never waits on a socket,
and a browser that stops reading for 15 s is cut off (it reconnects and gets a fresh seed).
The page drops a link that has carried no frame for 2.5 s and reconnects (`transport.js`).
The app's log (a dump's `logs.txt`) says why a screen went: each browser arriving and leaving with the reason
(closed by the page, cut off, link failed), a deadman with the state of its link, and what the page saw of itself
(`diag.page`: hidden, frozen, its timers stopped by the system, offline, a link given up), sent on its next link.

### Section control timing
`SectionControlService` runs every section by its own button state (Off / Auto / On); the master
buttons only set them all, as AgOpenGPS. In Auto the look-ahead asks for on/off early: the
**module** gets that request at once (`IsCommandedOn` → PGN 239/229 bits), while `IsOn` and the
coverage map wait the look-ahead time, so the valve acts at the edge and the map paints where
the product lands (#707). With look-ahead off at 0 the turn-off delay is a plain overrun of both. Look-ahead
installs at AgOpenGPS's 1.0 s on / 0.5 s off (`ToolConfig.DefaultLookAheadOn/Off`): the time it
assumes a sprayer's valves take; saved profiles keep their own.
The control loop sends the PGNs at 100 Hz, PGN 229 every tick whatever the section count (the
ISOBUS task controller reads sections from 229 only).

### The simulator never steers the module
The steer module stays on the network while the simulator runs. While the simulator is the chosen GPS
source (`GpsPipelineService.SetSimulatorEnabled`, from the moment it is chosen) `VehicleState.SimulatorHoldsSteering`
is set (`IAutoSteerService.SetSimulatorHoldsSteering`) and `PgnBuilder.BuildAutoSteerPgn` sends status 0:
the module would open the lock valve and steer a parked tractor to a simulated line. AutoSteer stays
engaged in the app, so the simulator follows the line, and the red "module not steering" button is not
shown. Turning the simulator on or off (the receiver's fix arriving does it too) takes AutoSteer off
first. Free Drive is the operator's own test of the steering and still sends status 1; PGN 239/229
(sections, lift) still go to the machine module, as a bench test of a sprayer needs. The hold is the
app's simulator, not the label on a fix: the scenario rigs feed their own fixes as
`GpsSentenceType.Simulator` with the simulator off, and are steered.

**Bench choice** (a steering motor on the desk): not in the UI yet (an enhancement). The seam is there:
`MainViewModel.IsSimulatorDrivingSteering` (`GpsPipelineService.SetSimulatorSteersModule`) lets AutoSteer on
the simulator send status 1; never saved, dropped when the simulator is turned off and when the steer module
goes silent, and a change takes AutoSteer off first. The web client harness reads PGN 254 on the simulator,
so its rig sets it (`WebClientRig`).

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
| `MainViewModel.FieldData.cs` | Field data card: trip distance, boundary area |
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

- **Formats**: a field is `field.geojson` (origin, boundaries, headland, tracks, flags, headland lines, background image placement) beside `background.png`, `contours.geojson`, `recorded-paths.geojson` and `elevation.csv`; a job's coverage is world-anchored tiles under `jobs/<task>/coverage/`. Profiles are JSON; the spec for other programs is `Docs/FIELD_FILE_FORMATS.md` (keep it in step with the code).
- **Migration**: AgOpenGPS files found in a field folder are imported once (on open) and **deleted**; AgOpenWeb never writes AgOpenGPS formats. AgOpenGPS 6.8.2+ profiles copied into the data folder (`VehicleProfiles/`, `ToolProfiles/`, `Environment/environment.xml` or, on a fresh 6.8.2+ install, `DefaultEnvironment.xml`, as AgOpenGPS reads them) and AgIO's NTRIP caster (`AgIO/*.xml`; not the one AgIO ships with, and the default only if NTRIP was on there) are converted at start the same way (`AgOpenGpsProfileImport`, one mapping in `AgOpenGpsSettings`, which turns AgOpenGPS's signs and units into AgOpenWeb's; `NtripProfileService.ImportAgIoCastersAsync`), a profile of the same name left alone. An AB line under 10 m (AgOpenGPS's A+ line puts B 1 m from A) is read with B 100 m out along its heading (`Track.LengthenShortABLine`, from `field.geojson`, `TrackLines.txt` and `ABLines.txt`): an offset pass leaves out every point within 1 m of the one before, so it had no pass but the line itself. No compatibility code for files from older AgOpenWeb builds (no installed base yet). See `Plans/Completed/FILE_FORMAT_MODERNIZATION_PLAN.md`.

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
| `Shared/AgOpenWeb.RemoteServer/wwwroot/js/` | The web client, one classic script per feature (`Docs/WEB_CLIENT.md`) |
| `Shared/AgOpenWeb.RemoteServer/wwwroot/i18n.js` | Client-side translation |
| `Shared/AgOpenWeb.Models/Track/Track.cs` | Unified track model (AB lines + curves) |
| `Shared/AgOpenWeb.Models/Base/GeometryMath.cs` | Shared geometry utilities |
| `Shared/AgOpenWeb.Models/Configuration/ConfigurationStore.cs` | Reactive config singleton |
| `Shared/AgOpenWeb.Services/Track/TrackGuidanceService.cs` | Pure Pursuit + Stanley guidance |
| `Shared/AgOpenWeb.Services/YouTurn/YouTurnStateMachine.cs` | U-turn planning and following |
| `Shared/AgOpenWeb.Services/Coverage/CoverageMapService.cs` | Coverage detection + display grids, tiles |
| `Shared/AgOpenWeb.Services/Ntrip/NtripSession.cs` | NTRIP connection: request, reply, retry, timeouts, status |
| `Shared/AgOpenWeb.Services/NtripClientService.cs` | NTRIP RTK corrections: frames the stream, forwards it to the GPS module |
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
- `INetworkWatcher` - the one place that notices network changes (plus a 30 s heartbeat); UDP discovery, NTRIP retry and the bug report outbox subscribe in that order
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
`Plans/Completed/AVALONIA_REMOVAL_PLAN.md`.

## Common Tasks

### Adding a web command
1. In the feature's file in `js/`, send it: `transport.send('mything.do|' + arg)`.
2. Route it in `RemoteServerWiring.cs` (or a `config.set` key in `RemoteServerWiring.Helpers.cs`) to a VM method or command.
3. It needs control automatically; add it to `ObserverIds` only if it changes nothing.

### Showing new state in the client
1. Add it to the DTO in `Contracts.cs`, fill it in `SceneProjector` (or the relevant projector).
2. Encode it in `WireCodec.cs` and decode it in the same order in `transport.js`.
3. Render it in the feature's file in `js/`. Frames are versioned by content hash where that matters (`SceneProjector`).

### Adding UI text
Write it in English; `index.html` needs nothing more, and strings in `js/` use `tr('…')`
(`tr('Pass {n}', { n })` for values). Then run `Tools/i18n-extract.py` and commit `en.json`.
See `Docs/TRANSLATIONS.md`.

### Verifying a change in the running app
Run the Desktop head `--headless` with `AGOPENWEB_DATA=<dir>` and drive it from a browser or
Playwright: wait for `iHoldControl === true`, then `transport.send(...)`; read `tick`,
`lastTick`, `scene`, `statusBar`, `config`. Kill any older instance first, or you test the
old build on `:5174`.

## NTRIP and RTCM
`NtripSession` (`Services/Ntrip/`) connects with NTRIP 2.0 and tries 1.0 once when the caster
doesn't understand it (never after a wrong login or mount point). `NtripResponse.Classify` sorts
every reply, every wait has a timeout (`NtripTimeouts`), and a network change ends a retry wait.
A profile may have a fallback service; the client switches to it by itself only with Auto-switch
on, and **never goes back by itself**. `NtripClientService` frames the stream into RTCM 3 messages
and forwards them to the GPS module. Request format, the two modes, the commands and RTCM
forwarding: `Docs/NTRIP.md`.

## Features switched off
The work is on the foundation and navigation first. The VT cards, web cards (with the card pane) and AgShare
are built but switched off (`FeatureSwitches` in `AgOpenWeb.Models`, `Default` none): the host doesn't register
the VT service (no terminal, no gateway browsing) and refuses `vt.*`, `webcard.*`, `agshare.*` and the
`conn.agShare*` settings; the page starts with them hidden (`fs-no-*` classes on the body, `css/base.css`)
and the Hello frame's feature byte shows the ones the host runs. Settings made for them are kept.
`AGOPENWEB_FEATURES=vt,webcards,agshare` (or `all`) turns them on for development. The ISOBUS task
controller's section control (#312) is not hidden.

## VT cards, web cards and the card pane
Built but switched off (above). How they work, the gateways, SharpIsoTerm (`-p:SharpIsoTermDir`
to build against a checkout) and the pane: `Docs/CARDS_INTERNALS.md`. What new map code must
still respect: an overlay keeps clear of the pane through `--sal` / `--sar` (a full-width bar
`--map-l` / `--map-r`, one centred with `left: 50%` adds `var(--map-dx)`, a window-wide panel
`--win-sal` / `--win-sar`); `vw` is the map's width, and a pointer is at `clientX - mapL` on the map.

## Debugging Tips

1. **iOS simulator issues**: Use `xcrun simctl` commands directly if `dotnet build -t:Run` fails
2. **iOS device**: `xcrun devicectl device process launch --console --terminate-existing com.agopenweb.ios` streams the app's console; its web server is reachable from the Mac over the USB link
3. **Android**: Debug builds enable WebView debugging (`chrome://inspect`); `adb forward tcp:5599 tcp:5174` reaches the host
4. **A client shows something a fresh connection doesn't**: compare the incremental stream with the full snapshot (coverage deltas vs `Snapshot()`)
5. **Bug Report Dump** (main menu) zips settings, state, the field, the active job's coverage tiles and the last 5 minutes of the GPS log (10 Hz; includes the sentence type, its pre-fusion heading and the receiver's differential age); with NTRIP in use, `ntrip_rtcm.txt` lists the RTCM messages the caster sent by type and `ntrip_session.txt` what the connection did and why it failed (no login in it; `system_info.txt` has an `NTRIP:` line with the same outcome). `system_info.txt` ends with each browser's GPU renderer, texture limit, canvas, frame rate, draw time and where the rest of a frame's time goes, for now and for the slowest stretch since the page loaded (`diag.client`, `ClientDiagnostics`; `js/alerts.js`), flagged `SOFTWARE RENDERING` when the renderer is the CPU (llvmpipe, SwiftShader): such a device will not draw the map smoothly whatever the app does
6. **iOS Release builds hang in CI**: Use Debug configuration (Release triggers AOT compilation that hangs on runners). Simulator builds run on the interpreter (`MtouchInterpreter=all` in the csproj), so they skip AOT altogether; device builds keep it
7. **The line moved to another pass**: a lateral move (the lateral buttons, the nudge flyout's Pass buttons) is logged (`[Guidance] Lateral move right: pass 19R -> 20R`, or ignored while a U-turn is driven); the AB line's debug heading is logged only when it changes, so a dump's 2000 log lines reach further back

## Code Style

- **Cross-platform parity is mandatory.** All code MUST go in `Shared/` unless it requires platform-specific APIs. Platform projects contain only the shell, DI setup and platform services. See `CONTRIBUTING.md`.
- The web client is plain HTML/JS: no framework, no build step. Keep its idioms (DOM overlays for text, CanvasKit for the map). Its scripts are classic scripts sharing one scope: code that runs while loading uses only earlier files, and anything asynchronous starts in `js/frame.js` (`Docs/WEB_CLIENT.md`).
- Use dependency injection for services
- Use shared `GeometryMath` utilities instead of duplicating distance/angle calculations
- Anything written to disk or a wire uses `CultureInfo.InvariantCulture` (see `CONTRIBUTING.md`)
- New `.cs`/`.js`/`.py`/`.sh` files start with the Apache 2.0 header (`// SPDX-License-Identifier: Apache-2.0`, see `CONTRIBUTING.md`); `Tools/license-headers.py` adds it, CI checks it

## Testing

```bash
dotnet test Tests/AgOpenWeb.Models.Tests/
dotnet test Tests/AgOpenWeb.Services.Tests/
dotnet test Tests/AgOpenWeb.ViewModels.Tests/
dotnet test Tests/AgOpenWeb.RemoteServer.Tests/

# Legacy guidance algorithm test harness
dotnet run --project TestRunner/TestRunner.csproj
```

**Test projects:**
- `AgOpenWeb.Models.Tests` - GeometryMath, GeoConversion, boundary/curve utilities
- `AgOpenWeb.Services.Tests` - NMEA parsing, TrackGuidanceService, coverage, pipeline, U-turn (uses the virtual UDP modules in `Simulators/AgOpenWeb.VirtualModules`)
- `AgOpenWeb.ViewModels.Tests` - ViewModel / control-brain logic
- `AgOpenWeb.RemoteServer.Tests` - web host: Bing tile replies, boundary imagery capture
- `AgOpenWeb.IntegrationTests` - test-support library (fake settings etc.); not a standalone test run
**Scenario harnesses** (eleven `*Scenarios.Tests` projects: fields, jobs, steer wizard, vehicle
and tool, machine module, GPS/NTRIP, ISOBUS, web client, updates, a working day, dump replay):
PR CI only builds them, and they run nightly. Run them only on the harness host
(`Tools/harness-runner`, docker context `runner`), never on a development machine; every harness
stops when another process holds UDP 9999 or 8888. A finding leaves the app's bug report dump
(`evidence/<job>.zip`): make the fix from it and prove it with a unit test built from it (failing
on develop, passing on the fix). A short named run (`AGOPENWEB_SCENARIO_ONLY`, `TURNS`,
`FROM_PASS`) is for a change to a harness itself. Each harness and the short-run settings:
`Docs/HARNESSES.md` and the harness's `README.md`.

**Hot-path budgets** (job `hot-path-budgets`): the GPS cycle, the 100 Hz control tick and the
host loop with a field open are held to budgets of `WorkCounters` (polygon edges, coverage
lookups, bytes allocated). A failure means new work runs every cycle: do it once and keep the
result. Run: `dotnet test Tests/AgOpenWeb.Services.Tests/ -c Release --filter TestCategory=HotPath`
and the same for `Tests/AgOpenWeb.VehicleToolScenarios.Tests/`.

CI also checks `en.json` (`Tools/i18n-extract.py --check`), the licence headers
(`Tools/license-headers.py --check`) and, when `wwwroot/` changes, that translated labels fit
(`Tools/i18n-limits.py --check`).

## U-Turn System
`YouTurnStateMachine` plans the turns (automatic at the turn line, manual from the buttons) and
`YouTurnCreationService` builds the paths. **Read `Docs/UTURN.md` before changing U-turn code**:
every rule is there with its reason. The ones most easily broken:
- **The turn line, not the headland**, as AgOpenGPS: the fence moved in by
  `UTurnDistanceFromBoundary` drives the trigger, zones and landing checks. The headland is for
  the sections, the lift and the drawing.
- **One end of a pass** is `PassEnds.Find` (walking the pass, the first point outside the turn
  line after it was inside); the distance to the turn, the arc start, the retry and the fallback
  all use it.
- The next pass needs 20 m of room at this end (`IsPathLandable`), else no turn and the operator
  is told why (`YouTurnEffects.TellOperator`, a toast; `StatusMessage` is not sent to the client).
- The fallback semicircle is used only if it stays in the field (`LeavesField`); otherwise no turn.
- `UTurnRadius` is the operator's, never filled in from the tractor.

## CI/CD
- `build-and-release.yml` ("CI"): the tests and a compile check of every head on each push and PR
  to `main` and `develop`.
- `build-deploy-bundles.yml`: packaging. A `v*` tag publishes a Release (marked prerelease); a
  daily run refreshes the rolling `beta` when `develop` has moved.
- Eleven nightly harness workflows, started by the bug report Worker (`Tools/bugreport-worker`).

Details: `Docs/CI.md`. To cut a release (about weekly): bump `sys/version.h` and the three heads'
display versions, then push a tag (`git tag v26.7.x && git push origin v26.7.x`).

## Legacy Code
`ABLine.cs` is `[Obsolete]` (AgOpenGPS file import only): use `Track`. `TestRunner/` is superseded
by `Tests/`; `Simulators/AgOpenWeb.VehicleSimulator` is an Avalonia dev tool, kept as is.
