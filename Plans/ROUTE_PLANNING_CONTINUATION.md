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

### Phase 3 — Route Following Guidance (Complete)
- Pac-Man guidance: dense waypoint list, Pure Pursuit, forward-only consumption
- Waypoint eating: proximity check within 3m, sequential only
- Smooth merge acquisition with locked 15m-ahead target
- Segment boundary tracking for accurate progress mapping
- Visual progress: completed=dim, current=green, future=cyan
- Status bar HUD: "Route: Swath 3/10 | 45%" in FieldStatsPanel
- Start/Stop/Skip commands

### Phase 4 — UI/UX Polish (Complete)
- Non-modal draggable Route Plan panel (bottom-right, 95% opacity)
- Auto-approach: finds nearest point on first swath, locks target 15m ahead
- "From Here" button: searches all swaths for nearest, acquires ahead
- Save/Load Route to JSON in field directory (RoutePlan.json)
- Cancel/restart via Stop + Start (forces fresh waypoint build)

### Phase 5 — Robustness (Partial)
- Off-route deviation warnings at 5m (drifting) and 10m (off-route)
- Inner boundary subtraction from swath clipping (code ready, no UI to create inner boundaries yet)
- Concave boundary fix: midpoint-in-polygon validation for multi-intersection clipping
- Route generation timing in status display
- Inner boundary creation UI — merged to develop in PR #243
- Headland circuit passes — HeadlandCircuitService generates outer + inner perimeter loops (displayed on map)
- Inner boundary buffer zone — swaths terminate at expanded inner boundary, leaving room for turns
- Turn validation against inner boundaries — turns crossing obstacles flagged red/dashed

## What's Left

### Phase 6 — Advanced (from master plan)

**Zone decomposition** (covers three related problems with one solution):
- Inner boundaries splitting swaths → needs zone grouping + cross-zone transit
- Concave outer boundaries splitting swaths → same problem (notch in field)
- Non-convex headland splitting swaths → same problem (narrow neck)

All three are connected-component detection: identify which swath segments are reachable from each other without crossing an obstacle, then plan the order to visit each zone plus transit paths between zones.

**Other Phase 6 items:**
- True spiral pattern (continuous inward offset following boundary)
- Optimal swath angle (brute-force sweep at 1-degree)
- Reeds-Shepp turns (reversing for tight headlands)
- Cost function optimization (time, fuel, off-target application)

## Known Issues / MVP Behavior
- Cross-zone turns (swath splits by inner boundary or concavity) flagged red/dashed; user manually drives around obstacles to rejoin route
- Route planning is on `feature/route-planning`, NOT merged to develop yet

## Key Files
- `Services/Pipeline/GpsPipelineService.cs` — route guidance, Pac-Man, acquire, off-route warning
- `Services/RoutePlanning/TurnPathService.cs` — boundary-validated turns
- `Services/RoutePlanning/RouteStitchingService.cs` — assembles RoutePlan
- `Services/RoutePlanning/RoutePlanPersistence.cs` — JSON save/load
- `Services/Track/SwathGenerationService.cs` — parallel swath generation + inner boundary subtraction
- `Models/RoutePlanning/` — RoutePlan, RouteSegment, GuidanceMode, RouteSegmentType
- `Models/State/RoutePlanState.cs` — runtime route state
- `ViewModels/MainViewModel.RoutePlanning.cs` — Start/Stop/Skip/FromHere/Save/Load commands
- `ViewModels/MainViewModel.Commands.Track.cs` — GenerateRoutePlan()
- `ViewModels/MainViewModel.ApplyResults.cs` — route progress UI updates
- `Views/Controls/Dialogs/RoutePlanDialogPanel.axaml(.cs)` — draggable non-modal panel
- `Views/Controls/Panels/FieldStatsPanel.axaml` — Route progress HUD
- `Views/Controls/DrawingContextMapControl.cs` — progress-colored rendering
