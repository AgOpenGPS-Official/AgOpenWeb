# Phase 2: Turn Path Validation and Route Assembly

## Context

Phase 1 delivered swath generation, boundary clipping, Dubins turn paths, ordering, and map rendering. The turn paths are generated but **not validated against the boundary** — they can clip outside the field at angled corners or narrow headlands. Phase 2 fixes this architecturally by trying all 6 Dubins path types and selecting one that stays in-bounds, then assembling everything into a `RoutePlan` data model.

## What Phase 1 Built

| Component | File | Status |
|-----------|------|--------|
| SwathGenerationService (generate + clip + turns) | `Services/Track/SwathGenerationService.cs` | Done |
| DubinsPathService (all 6 path types) | `Services/PathPlanning/DubinsPathService.cs` | Done |
| SwathOrderingService (boustrophedon/snake/spiral) | `Services/Track/SwathOrderingService.cs` | Done |
| Map rendering (swaths cyan, turns orange) | `Views/Controls/DrawingContextMapControl.cs` | Done |
| Route Plan dialog (pattern, count, clear) | `Views/Controls/Dialogs/RoutePlanDialogPanel.axaml` | Done |
| ViewModel commands (generate, clear, pattern) | `ViewModels/MainViewModel.Commands.Track.cs` | Done |

### Current Turn Generation (to be replaced)

In `SwathGenerationService.cs` lines 168-246, turns are generated inline:
- Fixed leg length: `max(HeadlandWidth - 1.5 * TurningRadius, 2.0)`
- Calls `DubinsPathService.GeneratePath(start, goal)` — returns **shortest** path only
- No boundary validation — arc can exit field at angled corners
- Turn paths stored as `List<List<Vec3>>` on `SwathPlan`

### DubinsPathService API

```csharp
public DubinsPathService(double turningRadius)
public List<Vec3> GeneratePath(Vec3 start, Vec3 goal)  // Returns shortest of 6 types
```

Internally generates all 6 types (RSR, LSL, RSL, LSR, RLR, LRL), sorts by length, returns shortest. The infrastructure to get all 6 paths exists but isn't exposed.

---

## Phase 2 Deliverables

### 1. RoutePlan Data Model

**New files in `Models/RoutePlanning/`:**

```csharp
// GuidanceMode.cs
public enum GuidanceMode { WhiteCane, PreComputedRoute }

// RouteSegmentType.cs
public enum RouteSegmentType { Swath, Turn }

// RouteSegment.cs
public class RouteSegment
{
    public RouteSegmentType Type { get; set; }
    public int SwathIndex { get; set; }         // Which swath (for Swath segments)
    public List<Vec3> Waypoints { get; set; }   // Dense waypoint list with headings
    public double Length { get; set; }           // Segment length in meters
    public bool IsReverse { get; set; }          // Travel direction
}

// RoutePlan.cs
public class RoutePlan
{
    public List<RouteSegment> Segments { get; set; }   // Alternating swath/turn/swath/turn...
    public int SwathCount { get; set; }
    public int TurnCount { get; set; }
    public double TotalSwathDistance { get; set; }
    public double TotalTurnDistance { get; set; }
    public double TotalDistance { get; set; }
    public SwathPattern Pattern { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

These models are the contract between planning (Phase 2) and guidance (Phase 3). Phase 3 will iterate `Segments` in order, delegating swaths to `TrackGuidanceService` and turns to `YouTurnGuidanceService`.

### 2. TurnPathService (boundary-validated turns)

**New file:** `Services/RoutePlanning/TurnPathService.cs`
**Interface:** `Services/Interfaces/ITurnPathService.cs`

```csharp
public interface ITurnPathService
{
    /// Generate a boundary-validated turn path between two swath endpoints.
    /// Returns null if no valid turn exists.
    TurnPathResult? GenerateTurn(TurnPathInput input);
}

public class TurnPathInput
{
    public Vec3 ExitPoint { get; set; }       // End of current swath
    public double ExitHeading { get; set; }   // Heading at exit
    public Vec3 EntryPoint { get; set; }      // Start of next swath
    public double EntryHeading { get; set; }  // Heading at entry
    public double TurningRadius { get; set; }
    public double HeadlandWidth { get; set; }
    public BoundaryPolygon Boundary { get; set; }  // Outer boundary for containment check
}

public class TurnPathResult
{
    public List<Vec3> Waypoints { get; set; }
    public double Length { get; set; }
    public string PathType { get; set; }      // e.g. "RSR", "LSL", "fallback"
    public bool IsValid { get; set; }         // All points inside boundary
}
```

**Algorithm:**

1. Compute exit leg from swath endpoint into headland (along exit heading)
2. Compute entry leg from next swath endpoint into headland (along reversed entry heading)
3. Generate all 6 Dubins path types between leg endpoints
4. For each path, build full turn: exit leg + Dubins arc + entry leg
5. Check every point against boundary using `GeometryMath.IsPointInPolygon()`
6. Filter to paths where **all points are inside** the outer boundary
7. Return shortest valid path
8. If no valid path: try with progressively shorter leg lengths (shrink by 25% each attempt, min 2m)
9. If still no valid path: return result with `IsValid = false` and `PathType = "none"` — flag for user

**Key difference from Phase 1:** Phase 1 called `GeneratePath()` which returns only the shortest. Phase 2 needs all 6 candidates to filter by boundary containment. This requires either:
- (a) Exposing a `GenerateAllPaths()` method on `DubinsPathService`, or
- (b) Having `TurnPathService` construct 6 `DubinsPathService` calls with different configurations

Option (a) is cleaner — add one method to `DubinsPathService`.

### 3. DubinsPathService Enhancement

**Modify:** `Services/PathPlanning/DubinsPathService.cs`

Add one public method:

```csharp
/// Returns all valid Dubins paths sorted by length (shortest first).
/// Existing GeneratePath() continues to return only the shortest.
public List<(List<Vec3> Path, string Type, double Length)> GenerateAllPaths(Vec3 start, Vec3 goal)
```

The internal logic already generates all 6 — this just exposes them before filtering to shortest.

### 4. RouteStitchingService

**New file:** `Services/RoutePlanning/RouteStitchingService.cs`
**Interface:** `Services/Interfaces/IRouteStitchingService.cs`

```csharp
public interface IRouteStitchingService
{
    /// Assemble ordered swaths and turn paths into a RoutePlan.
    RoutePlan StitchRoute(List<Track> swaths, SwathPattern pattern, 
                          ITurnPathService turnService, TurnPathConfig config);
}

public class TurnPathConfig
{
    public double TurningRadius { get; set; }
    public double HeadlandWidth { get; set; }
    public BoundaryPolygon Boundary { get; set; }
    public double ReferenceHeading { get; set; }  // From AB line
}
```

**Algorithm:**

1. For each swath, create a `RouteSegment` with `Type = Swath`
   - Waypoints = swath track points
   - Determine direction (alternating for boustrophedon)
   - Set `IsReverse` based on traversal direction
2. For each consecutive swath pair, call `TurnPathService.GenerateTurn()`
   - Create `RouteSegment` with `Type = Turn`
   - If turn is invalid, still include it but mark it (for UI warning)
3. Interleave: Swath 0 → Turn 0→1 → Swath 1 → Turn 1→2 → Swath 2 → ...
4. Compute totals (distances, counts)
5. Return `RoutePlan`

### 5. Refactor SwathGenerationService

**Modify:** `Services/Track/SwathGenerationService.cs`

- **Remove** inline turn path generation (lines 168-246)
- `SwathPlan` keeps only swaths — remove `TurnPaths` and `TotalTurningDistance`
- Turn generation moves to `TurnPathService` called by `RouteStitchingService`
- This cleans up the service to do one thing: generate and clip swaths

### 6. Update ViewModel

**Modify:** `ViewModels/MainViewModel.Commands.Track.cs`

The `GenerateRoutePlan()` method currently:
1. Calls `SwathGenerationService.GenerateSwaths()` → gets swaths + turns
2. Sends both to map

Change to:
1. Call `SwathGenerationService.GenerateSwaths()` → gets swaths only
2. Call `RouteStitchingService.StitchRoute()` → gets `RoutePlan` with validated turns
3. Send swaths and turn paths to map
4. Update status to include invalid turn warnings if any
5. Store `RoutePlan` for Phase 3 consumption

### 7. Enhanced Map Rendering

**Modify:** `Views/Controls/DrawingContextMapControl.cs`

Current rendering is basic (cyan swaths, orange turns). Enhance:
- **Valid turns:** orange (existing)
- **Invalid turns** (failed boundary check): red dashed, with warning icon
- **Swath numbering:** small label at swath midpoint showing traversal order
- **Direction arrows:** small chevrons along swaths showing travel direction

### 8. Tests

**New file:** `Tests/AgValoniaGPS.Services.Tests/RoutePlanning/TurnPathServiceTests.cs`

Test cases:
- Rectangular field: all turns valid, shortest Dubins path selected
- Angled corner: shortest path clips boundary, alternate path type selected
- Narrow headland: no valid path exists, returns `IsValid = false`
- Adjacent swaths (boustrophedon): tight 180-degree turns
- Skip-one swaths (snake): wider turns spanning 2 swath widths
- All 6 Dubins types: verify each type generated correctly

**New file:** `Tests/AgValoniaGPS.Services.Tests/RoutePlanning/RouteStitchingServiceTests.cs`

Test cases:
- 3 swaths stitched: produces 5 segments (swath-turn-swath-turn-swath)
- Alternating direction: odd swaths reversed
- Invalid turn flagged but included
- Total distances computed correctly
- Single swath: produces 1 segment, no turns

---

## Files Summary

| File | Action |
|------|--------|
| `Models/RoutePlanning/GuidanceMode.cs` | Create |
| `Models/RoutePlanning/RouteSegmentType.cs` | Create |
| `Models/RoutePlanning/RouteSegment.cs` | Create |
| `Models/RoutePlanning/RoutePlan.cs` | Create |
| `Services/Interfaces/ITurnPathService.cs` | Create |
| `Services/RoutePlanning/TurnPathService.cs` | Create |
| `Services/Interfaces/IRouteStitchingService.cs` | Create |
| `Services/RoutePlanning/RouteStitchingService.cs` | Create |
| `Services/PathPlanning/DubinsPathService.cs` | Modify (add `GenerateAllPaths`) |
| `Services/Track/SwathGenerationService.cs` | Modify (remove inline turn generation) |
| `Services/Interfaces/ISwathGenerationService.cs` | Modify (remove turn fields from SwathPlan) |
| `ViewModels/MainViewModel.Commands.Track.cs` | Modify (use RouteStitchingService) |
| `Views/Controls/DrawingContextMapControl.cs` | Modify (invalid turn rendering, labels) |
| `Tests/.../RoutePlanning/TurnPathServiceTests.cs` | Create |
| `Tests/.../RoutePlanning/RouteStitchingServiceTests.cs` | Create |

## Implementation Order

1. **Models first** — RoutePlan, RouteSegment, enums (no dependencies)
2. **DubinsPathService.GenerateAllPaths()** — expose existing internals
3. **TurnPathService** — boundary-validated turns (core of Phase 2)
4. **TurnPathService tests** — verify boundary validation works
5. **RouteStitchingService** — assemble swaths + validated turns into RoutePlan
6. **RouteStitchingService tests** — verify assembly
7. **Refactor SwathGenerationService** — remove inline turn code
8. **Update ViewModel** — wire new services into GenerateRoutePlan()
9. **Map rendering enhancements** — invalid turn warnings, labels
10. **Integration test** — generate full route for test field, verify all turns valid

## Verification

1. `dotnet build Platforms/AgValoniaGPS.Desktop/AgValoniaGPS.Desktop.csproj`
2. `dotnet test Tests/`
3. Manual: Open test field with angled corners → Plan Route → verify turns stay inside boundary
4. Manual: Try narrow headland → verify invalid turns flagged in red
5. Manual: Compare boustrophedon vs snake patterns → verify turn geometry differs appropriately
