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

## Phase-3 status (2026-05-18) — paused, unresolved visual issue

Phase 3 (pitch + zoom wiring) is committed locally on the spike branch as
`df4c7de` but **not pushed**. Camera math was rewritten to use AgOpenGPS's
exact matrix-mode transform sequence (research notes below), with
heading-up as the default mode matching AOG's "3D" button:

```
v * T(-vehicle) * R_z(+heading)? * T(pan) * R_x(aogPitch) * T(0,0,-dist)
```

The MainViewModel pitch convention (-90 overhead, -10 horizon) is converted
to AOG's (0 overhead, -65 default 3D tilt) at use time. Vehicle marker
counter-rotates by `R_z(-heading)` so it stays upright.

**Unresolved issue:** the user reports small vehicle motion (~4.4 ft, lat
delta ~2.4 m) producing dramatic visual change — far end of the field
disappearing, geometry appearing to "rotate around the X axis." The
diagnostic instrumentation logs viewport, pitch, zoom, distance, eye,
target, and the projected vehicle screen-pixel position every render, and
all of those stay constant across motion. The screenshots show effects
the math doesn't predict. We exhausted the "iterate on camera variants"
approach without resolving it.

**Suspect: Apple's deprecated OpenGL → Metal translation layer.** macOS GL
was deprecated in 10.14 (2018) and iOS GLES in iOS 12 (same year). The
spike already hit three Apple-GL-specific quirks (matrix transpose
direction, depth-buffer quantization on line overlays, `RenderingMode`
default flip to Metal). A fourth quirk causing motion artifacts when
working with large-magnitude world coordinates is plausible — and would
not appear on Android (native GLES) or Windows (ANGLE → D3D), neither of
which is on Apple's deprecated path.

### Next step when resuming

**Android reproduction test (2026-05-18): same behavior as Mac.** The
dramatic motion-vs-screen-change mismatch reproduces identically on the
Samsung Tab S7 FE (native GLES, not the Apple deprecated path).
**Conclusion: bug is in our cross-platform code, not Apple's GL
translation.** A Metal port would carry the bug with it.

When resuming, debug on the **Windows laptop with RenderDoc**. The bug is
in our cross-platform code, so it reproduces on Windows ANGLE-EGL, and
RenderDoc captures native GL calls (your `UniformMatrix4` uploads,
your VBO contents, your GLSL shaders) directly — no Metal-translation
layer to mentally undo. The Mac would work too via Xcode GPU Frame
Capture, but Frame Capture sees the Metal translation rather than the
GL calls our C# code actually made.

### Windows laptop setup checklist

1. **.NET 10 SDK** — the project targets `net10.0`. If the laptop is on
   .NET 8 or earlier, update first. Download:
   <https://dotnet.microsoft.com/download> (pick the preview/RC channel
   if .NET 10 hasn't reached GA yet on the laptop's installer page).
2. **RenderDoc** — free, <https://renderdoc.org>. Default install.
3. **Git** (probably already there) and the repo checked out at the
   spike branch:
   ```
   git clone https://github.com/AgOpenGPS-Official/AgValoniaGPS.git
   cd AgValoniaGPS
   git checkout spike/angle-silk-opengl-eval
   ```
4. **Verify build:**
   ```
   dotnet build Platforms/AgValoniaGPS.Desktop/AgValoniaGPS.Desktop.csproj
   ```

### Repro + capture procedure

1. In RenderDoc, **Launch Application** → point at
   `Platforms\AgValoniaGPS.Desktop\bin\Debug\net10.0\AgValoniaGPS.Desktop.exe`
   (or use `dotnet run` and let RenderDoc auto-attach).
2. Wait for the app to be running with the field open and 3D mode active.
3. RenderDoc's overlay shows in the corner. **Press F12** to capture a
   frame at the "start" (before motion). RenderDoc saves a `.rdc`.
4. Start the simulator at 0.3 mph. After ~10 seconds, **press F12**
   again for an "after-motion" frame capture.
5. Open both captures side-by-side. Compare the `u_mvp` uniform values
   for the boundary draw call in each capture. If they differ by more
   than the expected `T(-vehicle)` shift, we've found the bug. Also
   compare the boundary VBO vertex data — if it changed between
   captures, something is rebuilding the geometry mid-motion.

The text-log approach (full MVP bytes per frame + VBO vertex counts) is
the backup if RenderDoc setup hits a snag. Either way the next attempt
should produce ground-truth data, not screenshot interpretations.

### Apple-GL escape options (NOT NEEDED — kept for reference)

The bug reproduces on Android, so Apple-GL deprecation is not the
cause. The options below remain documented in case Apple GL becomes a
blocker for a different reason later.

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
