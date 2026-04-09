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

## Key Principle

**The UI thread should only handle quick state updates and input. ALL heavy work (GPS processing, coverage detection, file I/O, bitmap manipulation) runs on background threads. Data flows to the UI thread via `Post()`, never `Invoke()`. The ViewModel is thin. Services own computation.**
