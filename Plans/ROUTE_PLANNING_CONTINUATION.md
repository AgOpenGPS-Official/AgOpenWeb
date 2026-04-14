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

### Phase 3 — Route Following Guidance (Complete)
- `RoutePlanState` in `ApplicationState` tracks active plan and progress
- Start/Stop Route buttons in Route Plan dialog
- **Pac-Man guidance**: all segments stitched into one dense waypoint list (swaths densified at 1m spacing), Pure Pursuit follows forward-only
- Waypoint eating: simple proximity check — eat next point when within 1.5m. No dot products, no skipping. Sequential only.
- Acquire phase: driver manually positions near green dot heading the right way (within 5m, heading within 60°). Auto-approach deferred to Phase 4.
- Segment boundary tracking: recorded during stitching for accurate progress mapping
- Visual progress: completed segments dimmed, current swath green/thick, future cyan
- Status bar HUD: "Route: Swath 3/10 | 45%" in FieldStatsPanel (all platforms)
- Route progress updates live via `ApplyGpsCycleResult` on UI thread
- Skip Segment jumps waypoint index to next swath boundary
- Stop Route resets cleanly, Start Route forces fresh waypoint build
- Tested on 577ha field with 215 tracks — works and scales

### Key Architectural Decision
White-cane guidance uses nearest-point search on infinite lines. Route following uses forward-only waypoint consumption ("breadcrumb trail"). This eliminates:
- Nearest-point ambiguity when path doubles back (parallel swaths)
- Segment transition wobble
- Algorithm switching between TrackGuidanceService and YouTurnGuidanceService

The route guidance is entirely in `GpsPipelineService.CalculateRouteGuidance()` — direct Pure Pursuit math, no dependency on existing guidance services.

## What's Left

### Phase 4 — UI/UX Polish (from master plan)
- Auto-approach to route start (navigate to green dot from any position/heading)
- "Start from here" — begin mid-route from nearest swath
- Route plan save/load to JSON
- Cancel/restart mid-field
- Make Route Plan dialog non-modal / movable so it doesn't cover the map

### Phase 5 — Robustness (from master plan)
- Off-route recovery (>10m deviation warning)
- Obstacle avoidance (inner boundaries)
- Headland circuit passes
- Multi-segment swaths for concave fields (currently broken — see screenshots)
- Performance validation (<500ms for 100ha)

### Phase 6 — Advanced (from master plan)
- True spiral pattern (continuous inward offset following boundary)
- Optimal swath angle (brute-force sweep at 1-degree)
- Trapezoidal decomposition for concave fields
- Reeds-Shepp turns (reversing for tight headlands)
- Cost function optimization (time, fuel, off-target application)

## Known Issues
- Concave fields: swaths don't split into multiple segments properly, turns get wild legs
- Route planning is on `feature/route-planning`, NOT merged to develop yet — stays until fully working
- Bing Maps + 5-level resolution already merged to develop separately

## Key Files
- `Services/Pipeline/GpsPipelineService.cs` — route guidance (CalculateRouteGuidance, BuildStitchedWaypoints, PurePursuitToPoint)
- `Services/RoutePlanning/TurnPathService.cs` — boundary-validated turns
- `Services/RoutePlanning/RouteStitchingService.cs` — assembles RoutePlan
- `Services/Track/SwathGenerationService.cs` — parallel swath generation
- `Models/RoutePlanning/` — RoutePlan, RouteSegment, GuidanceMode, RouteSegmentType
- `Models/State/RoutePlanState.cs` — runtime route state (includes SkipSegmentRequested flag)
- `ViewModels/MainViewModel.RoutePlanning.cs` — Start/Stop/Skip commands, RouteProgressText
- `ViewModels/MainViewModel.ApplyResults.cs` — route progress UI updates from pipeline
- `ViewModels/MainViewModel.Commands.Track.cs` — GenerateRoutePlan()
- `Views/Controls/Dialogs/RoutePlanDialogPanel.axaml` — Route Plan dialog (Start/Stop buttons)
- `Views/Controls/Panels/FieldStatsPanel.axaml` — Route progress HUD in status bar
- `Views/Controls/DrawingContextMapControl.cs` — swath/turn/triangle rendering with progress colors
- `Plans/ROUTE_PLANNING_PHASE2.md`, `Plans/ROUTE_PLANNING_PHASE3.md` — detailed plans
- `Plans/PRE_COMPUTED_ROUTE_PLANNING.md` — 6-phase master plan
