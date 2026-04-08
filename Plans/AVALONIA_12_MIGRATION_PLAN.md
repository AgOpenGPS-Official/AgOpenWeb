# Avalonia 12 Migration Plan

## Context

Avalonia 12 released April 7, 2026 with 3x Android performance improvement. Our test confirmed Android idle FPS jumps from 18 → 29-30. However, the upgrade exposed three blocking issues:

1. **ReactiveUI threading crash** — `CanExecuteChanged` fires on non-UI thread, crashes on button tap
2. **FPS drops with field loaded** — 29 → 14 → 8 FPS as coverage painting starts
3. **ANR dialogs** — heavy bitmap work on UI thread blocks Android message queue

Branch `feature/avalonia-12-upgrade` has the package updates done but these issues remain.

## Root Cause Analysis

### Threading Crash
The ViewModel is created inside Android's `MainViewFactory` callback. In Avalonia 12's multi-dispatcher model, `DispatcherTimer` and `SynchronizationContext` bind to the **current thread's dispatcher**, not the UI thread. If the factory runs on a different thread, all `ReactiveCommand` canExecute observables fire on that thread, then try to access UI controls → crash.

**Fix**: Ensure ViewModel creation happens on the UI thread. Use `ReactiveWindow`/`ReactiveUserControl` base classes which handle dispatcher binding properly via `WhenActivated`.

### FPS Drops
Our `DrawingContextMapControl` uses a `DispatcherTimer` (33ms) calling `InvalidateVisual()` every frame. Coverage bitmap updates (`UpdateCoverageBitmapIncremental`, LOD rebuilds) happen synchronously inside the `Render()` method. On Avalonia 12's new compositor, this blocks the render pipeline more aggressively than Avalonia 11.

**Fix**: Move bitmap updates to background, use `Dispatcher.UIThread.Post` for dirty flagging, throttle LOD rebuilds further.

### ANR Dialogs
Android's Activity watchdog triggers ANR when the UI thread is blocked for >5 seconds. Our coverage bitmap full rebuilds and LOD regeneration can take hundreds of milliseconds, accumulating with other render work.

**Fix**: Same as FPS — move heavy work off UI thread.

## Phased Approach

### Phase 1: Prep Work on Avalonia 11 (Safe, No Risk)

These changes work on both Avalonia 11 and 12. Do them on `develop` first.

#### 1A: Fix Compiled Bindings in Views Project

Enable `AvaloniaUseCompiledBindingsByDefault` in `Shared/AgValoniaGPS.Views/AgValoniaGPS.Views.csproj`. Fix the ~8 AXAML files that need `x:DataType`:

| File | Issue |
|------|-------|
| `AgShareDownloadDialogPanel.axaml` | Missing DataType in DataTemplate |
| `AgShareUploadDialogPanel.axaml` | Missing DataType in DataTemplate |
| `AutoSteerConfigPanel.axaml` | Missing EditSidehillCompensationCommand |
| `ConfigurationDialog.axaml` | Missing IsMetric property |
| `NewFieldDialogPanel.axaml` | Missing x:DataType |
| `NumericStepView.axaml` | Missing x:DataType |
| `HelpDialogPanel.axaml` | Missing x:DataType |
| `LanguageDialogPanel.axaml` | Missing x:DataType |

**Benefit**: Performance improvement on Avalonia 11, required for Avalonia 12.

#### 1B: Move Coverage Bitmap Updates Off UI Thread

Current: `UpdateCoverageBitmapIfNeeded()` runs inside `Render()` → blocks render.

Change to:
1. `MarkCoverageDirty()` sets a flag
2. Background task picks up the flag, does the bitmap update
3. When done, `Dispatcher.UIThread.Post(() => InvalidateVisual())` to trigger re-render
4. LOD rebuilds also move to background

**Files**: `Shared/AgValoniaGPS.Views/Controls/DrawingContextMapControl.cs`

**Benefit**: Eliminates ANR on both Avalonia 11 and 12. Improves FPS on all platforms.

#### 1C: Audit DispatcherTimer Creation

Ensure all `DispatcherTimer` instances are created on the UI thread. In Avalonia 12, timers bind to the creating thread's dispatcher.

**Locations to audit**:
- `DrawingContextMapControl.cs` — render timer (line 534)
- `ChartControl.cs` — chart render timer (line 81)
- `MainViewModel.ViewSettings.cs` — CurrentTime timer

All are currently created in constructors which should be on the UI thread, but verify for Android's `MainViewFactory` path.

### Phase 2: ReactiveUI Modernization (Safe on Avalonia 11)

#### 2A: Switch View Base Classes

| Platform | Current | Target |
|----------|---------|--------|
| Desktop MainWindow | `Window` | `ReactiveWindow<MainViewModel>` |
| iOS MainView | `UserControl` | `ReactiveUserControl<MainViewModel>` |
| Android MainView | `UserControl` | `ReactiveUserControl<MainViewModel>` |

**Impact**: `ReactiveWindow<T>` adds a `ViewModel` property synced to `DataContext`. All existing `DataContext` binding still works — `ViewModel` is just a typed accessor.

**Key change**: Add `WhenActivated` blocks in code-behind for proper subscription lifecycle:
```csharp
public MainWindow()
{
    InitializeComponent();
    this.WhenActivated(disposables =>
    {
        // Subscriptions that need cleanup go here
        // They auto-dispose when the view deactivates
    });
}
```

#### 2B: Ensure ViewModel Creation on UI Thread

For Android's `IActivityApplicationLifetime` pattern, create the ViewModel in `OnFrameworkInitializationCompleted` (guaranteed UI thread), not inside `MainViewFactory`:

```csharp
// In App.axaml.cs OnFrameworkInitializationCompleted:
var viewModel = _serviceProvider.GetRequiredService<MainViewModel>(); // UI thread

if (ApplicationLifetime is IActivityApplicationLifetime activityLifetime)
{
    activityLifetime.MainViewFactory = () =>
    {
        // Factory only creates the View, ViewModel already exists
        return new MainView(viewModel, mapService, coverageService);
    };
}
```

**Files**: `Platforms/AgValoniaGPS.Android/App.axaml.cs`

### Phase 3: Package Upgrade (The Actual Switch)

Most of this is already done on `feature/avalonia-12-upgrade`. Cherry-pick or re-apply:

#### 3A: Package Updates
- All Avalonia packages: 11.3.x → 12.0.0
- `Avalonia.ReactiveUI` → `ReactiveUI.Avalonia` 11.4.12
- `Avalonia.Diagnostics` → `AvaloniaUI.DiagnosticsSupport` 2.2.0
- NUnit: 4.3.2 → 4.5.1
- Remove `Avalonia.Labs.Controls` and `Avalonia.Labs.Gif`
- Remove explicit SkiaSharp reference from iOS

#### 3B: Code Changes (already done on branch)
- Remove `DisableAvaloniaDataAnnotationValidation()` from Desktop and iOS
- `new Binding(...)` → `new ReflectionBinding(...)` in LocalizeExtension
- `using Avalonia.ReactiveUI` → `using ReactiveUI.Avalonia`
- `.UseReactiveUI()` → `.UseReactiveUI(_ => { })`
- GifImage → static Image in ToolTimingSubTab

#### 3C: Android Init Migration (already done on branch)
- `AvaloniaMainActivity<App>` → `AvaloniaMainActivity` (non-generic)
- New `AndroidApp.cs` with `AvaloniaAndroidApplication<App>`
- `ISingleViewApplicationLifetime` → `IActivityApplicationLifetime` + `MainViewFactory`
- Remove `CustomizeAppBuilder` from `MainActivity`, add to `AndroidApp`

### Phase 4: Validation & Optimization

#### 4A: Platform Testing Matrix

| Test | Desktop | iPad | Android |
|------|---------|------|---------|
| App launches | | | |
| All buttons/dialogs work | | | |
| Simulator runs | | | |
| Field load/close | | | |
| Coverage painting FPS | | | |
| Charts show live data | | | |
| AutoSteer guidance | | | |
| Day/Night toggle | | | |
| Zoom all levels | | | |
| FPS idle (no field) | | | |
| FPS with field painting | | | |

#### 4B: Android FPS Benchmarks

| Scenario | Avalonia 11 | Avalonia 12 Target |
|----------|-------------|-------------------|
| Idle, no field | 18-19 | 28-30 |
| Field loaded, no painting | 14-15 | 24+ |
| Actively painting coverage | 14-15 | 20+ |
| Zoomed out (LOD active) | 14-15 | 24+ |

#### 4C: Future Optimizations (Post-Migration)
- Use `AvaloniaObject.Dispatcher` instead of `Dispatcher.UIThread` for control-level code
- Investigate Avalonia 12 composition API for async bitmap rendering
- Consider `ReactiveUI.SourceGenerators` for command/property boilerplate
- Re-evaluate coverage bitmap approach with Avalonia 12's new Skia pipeline

## File Change Summary

| Phase | Files | Risk |
|-------|-------|------|
| 1A | ~8 AXAML files + 1 csproj | Low — adding x:DataType |
| 1B | DrawingContextMapControl.cs | Medium — async bitmap updates |
| 1C | 3 files — verify only | None |
| 2A | MainWindow.axaml.cs, MainView.axaml.cs (×2) | Medium — base class change |
| 2B | Android App.axaml.cs | Low — move ViewModel creation |
| 3A | 7 csproj files | Low — version bumps |
| 3B | 5 C# files | Low — already done on branch |
| 3C | 3 Android files | Low — already done on branch |

## Key Principle

**Phases 1-2 are safe to do on Avalonia 11 now.** They improve performance and code quality regardless of the upgrade. Phase 3 is the actual switch — by then, most of the hard work is done and the switch becomes a package version bump + namespace renames.
