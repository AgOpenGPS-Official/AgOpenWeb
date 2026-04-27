# Route Planning — Proper Inner-Boundary (Inner-Headland) Treatment

**Status:** in progress (started 2026-04-26)
**Branch:** `feature/route-planning`
**Predecessor:** `Plans/ROUTE_PLANNING_PHASE6_F2C_PORT.md` (Phase 12 partial validation left U-turns near inner boundary as known-broken)

This plan resolves the remaining issues in the F2C-style route planner around inner boundaries (ponds, in-field obstacles). It supersedes the "skip-row deferred" / "Held-Karp deferred" / "still some issues with the ones near the inner boundary" notes in the Phase 12 status memory. After this plan lands, the planner is **complete** for fields with inner obstacles, not partially complete.

## Background — research that drove this design

Four research papers in `Reference/Route Planning/` informed the design:

1. **Höffmann, Patel, Büskens (2023)** — *Optimal Coverage Path Planning for Agricultural Vehicles with Curvature Constraints*. Defines the four-stage CCPP pipeline (ROI decomposition → guidance tracks → route planning → smooth path planning). Critical insight: **inter-region paths use headland tracks as a routing network**, not direct Dubins/RS through the field (Fig 5).

2. **Hameed, Bochtis, Sørensen (2013)** — *An Optimized Field Coverage Planning Approach for Navigation of Agricultural Robots in Fields Involving Obstacle Areas*. Block-based decomposition with U-turns only at block-level headlands. Liu/Palmer rule: small obstacles (< 4 implement widths) → drive around per pass; large obstacles → headland on each side. Flow diagrams (Fig 6/7) show input vs output operation orderings.

3. **Versleijen (2019)** — *Path planning on agricultural fields with obstacles* (MSc thesis). Most comprehensive treatment. Categorizes obstacles into 4 types and assigns strategies. Adds decomposition/merge thresholds for noisy GPS-recorded boundaries. Honestly notes the gap his thesis didn't fill: realistic headland movements (which is what we are now adding).

4. **Chen, Xie, Sun, Shang (2023)** — *Tractor Optimal Obstacle Avoidance Path Planning*. Bezier-curve detour for the "circumvent each pass" case (small obstacles). Not directly applicable to our pond scenario (Category D / large obstacle), but documents the alternative branch.

The papers converge on the same architecture; this plan is that architecture.

## Why we are doing this

The current planner treats inner rings as second-class:
- Cells abut the pond directly (or, after the v26.4.67 buffer fix, abut a single offset of it).
- Cells touching the inner ring have **Internal** corners, not headland corners.
- Inter-cell transits use direct Reeds-Shepp / Dubins through the field, which:
  - Cuts straight across (Höffmann's anti-pattern, user calls it "long grey tracks").
  - Produces RS reverse cusps the user has explicitly rejected ("impossible turn angles").
- U-turns at the pond-side cell corners bulge **into the cordoned area** (user's repeated complaint).

The agricultural answer per all three relevant papers: treat inner rings symmetrically with the outer boundary. Each gets a multi-pass headland zone. Cells decompose against the expanded inner. Inter-cell transits ride a headland-track network. Headland passes themselves are coverage. U-turns happen only at headland-adjacent corners (outer or inner), where the buffer makes them safe.

## Configuration — answer to "what width for the inner headland?"

`State.Field.HeadlandDistance` is already `passes × ActualToolWidth` (`MainViewModel.cs:2808`). The user already controls it via the headland tool-width multiplier. Inner and outer headland widths are tied together — adjust the multiplier and both grow/shrink in lockstep. **No new config is needed for headland width.** Number of inner passes = number of outer passes.

## What changes

### 1. `InnerHeadland` corner classification (was: only Outer + Internal)

`Shared/AgValoniaGPS.Models/RoutePlanning/CellCornerKind.cs`
- Add `InnerHeadland` enum value.

`Shared/AgValoniaGPS.Services/RoutePlanning/CellCornerClassifier.cs`
- Classify cell vertices against **both** the outer clip-boundary AND each expanded inner ring.
- Vertex within tolerance of outer clip-boundary → `OuterHeadland`.
- Vertex within tolerance of an expanded inner ring → `InnerHeadland`.
- Otherwise → `Internal` (raycast cut, no headland on either side).

`Shared/AgValoniaGPS.Services/RoutePlanning/CellAwareRouteStitcher.cs`
- Treat both `OuterHeadland` and `InnerHeadland` as valid for cell entry/exit and as valid endpoints for intra-cell U-turns.
- Only `Internal` corners forbid U-turns (must be inter-cell).

This conceptual fix is what makes Hameed's "U-turns only at block headlands" work for our pond: the NORTH cell's south corners (touching the pond) become `InnerHeadland`, not `Internal`, so U-turns there are correct (arc bulges into the inner headland buffer = no-work zone).

### 2. Headland-track loop generator (new service)

`Shared/AgValoniaGPS.Services/RoutePlanning/HeadlandTrackGenerator.cs` (new)

Inputs: outer clipBoundary (already inward-offset by HeadlandDistance), expanded inner rings, opWidth, number of passes.

Outputs: list of `HeadlandLoop` objects, each a closed polyline with:
- The polygon points
- A perimeter length
- A `WalkPerimeter(fromIndex, toIndex, direction)` helper
- A `ProjectPose(Vec3 pose)` helper that returns the nearest perimeter point + tangent direction

Implementation: concentric polygon offsets via `PolygonOffsetService.CreateMultiPassOffset` (already exists). One loop per pass per boundary (outer + each inner ring).

### 3. Headland-routed inter-cell transits

`Shared/AgValoniaGPS.Services/RoutePlanning/CellAwareRouteStitcher.cs`

Replace the current `EmitDubinsTransit` between cells with `EmitHeadlandRoutedTransit`:
1. Project cell-exit pose to nearest point on nearest headland loop.
2. Project next-cell-entry pose to nearest point on nearest headland loop.
3. If on the same loop: walk the perimeter the shorter way.
4. If on different loops (e.g., cell on outer side → cell on pond side): walk one loop to its closest point on the other loop, short Dubins jump, walk the second loop. Network is small (1 + N_inner_rings loops, typically 2-3) so no general graph search needed.
5. Compose the result: short Dubins exit (cell→loop) + perimeter walk + (optional inter-loop hop) + short Dubins entry (loop→cell). Emit as `RouteSegmentType.Transit`.

Reeds-Shepp is removed from inter-cell transit entirely (it's already removed from intra-cell U-turns per the v26.4.66 fix).

### 4. Headland tracks as coverage

`Shared/AgValoniaGPS.Models/RoutePlanning/OperationDirection.cs` (new)
- Enum: `InputFlow` (interior first, then headlands — for seeding/fertilizing) and `OutputFlow` (headlands first, then interior — for harvesting/cutting).
- Default: `OutputFlow`.

`Shared/AgValoniaGPS.Models/Configuration/RoutePlanningConfig.cs`
- Add `OperationDirection` setting, persisted in AppSettings.

`Shared/AgValoniaGPS.Services/RoutePlanning/CellAwareRouteStitcher.cs`
- Emit each headland loop as a sequence of `RouteSegmentType.Swath` segments (driven, covered).
- Order per `OperationDirection`:
  - `InputFlow`: cells first → headlands last
  - `OutputFlow`: headlands first → cells last
- Connect successive loops with short Dubins/U-turn moves between them.

This closes Versleijen's explicit gap (his thesis "treated as if the headland was already removed"). Without this step we generate the headland tracks for routing but never cover them, leaving the headland buffer permanently uncovered.

### 5. Decomposition robustness (Versleijen §2.2.2-2.2.3)

`Shared/AgValoniaGPS.Models/Configuration/RoutePlanningConfig.cs`
- `DecompositionThresholdDegrees` (default `200`): field-interior angle a vertex must exceed to trigger a sweep-line cell split. Standard 180° splits at every reflex vertex; raising to 200° skips marginal reflexes (≤20° beyond straight) that would otherwise create narrow sliver subfields from GPS-recorded boundary jitter.

`Shared/AgValoniaGPS.Services/RoutePlanning/TrapezoidalDecomp.cs` (and `BoustrophedonDecomp` via shared `RaycastDecomp.Run`)
- Apply `DecompositionThreshold` in `CollectCuts` after the existing `IsCritical` check.
- New helper `FieldInteriorAngleDegrees(prev, v, next, isHole)` computes the field-side interior angle correctly for both outer (CCW, field-inside) and hole (CW, field-outside) vertices.

**Note on `MergeThresholdDegrees`**: Versleijen pairs the decomposition threshold with a merge threshold for "adjacent cells with similar optimal driving directions." That requires per-cell driving-direction optimization (his MSW method picks a separate driving angle for each subfield). Our planner uses a single swath heading from the active Track for the whole field — every cell already has the same direction, so a direction-match merge would either merge nothing or merge everything. The right place to add MergeThresholdDegrees is when per-cell direction optimization lands; at that point it goes into `RoutePlanningConfig`.

### 6. Auto-derived inner buffer sizing

`Shared/AgValoniaGPS.ViewModels/MainViewModel.Commands.Track.cs`
- Inner ring outward-offset distance = `HeadlandDistance` (same as outer). Already in place since v26.4.67.
- Verify that `HeadlandDistance ≥ opWidth/2 + safetyMargin` so single-arc U-turns fit. If user has set a sub-1-pass HeadlandDistance, log a warning (UI, not runtime exception).

## What this subsumes

After this lands, mark the following as resolved (don't carry them forward):
- "still some issues with U-turns near inner boundary" (Phase 12 known issue) — fixed by #1, #2, #3.
- "skip-row pattern deferred" — no longer needed once cells are bounded by both headland types.
- "Long grey transits across the field" — fixed by #3.
- "Impossible turn angles" — fixed by #3 (perimeter walks are smooth by construction; no RS cusps).
- Held-Karp DP for cell ordering — still optional, but with proper headland-routed transits the greedy ordering becomes much closer to optimal (cell ordering matters less when transits are cheap perimeter walks rather than long straight-line shots). Keep deferred unless benchmarks show greedy is bad.
- Hybrid A* fallback — still optional.

## Success criteria — visual validation on user's 66-acre field with central pond

The work is **not done** until all of the following hold:

1. No U-turn extends into the cordoned pond polygon.
2. Inter-cell transits visibly ride on the headland loops, not straight lines through the field.
3. No impossible turn angles / RS reverse cusps anywhere in the plan.
4. Headland zone gets covered (per the chosen `OperationDirection`).
5. Cells around the pond have `InnerHeadland`-classified corners; U-turns there bulge into the inner headland buffer, not into the pond.

Visual validation is the success criterion, not unit tests passing. Unit tests verify code correctness; only the rendered route on the actual field verifies feature correctness.

## File-by-file change list

| File | Change |
|---|---|
| `Shared/AgValoniaGPS.Models/RoutePlanning/CellCornerKind.cs` | Add `InnerHeadland` enum value |
| `Shared/AgValoniaGPS.Models/RoutePlanning/OperationDirection.cs` | New file — InputFlow / OutputFlow enum |
| `Shared/AgValoniaGPS.Models/RoutePlanning/HeadlandLoop.cs` | New file — closed-loop polyline with perimeter walker / pose projector |
| `Shared/AgValoniaGPS.Models/Configuration/RoutePlanningConfig.cs` | Add `OperationDirection`, `DecompositionThresholdDegrees`, `MergeThresholdDegrees` |
| `Shared/AgValoniaGPS.Services/RoutePlanning/CellCornerClassifier.cs` | Classify against both outer and expanded inner rings |
| `Shared/AgValoniaGPS.Services/RoutePlanning/HeadlandTrackGenerator.cs` | New file — concentric-offset loop generator |
| `Shared/AgValoniaGPS.Services/RoutePlanning/TrapezoidalDecomp.cs` (and `RaycastDecomp.Run`) | Apply `DecompositionThreshold`; post-decomp merge using `MergeThreshold` |
| `Shared/AgValoniaGPS.Services/RoutePlanning/CellAwareRouteStitcher.cs` | Replace `EmitDubinsTransit` with `EmitHeadlandRoutedTransit`; treat InnerHeadland as valid corner kind; emit headland coverage in operation-direction order |
| `Shared/AgValoniaGPS.ViewModels/MainViewModel.Commands.Track.cs` | Wire HeadlandTrackGenerator output into the stitcher; warn on undersized HeadlandDistance |
| `Tests/AgValoniaGPS.Services.Tests/...` | Tests for InnerHeadland classification, perimeter walker, loop projection, headland-routed transit composition |

## Out of scope (genuinely, not deferred)

- Multi-tractor coordination (Hameed §2 references this but it's its own feature).
- 3D terrain / DEM-aware planning (Hameed [13], Han 2019).
- Capacitated operations (refill stops) — Jensen 2018.
- Cooperating machines — Jensen 2012.

These are real future work but are completely orthogonal to the inner-boundary issue. They will be tracked separately if/when the user wants them.
