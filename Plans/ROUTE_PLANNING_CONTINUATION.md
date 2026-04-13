# Route Planning Continuation Prompt

## Branch: `feature/route-planning`

## What's Done

### Phase 1 — Swath Generation (Complete)
- `SwathGenerationService` generates parallel swaths clipped to boundary
- Route Plan dialog with Sequential/Snake patterns, track count selector
- Map rendering: cyan swaths, green start marker

### Phase 2 — Turn Path Validation (Complete)
- `TurnPathService` tries all 6 Dubins path types, selects shortest that stays inside boundary
- `RouteStitchingService` assembles swaths + validated turns into `RoutePlan`
- `RoutePlan`/`RouteSegment` models in `Models/RoutePlanning/`
- Invalid turns render as red dashed lines, valid as orange solid
- Directional numbered triangles at swath midpoints showing traversal order
- Spiral pattern removed from UI (was fake — real spiral is future phase)

### Phase 3 — Route Following Guidance (Complete — basic)
- `RoutePlanState` in `ApplicationState` tracks active plan and progress
- Start/Stop Route buttons in Route Plan dialog
- **Pac-Man guidance**: all segments stitched into one dense waypoint list (swaths densified at 1m spacing), Pure Pursuit follows forward-only
- Two-phase: navigate to route start (green dot), then consume waypoints
- No nearest-point search — forward-only index advancement via dot product
- Tested on 577ha field with 215 tracks — works and scales

### Key Architectural Decision
White-cane guidance uses nearest-point search on infinite lines. Route following uses forward-only waypoint consumption ("breadcrumb trail"). This eliminates:
- Nearest-point ambiguity when path doubles back (parallel swaths)
- Segment transition wobble
- Algorithm switching between TrackGuidanceService and YouTurnGuidanceService

The route guidance is entirely in `GpsPipelineService.CalculateRouteGuidance()` — direct Pure Pursuit math, no dependency on existing guidance services.

## What's Left

### Phase 3 Polish
- Route progress display not updating in UI (RouteProgressText property changes)
- No visual indication on map of which segment is current vs completed
- Stop Route should clear the stitched waypoints
- Skip Segment command needs to work with the stitched waypoint approach

### Phase 4 — UI/UX Polish (from master plan)
- Route progress HUD (current swath N of M, % complete)
- Completed segments dimmed, current highlighted
- "Start from here" — begin mid-route from nearest swath
- Route plan save/load to JSON
- Cancel/restart mid-field

### Phase 5 — Robustness (from master plan)
- Off-route recovery (>10m deviation warning)
- Obstacle avoidance (inner boundaries)
- Headland circuit passes
- Multi-segment swaths for concave fields (currently broken — see screenshots)
- Performance validation

### Phase 6 — Advanced (from master plan)
- True spiral pattern (continuous inward offset following boundary)
- Optimal swath angle
- Trapezoidal decomposition for concave fields
- Reeds-Shepp turns (reversing)
- Cost function optimization

## Known Issues
- Concave fields: swaths don't split into multiple segments properly, turns get wild legs
- The `display-tweaks` branch has Bing Maps + 5-level resolution (already merged to develop)
- Route planning is on `feature/route-planning`, NOT merged to develop yet — stays until fully working

## Key Files
- `Services/Pipeline/GpsPipelineService.cs` — route guidance (CalculateRouteGuidance, BuildStitchedWaypoints)
- `Services/RoutePlanning/TurnPathService.cs` — boundary-validated turns
- `Services/RoutePlanning/RouteStitchingService.cs` — assembles RoutePlan
- `Services/Track/SwathGenerationService.cs` — parallel swath generation
- `Models/RoutePlanning/` — RoutePlan, RouteSegment, GuidanceMode, RouteSegmentType
- `Models/State/RoutePlanState.cs` — runtime route state
- `ViewModels/MainViewModel.RoutePlanning.cs` — Start/Stop/Skip commands
- `ViewModels/MainViewModel.Commands.Track.cs` — GenerateRoutePlan()
- `Views/Controls/Dialogs/RoutePlanDialogPanel.axaml` — Route Plan dialog
- `Views/Controls/DrawingContextMapControl.cs` — swath/turn/triangle rendering (Skia path ~line 4299+)
- `Plans/ROUTE_PLANNING_PHASE2.md`, `Plans/ROUTE_PLANNING_PHASE3.md` — detailed plans
- `Plans/PRE_COMPUTED_ROUTE_PLANNING.md` — 6-phase master plan
