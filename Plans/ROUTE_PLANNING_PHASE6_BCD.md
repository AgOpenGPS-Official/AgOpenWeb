# Phase 6 — Boustrophedon Cellular Decomposition (BCD)

**Branch:** `feature/route-planning`

## Goal

Replace the current obstacle-bypass strategy (direct Dubins → circuit walk) with sweep-line cell decomposition. One algorithm handles all three connected-component cases that currently produce invalid or curly-Q paths:

1. Inner boundaries (creeks, ditches, ponds) splitting swaths
2. Concave outer boundaries (notches) splitting swaths
3. Non-convex headlands (necks) splitting swaths

## Why BCD

Boustrophedon Cellular Decomposition (Choset 2000) decomposes a polygon-with-holes into a set of cells where each cell is *simply traversable* by a back-and-forth (boustrophedon) sweep. The decomposition is exact (no missed area, no overlap) and the cells are convex with respect to the sweep direction.

Key property: every swath within a cell is a single uninterrupted segment — there are no obstacles inside a cell. This eliminates the entire class of bugs we've been hunting (split swaths, curly Qs, transits that loop around obstacles).

Inter-cell transitions happen at *shared edges* (cell boundaries internal to the field). These are short Dubins turns at well-defined endpoints — same primitive as the existing headland turn.

## Algorithm

### Sweep line

Choose a sweep direction `d` (perpendicular to the swath direction). Sweep a line in direction `d` across the field.

At each polygon vertex (outer boundary + every inner boundary), classify the vertex into one of these types based on how the local edges relate to the sweep direction:

| Type | Description | Action |
|---|---|---|
| **Open** | Both edges go *away* from the sweep direction (vertex is the "near side" of a region) | Start a new cell |
| **Close** | Both edges go *toward* the sweep direction (vertex is the "far side" of a region) | Finish a cell |
| **Split** | One edge goes away, the other returns; vertex is locally a peak that divides one cell into two going forward (e.g., top of an inner-boundary obstacle, top of an outer-boundary notch from the inside) | Close 1 cell, open 2 |
| **Merge** | One edge returns, the other continues; vertex is locally a valley that joins two cells back into one (e.g., bottom of an inner-boundary obstacle, bottom of an outer-boundary notch) | Close 2 cells, open 1 |

Each cell is a polygon bounded by:
- Two **scan-line segments** (the sweep line at the cell's start and end)
- Two **edge chains** (portions of the outer/inner boundary on each side of the cell)

### Reeb graph

After decomposition, build a *Reeb graph*:
- Nodes = cells
- Edges = adjacency at critical points (a Split connects 1 parent to 2 children; a Merge connects 2 parents to 1 child)

The Reeb graph captures the topology of how cells connect. Traversal of the Reeb graph gives a visit order for cells.

### Traversal order

For now, use a simple DFS from a starting cell (the cell containing the vehicle, or the cell with the smallest sweep-direction coordinate if no vehicle position).

Future: TSP-style optimization minimizing inter-cell transit distance. This is a small finite problem (Reeb graph is sparse, typically ≤10 cells for realistic fields) and can use exact branch-and-bound.

### Per-cell swath generation

For each cell, generate parallel swaths in the swath direction (perpendicular to sweep direction):

1. Determine cell bounds along the sweep direction (cell's start scan-line, end scan-line).
2. For each swath offset within those bounds, clip the swath line against the cell's edge chains.
3. The clipped segment is a single unbroken swath (because the cell is convex w.r.t. sweep direction).

Order swaths within the cell as standard boustrophedon: alternate direction.

### Inter-cell transitions

Between two adjacent cells, the transition happens at a critical point where they share a boundary segment. The vehicle exits the last swath of cell A, performs a short Dubins turn, and enters the first swath of cell B.

The Dubins is constrained by:
- Exit pose: end of cell A's last swath
- Entry pose: start of cell B's first swath
- Turning radius
- Must stay inside the outer boundary
- Must not enter any inner boundary (raw, no buffer needed — cells already exclude inner regions)

This is exactly the existing TurnPathService primitive, re-used.

## Data structures

```csharp
// Models/RoutePlanning/Cell.cs
public class Cell {
    public int Id;
    public List<Vec2> Polygon;       // Closed polygon defining cell boundary
    public double SweepStart;         // Sweep coordinate at cell's start
    public double SweepEnd;           // Sweep coordinate at cell's end
    public List<int> AdjacentCellIds; // Reeb graph adjacency
    // Computed lazily:
    public List<Track> Swaths;        // Boustrophedon swaths within this cell
}

// Models/RoutePlanning/ReebGraph.cs
public class ReebGraph {
    public List<Cell> Cells;
    public List<(int FromCell, int ToCell, Vec2 SharedPoint)> Edges;
}

// Models/RoutePlanning/CriticalPoint.cs
public enum CriticalPointType { Open, Close, Split, Merge, Regular }

public class CriticalPoint {
    public Vec2 Position;
    public CriticalPointType Type;
    public double SweepCoordinate;
    public int VertexIndexInPolygon;
    public bool IsInnerBoundary;
}
```

## Services

```csharp
// Services/RoutePlanning/CellDecompositionService.cs
public interface ICellDecompositionService {
    ReebGraph Decompose(BoundaryPolygon outer, List<BoundaryPolygon> inner, double sweepHeading);
}

// Services/RoutePlanning/CellSwathGenerator.cs
public class CellSwathGenerator {
    List<Track> GenerateSwathsInCell(Cell cell, double swathHeading, double toolWidth, double overlap);
}

// Services/RoutePlanning/CellTraversalPlanner.cs
public class CellTraversalPlanner {
    List<int> PlanVisitOrder(ReebGraph graph, Vec2 startPosition);
}
```

## Implementation phases

### Phase A — Models + scaffolding (~1 hour)
- Create `Cell`, `ReebGraph`, `CriticalPoint` models
- Create empty service interfaces
- Stub implementations that return single-cell decomposition (for fields with no obstacles, BCD reduces to one cell — current behavior)
- Wire stub into `GenerateRoutePlan` behind a flag, verify no regression on simple fields

### Phase B — Critical point classification (~2 hours)
- For each polygon vertex (outer + inner), determine its type given sweep direction
- Sort vertices by sweep coordinate
- Unit tests: rectangle (4 Open/Close pairs), rectangle with rectangular hole (4 Open/Close + 1 Split + 1 Merge), L-shape outer (1 Split + 1 Merge), inverted-T outer (notch, 1 Split + 1 Merge)

### Phase C — Sweep-line cell construction (~3 hours)
- Process critical points in sweep order
- Maintain a list of "active cells" (cells currently being constructed)
- At each critical point, modify the active list per the type's action
- Each cell tracks its left/right edge chains
- Close cells emit a finished `Cell` polygon
- Tests: same fixtures as Phase B, verify cell count, polygon closure, no overlap

### Phase D — Reeb graph adjacency (~1 hour)
- Track which cells are joined at each critical point
- Build adjacency list
- Tests: verify expected graph topology for fixtures

### Phase E — Cell traversal + per-cell swaths (~2 hours)
- DFS traversal from start cell
- Per-cell swath generation (clip parallel lines against cell polygon)
- Boustrophedon ordering within cell
- Tests: visit-all-cells-once verification, swath continuity within cell

### Phase F — Cell-aware stitching + integration (~2 hours)
- Inter-cell Dubins at shared edge
- Replace `RouteStitchingService` with cell-aware variant (or add a parallel path behind a flag)
- Update `GenerateRoutePlan` in MainViewModel.Commands.Track
- Remove direct-bypass and circuit-walk fallback for obstacle case (keep them for outer headland passes only)
- Tests: end-to-end with all three fixture types

### Phase G — Cleanup (~1 hour)
- Remove `TransitPathService.TryDirectBypass` if no longer needed
- Remove `RouteStitchingService.ReorderSplitPiecesForTraversal` (cells eliminate split siblings)
- Remove `SwathGenerationService.SubtractInnerBoundary` (cells handle obstacles)
- Update plan docs

Total: ~12 hours.

## Test fixtures

### Fixture 1: Rectangle (no obstacles)
- Verify single cell, all swaths uninterrupted, plan matches current behavior on simple fields.

### Fixture 2: Rectangle with rectangular hole
- 1 outer + 1 inner = 2 cells (left of hole, right of hole, with split at hole-top and merge at hole-bottom OR depending on sweep direction).
- Verify route visits both cells, transition at the cell shared edge is short Dubins.

### Fixture 3: L-shape outer boundary
- 1 cell becomes 2 cells at the L's inside corner (Split critical point).
- Verify route visits both cells.

### Fixture 4: Inverted-T outer ("notch field")
- Notch in the middle of the top edge → field splits into left arm + right arm + top column.
- Verify 3 cells with appropriate adjacency.

### Fixture 5: Hourglass outer (non-convex headland with neck)
- Headland zone has a narrow neck → 2 cells on either side of the pinch.
- Verify route visits both cells via Dubins through the neck.

## Migration

The existing route plan code remains functional. BCD will be added as a NEW path:

- New service `ICellDecompositionService`
- New stitching path that uses cells
- `GenerateRoutePlan` calls BCD by default; old code preserved behind a feature flag for one release
- After visual verification across all 5 fixtures, remove the old code path

## Open questions

1. **Sweep direction:** Always perpendicular to swath direction (the natural choice), but could be tunable for irregular fields.
2. **Headland circuit passes:** Keep the existing `HeadlandCircuitService` for outer perimeter passes — those are useful independent of obstacle handling.
3. **Cell visualization:** Should we render cell boundaries in the map for debugging? Probably yes during development, hidden in production.
4. **Resumability:** Loaded routes (`RoutePlan.json`) don't store cells — they store waypoints. So persistence is unaffected. BCD only runs at plan generation.

## References

- Choset, H. "Coverage of Known Spaces: The Boustrophedon Cellular Decomposition." *Autonomous Robots*, 2000.
- Driscoll, B. "Complete Coverage Path Planning for Polygons with Holes." MS Thesis, 2011.
- Bochkarev, S. and Smith, S. "On Minimizing Turns in Robot Coverage Path Planning." *CASE*, 2016.
- Galceran, E. and Carreras, M. "A Survey on Coverage Path Planning for Robotics." *Robotics and Autonomous Systems*, 2013.
