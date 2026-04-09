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

## Key Principle

**The UI thread should only handle quick state updates and input. ALL heavy work (GPS processing, coverage detection, file I/O, bitmap manipulation) runs on background threads. Data flows to the UI thread via `Post()`, never `Invoke()`.**
