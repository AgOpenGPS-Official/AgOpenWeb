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

### Phase A: Fix blocking calls (immediate)
- Replace `Dispatcher.UIThread.Invoke()` with `Post()` in GpsHandling.cs and Ntrip.cs
- Make `MainView_Unloaded` save async (don't block the UI thread on app background)
- Make field load/save async

### Phase B: Move GPS processing off UI thread
- Simulator tick runs on background thread
- GPS data processing (guidance, coverage detection) on background thread
- Only property updates posted to UI thread

### Phase C: Batch property updates
- Collect GPS-derived property changes
- Post batch update to UI thread at max 10 Hz
- Reduces UI thread wakeups from ~30/sec to ~10/sec

### Phase D: CompositionCustomVisualHandler for map (future)
- Move entire map rendering to render thread
- Data passed via `SendHandlerMessage()`
- UI thread completely free for input and layout

## Files Affected

| File | Change |
|------|--------|
| `MainViewModel.GpsHandling.cs` | `Invoke()` → `Post()`, move processing to background |
| `MainViewModel.Ntrip.cs` | `Invoke()` → `Post()` |
| `MainViewModel.Simulator.cs` | Run tick on background thread |
| `MainViewModel.cs` | Batch property update pattern |
| `Android/Views/MainView.axaml.cs` | Async `MainView_Unloaded` |
| `iOS/Views/MainView.axaml.cs` | Async close handlers |
| `Desktop/Views/MainWindow.axaml.cs` | Async `MainWindow_Closing` |
| `CoverageMapService.cs` | Async `LoadFromFile`, `SaveToFile` |
| `DrawingContextMapControl.cs` | Already using ICustomDrawOperation + SKBitmap |

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

## Key Principles

1. **Single concern**: Each service does one thing. No service knows about Avalonia.
2. **UI thread is sacred**: Only property changes and command dispatch. Zero computation, zero I/O.
3. **Services own their threads**: GPS pipeline runs on background. File I/O on Task.Run. NTRIP on its own thread.
4. **Data flows one direction**: Services → ViewModel → UI. Never UI → compute → UI.
5. **Shared data is lock-free**: SKBitmap for coverage, atomic values for position. No WriteableBitmap.Lock in the hot path.
