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

The largest design decision in the plan. Today coverage is a ~50 MB CPU
bitmap with triangle strips drawn into it, then displayed as a
`DrawingImage`. Two options for the GL path:

- **(a) Bitmap → GL texture, incremental upload via `glTexSubImage2D`.**
  Lower lift, keeps the existing `ICoverageMapService` shape. But stays
  CPU-side-bound, which is the existing bottleneck — doesn't really earn
  the GL switch.
- **(b) Render coverage as GL geometry directly.** Chunked VBOs of triangle
  strips, append per GPS tick, draw what's visible. Skips the bitmap
  entirely. Bigger phase, but this is where iPad / Android FPS could
  actually improve.

**Recommended:** (b). Committing up front, since (a) doesn't move the
bottleneck. Either way, iPad Pro 2nd gen (Eagl GLES, our perf floor)
decides whether the choice was right.

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

1. **Coverage representation (Phase 4 option a vs b).** Affects buffer
   management and shapes the entire 4-phase build. Default position is (b);
   confirm or override before Phase 4 starts.
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
