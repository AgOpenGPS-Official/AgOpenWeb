# CLAUDE.md - AgOpenWeb3

This file provides guidance to Claude Code when working with this repository.

**Key documentation:**
- **[Plans/ARCHITECTURE.md](Plans/ARCHITECTURE.md)** - Full architecture: services, state management, data flow
- **[CONTRIBUTING.md](CONTRIBUTING.md)** - Contributor guide with cross-platform parity rules
- **[PGN.md](PGN.md)** - UDP packet protocol for hardware communication
- **[Docs/TRANSLATIONS.md](Docs/TRANSLATIONS.md)** - UI strings and Weblate
- **[Docs/GPS_RECEIVERS.md](Docs/GPS_RECEIVERS.md)** - Receivers, sentence families, the bridge contract, per-receiver setup

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

### Control authority
Several browsers may be connected; only one holds control (`ControlAuthority`, deadman model).
Every command is dropped unless the sender holds fresh control: the others are observers, who
see everything and change nothing (#397). `ObserverIds` lists the few requests an observer may
send. A client can't take control from a live operator, only fill an empty seat.

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
`Plans/AVALONIA_REMOVAL_PLAN.md`.

## Common Tasks

### Adding a web command
1. In `app.js`, send it: `transport.send('mything.do|' + arg)`.
2. Route it in `RemoteServerWiring.cs` (or a `config.set` key in `RemoteServerWiring.Helpers.cs`) to a VM method or command.
3. It needs control automatically; add it to `ObserverIds` only if it changes nothing.

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
`NtripSession` (`Services/Ntrip/`) makes the connection and asks with NTRIP 2.0:
```
GET /mountpoint HTTP/1.1
Host: caster.example.com:2101
Ntrip-Version: Ntrip/2.0
User-Agent: NTRIP AgOpenWeb
Authorization: Basic base64(username:password)
Ntrip-GGA: $GPGGA,...
Connection: close
```
A caster that did not understand it (a reply that is no NTRIP or HTTP reply, or HTTP 400 or 505) gets the
NTRIP 1.0 request (`GET /mountpoint HTTP/1.0`, no Host or version header, the position sent right after
the answer) once; never after a 401, 403, 404, an error text or a source table. The version that delivered
corrections is remembered per service. `NtripResponse.Classify` sorts every reply into an `NtripCategory`
(wrong login and unknown mount point end the session, the rest is retried after 1, 2, 4, 8, 15 s, and a
network change ends the wait); `NtripConnectionTester` (the Test button) opens its connection the same way.
Every wait has a timeout (`NtripTimeouts`). Design: `Plans/NTRIP_MAIN_FALLBACK_PLAN.md`.

**Main and fallback service.** Two exclusive modes (`Connections.NtripProfilePerField`, `config.set|conn.ntripProfilePerField`).
The default is one profile (the default profile) with an optional fallback service (`NtripProfile.Fallback`): a field load
changes nothing. With the setting on, a loaded field switches the profile and the fallback is not used. The client
(`NtripClientService`, rules in `NtripFailover`) switches to the fallback by itself only with the profile's Auto-switch on:
after `SwitchAfterFailures` failed attempts, or at once for a wrong login or mount point. The operator switches in both
directions at any time (`ntrip.switch|main|fallback`, `INtripClientService.SwitchToAsync`). It **never goes back by itself**:
while the fallback is in use after a failure, the main service is tried slowly in the background and a `MainBack` notice
says when it answers; not when both are one login on one caster. Every switch raises `Notice` and says that RTK may be lost.
In the web client the Network IO card shows it (`renderNtripSession` in `app.js`: headline, advice, checks, the switch,
the mode) from the `NtripSessionDto` that ends the Status frame; a profile's fallback ends its entry in the profiles
frame; `ntrip.save` carries it (`NtripProfileFields`), `ntrip.test` can test it. A switch is a toast, "the main service
is back" is the host's confirmation dialog.

### RTCM forwarding
`NtripClientService` frames the caster's stream into RTCM 3 messages (`RtcmFramer`, after
`ChunkedDecoder` if the reply is chunked) and queues whole messages (`RtcmQueue`): 256-byte
datagrams about 25 ms apart, never closer than 10 ms; on a backlog the newest observation
epoch and newest station data win, and nothing is cut mid-message. Datagrams go to the GPS
module's own address when its position sentences have been heard in the last 10 s, else to
the subnet broadcast (or always, with Network IO → "Broadcast corrections"). Network IO shows
the receiver's correction age, warnings and a message table; bug report dumps carry
`ntrip_rtcm.txt`. Design, bench results and open checks: `Plans/RTCM_FORWARDING_PLAN.md`;
bench scripts: `Tools/rtcm-bench`.

## ISOBUS virtual terminal (VT card)
The VT cards show ISOBUS implements' own screens. `Shared/AgOpenWeb.VirtualTerminal`
(`IVirtualTerminalService`) runs SharpIsoTerm's terminal stack on the implement bus through
an AOW link gateway (`AppSettings.VtGateway`, port 2323; the HAT's `aow-linkd`) or an slcan USB
adapter on the device itself (`AppSettings.VtAdapter`, a CANable; not on iOS). Gateways announcing
themselves by DNS-SD (`_aowlink._tcp`) are listed in the card's gear by name (`AowLinkBrowser`); one
picked from the list is kept by name too (`VtGatewayName`) and followed to a new address. One to four
terminals (`AppSettings.VtCount`), as displays in one cab, each implement choosing its terminal in
its own menu, one card each. Their messages and pictures go to the browsers as `Vt` (with the
terminal) and `VtImage` frames (`VirtualTerminalRemote`), the painter is in `app.js`, and each
card sends `vt.*` commands naming its terminal. Every head has it (iOS: device builds). SharpIsoTerm
comes as its NuGet package from its GitHub release, pinned by version and SHA-256 in
`Shared/AgOpenWeb.VirtualTerminal/AgOpenWeb.VirtualTerminal.csproj` and fetched by the build into
`nuget-local/` (a package source in `nuget.config`); to take a new SharpIsoTerm, change both to its
release's. To work on SharpIsoTerm itself, build against a checkout instead:
`dotnet build Platforms/AgOpenWeb.Desktop/AgOpenWeb.Desktop.csproj -p:SharpIsoTermDir=$HOME/Code/SharpIsoTerm`
(an absolute path; its native libraries built with its `native/build.sh`). The service
is disposed with the backend: on Android and iOS the process outlives it, and the library allows
one stack per process.

## Debugging Tips

1. **iOS simulator issues**: Use `xcrun simctl` commands directly if `dotnet build -t:Run` fails
2. **iOS device**: `xcrun devicectl device process launch --console --terminate-existing com.agopenweb.ios` streams the app's console; its web server is reachable from the Mac over the USB link
3. **Android**: Debug builds enable WebView debugging (`chrome://inspect`); `adb forward tcp:5599 tcp:5174` reaches the host
4. **A client shows something a fresh connection doesn't**: compare the incremental stream with the full snapshot (coverage deltas vs `Snapshot()`)
5. **Bug Report Dump** (main menu) zips settings, state, the field, the active job's coverage tiles and the last 5 minutes of the GPS log (10 Hz; includes the sentence type, its pre-fusion heading and the receiver's differential age); with NTRIP in use, `ntrip_rtcm.txt` lists the RTCM messages the caster sent by type and `ntrip_session.txt` what the connection did and why it failed (no login in it; `system_info.txt` has an `NTRIP:` line with the same outcome)
6. **iOS Release builds hang in CI**: Use Debug configuration (Release triggers AOT compilation that hangs on runners). Simulator builds run on the interpreter (`MtouchInterpreter=all` in the csproj), so they skip AOT altogether; device builds keep it

## Code Style

- **Cross-platform parity is mandatory.** All code MUST go in `Shared/` unless it requires platform-specific APIs. Platform projects contain only the shell, DI setup and platform services. See `CONTRIBUTING.md`.
- The web client is plain HTML/JS: no framework, no build step. Keep `app.js` idioms (DOM overlays for text, CanvasKit for the map).
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

CI also runs `Tools/i18n-extract.py --check` so `en.json` stays current, `Tools/license-headers.py --check` for the licence headers, and `Tools/i18n-limits.py --check` (job `translations-fit`) so every translated label fits the English layout.

**Hot-path budgets** (job `hot-path-budgets`, `Tests/AgOpenWeb.Services.Tests/Performance`): a 300 × 500 m field, with a headland and with only the outer boundary, is driven through the real GPS cycle and the 100 Hz control tick, and every cycle is held to a budget of `WorkCounters` (polygon edges, coverage lookups, offset points, bytes allocated). Counts don't depend on the machine, so CI can enforce them. A failure means new work runs every cycle: do it once and keep the result. `HotPathBudgetTests.Report` (explicit) prints the counts. Run locally: `dotnet test Tests/AgOpenWeb.Services.Tests/ -c Release --filter TestCategory=HotPath`.

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
  - on a **`v*` tag** (or manual dispatch with a tag) → publishes one complete Release, marked
    prerelease so it never shows as "Latest" (the beta is the build users run), with every
    artifact: Linux daemon (x64/arm64) + desktop launcher (x64/arm64) tarballs, Windows zip
    (launcher + service installer), macOS `.dmg`, and the Android APK;
  - on a **daily schedule** → refreshes a rolling `beta` prerelease with the same artifacts, only
    when `develop` has moved on since the beta was built (otherwise the run builds nothing);
  - on a manual dispatch, by its `publish` input: `beta` (default) refreshes the rolling
    beta from `develop` right away (title carries build time + commit); `release` publishes
    the Release named by `release_tag`; `none` builds + uploads artifacts only (dry run).

To cut a release: bump `sys/version.h`, then push a tag — `git tag v26.6.x && git push origin v26.6.x`.

## Legacy Code

- `ABLine.cs` - Marked `[Obsolete]`, retained only for AgOpenGPS file I/O compatibility
- Use `Track` model for all new guidance code
- `TestRunner/` - Legacy console test harness, superseded by NUnit test projects in `Tests/`
- `Simulators/AgOpenWeb.VehicleSimulator` - Avalonia desktop dev tool, kept as is
