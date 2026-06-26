<!--
AgOpenWeb
Copyright (C) 2024-2026 AgOpenWeb Contributors

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program. If not, see <https://www.gnu.org/licenses/>.
-->

# AGENTS.md - AgOpenWeb3

This file provides guidance to Codex when working with this repository.

**Key documentation:**
- **[Plans/ARCHITECTURE.md](Plans/ARCHITECTURE.md)** - Full architecture: services, state management, data flow
- **[CONTRIBUTING.md](CONTRIBUTING.md)** - Contributor guide with cross-platform parity rules
- **[PGN.md](PGN.md)** - UDP packet protocol for hardware communication

## Project Overview

AgOpenWeb3 is a cross-platform agricultural GPS guidance application built with Avalonia UI. It's a clean rewrite achieving **91.7% shared code** across platforms.

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

```
AgOpenWeb3/
├── Shared/                              # ~92% - Platform-agnostic code
│   ├── AgOpenWeb.Models/            # Data models, geometry, configuration, DTOs
│   ├── AgOpenWeb.Services/          # Business logic, GPS, NTRIP, UDP
│   ├── AgOpenWeb.ViewModels/        # MVVM ViewModels (ReactiveUI)
│   └── AgOpenWeb.Views/             # Shared UI controls, panels, dialogs
│
├── Platforms/                           # ~8% - Platform-specific code
│   ├── AgOpenWeb.Desktop/           # Windows/macOS/Linux
│   ├── AgOpenWeb.iOS/              # iOS/iPadOS
│   └── AgOpenWeb.Android/          # Android
│
├── Tests/                              # NUnit test projects
│   ├── AgOpenWeb.Models.Tests/     # Geometry, coordinate conversion (72 tests)
│   ├── AgOpenWeb.Services.Tests/   # NMEA parsing, guidance (21 tests)
│   └── AgOpenWeb.UI.Tests/         # Headless UI tests via Avalonia.Headless (18 tests)
│
├── TestRunner/                         # Legacy test harness for guidance algorithms
└── AgOpenWeb.sln                    # Solution file
```

### Platform Support

| Platform | Project | Notes |
|----------|---------|-------|
| Windows | AgOpenWeb.Desktop | Same codebase as macOS/Linux |
| macOS | AgOpenWeb.Desktop | Same codebase as Windows/Linux |
| Linux | AgOpenWeb.Desktop | Same codebase as Windows/macOS |
| iOS/iPadOS | AgOpenWeb.iOS | Requires Xcode 26.3+, runs on ARM64 simulator |
| Android | AgOpenWeb.Android | APK build, sideload install |

## Build Commands

```bash
# Build and run Desktop (works on Windows, macOS, Linux)
dotnet build Platforms/AgOpenWeb.Desktop/AgOpenWeb.Desktop.csproj
dotnet run --project Platforms/AgOpenWeb.Desktop/AgOpenWeb.Desktop.csproj

# Build iOS (requires macOS with Xcode 26.3+)
dotnet build Platforms/AgOpenWeb.iOS/AgOpenWeb.iOS.csproj -c Debug -f net10.0-ios -r iossimulator-arm64

# Deploy and run iOS on simulator
dotnet build Platforms/AgOpenWeb.iOS/AgOpenWeb.iOS.csproj -c Debug -f net10.0-ios -r iossimulator-arm64 -t:Run

# Alternative iOS deployment (if -t:Run doesn't work)
xcrun simctl install booted Platforms/AgOpenWeb.iOS/bin/Debug/net10.0-ios/iossimulator-arm64/AgOpenWeb.iOS.app
xcrun simctl launch booted com.agopenweb.ios

# Build Android APK
dotnet build Platforms/AgOpenWeb.Android/AgOpenWeb.Android.csproj

# Build entire solution
dotnet build AgOpenWeb.sln

# Run tests
dotnet test Tests/
```

## Key Design Decisions

### Rendering: SkiaMapControl via CompositionCustomVisualHandler
All platforms render the map through `SkiaMapControl`, which leases the Skia
GPU surface inside a `CompositionCustomVisualHandler` and re-arms via
`RegisterForNextAnimationFrameUpdate`. This bucket sits outside the Av12
commit throttle that capped `OpenGlControlBase` at ~32 FPS on iPad
([issue #21409](https://github.com/AvaloniaUI/Avalonia/issues/21409)).
True perspective comes from `SKMatrix44`; top-down mode is just
`pitch = 90°` on the same control — no second renderer behind a toggle.

### Shared UI Components
All panels, dialogs, and controls live in `AgOpenWeb.Views`:
- `Controls/SkiaMapControl.cs` - Main map rendering
- `Controls/DialogOverlayHost.axaml` - Hosts all modal dialog overlays (shared across platforms)
- `Controls/Panels/` - LeftNavigationPanel, SimulatorPanel, SectionControlPanel, etc.
- `Controls/Dialogs/` - All modal dialogs (FieldSelection, DataIO, AgShare, etc.)
- `Converters/` - Shared value converters (BoolToColor, FixQualityToColor, etc.)

### Dialog System
Dialogs use a centralized state machine in `UIState`. Only one dialog can be open at a time.

**To show a dialog from a ViewModel:**
```csharp
State.UI.ShowDialog(DialogType.YourDialog);  // Opens dialog
State.UI.CloseDialog();                       // Closes any open dialog
```

**Dialog visibility in AXAML** binds to computed properties on `UIState`:
```xml
<dialogs:YourDialogPanel
    IsVisible="{Binding State.UI.IsYourDialogVisible}"
    IsHitTestVisible="{Binding State.UI.IsYourDialogVisible}"/>
```

All dialog panels are registered in `DialogOverlayHost.axaml` (shared, not per-platform).

**Confirmation dialogs** use a callback pattern:
```csharp
ShowConfirmationDialog("Title", "Message", () => { /* on confirm */ });
```

### MainViewModel Structure
`MainViewModel` is a large partial class split across ~19 files by domain:

| File | Domain |
|------|--------|
| `MainViewModel.cs` | Core state, constructor, DI, properties |
| `MainViewModel.Commands.Track.cs` | Track/AB line commands |
| `MainViewModel.Commands.Boundary.cs` | Boundary, headland, AgShare commands |
| `MainViewModel.Commands.Fields.cs` | Field open/close/create commands |
| `MainViewModel.Commands.Ntrip.cs` | NTRIP profile management |
| `MainViewModel.Commands.Navigation.cs` | View settings, camera, zoom |
| `MainViewModel.Commands.Hotkeys.cs` | Hotkey configuration and dispatch |
| `MainViewModel.Commands.Settings.cs` | App directories, reset settings |
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

### ConfigurationStore
`ConfigurationStore` is a reactive singleton holding all runtime configuration (vehicle, tool, guidance, hotkeys, etc.). It syncs to/from `AppSettings` JSON via `ConfigurationService`.

```csharp
ConfigStore.Vehicle.AntennaHeight    // Vehicle config
ConfigStore.Tool.ToolWidth           // Tool/implement config
ConfigStore.Hotkeys.GetActionForKey("A")  // Hotkey lookup
```

### Draggable Panels
Panels use Canvas positioning with pointer event handlers for dragging:
- Desktop: Handlers in `MainWindow.axaml.cs`
- iOS: Handlers in `MainView.axaml.cs`
- LeftNavigationPanel has built-in drag support for sub-panels

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

- **Current**: Legacy text formats (Field.txt, Boundary.txt, etc.) and XML profiles
- **Future**: Unified JSON formats (see `Plans/FILE_FORMAT_MODERNIZATION_PLAN.md`)
- **Migration**: Auto-detect legacy files, import once, save in new format only

## Technology Stack

- **.NET 10.0** - Target framework
- **Avalonia 11.3.9** - Cross-platform UI framework
- **ReactiveUI 20.1.1** - MVVM framework with reactive extensions
- **Microsoft.Extensions.DependencyInjection** - Dependency injection
- **NUnit 4.3 + Avalonia.Headless.NUnit** - Testing framework
- **NSubstitute** - Mocking for UI tests

## Key Files Reference

| File | Purpose |
|------|---------|
| `Shared/AgOpenWeb.ViewModels/MainViewModel.cs` | Main application state, constructor, DI |
| `Shared/AgOpenWeb.Views/Controls/SkiaMapControl.cs` | Map rendering (Skia via CompositionCustomVisualHandler) |
| `Shared/AgOpenWeb.Views/Controls/DialogOverlayHost.axaml` | All dialog overlay registrations |
| `Shared/AgOpenWeb.Views/Controls/Panels/LeftNavigationPanel.axaml` | Main navigation sidebar |
| `Shared/AgOpenWeb.Models/Track/Track.cs` | Unified track model (AB lines + curves) |
| `Shared/AgOpenWeb.Models/Base/GeometryMath.cs` | Shared geometry utilities |
| `Shared/AgOpenWeb.Models/State/UIState.cs` | Dialog state machine, panel visibility |
| `Shared/AgOpenWeb.Models/Configuration/ConfigurationStore.cs` | Reactive config singleton |
| `Shared/AgOpenWeb.Models/Configuration/HotkeyConfig.cs` | Hotkey bindings model |
| `Shared/AgOpenWeb.Services/Track/TrackGuidanceService.cs` | Pure Pursuit + Stanley guidance |
| `Shared/AgOpenWeb.Services/YouTurn/YouTurnGuidanceService.cs` | U-turn path following |
| `Shared/AgOpenWeb.Services/NtripClientService.cs` | NTRIP RTK corrections |
| `Shared/AgOpenWeb.Services/GpsService.cs` | GPS data processing |
| `Shared/AgOpenWeb.Services/ConfigurationService.cs` | AppSettings ↔ ConfigurationStore sync |
| `Platforms/AgOpenWeb.Desktop/Views/MainWindow.axaml` | Desktop main window |
| `Platforms/AgOpenWeb.iOS/Views/MainView.axaml` | iOS main view |
| `Tests/AgOpenWeb.UI.Tests/MainViewModelBuilder.cs` | Test helper: builds fully-mocked MainViewModel |

## Service Interfaces

Services use interface-based design in `Shared/AgOpenWeb.Services/Interfaces/`:
- `ITrackGuidanceService` - Unified guidance (Pure Pursuit + Stanley) for all track types
- `IGpsService` - GPS data processing and position updates
- `IUdpCommunicationService` - UDP communication with AgOpenGPS modules
- `INtripClientService` - NTRIP caster connections for RTK
- `IFieldService` - Field loading/saving/management
- `IBoundaryRecordingService` - Recording field boundaries
- `IMapService` - Map control registration and track/boundary rendering
- `IConfigurationService` - AppSettings ↔ ConfigurationStore sync, vehicle profiles
- `IVehicleProfileService` - Vehicle profile CRUD
- `INtripProfileService` - NTRIP profile CRUD
- `IAutoSteerService` - Zero-copy GPS→steering pipeline
- `ISettingsService` - AppSettings JSON persistence
- `ICoverageMapService` - Worked area tracking (triangle strips)
- `ISectionControlService` - Automatic section on/off based on coverage/boundaries

## Platform-Specific Code

Platform projects contain only what **must** differ per platform. All UI, dialogs, and business logic live in Shared.

### Desktop
- `App.axaml/cs` - Application entry point, DI container setup
- `Program.cs` - Main entry point
- `MainWindow.axaml/cs` - Window with drag handlers, hotkey dispatch, styles
- `Services/MapService.cs` - Map control registration
- `DependencyInjection/ServiceCollectionExtensions.cs` - DI setup

### iOS
- `App.axaml/cs` - Application entry point
- `AppDelegate.cs` - iOS app delegate
- `MainView.axaml/cs` - Main view with drag handlers
- `Services/MapService.cs` - Map control registration
- `Info.plist` - iOS app configuration

### Android
- `App.axaml/cs` - Application entry point
- `MainActivity.cs` - Android activity with immersive mode
- `MainView.axaml/cs` - Main view with drag handlers
- `Services/MapService.cs` - Map control registration

## Common Tasks

### Adding a New Dialog
1. Add a new `DialogType` enum value in `Shared/AgOpenWeb.Models/State/UIState.cs`
2. Add `IsYourDialogVisible` computed property and `RaisePropertyChanged` call in `UIState`
3. Create `YourDialogPanel.axaml/cs` in `Shared/AgOpenWeb.Views/Controls/Dialogs/`
   - Bind `IsVisible` and `IsHitTestVisible` to `State.UI.IsYourDialogVisible`
   - Include semi-transparent backdrop with `PointerPressed` handler to close
4. Register in `Shared/AgOpenWeb.Views/Controls/DialogOverlayHost.axaml`
5. Add show/close commands in a `MainViewModel.Commands.*.cs` partial class file:
   ```csharp
   ShowYourDialogCommand = ReactiveCommand.Create(() =>
       State.UI.ShowDialog(DialogType.YourDialog));
   CloseYourDialogCommand = ReactiveCommand.Create(() =>
       State.UI.CloseDialog());
   ```

### Adding a New Panel
1. Create `YourPanel.axaml/cs` in `Shared/AgOpenWeb.Views/Controls/Panels/`
2. Add to `LeftNavigationPanel.axaml` if it's a sub-panel
3. Or add directly to platform views if standalone

### Frame Rate
`SkiaMapControl` drives frames via `RegisterForNextAnimationFrameUpdate`,
which runs at the platform's display refresh rate (60 Hz on most devices,
120 Hz on ProMotion iPad). There is no fixed-rate `DispatcherTimer`. To
gate redraws on state changes (instead of every animation tick), see
`SendStateToHandler` and the `_pendingComposite` coalescing logic.

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
2. **Frame rate**: ARM64 Macs handle 60 FPS fine; Intel Macs may need 10-15 FPS due to emulation
3. **Dialog not showing**: Check `DialogType` enum, `UIState` visibility property, and `DialogOverlayHost.axaml` registration
4. **Panel not dragging**: Verify Canvas positioning and pointer event handlers
5. **iOS Release builds hang in CI**: Use Debug configuration (Release triggers AOT compilation that hangs on runners)

## Code Style

- **Cross-platform parity is mandatory.** All code MUST go in `Shared/` unless it requires platform-specific APIs. Platforms only contain: app entry point, DI setup, MainWindow/MainView shell with drag handlers, MapService registration. See `CONTRIBUTING.md` for examples of violations fixed in #187-192.
- Use `Classes.Active` binding for state-based styling instead of converters where possible
- Dialogs are overlay panels via `DialogOverlayHost`, not separate windows
- Use dependency injection for services
- Use shared `GeometryMath` utilities instead of duplicating distance/angle calculations
- New dialog state goes through `UIState.ShowDialog(DialogType.X)`, not ad-hoc boolean properties

## Testing

```bash
# Run all tests (111 total: 72 model + 21 service + 18 UI)
dotnet test Tests/

# Run specific test project
dotnet test Tests/AgOpenWeb.Models.Tests/
dotnet test Tests/AgOpenWeb.Services.Tests/
dotnet test Tests/AgOpenWeb.UI.Tests/

# Legacy guidance algorithm test harness
dotnet run --project TestRunner/TestRunner.csproj
```

**Test projects:**
- `AgOpenWeb.Models.Tests` - GeometryMath, GeoConversion, boundary/curve utilities
- `AgOpenWeb.Services.Tests` - NMEA parsing, TrackGuidanceService
- `AgOpenWeb.UI.Tests` - Headless Avalonia UI tests using `[AvaloniaTest]` attribute, `MainViewModelBuilder` for fully-mocked VM construction

## U-Turn System

U-turns are generated in `MainViewModel.CreateSimpleUTurnPath()`:
- Entry leg: straight line from cultivated area into headland
- Arc: semicircle positioned so it fits within headland zone
- Exit leg: straight line back to next track

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
  - on a plain dispatch with no tag → builds + uploads artifacts only (dry run, no Release).

To cut a release: bump `sys/version.h`, then push a tag — `git tag v26.6.x && git push origin v26.6.x`.

## Legacy Code

- `ABLine.cs` - Marked `[Obsolete]`, retained only for AgOpenGPS file I/O compatibility
- Use `Track` model for all new guidance code
- `TestRunner/` - Legacy console test harness, superseded by NUnit test projects in `Tests/`
