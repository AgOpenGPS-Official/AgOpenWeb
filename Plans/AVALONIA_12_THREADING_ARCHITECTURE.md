# Avalonia 12 Threading Architecture Redesign

## Status: Planning

## Problem

The app works on Avalonia 11 because the UI thread handled everything — rendering, GPS processing, coverage updates, file I/O — and the single dispatcher kept it all cooperatively scheduled. On Avalonia 12, the compositor uses the UI thread more aggressively, and the dispatcher model changed (one per thread). Our Avalonia 11 patterns now cause ANR on Android and stuttering everywhere.

## Current Architecture (Avalonia 11 — broken on Avalonia 12)

```
UI Thread (does everything):
├── DispatcherTimer (33ms) → InvalidateVisual() → Render()
├── Simulator tick → GPS processing → property updates → coverage detection
├── Coverage service → SetCoveragePixel() (was Lock/Unlock per pixel)
├── Dispatcher.UIThread.Invoke() from GPS background thread (BLOCKING)
├── File I/O on field load/save/close (synchronous)
├── NTRIP connection callbacks (Invoke, blocking)
└── All property change notifications → UI updates
```

Everything fights for the same thread. On Avalonia 11, the cooperative scheduler made this work. On Avalonia 12, the compositor pre-empts, causing timeouts.

## Avalonia 12 Threading Model

From the docs:
- **UI thread**: Property changes, layout, input handling. Compositor schedules frames.
- **Render thread**: Scene graph composition, GPU operations. Managed by Avalonia.
- **Background threads**: App work that doesn't touch UI.

Key changes:
- `DispatcherTimer` binds to creating thread's dispatcher (not always UI thread)
- `Dispatcher.UIThread.Invoke()` blocks caller — deadlock risk with compositor
- `ICustomDrawOperation.Render()` runs during composition (may be render thread)
- `CompositionCustomVisualHandler.OnRender()` runs on render thread explicitly

## Proposed Architecture (Avalonia 12)

### Thread Separation

```
Background Thread(s):
├── GPS/Simulator processing (position calculation, guidance)
├── Coverage detection (point-in-polygon, cell marking)
├── Coverage pixel updates (write to SKBitmap directly)
├── File I/O (field load/save, settings)
├── NTRIP connection management
└── RLE compression/decompression

UI Thread (lightweight):
├── Property change notifications (batch, throttled)
├── Command execution (quick state changes only)
├── Layout and input handling
└── InvalidateVisual() scheduling

Render Thread (via ICustomDrawOperation / CompositionCustomVisualHandler):
├── Coverage bitmap drawing (from SKBitmap, lock-free)
├── Vehicle/tool/boundary rendering
├── Grid, track, headland rendering
└── Ground texture tiling
```

### Key Patterns

1. **Never `Invoke()` — always `Post()`**: Replace all `Dispatcher.UIThread.Invoke()` with `Post()`. If the caller needs a result, use `InvokeAsync()` with `await`.

2. **SKBitmap as primary coverage store**: Already done. `SetCoveragePixel` writes to SKBitmap without any Lock. WriteableBitmaps synced lazily for save.

3. **Batch property updates**: Instead of updating 20 properties per GPS tick on the UI thread, collect changes and post a single batch update at 10 Hz max.

4. **Async file I/O**: All `SaveToFile()`, `LoadFromFile()`, `SaveAppSettings()` become async and run on `Task.Run()`.

5. **GPS processing off UI thread**: The simulator tick and real GPS processing should happen on a dedicated background thread. Only the final property updates touch the UI thread via `Post()`.

6. **CompositionCustomVisualHandler for map**: Long-term, move the entire map rendering to the render thread. This completely decouples rendering from UI thread work.

## Implementation Phases

### Phase A: Stop blocking the UI thread (immediate, low risk)

Eliminate all synchronous blocking on the UI thread. This doesn't restructure the architecture but stops the ANR bleeding.

| Task | File | Change |
|------|------|--------|
| A1 | `MainViewModel.GpsHandling.cs` | `Dispatcher.UIThread.Invoke()` → `Post()` |
| A2 | `MainViewModel.Ntrip.cs` | `Dispatcher.UIThread.Invoke()` → `Post()` (2 calls) |
| A3 | `Android/Views/MainView.axaml.cs` | `MainView_Unloaded` → wrap saves in `Task.Run()` |
| A4 | `iOS/Views/MainView.axaml.cs` | Same as A3 |
| A5 | `Desktop/Views/MainWindow.axaml.cs` | `MainWindow_Closing` → async saves |
| A6 | `CoverageMapService.cs` | `LoadFromFile()` → `LoadFromFileAsync()` |
| A7 | `CoverageMapService.cs` | `SaveToFile()` → `SaveToFileAsync()` |
| A8 | `MainViewModel.cs` | Field open/close → `await Task.Run(() => loadOrSave)` |

**Test**: ANR dialogs should stop on Android. Field load/save should not freeze UI.

### Phase B: Extract ViewModel computation to services (the real fix)

Move the GPS processing pipeline out of the ViewModel and into background services. Each ViewModel partial becomes a service method call.

| Task | From (ViewModel) | To (Service) | What moves |
|------|-----------------|-------------|-----------|
| B1 | `MainViewModel.GpsHandling.cs` | `GpsService` (already exists) | `UpdateGpsProperties()` processing logic |
| B2 | `MainViewModel.Guidance.cs` | `TrackGuidanceService` (already exists) | `CalculateAutoSteerGuidance()` |
| B3 | `MainViewModel.YouTurn.cs` | `YouTurnGuidanceService` (already exists) | `ProcessYouTurn()`, `CreateSimpleUTurnPath()` |
| B4 | `MainViewModel.SectionControl.cs` | `SectionControlService` (already exists) | `UpdateSectionStates()` |
| B5 | `MainViewModel.Simulator.cs` | `SimulatorService` (already exists) | `OnSimulatorTick()` computation |
| B6 | `MainViewModel.BoundaryRecording.cs` | `BoundaryRecordingService` (already exists) | Recording logic |

**For each extraction:**
1. Move the computation code from the ViewModel partial into the existing service
2. The service runs the computation on a background thread (its own timer or event-driven)
3. The service exposes results via events or observable state
4. The ViewModel subscribes and updates bound properties via `Post()`
5. The ViewModel partial becomes a thin subscription + command dispatch file

**The GPS pipeline after Phase B:**
```
SimulatorService.Tick (background timer)
  → GpsService.ProcessPosition (background)
    → AutoSteerService.ProcessPosition (background)
      → TrackGuidanceService.Calculate (background)
      → SectionControlService.Update (background)
      → CoverageMapService.RasterizeQuad (background, writes SKBitmap)
    → YouTurnService.Check (background)
  → Batch results → Post to ViewModel (UI thread, throttled)
```

**Test**: Simulator runs smoothly on Android. Coverage paints without stutter. No ANR.

### Phase C: Batch property updates to ViewModel (performance)

Instead of posting individual property changes from services, collect them into a state snapshot and post once per GPS cycle (10 Hz max).

| Task | Change |
|------|--------|
| C1 | Create `GpsStateSnapshot` record — position, heading, speed, fix quality, steer angle, XTE, section states, coverage stats |
| C2 | Services populate the snapshot on the background thread |
| C3 | One `Dispatcher.UIThread.Post()` per GPS cycle delivers the snapshot |
| C4 | ViewModel's `ApplySnapshot()` sets all bound properties in one batch |
| C5 | Throttle: if UI thread is busy, skip the oldest snapshot (drop frames, not lag) |

**Result**: UI thread processes ~10 property-batch updates per second instead of ~300 individual property changes.

### Phase D: CompositionCustomVisualHandler for map rendering (future)

Move the entire map rendering to the render thread using Avalonia 12's composition API.

| Task | Change |
|------|--------|
| D1 | Create `MapRenderHandler : CompositionCustomVisualHandler` |
| D2 | Move all drawing code from `DrawingContextMapControl.Render()` to `OnRender(SKCanvas)` |
| D3 | Data passed from UI thread via `SendHandlerMessage()` |
| D4 | Map control becomes a thin host for the composition visual |
| D5 | Remove DispatcherTimer — use `RequestNextFrameRendering()` instead |

**Result**: UI thread is completely free for input and layout. Map renders at GPU native rate.

### Phase E: Compiled bindings (cleanup)

Fix the ~8 AXAML files that need `x:DataType` and enable `AvaloniaUseCompiledBindingsByDefault` in the Views project. Performance win on binding evaluation.

### Phase 0: Test harness for ViewModel behavior (BEFORE refactoring)

We need tests that validate the current behavior BEFORE we extract computation to services. These tests become the safety net for the refactor and the regression suite going forward.

#### 0A: Create `Tests/AgValoniaGPS.ViewModels.Tests/` project

A pure C# test project — no Avalonia headless dependency. Uses `MainViewModelBuilder` (moved from UI.Tests) with NSubstitute mocks.

```xml
<PackageReference Include="NUnit" Version="4.5.1" />
<PackageReference Include="NSubstitute" Version="5.3.0" />
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.0" />
<!-- NO Avalonia packages -->
```

#### 0B: Test current ViewModel computation (the code that's about to move)

| Test Area | ViewModel Partial | What to test |
|-----------|------------------|-------------|
| Guidance | `MainViewModel.Guidance.cs` | Given position off-track → steer angle is non-zero and toward track |
| Guidance | `MainViewModel.Guidance.cs` | Given position on-track → steer angle is near zero |
| Guidance | `MainViewModel.Guidance.cs` | Offset pass calculation produces correct offset |
| YouTurn | `MainViewModel.YouTurn.cs` | Approaching headland → U-turn path is generated |
| YouTurn | `MainViewModel.YouTurn.cs` | U-turn path has correct arc radius for implement width |
| Section Control | `MainViewModel.SectionControl.cs` | Sections turn on inside boundary |
| Section Control | `MainViewModel.SectionControl.cs` | Sections turn off outside boundary |
| Section Control | `MainViewModel.SectionControl.cs` | Sections turn off on already-covered area |
| GPS Handling | `MainViewModel.GpsHandling.cs` | GPS update triggers property changes (Lat, Lon, Speed, Heading) |
| GPS Handling | `MainViewModel.GpsHandling.cs` | Drift compensation applies correctly |
| Simulator | `MainViewModel.Simulator.cs` | Simulator tick advances position |
| Simulator | `MainViewModel.Simulator.cs` | Autosteer engaged → tractor follows track |
| Boundary Recording | `MainViewModel.BoundaryRecording.cs` | Recording accumulates points |
| Commands | Various | All commands execute without exceptions |
| Track Management | `MainViewModel.Commands.Track.cs` | Track selection updates active track |

#### 0C: Move `MainViewModelBuilder` to shared test utility

Currently in `UI.Tests`. Copy to `ViewModels.Tests` (or extract to a shared test helpers project) so both projects can build ViewModels with mocks.

#### 0D: Test services in isolation (already partly done)

Verify existing `Services.Tests` cover the service methods that will receive the extracted computation. Add tests for any gaps:

| Service | Existing Tests? | Gaps |
|---------|----------------|------|
| TrackGuidanceService | ✅ Yes (21 tests) | May need offset pass tests |
| YouTurnCreationService | ✅ Yes | May need headland approach tests |
| SectionControlService | ✅ Yes | May need coverage overlap tests |
| CoverageMapService | ✅ Yes | May need pixel write verification |
| GpsService | ✅ Yes | May need drift compensation tests |
| SimulatorService | ⬜ No | Need tick + position advance tests |

**After Phase 0**: We have a test suite that validates the current behavior. When we extract code in Phase B, we run these tests to prove nothing broke. Going forward, every ViewModel and service change is tested.

## Completion Checklist

| Phase | Status | Blocks |
|-------|--------|--------|
| CommunityToolkit.MVVM migration | ✅ Done | — |
| Avalonia 12 packages | ✅ Done | — |
| ICustomDrawOperation + SKBitmap | ✅ Done | — |
| Console.WriteLine → Debug.WriteLine | ✅ Done | — |
| **Phase 0: Test harness** | ⬜ Next | — |
| **Phase A: Stop blocking UI thread** | ⬜ | — |
| **Phase B: Extract ViewModel → services** | ⬜ | Phase 0, Phase A |
| **Phase C: Batch property updates** | ⬜ | Phase B |
| **Phase D: CompositionCustomVisualHandler** | ⬜ Future | Phase B |
| **Phase E: Compiled bindings** | ⬜ Anytime | — |

## What Actually Needs the UI Thread

The UI thread exists for one purpose: keep the screen responsive to the user. Anything that doesn't directly involve user interaction or screen updates should NOT be there.

### MUST be UI thread (Avalonia requires it)
- Property change notifications that trigger AXAML binding updates
- `InvalidateVisual()` calls
- Control creation/destruction
- Input event handling (touch, tap, drag)
- Reading control layout properties (Bounds, IsVisible)

### Currently on UI thread but SHOULD NOT be
These are computational or I/O tasks masquerading as UI work because the ViewModel does them:

| Currently in ViewModel | Should be | Why it's wrong |
|----------------------|-----------|---------------|
| `MainViewModel.Guidance.cs` — Pure Pursuit/Stanley calculation | Background service | Math-heavy, runs every GPS tick |
| `MainViewModel.YouTurn.cs` — U-turn path generation | Background service | Geometry computation |
| `MainViewModel.SectionControl.cs` — section on/off logic | Background service | Coverage detection, point-in-polygon |
| `MainViewModel.Simulator.cs` — simulator tick + position calc | Background service | Drives everything else, should be its own loop |
| `MainViewModel.GpsHandling.cs` — GPS data processing | Background service | Triggers all guidance/coverage work |
| `MainViewModel.BoundaryRecording.cs` — boundary point collection | Background service | Can accumulate work |
| `CoverageMapService.RasterizeQuadToBitmap` — per-cell coverage detection | Background thread | Tight loop with cross-product math |
| `CoverageMapService.LoadFromFile/SaveToFile` — RLE compression/decompression | Background thread | File I/O + CPU-heavy compression |
| `SettingsService.Save/Load` — JSON serialization + file I/O | Background thread | File I/O |
| `FieldService` — field loading, boundary parsing | Background thread | File I/O |

### The ViewModel's actual job

The ViewModel should be a **thin adapter** between services and the UI:

```
Services (background) ──batch update──> ViewModel (UI thread) ──binding──> AXAML
     ↑                                       │
     └──── commands (quick state changes) ───┘
```

1. **Receive** batched state updates from services (position, guidance, coverage stats)
2. **Expose** properties for AXAML bindings
3. **Dispatch** commands to services (toggle autosteer, open field, etc.)
4. **Never compute**, never do I/O, never iterate large collections

### The Map Control's actual job

The map control should be a **read-only renderer**:

```
Services (background) ──write pixels──> SKBitmap (shared memory)
                                              │
Map Control (render thread) ──read──> SKBitmap ──draw──> Screen
```

1. **Read** position, boundary, track data from shared state
2. **Read** coverage from SKBitmap (lock-free)
3. **Draw** everything via `ICustomDrawOperation` or `CompositionCustomVisualHandler`
4. **Never** modify bitmaps, compute guidance, or do file I/O

### The Service Layer's actual job

Services own the data and the computation. They run on their own threads:

```
GPS/Simulator ──position──> AutoSteerService ──steer angle──> UDP (hardware)
                    │
                    ├──> TrackGuidanceService ──XTE, heading error──> batch to ViewModel
                    │
                    ├──> CoverageMapService ──pixel writes──> SKBitmap (direct)
                    │                       ──stats──> batch to ViewModel
                    │
                    └──> YouTurnService ──path──> shared state
```

Services communicate via:
- Direct method calls (same thread)
- Shared thread-safe data structures (SKBitmap, atomic values)
- Batched `Dispatcher.UIThread.Post()` for ViewModel property updates (max 10 Hz)

## What This Means for the Refactor

The `MainViewModel` partial classes are actually **services in disguise**:

| Partial File | Real Identity | Refactor |
|-------------|--------------|---------|
| `MainViewModel.Guidance.cs` | GuidanceOrchestrationService | Extract, run on background |
| `MainViewModel.YouTurn.cs` | YouTurnService (already exists) | Move logic to service |
| `MainViewModel.SectionControl.cs` | SectionControlService (already exists) | Move logic to service |
| `MainViewModel.Simulator.cs` | SimulatorService (already exists) | Run tick on own timer/thread |
| `MainViewModel.GpsHandling.cs` | GpsProcessingService | Extract, background thread |
| `MainViewModel.BoundaryRecording.cs` | BoundaryRecordingService (already exists) | Move logic to service |
| `MainViewModel.Ntrip.cs` | NtripClientService (already exists) | Already background, fix Invoke→Post |

Many of these services already exist in `Shared/AgValoniaGPS.Services/` — the ViewModel just duplicates their work or orchestrates them synchronously on the UI thread.

## Service Pipeline Architecture

The application is fundamentally a **data pipeline**, not a UI app that happens to process GPS. Each stage is a single-concern service running on its own thread:

```
Hardware/Simulator
       │
       ▼
┌──────────────┐
│  GPS Service │  Pure C#, background thread. Parses NMEA or simulator data.
│              │  Produces: Position, Heading, Speed, Fix Quality
└──────┬───────┘
       │ position event
       ▼
┌──────────────────┐     ┌─────────────────────┐
│ Guidance Service │     │  Section Control     │
│                  │     │  Service             │
│ Consumes: pos,   │     │                      │
│   track, config  │     │ Consumes: tool pos,  │
│ Produces: steer  │     │   boundary, coverage │
│   angle, XTE,    │     │ Produces: section    │
│   goal point     │     │   on/off states      │
└──────┬───────────┘     └──────────┬───────────┘
       │                            │
       ▼                            ▼
┌──────────────────┐     ┌─────────────────────┐
│ AutoSteer Service│     │ Coverage Map Service │
│                  │     │                      │
│ Consumes: steer  │     │ Consumes: tool pos,  │
│   angle, config  │     │   section states     │
│ Produces: PGN    │     │ Produces: pixel      │
│   to hardware    │     │   writes to SKBitmap │
└──────────────────┘     └──────────────────────┘

       All services run on background threads.
       None of them know about Avalonia, AXAML, or the UI.

                    │ batched updates (max 10 Hz)
                    ▼
            ┌──────────────┐
            │  ViewModel   │  UI thread. Thin property bag.
            │              │  Receives: position, stats, states
            │              │  Exposes: bound properties for AXAML
            │              │  Handles: user commands → dispatches to services
            └──────┬───────┘
                   │ data binding
                   ▼
            ┌──────────────┐
            │  Map Control │  Render thread (ICustomDrawOperation).
            │              │  Reads: SKBitmap, position, boundary
            │              │  Draws: coverage, vehicle, tracks, grid
            │              │  Writes: nothing
            └──────────────┘
```

### Single Concern Per Service

| Service | Single Concern | Thread | Knows About UI? |
|---------|---------------|--------|-----------------|
| GpsService | Parse GPS data, produce position | Own thread | No |
| SimulatorService | Generate fake GPS positions | Own timer thread | No |
| TrackGuidanceService | Calculate steer angle from position + track | Called by GPS pipeline | No |
| AutoSteerService | Build PGNs, send to hardware | GPS pipeline thread | No |
| SectionControlService | Decide section on/off | GPS pipeline thread | No |
| CoverageMapService | Detect coverage, write SKBitmap pixels | GPS pipeline thread | No |
| YouTurnService | Generate U-turn paths | On demand | No |
| NtripService | Manage RTK connection | Own thread | No |
| UdpService | Send/receive UDP packets | Own thread | No |
| FieldService | Load/save field files | Task.Run | No |
| SettingsService | Load/save settings | Task.Run | No |
| **ViewModel** | **Property bag + command dispatch** | **UI thread** | **Yes — only thing that does** |
| **Map Control** | **Read-only rendering** | **Render thread** | **Render only** |

### What's Wrong Today

The ViewModel is the hub of everything:

```
Today (broken):

GPS data → ViewModel.GpsHandling (UI thread)
         → ViewModel.Guidance (UI thread)
         → ViewModel.SectionControl (UI thread)  
         → ViewModel.YouTurn (UI thread)
         → CoverageService.SetPixel (UI thread via callback)
         → ViewModel property updates (UI thread)
         → InvalidateVisual (UI thread)

Everything serialized on one thread. 10 Hz × all that work = ANR on Android.
```

### What It Should Be

```
Target (correct):

GPS data → GpsService (background)
         → AutoSteerService.ProcessPosition (background)
           ├── TrackGuidanceService.Calculate (background)
           ├── SectionControlService.Update (background)  
           ├── CoverageMapService.Paint (background, writes SKBitmap directly)
           └── YouTurnService.Check (background)
         → Batch results → Post to ViewModel (UI thread, 10 Hz)
         → ViewModel sets properties (fast, no computation)
         → Map control renders on next frame (render thread, reads SKBitmap)
```

The GPS pipeline runs entirely on a background thread. The UI thread only receives pre-computed results 10 times per second. The render thread only reads shared data structures.

## MVVM Done Right

```
┌───────────┐  Data Binding   ┌────────────┐  Updates model  ┌───────────┐
│           │  and Commands   │            │                 │           │
│   View    │ ───────────────>│  ViewModel │ ───────────────>│   Model   │
│           │                 │            │                 │           │
└───────────┘                 └────────────┘                 └───────────┘
      ▲    Send notifications       ▲    Send notifications       │
      └─────────────────────────────┴─────────────────────────────┘
```

### View (UI thread — Avalonia controls)
- **User input**: touch, tap, drag, keyboard
- **Data visualization**: render map, display values, show dialogs
- **NEVER**: compute, do I/O, process GPS, detect coverage

### ViewModel (UI thread — thin arbitrator)
- **Arbitrates** between View and Models
- **Exposes** properties for data binding (position, speed, heading, stats)
- **Dispatches** commands to models (toggle autosteer, open field, start simulator)
- **Receives** notifications from models, updates bound properties
- **NEVER**: calculate guidance, detect coverage, parse GPS, do file I/O

### Model / Services (background threads — async, event-driven)
- **Do the heavy lifting**: GPS parsing, guidance math, coverage detection, section control
- **Are asynchronous**: respond to input, produce output via events/notifications
- **Own their state**: each service manages its own data on its own thread
- **Know nothing about Avalonia**: pure C#, no Dispatcher, no UI dependencies
- **Communicate**: via events, shared thread-safe data structures, and batched notifications to ViewModel

### Where We Went Wrong

The ViewModel became the Model. The `MainViewModel.Guidance.cs`, `.YouTurn.cs`, `.SectionControl.cs`, `.GpsHandling.cs`, `.Simulator.cs` partial files ARE models — they compute, process, and manage state. But because they live in the ViewModel, they execute on the UI thread. This worked on Avalonia 11's cooperative scheduler. Avalonia 12's compositor exposes that the UI thread is overloaded.

The fix: move the computation back to where it belongs — the Model layer (services). The ViewModel becomes what it was always supposed to be: a thin arbitrator that translates between user interaction and application logic.

## Key Principles

1. **MVVM boundaries are real**: View displays, ViewModel arbitrates, Model computes. No layer does another's job.
2. **Models are async**: Services respond to inputs and produce outputs on their own threads. They don't wait for the UI.
3. **UI thread is sacred**: Only property changes, command dispatch, and rendering. Zero computation, zero I/O.
4. **Services own their threads**: GPS pipeline runs on background. File I/O on Task.Run. NTRIP on its own thread.
5. **Data flows via notifications**: Services notify ViewModel of changes. ViewModel notifies View via binding. Never synchronous cross-thread calls.
6. **Shared data is lock-free**: SKBitmap for coverage, atomic values for position. No WriteableBitmap.Lock in the hot path.
