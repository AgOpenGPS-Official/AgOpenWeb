# Phase 3: Route Following Guidance Mode

## Context

Phases 1-2 built swath generation, boundary-validated turns, and the RoutePlan model. Phase 3 adds the ability to *follow* the pre-computed route instead of reactive white-cane guidance. This is the first phase that touches the guidance loop.

## Current Guidance Architecture

The guidance loop runs in `GpsPipelineService.ProcessCycle()`, triggered by GPS data at ~10 Hz:

1. GPS data arrives → `OnGpsDataUpdated()` fires `Task.Run(ProcessCycle)`
2. Pipeline computes vehicle state (heading, speed, position)
3. **Decision point:** if `_isYouTurnTriggered` → `CalculateYouTurnGuidance()`, else → `CalculateTrackGuidance()`
4. `CalculateTrackGuidance()` recomputes the offset track every frame from `passNumber`, then calls `TrackGuidanceService.CalculateGuidance()`
5. Results packaged into immutable `GpsCycleResult` → fired to MainViewModel via `CycleCompleted` event

### Key Services (already exist, reused as-is)

| Service | Input | Output | Used For |
|---------|-------|--------|----------|
| `TrackGuidanceService` | Track + vehicle pose | SteerAngle, XTE, GoalPoint | Swath following |
| `YouTurnGuidanceService` | TurnPath + vehicle pose | SteerAngle, IsTurnComplete | Turn path following |

### State Persistence Between Frames

`TrackGuidanceState` carries forward: integral error, derivative, curve index. Must be reset on segment transitions.

## Phase 3 Deliverables

### 1. RoutePlanState

**New file:** `Models/State/RoutePlanState.cs`

```csharp
public class RoutePlanState : ObservableObject
{
    public RoutePlan? ActivePlan { get; set; }
    public GuidanceMode Mode { get; set; } = GuidanceMode.WhiteCane;
    public bool IsRouteActive => ActivePlan != null && Mode == GuidanceMode.PreComputedRoute;

    // Progress tracking
    public int CurrentSegmentIndex { get; set; }
    public RouteSegmentType CurrentSegmentType { get; set; }
    public bool IsRouteComplete { get; set; }
}
```

**Add to `ApplicationState`:** `public RoutePlanState RoutePlan { get; } = new();`

### 2. Route Guidance in GpsPipelineService

**Modify:** `Services/Pipeline/GpsPipelineService.cs`

The decision logic expands from 2-way to 3-way:

```
if (routePlanActive)
  → CalculateRouteGuidance()     [NEW — follows RoutePlan segments]
else if (isYouTurnTriggered)
  → CalculateYouTurnGuidance()   [existing — reactive U-turns]
else
  → CalculateTrackGuidance()     [existing — white-cane swaths]
```

Route guidance takes priority because when following a pre-computed route, the reactive U-turn system is disabled — turns are pre-planned.

### 3. CalculateRouteGuidance() Method

**New method in `GpsPipelineService`:**

Gets the current segment from `State.RoutePlan`, delegates to existing services:

- **Swath segment:** Create a Track from `segment.Waypoints`, call `TrackGuidanceService.CalculateGuidance()` with it
- **Turn segment:** Call `YouTurnGuidanceService.CalculateGuidance()` with `segment.Waypoints` as the turn path

**Segment transition logic:**
- After each guidance calc, check if segment is complete
- For swaths: `TrackGuidanceOutput.IsAtEndOfTrack` or distance to segment endpoint < 2m
- For turns: `YouTurnGuidanceOutput.IsTurnComplete`
- On transition: increment `CurrentSegmentIndex`, reset `TrackGuidanceState`, log transition
- When last segment completes: set `IsRouteComplete = true`, revert to WhiteCane mode

**Hysteresis on transition:**
- Must be >2m past segment end AND heading within 30° of next segment heading
- Prevents oscillation at segment boundaries

### 4. MainViewModel.RoutePlanning.cs

**New file:** `ViewModels/MainViewModel.RoutePlanning.cs` (partial class)

Commands:
- `StartRouteCommand` — sets `State.RoutePlan.Mode = PreComputedRoute`, loads current plan
- `StopRouteCommand` — reverts to `WhiteCane`, clears progress
- `SkipSegmentCommand` — manually advance to next segment

Properties:
- `IsRouteActive` — bound to UI for route-mode indicators
- `RouteProgressText` — "Swath 3/10 | Turn | 45% complete"
- `CurrentSegmentLabel` — "Swath 3" or "Turn 3→4"

### 5. Wire RoutePlan from GenerateRoutePlan to State

**Modify:** `MainViewModel.Commands.Track.cs`

After generating the route plan, store it:
```csharp
State.RoutePlan.ActivePlan = routePlan;
```

The "Start Route" button in the dialog activates it:
```csharp
State.RoutePlan.Mode = GuidanceMode.PreComputedRoute;
State.RoutePlan.CurrentSegmentIndex = 0;
State.RoutePlan.IsRouteComplete = false;
```

### 6. GpsPipelineService Integration

**Modify:** `Services/Pipeline/GpsPipelineService.cs`

The pipeline needs access to route plan state. It already has `ApplicationState` via DI.

```csharp
// In ProcessCycle(), before the existing guidance decision:
var routePlan = _state.RoutePlan;
if (routePlan.IsRouteActive && routePlan.ActivePlan != null)
{
    CalculateRouteGuidance(routePlan, vehicleState);
    return; // Skip white-cane and reactive U-turn
}
// ... existing white-cane / U-turn code unchanged
```

### 7. Route Plan Dialog Updates

**Modify:** `RoutePlanDialogPanel.axaml`

Add "Start Route" and "Stop Route" buttons:
- "Start Route" — only enabled when a plan exists and we have GPS fix
- "Stop Route" — only visible when route is active
- Status updates to show progress when route is active

## Files to Create/Modify

| File | Action |
|------|--------|
| `Models/State/RoutePlanState.cs` | Create |
| `Models/State/ApplicationState.cs` | Modify (add RoutePlan property) |
| `ViewModels/MainViewModel.RoutePlanning.cs` | Create |
| `ViewModels/MainViewModel.cs` | Modify (command declarations) |
| `Services/Pipeline/GpsPipelineService.cs` | Modify (add route guidance branch) |
| `ViewModels/MainViewModel.Commands.Track.cs` | Modify (store plan in state) |
| `Views/Controls/Dialogs/RoutePlanDialogPanel.axaml` | Modify (Start/Stop buttons) |
| `Tests/AgValoniaGPS.Services.Tests/RoutePlanning/RouteGuidanceTests.cs` | Create |

## Implementation Order

1. **RoutePlanState model** — no dependencies
2. **Add to ApplicationState** — wire into existing state
3. **Store RoutePlan in state** from GenerateRoutePlan() (ViewModel change)
4. **MainViewModel.RoutePlanning.cs** — Start/Stop/Skip commands
5. **GpsPipelineService route guidance branch** — the core logic
6. **CalculateRouteGuidance()** — delegates to existing Track/YouTurn services
7. **Segment transition logic** — advance through plan
8. **Dialog UI** — Start/Stop buttons
9. **Tests** — segment transitions, mode switching, completion
10. **Integration test** — simulator drive through a short route

## Key Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| Breaking white-cane guidance | `GuidanceMode` switch — route code only runs when explicitly activated |
| Segment transition jitter | Hysteresis: >2m past end AND heading aligned |
| TrackGuidanceState stale on transition | Reset state on each segment change |
| Turn completion detection unreliable | Use both IsTurnComplete and distance-to-endpoint |
| Route gets out of sync with vehicle | "Stop Route" always available, reverts to white-cane |

## Verification

1. `dotnet build` — all platforms
2. `dotnet test Tests/` — all tests pass
3. Manual: Generate route → Start Route → drive simulator through swaths and turns
4. Manual: Stop Route mid-field → verify white-cane resumes
5. Manual: Complete all segments → verify route marks complete
6. Manual: Skip segment → verify next segment activates correctly
