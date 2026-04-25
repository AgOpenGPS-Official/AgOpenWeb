# Phase 6 — Hybrid Route Planning

**Branch:** `feature/route-planning`

## Goal

Single route planning pipeline that handles every realistic field feature:
1. **Concave outer boundary** (notch) — field-topology change
2. **Bottleneck / non-convex headland** (neck) — field-topology change
3. **Pond / ditch / creek floating in field** — undrivable local exclusion
4. **Mud patch** — drivable local exclusion (`IsDriveThrough = true`)
5. **Creek crossing field-edge to field-edge** — represented as concavity in the outer boundary; handled as topology

## Two distinct mechanisms

| Feature class | Mechanism | Why |
|---|---|---|
| Outer-boundary topology (concavity, bottleneck) | **BCD cell decomposition** | Field is geometrically split into multiple regions; needs cell-level traversal planning |
| Inner boundary, undrivable (pond) | **Swath split + tangent-line bypass** | Doesn't disconnect field; vehicle drives around with implement up |
| Inner boundary, drivable (mud) | **Swath split + straight-line drive-through** | Same as above but section control lifts implement; vehicle drives over |

Critical observation: **Inner boundaries are always local in the AgValoniaGPS data model.** `BoundaryPolygon.InnerBoundaries` are holes in the outer. They cannot disconnect the field — by definition they don't touch the outer. Topology comes purely from outer boundary shape (concavities, narrow necks).

This simplifies the classifier: every entry in `BoundaryPolygon.InnerBoundaries` is a local exclusion. Topology arises only from the outer polygon itself.

## Architecture

```
GenerateRoutePlan(field):

  1. BCD decomposes OUTER boundary (with no inner holes given to BCD)
     → produces convex cells, each with sweep extents classified as:
        HEADLAND  — extent is on the outer-boundary edge
        INTERNAL  — extent is a virtual sweep-line at a concavity / neck

  2. For each cell, in optimal visit order (Held-Karp):
     a. Generate parallel swaths spanning the cell polygon
     b. For each swath, clip against EVERY inner boundary
        - drivable inner (IsDriveThrough): swath stays as one segment
          (vehicle drives over with section control lifting; the pieces are
          notionally split but treated as one segment for routing purposes)
        - undrivable inner: swath splits into segments at the inner-buffer edge
     c. Boustrophedon order over swaths, plus extra segments for splits:
        Drive swath_0 piece_a → bypass around obstacle → swath_0 piece_b →
        U-turn at headland → swath_1 piece_b → bypass → swath_1 piece_a →
        U-turn at headland → ...
     d. **U-turns at HEADLAND extents only.** Bypass / inter-cell transit
        at INTERNAL extents.

  3. Inter-cell transit (BCD-level): from one cell's last swath endpoint to
     next cell's first swath endpoint. Transit goes along the outer boundary's
     headland zone or through the bottleneck neck. Dubins primitive, validated
     against outer.
```

## Key new components

### A. Cell extent classifier
For each cell, label each of its 4 corner positions (low-sweep low-perp / low-sweep high-perp / high-sweep low-perp / high-sweep high-perp) as:
- **Headland**: corner lies on the outer boundary edge
- **Internal**: corner is at a virtual sweep-line segment (Open/Close vertex of outer counts as Headland; Split/Merge vertex from outer concavity counts as Internal)

Source data: each Cell already knows its floor (from `_cellFloor`) and its polygon vertices. Classify by whether floor/ceiling endpoints lie on the outer boundary.

### B. Tangent-line bypass service
Given:
- Exit pose (split-sibling endpoint, heading along swath direction)
- Entry pose (next sibling endpoint, same direction)
- Inner-buffer polygon

Compute:
- The two outer common tangents from exit to the buffer polygon
- The two outer common tangents from entry to the buffer polygon
- Paths: exit → tangent point → walk buffer edge → tangent point → entry
- Pick the (CW or CCW) walk direction giving shorter total path

This replaces the curly-Q Dubins approach. Tangent lines naturally align with polygon-edge tangents at the contact point — no heading-mismatch loops.

### C. Drive-through swath connector
For drivable inner boundaries: the split is bookkept (so section control engages over the buffer) but the route just drives straight from one piece to the next. Visually the swath is rendered uninterrupted; the implement lifts automatically over the muddy area.

Wait — the swath may need to deviate slightly if the driveable inner is irregularly shaped. Decision: for drivable boundaries, route does NOT split the swath. The vehicle drives the original full-length swath; section control handles spraying.

### D. Per-cell boustrophedon with proper extent semantics
Within a cell:
- Headland-headland cell (typical interior cell): standard boustrophedon, all U-turns at headland.
- Headland-internal cell (cell next to a concavity): U-turns at the headland end, transit-to-adjacent-cell at the internal end.
- Internal-internal cell (cell sandwiched between two topology features): all transitions are inter-cell transits.

The boustrophedon walker needs the cell's extent classification to choose between U-turn and transit at each swath endpoint.

### E. Held-Karp updated
Cell exit options become headland-corner-only (4 → 2 if cell has 2 headland corners, etc.). Internal corners aren't valid exit points — you can't end your boustrophedon there without doing an inter-cell transit.

## What we keep / what we replace / what we delete

**Keep:**
- BCD core (`BcdSweep`, `CellDecompositionService`, `CellSwathGenerator`, `Cell`, `ReebGraph`, `CriticalPointClassifier`) — proven primitives
- `SwathGenerationService.SubtractInnerBoundary` — already does swath clipping against inner boundaries, reuse for per-cell swath splitting
- `SectionControlService` — already lifts implement over inner boundaries
- `TurnPathService` — Dubins U-turns at headlands

**Replace:**
- `TransitPathService.TryDirectBypass` and `TryBuildTransit` — replace with tangent-line bypass for inner boundaries
- `RouteStitchingService.StitchFromCells` — needs extent-aware boustrophedon

**Delete:**
- `HeadlandCircuitService` (and the outer-circuit-pass UI hooks) — outer perimeter is now covered by Held-Karp ordering through cells, no separate circuit needed
- The current `StitchRoute` (legacy) once `StitchFromCells` covers all cases

## Implementation phases

### Phase 1 — Cell extent classification (~2 hours)
- Annotate each `Cell` with which of its corners are HEADLAND vs INTERNAL
- For BCD outer-only decomposition (no inner holes), classify by checking whether each corner lies on the outer polygon edge
- Tests: rectangle (4 headland corners), L-shape (2 cells, each with 2 headland + 2 internal corners), notched outer (3 cells, similar)

### Phase 2 — Tangent-line bypass service (~3 hours)
- New `TangentBypassService.GenerateBypass(exit, entry, innerBuffer, swathHeading)`
- Compute outer common tangents from each pose to the buffer polygon
- Walk buffer edge between tangent contact points (CW and CCW)
- Pick shorter valid path
- Tests: pond between two split swaths, irregular polygon obstacle, multiple buffers

### Phase 3 — Per-cell swath generator with split-against-inner (~2 hours)
- Extend `CellSwathGenerator` to subtract inner boundaries from each swath
- Drivable boundaries: track but don't split the route segment
- Undrivable boundaries: split + flag for tangent bypass
- Tests: cell with central pond, cell with mud patch, cell with both

### Phase 4 — Extent-aware route stitcher (~3 hours)
- Rewrite `StitchFromCells` to use cell extent classification
- U-turns only at HEADLAND extents
- Tangent bypasses for split-sibling pairs within a cell (undrivable inner)
- Drive-through for split-sibling pairs (drivable inner)
- Inter-cell transits at INTERNAL extents (along headland)
- Tests: full plan over fixture fields

### Phase 5 — Held-Karp updates (~1 hour)
- Restrict entry/exit corners to HEADLAND corners only
- Re-tune state representation if needed (might be 2 entries × 2 exits = 4 options instead of 4×4)

### Phase 6 — Integration into GenerateRoutePlan (~1 hour)
- Wire BCD-on-outer-only + per-cell swath/split + extent-aware stitching
- Remove dead code (HeadlandCircuitService, old TransitPathService legacy paths)

### Phase 7 — Visual rendering (~1 hour)
- Tangent bypasses render gray (transit, implement up)
- Drive-throughs render light cyan (working but in lifted zone)
- Inter-cell transits render gray
- Boustrophedon U-turns render orange (working transitions in headland)

### Phase 8 — Tests on real-field fixtures (~2 hours)
- Pond floating in convex field
- Mud patch in convex field
- Pond + mud + concave outer in same field
- Bottleneck headland with inner pond
- 1.5-acre pond in 66-acre field (the actual test)

Total: ~15 hours.

## Test fixtures (each gets a unit test + visual verification)

1. **Pond in rectangle** — 4 cells of BCD avoided; one cell with inner exclusion. Swaths split, tangent bypass.
2. **Mud patch in rectangle** — One cell, swaths uninterrupted, section control engages.
3. **L-shape (concave outer)** — 2 cells, transit at the inside corner.
4. **Notched outer** — 3 cells, transits at the notch.
5. **Hourglass headland** — 2 cells, transit through neck.
6. **Pond inside L-shape** — 2 cells, with pond inside one cell. Tangent bypass + cell transit.
7. **The 66-acre field with 1.5-acre pond** — visual ground truth.

## Migration

- Old `StitchRoute` and `TransitPathService` (curly-Q variant) stay until phases 4-6 land, then delete.
- BCD on outer-only is a flag/argument to `CellDecompositionService` — pass empty inner list. Existing tests with inner boundaries become integration tests for the COMBINED pipeline (BCD outer + inner exclusions).

## Open questions for you

1. **Tangent direction preference.** When two CW/CCW walks have similar length, prefer the one that keeps the vehicle further from the obstacle? Or just shorter?
2. **Section control / drive-through for partial obstacles.** If a swath only barely clips a drivable inner, do we still consider it a drive-through, or does section control just lift the affected section?
3. **Visualizing the cell decomposition.** Useful during dev — a "show cells" toggle? Or just trust the route output?
