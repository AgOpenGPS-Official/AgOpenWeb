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
- **Not done**: Headland circuit passes (needs boundary-offset curves — Phase 6 territory)
- **Not done**: Inner boundary creation UI (separate feature, not route-planning specific)

## What's Left

### Phase 5 Remaining
- Headland circuit passes (work the perimeter before interior swaths)

### Phase 6 — Advanced (from master plan)
- True spiral pattern (continuous inward offset following boundary)
- Optimal swath angle (brute-force sweep at 1-degree)
- Trapezoidal decomposition for concave fields
- Reeds-Shepp turns (reversing for tight headlands)
- Cost function optimization (time, fuel, off-target application)

## Known Issues
- Concave fields: turns still get wild legs crossing the concave gap (needs decomposition)
- Inner boundary creation UI does not exist — model supports it, swath clipping supports it, but no way to draw one
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
