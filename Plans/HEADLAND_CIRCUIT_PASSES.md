# Headland Circuit Passes

## Issue #252

## Summary

Generate tracks that follow the boundary perimeter at offset multiples of the tool width. These passes:

1. Work the perimeter before/after the interior swaths
2. Create a buffer zone around inner boundaries (obstacles) so route planning can safely turn at the edge

## Background

`PolygonOffsetService` (Clipper2) already has:
- `CreateInwardOffset(boundaryPoints, offsetDistance)` — shrinks polygon inward
- `CreateOutwardOffset(boundaryPoints, offsetDistance)` — grows polygon outward

Both return `List<Vec2>?` — null when the polygon collapses (offset too large).

## Implementation

### Step 1: HeadlandCircuitService

**New file:** `Services/RoutePlanning/HeadlandCircuitService.cs`

```csharp
public class HeadlandCircuitPass
{
    public List<Vec3> Points { get; set; } = new();
    public int PassNumber { get; set; }      // 0 = outermost, 1 = one in, etc.
    public double OffsetDistance { get; set; }
    public bool IsInnerBoundary { get; set; } // true if around an obstacle
}

public class HeadlandCircuitService
{
    public List<HeadlandCircuitPass> GenerateOuterPasses(
        BoundaryPolygon outerBoundary,
        double toolWidth,
        int passCount);

    public List<HeadlandCircuitPass> GenerateInnerPasses(
        BoundaryPolygon innerBoundary,
        BoundaryPolygon outerBoundary, // for clipping
        double toolWidth,
        int passCount);
}
```

**Algorithm:**
- Outer: progressively inset, offset at `(pass + 0.5) * toolWidth` from outer edge
- Inner: progressively expand outward, offset at `(pass + 0.5) * toolWidth` from inner edge, intersected with outer boundary
- Convert polygon points to `Vec3` with headings (tangent direction along polygon)

### Step 2: Integrate with Route Planning

**Modify:** `RouteStitchingService.StitchRoute()`

Add optional headland passes before and/or after the interior swaths:

```csharp
public class RouteStitchConfig
{
    // ... existing fields ...
    public List<HeadlandCircuitPass>? HeadlandPassesBefore { get; set; }
    public List<HeadlandCircuitPass>? HeadlandPassesAfter { get; set; }
}
```

Stitch order:
1. Outer headland passes (if configured "before")
2. Interior swaths with inner-boundary-safe turns
3. Outer headland passes (if configured "after")

For now, start with "before" only (simpler). Inner boundary buffer zones come next.

### Step 3: Turn Inner Swath Segments at Inner Buffer

**Modify:** `SwathGenerationService`

When subtracting inner boundaries, also subtract an expanded version (outward offset by N × tool_width). This creates a buffer where swaths terminate early, giving room for turns.

Or simpler: let the swath segments terminate at the raw inner boundary, but have the turn path service generate short return turns at the inner edge. The headland passes around the inner boundary provide the working coverage of the buffer zone.

### Step 4: UI

**Modify:** `RoutePlanDialogPanel`

Add options:
- [ ] Include outer headland passes
- Number of passes: [1] [2] [3]
- [ ] Include inner boundary buffer passes

## Implementation Order

1. `HeadlandCircuitService` with outer passes
2. Test visually with outer passes on a simple field
3. Integrate into RouteStitchConfig
4. Add UI toggle
5. Inner boundary passes
6. Route planning integration for inner boundaries

## Files

| File | Action |
|------|--------|
| `Services/RoutePlanning/HeadlandCircuitService.cs` | Create |
| `Services/Interfaces/IRouteStitchingService.cs` | Modify — add config fields |
| `Services/RoutePlanning/RouteStitchingService.cs` | Modify — emit headland segments |
| `Models/RoutePlanning/RouteSegment.cs` | Modify — add HeadlandCircuit segment type? |
| `ViewModels/MainViewModel.Commands.Track.cs` | Modify — call HeadlandCircuitService |
| `Views/Controls/Dialogs/RoutePlanDialogPanel.axaml` | Modify — add toggles |
| `Tests/.../HeadlandCircuitServiceTests.cs` | Create |

## Verification

1. Rectangular field, 1 outer pass — verify a single rectangular loop inside the boundary
2. Rectangular field, 3 outer passes — verify 3 concentric loops
3. Field with inner boundary, inner buffer enabled — verify loop around the obstacle
4. Full route plan with outer + interior + inner buffer — verify everything stitches correctly
