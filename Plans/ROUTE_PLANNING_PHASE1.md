# Phase 1: Pre-Computed Route Planning — Track Generation

## Context

Issue #128 describes a pre-computed route planning system as an alternative to the current reactive "white cane" guidance. Phase 1 focuses on generating finite-endpoint parallel swaths clipped to the field boundary, displaying them on the map, and letting the user preview planned tracks before driving.

The user envisions a "Plan Route" button that opens a dialog with incremental options: start with "Next N Tracks", then build toward "Whole Route" and "Include Headland Tracks." Each capability builds on the previous one.

The existing guidance pipeline stays intact — route planning is an additional mode, not a replacement.

## What Exists Already

| Component | Status | File |
|-----------|--------|------|
| Track model (unified AB/curve) | Done | `Models/Track/Track.cs` |
| SwathOrderingService (snake/spiral/boustrophedon) | Done | `Services/Track/SwathOrderingService.cs` |
| TrackGuidanceService (Pure Pursuit + Stanley) | Done | `Services/Track/TrackGuidanceService.cs` |
| PolygonOffsetService (Clipper2-based) | Done | `Services/Geometry/PolygonOffsetService.cs` |
| Boundary/Headland model | Done | `Models/Boundary.cs`, `Models/BoundaryPolygon.cs` |
| GeometryMath (point-in-polygon, ray-cast, intersections) | Done | `Models/Base/GeometryMath.cs` |
| Map track rendering | Done | `Views/Controls/DrawingContextMapControl.cs` |

**Main gap:** No track-to-boundary clipping (line-polygon intersection to get finite endpoints).

## Plan

### Step 1: SwathGenerationService

New service that generates parallel finite tracks clipped to a boundary polygon.

**File:** `Shared/AgValoniaGPS.Services/Track/SwathGenerationService.cs`
**Interface:** `Shared/AgValoniaGPS.Services/Interfaces/ISwathGenerationService.cs`

```csharp
public interface ISwathGenerationService
{
    /// Generate parallel swaths from a reference track, clipped to boundary.
    SwathPlan GenerateSwaths(SwathPlanInput input);
}

public class SwathPlanInput
{
    public Track ReferenceTrack { get; set; }       // AB line or curve (defines heading)
    public BoundaryPolygon ClipBoundary { get; set; } // Headland or outer boundary
    public double ToolWidth { get; set; }
    public double Overlap { get; set; }
    public SwathPattern Pattern { get; set; }        // From SwathOrderingService
    public int? MaxTracks { get; set; }              // null = all, N = next N from vehicle
    public Vec3? VehiclePosition { get; set; }       // For "next N" — start from nearest track
    public int SkipWidth { get; set; }               // 1 = every track, 2 = skip one, etc.
}

public class SwathPlan
{
    public List<Track> Swaths { get; set; }          // Ordered, finite-endpoint tracks
    public int TotalPossibleTracks { get; set; }     // How many exist across the whole field
    public double TotalWorkingDistance { get; set; }  // Sum of swath lengths
}
```

**Algorithm:**
1. From the reference AB line, compute the heading and perpendicular offset direction
2. Determine how many parallel tracks fit across the boundary (based on tool width - overlap)
3. Apply SwathOrderingService to get traversal sequence
4. For "Next N": find which track is nearest to vehicle, take next N in sequence
5. For each track offset: generate the infinite line, then clip to the boundary polygon
6. Clipping: use `GeometryMath.RaycastToPolygon()` or line-polygon intersection to find entry/exit points
7. Return ordered list of finite Track objects

**Clipping logic:** Cast a ray from the track's reference point in both directions along the heading. Find all intersections with the boundary polygon. Pair them up to get segments inside the boundary. Handle concave fields (may produce multiple segments per track — each becomes a separate Track).

### Step 2: Route Planning Dialog

**File:** `Shared/AgValoniaGPS.Views/Controls/Dialogs/RoutePlanDialogPanel.axaml(.cs)`

Simple dialog with:
- **Pattern selector:** Boustrophedon / Snake / Spiral (radio buttons or segmented control)
- **Track count:** "Next 5" / "Next 10" / "All" (buttons or dropdown)
- **Skip width:** 1 / 2 / 3 (for skip-and-fill)
- **Clip to:** Headland / Outer Boundary (toggle)
- **Plan button:** Generates swaths and displays on map
- **Clear button:** Removes planned swaths from map

Wire through UIState dialog system (add `DialogType.RoutePlan`).

### Step 3: ViewModel Commands

**File:** `Shared/AgValoniaGPS.ViewModels/MainViewModel.Commands.Track.cs`

- `PlanRouteCommand` — opens the route plan dialog
- `GenerateSwathsCommand` — calls SwathGenerationService, sends results to map
- `ClearPlannedSwathsCommand` — clears planned swaths from map

### Step 4: Map Rendering of Planned Swaths

Use existing `SetRecordedPaths()` or add a new `SetPlannedSwaths(IReadOnlyList<Track> swaths)` method on DrawingContextMapControl. Planned swaths should be visually distinct from recorded paths:
- Dashed or semi-transparent lines
- Numbered labels (Track 1, 2, 3...) showing traversal order
- Different color from active track (e.g. light blue or cyan)

### Step 5: Tests

**File:** `Tests/AgValoniaGPS.Services.Tests/SwathGenerationServiceTests.cs`

Test cases:
- Rectangular boundary: N parallel tracks, correct count, correct endpoints
- Concave boundary: tracks split into multiple segments
- "Next N" from vehicle position: correct subset selected
- Skip width: correct tracks skipped
- Snake/spiral ordering: correct traversal sequence applied
- Edge cases: tool wider than field, single track, vehicle outside boundary

## Files to Create/Modify

| File | Action |
|------|--------|
| `Services/Interfaces/ISwathGenerationService.cs` | Create |
| `Services/Track/SwathGenerationService.cs` | Create |
| `Models/Track/SwathPlan.cs` | Create (input/output DTOs) |
| `Models/State/UIState.cs` | Modify (add DialogType.RoutePlan) |
| `Views/Controls/Dialogs/RoutePlanDialogPanel.axaml(.cs)` | Create |
| `Views/Controls/DialogOverlayHost.axaml` | Modify (register dialog) |
| `ViewModels/MainViewModel.Commands.Track.cs` | Modify (add commands) |
| `ViewModels/MainViewModel.cs` | Modify (command declarations) |
| `Views/Controls/DrawingContextMapControl.cs` | Modify (add SetPlannedSwaths) |
| `Tests/AgValoniaGPS.Services.Tests/SwathGenerationServiceTests.cs` | Create |

## Reuse

- `SwathOrderingService.GenerateSequence()` — ordering logic already done
- `GeometryMath.RaycastToPolygon()` — ray-casting for clipping
- `GeometryMath.IsPointInPolygon()` — validate points inside boundary
- `Track.FromCurve()` — create finite tracks from clipped points
- `PolygonOffsetService` — if headland needs to be generated from outer boundary
- Dialog system pattern from existing dialogs (e.g., HeadlandDialogPanel)

## Verification

1. `dotnet build Platforms/AgValoniaGPS.Desktop/AgValoniaGPS.Desktop.csproj`
2. `dotnet test Tests/AgValoniaGPS.Services.Tests/`
3. Manual: Create field with boundary + headland + AB line → open Route Plan dialog → generate swaths → verify tracks display correctly on map with finite endpoints clipped to boundary
4. Test with concave boundary to verify multi-segment handling
5. Test "Next N" from different vehicle positions
