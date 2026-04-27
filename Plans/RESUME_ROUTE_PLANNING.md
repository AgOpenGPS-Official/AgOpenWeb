# Resume — Route Planning (paused 2026-04-27)

This branch (`feature/route-planning`) is paused. The block-clustering pipeline is wired in and most of the field renders correctly, but the user's last test field still shows residual U-turn glitches around the central pond. Resume here when ready.

## Paste this prompt to pick it back up

> I'm resuming work on `feature/route-planning`. The route planner now uses block clustering (Hameed 2013 / Höffmann 2024 §5.7) instead of cellular decomposition. As of v26.4.86 the pipeline produces a valid plan for fields with one inner ring, but the user's last screenshot showed two issues near the pond:
>
> 1. "impossibly sharp turns" at the rounded corners of the inner-ring buffer
> 2. "turns in the water" — at least one U-turn arc still bulged into the pond
>
> Read these first, in order:
> - `Plans/RESUME_ROUTE_PLANNING.md` (this file — open issues + entry points)
> - `Plans/ROUTE_PLANNING_INNER_HEADLAND.md` (architectural plan; cell-decomp parts are superseded by block clustering, headland-loop / headland-routed-transit parts are still active)
> - `Reference/Route Planning/` (curated papers — consult before iterating on geometry; per user feedback)
>
> Memory entry `project_route_planning_phase12.md` captures the state in detail. Do NOT switch back to cellular decomposition; the block-clustering pivot was deliberate.
>
> Goal: eliminate the two pond-side glitches without regressing the rest of the field.

## Where to start looking

| File | Why |
|------|-----|
| `Shared/AgValoniaGPS.Services/RoutePlanning/BlockClusterer.cs` | New entry point — generates field-wide swaths, groups into blocks |
| `Shared/AgValoniaGPS.Services/RoutePlanning/CellAwareRouteStitcher.cs` | `StitchBlocks` is the active path; `EmitBlocks` + `DriveBlockBoustrophedon` produce the snake order; `TryEmitSemicircleUTurn` is where the rejected-arc logic lives |
| `Shared/AgValoniaGPS.Services/RoutePlanning/HeadlandTrackGenerator.cs` | New — concentric outer + inner headland loops |
| `Shared/AgValoniaGPS.ViewModels/MainViewModel.Commands.Track.cs` | Wires the pipeline; cells are still computed for the debug overlay only |
| `Tests/AgValoniaGPS.Services.Tests/RoutePlanning/BlockClustererTests.cs` | Mirror new logic here when fixing |

## Open issues (from last user test)

1. **Sharp turns at rounded pond corners.** The inner buffer is auto-sized to `max(HeadlandDistance, UTurnRadius + opWidth/2)`, which guarantees a semicircle *can* fit at perpendicular antiparallel ends — but at the rounded portions of the offset pond, swath endings aren't perpendicular. `TryEmitSemicircleUTurn` rejects these (along-offset > 10% of perp-offset) and falls through to Dubins. The Dubins fallback isn't always finding a clean LSL/RSR — sometimes it produces a tight CCC that looks "impossibly sharp." Consider: (a) Höffmann's Omega/Hook turn primitives (Fig 12), (b) tightening the Dubins admissibility filter to reject CCC near boundaries, (c) widening the inner buffer when the local boundary curvature is high.

2. **Turns in the water.** A U-turn arc center is supposed to live inside the headland buffer (no-work zone), so the arc bulges *away* from the worked area. If a U-turn at a pond-side track end is using the wrong side, the arc bulges into the pond. Verify `TryEmitSemicircleUTurn` orients the arc against the operation direction at every pond-side end; check whether the rounded-corner Dubins fallback respects the same side constraint.

## Things NOT to do

- Don't revert to cellular decomposition. The cell-with-hole / sliver-filtering rabbit hole was the reason for the pivot.
- Don't add a "later" / "deferred" / "phase N+1" milestone. Per user: do the proper implementation in one go.
- Don't band-aid by clamping outputs (e.g., snapping arcs to "looks reasonable"). Fix the geometry.
- The user prefers single-arc forward U-turns. Backing turns are unacceptable. Dubins-shortest that produces an RS reverse cusp is wrong here.

## Sanity check before claiming done

Run the user's pond field. Both the "sharp turns" and "in the water" annotations should be gone. All N-S swaths north and south of the pond should U-turn cleanly into the inner-headland buffer. Outer headland coverage should still drive first.
