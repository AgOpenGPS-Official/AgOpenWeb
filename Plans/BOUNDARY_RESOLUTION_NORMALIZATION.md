# Boundary Resolution & Normalization

Status: **Phase 1 + concentric-tram landed** · Owner: boundary/guidance · Related issue: #422

Verified on the 328 ha "Res Test" field: boundary 3431→342 points, `TramLines.txt`
14.4 MB→1.9 MB, field open 2–3 min→~12 s→fast (tram now on-demand), and the legacy
tram "web" replaced by clean concentric controlled-traffic lanes.

## Problem

A 440-acre (~178 ha) irregular European field with curved edges records a boundary
of **~3431 points at ~1.16 m spacing**. That density is the *only* representation we
have: it is the in-memory geometry, the on-disk format, the input to every derived
layer, and the per-frame render source. Symptoms:

- `TramLines.txt` grows to **14.4 MB / 759k lines** (1168 tram lines × up to 3432 pts).
- Opening the field takes **2–3 minutes** (parse + push + per-frame render of ~750k pts).
- Tram generation is **O(numLines × pts × boundaryPts)** — ~10 billion point-in-polygon
  ops — because `GenerateParallelTramLines` offsets the dense boundary-derived curve
  ~1168× and clips every offset point against the full dense fence.

These are all symptoms of **one missing abstraction**: there is no step that
normalizes boundary resolution once, at the source. Capture density propagates
unfiltered into storage, compute, derived geometry, and rendering.

## How AgOpenGPS avoids this (reference)

Studied in `/Users/chris/Code/AgOpenGPS` (legacy `SourceCode/GPS/Classes/` is the live
layer; `SourceCode/AgOpenGPS.Core/Models/Field/` is the cleaner model-only future).

1. **Area-scaled spacing** — `CFenceLine.FixFenceLine` resamples a recorded boundary to
   **1.1 m (<20 ha) / 2.2 m (<40 ha) / 3.3 m (>40 ha)** (inner rings ×0.5). Densifies big
   gaps, decimates tight ones. Bounds point count by field size; a 178 ha field → ~3.3 m,
   not 1.16 m.
2. **Dense-geometry / sparse-hit-test split** — `fenceLine` (dense) for geometry +
   `fenceLineEar` (corners only, decimated by 0.005 rad heading change) for the O(n)
   inside-tests.
3. **Cheap derived geometry** — headland/tram are a single perpendicular offset +
   proximity-reject cleanup (no repeated whole-polygon offsetting); tram field-passes
   come from the AB reference, with only **two** boundary-follow tracks.
4. No external clipping library; no spatial index (decimation alone keeps O(n) cheap).

Where **we are already ahead**: AgOpenWeb uses **Clipper2** for offsetting
(`PolygonOffsetService`) and has a **spatial index** on `BoundaryPolygon` (used by section
control). We also already have `DouglasPeuckerSimplify`, `ResamplePolygon`,
`DensifyPolygon` — but they are **private/orphaned** and unreachable from any public path.

## Design principles (adopt + improve)

- **Normalize once, at the source.** A single canonical step runs on every boundary
  entry path (record-finalize, build-from-tracks, AgOpen/GeoJSON import) and produces the
  canonical geometry that everything else consumes and that we persist.
- **Improve on AgOpen's uniform resample with curvature-adaptive simplification**
  (Option 1, chosen): Douglas-Peucker (ε ≈ 0.1 m) drops collinear runs and keeps curve
  detail, paired with a **max-gap densify cap** so long straight edges still carry points
  for offsetting/rendering. One tuning constant; shape-adaptive; reuses our DP code.
  - *Why not area-scaled-ε (the “hybrid”)?* DP bounds *deviation*, not *count*, so an
    area-tolerance curve is an indirect, hard-to-calibrate lever and adds a second knob.
    Fixed-ε DP is already scale-robust (ε is a deviation budget, not a spacing). If real
    fields later show fixed-ε mis-sized, add a **hard point-count ceiling** (re-run DP with
    larger ε when count > N) — more direct and legible than an area curve. Deferred until
    data demands it.
- **Unify inside-tests on the existing spatial index** — route guidance, U-turn, and
  headland-detection through `BoundaryPolygon`'s indexed test; delete the two raw-O(n)
  implementations. (Better than AgOpen's decimated-ring approach.)
- **Derived geometry consumes normalized input and decimates its own output.** Tram and
  headland keep Clipper2 but feed it normalized (sparse) input; tram lines are DP-simplified
  before storage.
- **Render LOD** for boundary/tram, mirroring the just-landed grid LOD. Largely mitigated
  once geometry is normalized; the proper finish.

## Phasing

### Phase 1 — Normalize keystone + immediate tram relief *(this PR)*
- `BoundaryResolution.Normalize` in `AgOpenWeb.Models` (DP ε≈0.1 m + max-gap densify
  for closed rings, recompute per-point heading). Unit tests.
- Apply at boundary finalize (recording stop), build-from-tracks finalize, and import
  (AgOpen + GeoJSON). Existing dense boundaries normalized + resaved on field open
  (one-way migration, per file-format philosophy).
- Decimate tram lines (DP) at generation output **and** in `TramLineService.LoadFromFile`,
  so the existing 14 MB `Res Test` file loads fast without regeneration.
- **Tram is now compute-on-demand**, not loaded eagerly on field open. Tram lines are
  rarely used and parsing a large saved `TramLines.txt` cost ~12 s on the open critical
  path. The tram buttons (`BuildTramLines` / `ToggleTramDisplay`) already regenerate via
  `UpdateTramLines` from the current track/systems, so the on-disk file is just a
  persistence cache; field open starts with tram cleared.

### Phase 2 — Tram generation model (ties to #422)
- **Done:** the legacy `GenerateParallelTramLines` per-point lateral offset (which folded a
  curved reference into a self-intersecting "web") is replaced for boundary-referenced
  fields by `GenerateConcentricTramLanes` — clean concentric **Clipper** offsets of the
  boundary at the tram (CTF/sprayer) width, wheel-track pairs, marching inward. Concave-safe,
  no folding.
- **Remaining:** straight-AB controlled-traffic lanes (parallel to a straight guidance line
  rather than concentric to the boundary) for fields driven in straight passes; and the
  curved-guidance offset math behind #422's "gets lost on direction" behavior.

### Phase 3 — Inside-test unification
- Single spatial-indexed point-in-boundary used by guidance, U-turn, headland detection;
  remove `GeometryMath.IsPointInPolygon`/`RaycastToPolygon` raw paths and
  `BoundaryPolygon.IsPointInside`'s raw O(n) fallback where the index suffices.

### Phase 4 — Render LOD
- Zoom-based decimation for boundary + tram polylines in `SkiaMapControl`; build one
  `SKPath` per tram line (path-level near-plane clip) instead of per-segment `DrawLine`.

## Key files

- Model: `Shared/AgOpenWeb.Models/Boundary.cs`, `BoundaryPolygon.cs` (spatial index),
  `Base/GeometryMath.cs`.
- New: `Shared/AgOpenWeb.Models/Base/BoundaryResolution.cs` (normalizer).
- Capture/build/import: `Services/BoundaryRecordingService.cs`,
  `Services/BoundaryBuilderService.cs`, `Services/BoundaryFileService.cs`,
  `Services/GeoJson/GeoJsonFieldService.cs`.
- Tram: `Services/Tram/TramLineService.cs`, `ViewModels/MainViewModel.cs:UpdateTramLines`.
- Offset helpers (DP/resample, currently orphaned): `Services/Geometry/PolygonOffsetService.cs`.
- Render: `Views/Controls/SkiaMapControl.cs`.
