# GL Map Rendering Plan

Plan to replace `DrawingContextMapControl`'s 3D mode with a true perspective
camera via Avalonia `OpenGlControlBase` + Silk.NET.OpenGLES. The 2D path
remains the working baseline; this work is about giving 3D mode a real
perspective camera so tilting tightens distant features and widens near ones,
instead of the SkiaSharp matrix hack stretching content east-west.

## Status

Spike branch `spike/angle-silk-opengl-eval` validates a single cross-platform
C# GL code path across all five supported platforms.

| Platform | Backend | Status | Shim required |
|---|---|---|---|
| macOS (Apple Silicon) | Avalonia.Native + OpenGL | Validated | `AvaloniaNativePlatformOptions.RenderingMode = [OpenGl, Software]` |
| iOS (iPad Pro 2nd gen) | Eagl (GLES) | Validated | `iOSPlatformOptions.RenderingMode = [OpenGl, Metal]` |
| Windows (x64 laptop) | Win32 ANGLE-EGL | Validated | None — Av12 default |
| Linux (Parallels arm64 VM) | X11 EGL/GLX | Validated | None — Av12 default |
| Android (Adreno 642L tablet) | Native GLES | Validated | None — Av12 default |

Spike control (`Shared/AgValoniaGPS.Views/Controls/GlSpikeControl.cs`) renders
a green ground, yellow boundary rectangle, and red vehicle cross identically
on all five platforms. The reusable findings the spike produced:

- Shader version selected from `GlVersion.Type` (`#version 330 core` for
  desktop GL, `#version 300 es` for GLES).
- `UniformMatrix4(... transpose: true ...)` for `System.Numerics.Matrix4x4`
  row-major bytes is the spec-compliant path and works on both backends.
- Line overlays render with depth test disabled. Apple's Metal-backed GL
  quantizes depth aggressively at far distances; with the camera ~80 m up
  and `near=1, far=1000`, a 0.5 m z-gap between boundary and ground wasn't
  enough to win the depth test on some pixels.
- `Avalonia 12` defaults to Metal on both Apple platforms. Without the
  explicit `RenderingMode` shim, `OpenGlControlBase` silently fails — black
  screen, no `OnOpenGlInit` callback. Captured in
  `memory/reference_avalonia_gl_rendering_mode.md`.

## Architecture

Introduce `GlMapControl : OpenGlControlBase` as a sibling of the existing
`DrawingContextMapControl`. Both implement `IMapControl`. The
`Toggle2D3DCommand` swaps which one is visible. Each control subscribes to
the same data sources (boundary service, track service, coverage service,
GPS service) and the same `MainViewModel` properties — no service-layer or
ViewModel changes needed.

Per-platform `MapService` registers the GL control via the existing DI path.
The macOS + iOS rendering-mode shims live in `Platforms/AgValoniaGPS.Desktop/Program.cs`
and `Platforms/AgValoniaGPS.iOS/AppDelegate.cs` respectively.

The 2D path stays on `DrawingContextMapControl` indefinitely. Retiring it is
deferred to Phase 6 and is conditional on the GL path beating it at top-down
on the Android tablet (our perf floor).

## Phases

### Phase 1 — Scaffold the GL map control

Promote `GlSpikeControl` → `GlMapControl`. Give it `IMapControl`. Register
in DI on all three platforms (Desktop / iOS / Android — DI changes touch all
three per the standing rule). Wire `Toggle2D3DCommand` to swap visibility
between the DrawingContext and GL controls. Spike scene is the placeholder.
**Exit criteria:** F8 toggles between 2D map and the GL placeholder scene on
all five platforms; no data binding work yet.

### Phase 2 — Static map elements from real state

Render outer boundary, inner boundaries, tracks (AB + curves), headland, and
vehicle + tool from real `MainViewModel` state. All as `GL_LINES` plus one
textured quad for the vehicle. **Exit criteria:** opening a field shows the
boundary and tracks correctly on all five platforms; vehicle moves with GPS
updates. No tile imagery yet, no coverage yet.

### Phase 3 — Camera + tilt control

Wire `CameraPitch`, `CameraZoom`, pan offset, and rotation into a real
perspective camera (`LookAt` × `PerspectiveFOV`). Pitch=90° matches the
existing 2D top-down look; tilted gives true perspective. **Exit criteria:**
the tilt button visibly produces foreground-widening / background-narrowing
perspective. This is the user-visible feature deliverable — everything after
is fidelity.

### Phase 4 — Coverage map

**Approach: texture-based.** Coverage is already cell-based — see
`memory/project_coverage_architecture.md`. `CoverageMapService` rasterizes
the quad formed by previous + current section edges into a 1-bit-per-cell
detection array at 0.1 m resolution; per-cell color was a separate RGB565
bitmap living in `DrawingContextMapControl`. `CoveragePatch` /
`GetPatches()` are vestigial (return empty) — patches were removed for
mobile perf and must not come back.

Concrete plan:

1. Move the RGB565 pixel buffer + bitmap-dimension state from
   `DrawingContextMapControl` into `CoverageMapService` itself. The service
   becomes self-contained: detection bits AND display pixels. The pixel-
   access callback indirection (`SetPixelAccessCallbacks`,
   `GetPixelBufferCallback`, `SetPixelBufferCallback`,
   `GetDisplayBitmapInfoCallback`) is deleted from `ICoverageMapService`.
2. The service paints into its own buffer when cells are marked covered
   (today the 2D control polls and paints; for GL-only on this branch the
   service is the one source of truth).
3. The service exposes a dirty-rect since last query so the renderer can do
   incremental uploads. Full-buffer access stays available for save/load
   and for first-frame texture init.
4. `GlMapControl` keeps one GL2D texture sized to the field bounds.
   `glTexSubImage2D` uploads only the dirty rect each `CoverageUpdated`
   event. One field-aligned quad draws coverage; fragment shader samples
   the texture (texel == 0 → discard for transparency, else paint with the
   texel color decoded from RGB565).
5. On `BoundsExpanded`, allocate a new texture at the new size and re-
   upload the full buffer. Same lifecycle the 2D control already handles.

Earlier draft of this section recommended option (b) "render coverage as
GL geometry directly, chunked VBOs of triangle strips" — that was stale
guidance, written when patches still existed. See
`memory/project_coverage_architecture.md`.

This branch is GL-only (no toggle) — `DrawingContextMapControl` does not
need to be maintained alongside. The 2D bitmap-painting code path inside
that control can be removed once GL renders coverage correctly. iPad Pro
2nd gen (Eagl GLES, our perf floor) decides whether the texture upload
cadence works at field-coverage scale.

### Phase 5 — Tile imagery

Existing BruTile tile cache → GL textures, drawn as textured quads at world
positions. LRU eviction. Frustum-cull invisible tiles. WGS84 → local NED
conversion for tile corners reuses `GeoConversion`. **Exit criteria:**
online + offline tile providers render correctly in both pitch=90° and
tilted views.

### Phase 6 — Optional retirement of `DrawingContextMapControl`

Only if GL beats DrawingContext at top-down on the Android tablet. Otherwise
keep both and ship the toggle indefinitely. Lower priority.

## Decisions to confirm before Phase 1

1. **Coverage representation.** Resolved: texture-based with service-owned
   RGB565 buffer. See Phase 4 section above and
   `memory/project_coverage_architecture.md`.
2. **In-scene text.** HUD elements (FPS counter, speed, heading) stay as
   regular Avalonia controls layered on top of the GL surface. Labels
   *inside* the world (waypoint annotations, etc.) — currently none, so
   default is "skip text-in-scene." Revisit if a feature ever needs it.
3. **Frame timing.** Drive `RequestNextFrameRendering` from state changes
   (GPS tick, pan, zoom) rather than a fixed-rate timer. Saves battery on
   mobile and matches the existing dirty-render philosophy.

## Risks

- **Adreno 642L (Android tablet) perf at full field coverage** is the
  primary unknown. Phase 2 establishes the floor; Phase 4(b) is where the
  geometry pipeline gets stressed.
- **Avalonia `OpenGlControlBase` composition overhead** is unmeasured. If
  it adds per-frame cost the DrawingContext path doesn't have, the
  top-down GL case could be slower than 2D on a CPU-bound device — which
  is why Phase 6 is conditional, not assumed.
- **DI three-platform discipline.** Each phase that introduces a GL service
  must update Desktop / iOS / Android `ServiceCollectionExtensions.cs` in
  the same commit. Tracked in `memory/feedback_di_three_platforms.md`.

## What this plan is not

- It is not a renderer-swap for FPS gains. The current bottleneck is
  CPU-bound (per `memory/reference_avalonia12.md`). The 2.5D switch is a
  view-quality fix, not a perf fix. Any perf improvement is incidental.
- It is not a return to "3D rendering" in the terrain-mesh sense. Source
  imagery is still 2D orthorectified raster; no DEMs, no hills. The tilt
  is a camera change applied to flat agronomic data. The original
  reasoning in `memory/project_no_3d_rendering.md` still applies to
  terrain meshes — only the camera-tilt position has reversed.

## Phase-3 status (2026-05-19) — RESOLVED

Phase 3 (pitch + zoom wiring) shipped with a real perspective camera that
tilts correctly, follows the vehicle, and produces visible foreshortening.
The "world rotating about X axis" / "tractor never moves away from
boundary" / "track diverging to perpendicular" visual artifacts that
blocked Phase 3 for a week were all symptoms of **one** root-cause bug
described below.

### Root cause: matrix transpose convention mismatch

`gl.UniformMatrix4(..., transpose: true, ...)` on the line shader's MVP
upload was producing orthographic-like rendering. The math:

- `System.Numerics.Matrix4x4` is **row-major** with **row-vector**
  multiplication (`v * M`). Translation lives in `M41, M42, M43`.
- GLSL ES uses **column-vector** multiplication (`M * v_col`). Translation
  lives in `M14, M24, M34`.
- `glUniformMatrix4fv(transpose=GL_TRUE)` tells GL "input is row-major,
  please store as column-major." The *logical* matrix in GLSL after this
  is identical to our input — same `M_(i,j)` at row i, col j.
- GLSL's `M * v_col` uses **rows** of the stored matrix. For our matrix,
  ROW 4 is the translation row `(0, 0, 0, 40.5)` — pure W, no Y/Z
  contribution to `clip.w`.
- So `clip.w` came out constant regardless of vertex world position. No
  perspective division → no foreshortening → everything renders as if
  through an orthographic-leaning projection. Grid lines stayed parallel
  to the horizon, near features didn't shift more than far features
  during motion, the world appeared to slide bizarrely instead of
  scrolling under the camera.

The fix is `transpose=false`. With `transpose=false` GL interprets our
row-major bytes as column-major and the logical matrix in GLSL becomes
our matrix **transposed**. The shader's `M_transposed * v_col` then equals
our `v_row * M` — i.e., true perspective.

This is counterintuitive — tutorials usually say to use `transpose=true`
for row-major libraries. That advice assumes the library's *math
convention* matches GLSL's column-vector convention. System.Numerics
doesn't (it uses row-vector math), so the transpose flag has to flip.

See `reference_glsl_matrix_transpose` memory note for the long version.

**Single-line fix:** `Shared/AgValoniaGPS.Views/Controls/GlMapControl.cs`,
`SetMvp(...)` and the ground shader's `u_inv_mvp` upload both use
`transpose=false`.

### Misdiagnosis chain (so future debugging skips this)

In the ~12 hours of debugging before finding the right answer, the
following theories were explored and discarded — all wrong:

1. **Apple OpenGL → Metal translation quirk.** Disproven: the same
   artifact reproduces on Android (native GLES) and Windows (ANGLE → D3D).
   Apple's deprecated GL path is not at fault.
2. **ANGLE's D3D11 hooks corrupting matrix uploads.** Disproven via
   `glGetUniformfv` readback after `glUniformMatrix4fv` — the GPU
   received exactly the bytes we sent, bit-for-bit.
3. **Per-frame VBO churn causing visual instability.** Real but minor.
   Active track was being rebuilt every cycle because of an
   unconditional `_mapService.SetNextTrack(yt.NextTrack)` in
   `MainViewModel.ApplyResults.cs`. Fixed with a `ReferenceEquals` gate
   and defense-in-depth `ReferenceEquals` checks inside the GL control's
   `Set*` methods. Did not change the visual artifact at all.
4. **Float32 precision loss at large world coordinates.** Not the cause.
   World vertices have plenty of precision at field-scale magnitudes.
5. **Auto-zoom putting the camera blind-spot underneath itself.** Real
   contributor to one specific subjective complaint at extreme zoom-out
   (camera 290m up + finite ground quad), but not the underlying bug.
6. **Finite ground quad's negative-w clipping.** Real but tangential —
   prompted the move to a fragment-shader ray-cast ground, which then
   surfaced the actual convention bug because the ground shader's
   inverse-MVP upload had the same `transpose=true` mistake.

The thing that actually broke through: a fragment shader that ray-casts
to Z=0 (sky/brown horizon test). It only showed correct ground orientation
when both the line shader AND the ground shader switched to
`transpose=false`. That forced an explanation of *why* `transpose=true`
was being honored differently than expected — which is when the
column-vector vs row-vector convention came into focus.

### Other fixes landed during this session

- **Pitch propagation on app startup.** `ConfigurationService` loads the
  saved `CameraPitch` directly into `DisplayConfig.CameraPitch` (backing
  field), bypassing the `MainViewModel.CameraPitch` setter that normally
  calls `_mapService.SetCameraPitchDegrees(value)`. Result: the GL
  renderer was stuck at its hardcoded default forever and the View
  Settings tilt slider only affected the 2D DrawingContext path. Fixed
  in `MainViewModel.RestoreSettings()` with an explicit
  `_mapService.SetCameraPitchDegrees(_displaySettings.CameraPitch)` push.
- **Window resize unlocked for dev sessions.** `MainWindow.axaml.cs`
  DEBUG block now starts maximized and allows resize, instead of pinning
  to 1200×720. The hard lock is commented out and can be restored if
  small-viewport simulation work is needed.
- **AOG-matched defaults.** FOV is now 0.7 rad (40°) to match AOG.
  Distance formula is `0.5 * zoomScalar²` where `zoomScalar = 9 / zoom`,
  so the default `zoom=1` gives `distance=40.5m` — same as AOG's default
  3D camera at `ZoomValue=9`.
- **World grid + horizon background.** GL control draws a 10m × 10m
  world-aligned grid for motion reference, and a fragment-shader infinite
  ground/sky plane so the horizon is always visible at moderate-to-high
  tilt. Both stay underneath the boundary/track line overlays.

### Still pending (for the next session)

1. **Upstream Track allocation churn.** Even with the per-frame VBO
   rebuild fix, a new `Track` instance is allocated per GPS cycle
   somewhere in the guidance pipeline (~28/sec). The `Points` content
   is bit-identical between cycles so the visual is fine, but the
   defense-in-depth `ReferenceEquals` gate inside `GlMapControl` is
   being defeated each tick. Worth chasing for perf — but it does not
   affect rendering correctness. Source is somewhere in
   `MainViewModel.ApplyResults` or the guidance service that produces
   `DisplayTrack`.
2. **Phase 4 (coverage).** With perspective working, coverage strips
   under the vehicle are the next visible deliverable and the biggest
   visual cue for "the world is moving" once you're in a field.
3. **Phase 5 (tile imagery).** Aerial tiles as textured GL quads —
   completes the AOG-like view.
4. **Diagnostic logging cleanup.** `GlMapControl` currently logs
   `[GlMap-FRAME]`, `[GlMap-MVP]`, `[GlMap-CNTS]`, `[GlMap-REB-*]`,
   `[GlMap-SET-TRK]`, `[GlMap-SOUTH]`, `[GlMap-UPLOAD]`,
   `[GlMap-GROUND]` per render. Most of this was for this session's
   investigation; keep what's cheap and useful (`REB-*`, `CNTS` at 1Hz),
   strip the per-frame spam (`FRAME`, `MVP`, `SOUTH`, `UPLOAD`,
   `GROUND`) before merging.

### Apple-GL escape options (NOT NEEDED — kept for reference)

The bug was a convention mismatch, not anything Apple-specific. The
options below remain documented in case Apple GL becomes a blocker for
a different reason later.

Three rough levels of effort to leave Apple's deprecated GL behind:

1. **Split renderer — Metal on Apple, GL elsewhere.** Write a
   `MetalMapControl` for Mac+iOS using `MTKView` / `MTLDevice` / MSL
   shaders. Avalonia.iOS exposes `IMetalPlatformSurface`; Avalonia.Native
   on Mac has Metal support too. Keep `GlMapControl` for Windows / Linux
   / Android (non-deprecated GL paths). **Maintenance cost:** every scene
   change has to land in both renderers forever. ~1–2 weeks initial.
2. **Veldrid (cross-platform .NET graphics abstraction).** Single C#
   rendering code; Veldrid selects Metal on Apple, Vulkan / D3D / GL on
   others. Different paradigm (explicit pipeline state objects, command
   buffers) vs raw GL — most of the camera math and scene layout carries
   over conceptually but the per-draw-call API changes shape. Avalonia
   integration (getting a Veldrid-compatible render target out of an
   Avalonia control) is the unknown. **Maintenance cost:** one codebase.
   ~1–2 weeks initial. The healthiest long-term answer if Apple GL
   actually blocks us.
3. **MoltenVK + Vulkan everywhere.** Replace Apple's deprecated GL→Metal
   translator with MoltenVK's Vulkan→Metal translator. Not recommended —
   swaps one translation layer for another, and MoltenVK is community-
   maintained, not Apple-blessed. Multi-week rewrite for marginal gain.

### Research notes — AgOpenGPS camera model (2026-05-18)

Pulled from `/Users/chris/Code/AgOpenGPS` upstream. The relevant files are
`SourceCode/AgOpenGPS.Core/Models/Camera.cs:41-54` (SetLookAt) and
`SourceCode/GPS/Classes/CVehicle.cs:143` (vehicle counter-rotation). Key
findings:

- AOG's matrix-mode transform sequence is what our spike now mirrors.
- Heading-up (`FollowDirectionHint = true`) is the AOG default; the world
  rotates by `RotateZ(+headingDegrees)`, not the vehicle marker. The
  vehicle marker is counter-rotated by `Rotate(-fixHeading)` to stay
  upright after the world rotates.
- Three modes via three buttons: N2D (north-up, pitch=0), 2D (heading-up,
  pitch=0), 3D (heading-up, pitch=-65). Phase 3 should expose these as a
  toggle bound to the existing 2D/3D button + a separate North/Heading
  toggle.
- AOG's `DistanceToLookAt = 0.5 * ZoomValue²`, zoom default 9 → distance
  40.5 m. Our spike currently uses `distance = 80 / zoom`, default zoom 1
  → distance 80 m. Roughly similar at default settings, different scaling
  laws under zoom commands.
- Vehicle is always at exact screen center (no off-center "chase-cam"
  positioning). Manual pan is via separate `PanX` / `PanY` offsets.

## References

- Spike branch: `spike/angle-silk-opengl-eval`
- Spike control: `Shared/AgValoniaGPS.Views/Controls/GlSpikeControl.cs`
- Rendering-mode shims:
  - `Platforms/AgValoniaGPS.Desktop/Program.cs`
  - `Platforms/AgValoniaGPS.iOS/AppDelegate.cs`
- CI validation run: GitHub Actions run 26048318624 (all five desktop
  artifacts plus Android + iOS).
- Related memory:
  - `reference_avalonia_gl_rendering_mode` — RenderingMode shims
  - `project_no_3d_rendering` — what's in/out of scope
  - `feedback_di_three_platforms` — DI discipline
  - `reference_test_devices` — perf-floor hardware
