# Phase 6 — Selective F2C Port

**Branch:** `feature/route-planning`

## Goal

Native C# route planner that handles every realistic agricultural field feature, modeled on Fields2Cover (F2C, BSD-3, Wageningen University 2020-2024) but reimplemented to use our existing primitives (Vec2/Vec3, Clipper2, our DubinsPathService).

Result: full ag-coverage planner in pure managed code, no native C++ deps, single binary on every target platform (Windows / macOS / Linux / iOS / Android).

## Why this approach

Fields2Cover's *algorithm choices* are battle-tested over 4+ years of academic + practical use. But its implementation leans heavily on:

- **GDAL / GEOS** for geometry (we already have Clipper2 + Vec2/Vec3 — equivalent capability)
- **steering_functions** for Dubins / Reeds-Shepp / continuous-curvature variants (port the steering algorithms we want)
- **Google OR-Tools** for routing TSP (our cell counts are small enough for Held-Karp DP — already implemented)
- **CGAL / Boost** (replace with primitives we already have)

The *F2C C# package on NuGet would be ~50 MB of native code we'd be binding to*. A literal port of F2C with all dependencies = porting 25-30K lines including external libs. We'd own a C# fork that drifts from upstream forever.

A *selective port* — taking F2C's algorithms and architecture, reimplementing in our stack — gives us the same capabilities with ~4,000 lines of native C# we can read, debug, and own.

## What we take from F2C

### Architecture patterns

- **Strategy interfaces** for swath generation, route planning, path planning. F2C uses C++ inheritance; we'll use C# interfaces.
- **Pluggable cost objectives** for swath direction optimization (`SGObjective` family). Useful for "minimize total turn count" vs "minimize total swath length" tradeoffs.
- **Cells-as-polygons-with-holes** model. F2C's `F2CCells` are polygons with optional inner rings. We extend our `Cell` model the same way.

### Specific algorithms

| Algorithm | F2C source | C# adaptation |
|---|---|---|
| Trapezoidal cell decomposition | `decomposition/trapezoidal_decomp.cpp` | Raycast from each polygon vertex; split along ray-to-border lines using Clipper2 polygon ops |
| Boustrophedon cellular decomposition | `decomposition/boustrophedon_decomp.cpp` | Trapezoidal, but only at *critical* (non-edge-aligned) vertices |
| Brute-force optimal swath angle | `swath_generator/brute_force.cpp` | Try every degree, pick min-cost. ~50 lines |
| Swath generation by rotation | `swath_generator/swath_generator_base.cpp` | Rotate cell to align swath direction with x-axis, generate horizontal lines, rotate back. Cleaner than my current approach |
| Boustrophedon / Snake / Spiral / Custom orderings | `route_planning/*_order.cpp` | Each is <50 lines. Direct port |
| Dubins curves | `path_planning/dubins_curves.cpp` (wraps `steering_functions`) | We already have DubinsPathService |
| Continuous-curvature Dubins | `path_planning/dubins_curves_cc.cpp` (wraps `steering_functions`) | Port from `steering_functions` repo (~300 lines of math) |
| Reeds-Shepp curves | `path_planning/reeds_shepp_curves.cpp` (wraps `steering_functions`) | Port from `steering_functions` repo (~700 lines of math, well documented in original Reeds & Shepp 1990 paper) |
| Coverage routing TSP | `route_planning/route_planner_base.cpp` (uses OR-Tools) | Held-Karp DP for cell-level TSP; we don't need swath-level TSP because boustrophedon fixes order |
| Visibility graph for shortest paths around obstacles | `route_planning/route_planner_base.cpp` | Reduced version: only needed for inter-cell transit when boundary geometry is non-trivial |

### Path-planning primitives we add

**Reeds-Shepp** (~700 lines): like Dubins but allows reverse motion. F2C uses 48 path types; we'll port the full set. Useful for tight headland turns where Dubins forward-only fails.

**Continuous-curvature Dubins** (~300 lines): Bezier-smoothed Dubins arcs. Eliminates the curvature discontinuity at arc-to-line transitions, producing paths the tractor can actually follow without jerk. F2C's `dubins_curves_cc.h` is the reference.

**Hybrid A\*** (~1,500 lines, OPTIONAL): for obstacle-aware planning where Dubins/Reeds-Shepp validation fails. Search in (x, y, θ) discretized state space with kinematic primitives. Use only when the simpler primitives can't find a valid path. F2C doesn't include this — it's an enhancement beyond F2C.

## Architecture

```
GenerateRoutePlan(field):

  1. Decomposition (BCD / Trapezoidal)
     Input: outer polygon, inner-boundary polygons that are TOPOLOGICAL
            (touch or cross the outer boundary in a way that disconnects)
     Output: list of convex Cells. Each cell is a polygon-with-holes:
            - outer ring: a sub-region of the original outer
            - inner rings: any local obstacles falling within this cell
     Cell extents classified HEADLAND (on outer boundary) vs INTERNAL
     (virtual sweep-line at decomposition split).

  2. Swath generation per cell
     For each cell:
       a. Optional: brute-force optimal swath angle (else use AB line heading)
       b. Generate parallel swaths at op_width spacing
       c. For each swath, clip against cell's local inner-boundary obstacles
          - DRIVABLE inner (IsDriveThrough=true): swath kept as one segment;
            section control engages over the obstacle
          - UNDRIVABLE inner: swath splits at the obstacle's expanded buffer

  3. Cell visit order (Held-Karp on cell graph)
     - 4 entry/exit corner options per cell (constrained to HEADLAND corners)
     - Pairwise inter-cell transit costs (Reeds-Shepp distance, computed in mm)
     - State space 2^N · N · 4 — exact for N ≤ 20

  4. Per-cell stitching
     For each cell visit:
       a. Boustrophedon swath order based on entry corner
       b. For each consecutive swath pair:
          - Same swath, drivable obstacle split: straight-line connection (drive over)
          - Same swath, undrivable obstacle split: tangent-line bypass around buffer
            (or hybrid A* if buffer geometry is complex)
          - Adjacent swaths within cell: Reeds-Shepp U-turn at headland
       c. Exit at the chosen exit corner

  5. Inter-cell transit
     For each cell-to-cell hop:
       a. Reeds-Shepp / continuous-curvature Dubins from previous exit
          to next entry
       b. If validation fails (path crosses outer or inner): hybrid A* fallback

  6. Output: RoutePlan
     - Ordered list of segments (swath / turn / transit / drive-through)
     - Total distance, turn count, transit count, invalid count
     - Cell visualization data (for debug overlay)
```

## Code organization (after port)

```
Shared/AgValoniaGPS.Models/RoutePlanning/
  Cell.cs                       (existing, extend with CornerKind enum)
  ReebGraph.cs                  (existing)
  CriticalPoint.cs              (existing)
  RoutePlan.cs / RouteSegment.cs / RouteSegmentType.cs (existing, extend Type)
  CellCornerKind.cs             (NEW: Headland / Internal)

Shared/AgValoniaGPS.Services/RoutePlanning/
  CellDecompositionService.cs   (rewrite using raycast approach)
    Raycast.cs                  (NEW: ray-to-polygon-border)
    TrapezoidalDecomp.cs        (NEW: F2C-style decomposition)
    BoustrophedonDecomp.cs      (NEW: critical-points-only variant)
  CellSwathGenerator.cs         (rewrite with rotation approach)
    BruteForceSwathAngle.cs     (NEW: optimal angle via 1° sweep)
  CellTraversalPlanner.cs       (existing, extend for headland-only corners)
  RouteStitchingService.cs      (rewrite StitchFromCells with new primitives)
  TangentLineBypass.cs          (NEW: outer-tangent geometry for inner obstacles)

Shared/AgValoniaGPS.Services/PathPlanning/
  DubinsPathService.cs          (existing)
  DubinsCC.cs                   (NEW: continuous-curvature variant via Bezier)
  ReedsShepp.cs                 (NEW: 48 path types, ported from steering_functions)
  HybridAStar.cs                (NEW: A* in (x,y,θ) state space — OPTIONAL phase)
```

Files to delete after port stabilizes:
- `BcdSweep.cs` — replaced by trapezoidal/raycast decomposition
- `HeadlandCircuitService.cs` — circuit passes are subsumed by per-cell swath generation
- `TransitPathService.cs` — replaced by tangent-line bypass + hybrid A* fallback
- `BcdSweepTests.cs` — replaced by raycast decomposition tests

## Implementation phases

### Phase 1 — Reeds-Shepp curves (~3 hours)
Port `steering_functions` Reeds-Shepp implementation to C#. Two parts:
- 48 analytical path families (CSC, CCC, CCCC, etc.) from Reeds & Shepp 1990
- `getPath(start, goal)` that picks shortest valid

Test against fixtures: known-shortest paths for various poses. Compare lengths to F2C output.

### Phase 2 — Continuous-curvature Dubins (~2 hours)
Port `dubins_curves_cc` from `steering_functions`. Bezier-smoothed transitions between arcs. Useful when downstream guidance can't handle curvature discontinuities.

Test: visual smoothness, path length within 5% of plain Dubins.

### Phase 3 — Raycast-based decomposition (~4 hours)
Replace `BcdSweep` with F2C-style trapezoidal + boustrophedon decomposition.

- For each polygon vertex, cast two rays at split_angle and split_angle+π
- Use Clipper2 to split the polygon along the rays
- Boustrophedon variant: only generate split lines at *critical* vertices (where rays don't lie on a polygon edge)
- Output: list of Cell polygons (each a polygon-with-holes if local obstacles are inside)

Tests: rectangle (1 cell), L-shape (2 cells), notched outer (3 cells), rectangle with central hole (1 cell with one inner ring), hourglass (2 cells).

### Phase 4 — Cell-corner classification (~2 hours)
For each cell, classify each of its 4 corners (low-sweep low-perp, low-sweep high-perp, high-sweep low-perp, high-sweep high-perp) as HEADLAND or INTERNAL based on whether the corner lies on the outer-boundary polygon edge.

Tests: rectangle (4 headland), L-shape (2 cells with 2 + 2 internal/headland mix), notched outer.

### Phase 5 — Brute-force optimal swath angle (~2 hours)
Port `BruteForce::computeBestAngle`. Takes a cell + cost objective, sweeps angles in 1° steps, returns lowest-cost angle.

Cost objectives:
- `MinTurnCount`: equivalent to F2C's NSwath (number of swaths)
- `MinTotalLength`: equivalent to FieldCoverage objective
- `MinDistance` (default for now)

Tests: irregular-shape cells where optimal angle is known geometrically (e.g., rectangle's optimal is along its long axis).

### Phase 6 — Cell-swath rotation + clipping (~3 hours)
Replace current `CellSwathGenerator` with F2C's rotation-based approach:
- Rotate cell polygon by -angle so swaths align with x-axis
- Generate horizontal lines at op_width spacing
- For each line, clip against rotated cell polygon (gives 2-point segments since cell is convex w.r.t. swath direction... mostly)
- Handle local-obstacle clipping per swath
- Rotate results back by +angle

Tests: regression vs current implementation for rectangle + hole, plus new cases.

### Phase 7 — Tangent-line bypass + drive-through connector (~3 hours)
For undrivable inner-boundary obstacles, compute the outer common tangent geometry:
- Two tangent lines from each split-sibling endpoint to the inner-buffer polygon
- Walk buffer edge between contact points (CW or CCW)
- Build path: line → buffer-edge polyline → line

For drivable obstacles: straight-line connection (section control handles lift).

Tests: pond geometry (convex hull of inner buffer), irregular obstacle, multiple obstacles in one cell.

### Phase 8 — Cell-aware route stitcher rewrite (~4 hours)
Rewrite `StitchFromCells`:
- Boustrophedon swath order from chosen entry corner
- Reeds-Shepp U-turns at HEADLAND corners only
- Tangent bypass / drive-through for split-sibling pairs
- Inter-cell transit: Reeds-Shepp first; if invalid, hybrid A* fallback (Phase 9 if we add A*)
- Held-Karp constrained to HEADLAND-only entry/exit options

Tests: end-to-end on all fixture fields.

### Phase 9 — Hybrid A\* (~6 hours, OPTIONAL)
A* in (x, y, θ) state space with kinematic motion primitives. Use only when Reeds-Shepp validation fails for a transit segment.

- Discretization: 1m × 1m × 36 angle bins for typical fields
- Heuristic: max(Reeds-Shepp distance ignoring obstacles, Euclidean shortest path respecting obstacles)
- Motion primitives: forward arcs ± straight ± reverse arcs, scaled to grid
- Termination: within tolerance of goal

Decide inclusion based on whether Reeds-Shepp + tangent bypass cover the test fields' obstacle scenarios. Skip if not needed.

### Phase 10 — Integration into GenerateRoutePlan (~2 hours)
Wire up: decomposition → swath gen → cell ordering → stitching. Replace current pipeline.

### Phase 11 — Visualization (~2 hours)
- Optional cell-boundary overlay (debug toggle)
- Distinct rendering for transit (gray), drive-through (light cyan), Reeds-Shepp turns with reverse arcs (orange + reverse-arrow markers)

### Phase 12 — Validation on real fields (~3 hours)
- 1.5-acre pond in 66-acre field (the actual test)
- L-shaped field (concavity)
- Hourglass field (bottleneck)
- Mixed: pond + concavity + drivable mud

Total: **~36 hours** (~30 if we skip Phase 9 hybrid A*).

## Test fixtures

1. **Rectangle, no obstacles** — 1 cell, no transits, simple boustrophedon.
2. **Rectangle + small drivable obstacle** — 1 cell, swaths uninterrupted, section control engages.
3. **Rectangle + small undrivable obstacle (pond)** — 1 cell, split swaths reconnected via tangent-line bypass.
4. **Rectangle + multiple obstacles** — 1 cell, multiple bypasses per swath.
5. **L-shape outer** — 2 cells, transit at concavity.
6. **Notched outer** — 3 cells.
7. **Hourglass headland (bottleneck)** — 2 cells, transit through neck.
8. **L-shape outer + pond** — 2 cells with local obstacle in one.
9. **66-acre field with 1.5-acre pond** — actual user scenario.
10. **66-acre field with pond + concavity + drivable mud** — combined complexity.

## Decisions

1. **Decomposition default:** F2C-style Boustrophedon (Choset 2000) — critical-points-only, fewer cells than plain Trapezoidal.
2. **Swath-angle cost objective:** user-configurable, default = minimize number of swaths (favors long swaths along the cell's long axis).
3. **Hybrid A\*:** included in scope (Phase 9). Cleaner fallback for the rare cases where Reeds-Shepp + tangent-bypass both fail.
4. **Cell visualization:** user toggle in view settings, default ON during development phase, default OFF for release.
5. **Section control over drivable inners:** existing behavior unchanged; the swath remains a single segment in the route plan, section control engages during execution as the vehicle crosses the inner-buffer.

## Migration

The new pipeline replaces `BcdSweep` + `HeadlandCircuitService` + `TransitPathService` (curly-Q variant). The current BCD work is committed to feature/route-planning and stays in history; deletion in Phase 10 cleanup.

The `feature/route-planning` branch stays — all phases land here. No PR until Phase 12 passes all fixtures.

## References

- Mier, G. et al. (2023). "Fields2Cover: An Open-Source Coverage Path Planning Library for Unmanned Agricultural Vehicles."
- Choset, H. (2000). "Coverage of Known Spaces: The Boustrophedon Cellular Decomposition." Autonomous Robots.
- Reeds, J. and Shepp, L. (1990). "Optimal paths for a car that goes both forwards and backwards." Pacific Journal of Mathematics.
- Dolgov, D. et al. (2010). "Path Planning for Autonomous Vehicles in Unknown Semi-structured Environments." IJRR.
- F2C source: https://github.com/Fields2Cover/Fields2Cover (BSD-3, Wageningen University)
- Steering Functions source: https://github.com/Fields2Cover/steering_functions (BSD-3)
