# GL Map — CompositionCustomVisualHandler Pivot Plan

Supersedes the `OpenGlControlBase` approach in
[GL_MAP_RENDERING_PLAN.md](GL_MAP_RENDERING_PLAN.md). The spike on
`spike/angle-silk-opengl-eval` validated the cross-platform Silk.NET GL code
path, but uncovered an iOS-side performance ceiling we can't engineer around
inside `OpenGlControlBase`. This plan moves the renderer to the same Avalonia
scheduling bucket that the existing 2D control uses, where iPad is uncapped.

## Why the pivot

The decisive data (Plans/perf_data/2026-05-21/):

| Path | iPad idle FPS | Cap source |
|---|---|---|
| 2D — `DrawingContextMapControl` (CompositionCustomVisualHandler + `RegisterForNextAnimationFrameUpdate`) | **117** | Uncapped |
| 3D — `GlMapControl` (`OpenGlControlBase` + `RequestCompositionUpdate`) | **32** | `MediaContext.CommitCompositorsWithThrottling` single-batch-in-flight throttle |
| DispatcherTimer @ 16 ms (Normal priority) | **61** | Proves UI dispatcher + CADisplayLink are healthy at 60 Hz |

Root cause confirmed in Avalonia 12.0.3 source — see
[AvaloniaUI/Avalonia#21409](https://github.com/AvaloniaUI/Avalonia/issues/21409).
The throttle skips commits whenever `_pendingCompositionBatches.Count > 0`,
and the UI-thread round-trip to clear that flag exceeds 16 ms on iPad
A10X / iOS 17.7. **Any** Avalonia visual that routes through
`Compositor.RequestCompositionUpdate(Action)` hits the same cap. That
explicitly includes:

- `OpenGlControlBase` (our current GL control's base).
- `CompositionDrawingSurface` from the GpuInterop sample (we read the source;
  it uses `RequestCompositionUpdate` too).

The only proven-fast path is `CompositionCustomVisualHandler` with
`RegisterForNextAnimationFrameUpdate`, which is in a separate, throttle-free
scheduling bucket.

Decision drivers (user-validated):

- **No fork.** Patching `MediaContext` pins us to a specific Avalonia point
  release and requires re-patching on every upgrade. Memory:
  [[gl-compositionvisual-pivot]].
- **No "wait for upstream."** The bug is filed but has no timeline.
- **24 FPS floor on the slowest device matters most.** Memory:
  [[fps-floor]], [[turns-are-critical]]. We're at 32 FPS idle on iPad with
  nothing rendered yet — adding imagery (~-30 FPS, per
  [[imagery-png-render-cost]]) and the tractor sprite would land us below the
  floor with no recovery path.

## What we keep from the GL spike

- The Silk.NET.OpenGLES integration — `null`; we drop it.
- Shader code — `null`; we drop GLSL entirely.
- AOG camera model (LookAt × PerspectiveFOV, pitch + zoom + pan + rotation
  wiring in `MainViewModel`) — **keep**. The math is renderer-agnostic; we
  just feed it into `SKMatrix44` instead of a GL uniform.
- `[GlMap-PERF]` per-section timing instrumentation — **keep the shape**,
  rename to `[MapRender-PERF]` and re-anchor to the Skia draw phases.
- Coverage-as-texture upload path (Phase 4 of the GL plan) — **conceptually
  keep**, but implement via `SKBitmap` / `SKImage` (which Skia uploads to
  Metal-backed textures internally on iOS) instead of `glTexImage2D`.
- Per-frame VBO caching (cached `_lastMirroredNextTrack` etc. from the PERF-05
  work that landed in the rebase) — **keep**. Same value at the Skia layer.

## Open architectural questions (research-first phase)

These must be answered before Phase 1 — they each could invalidate the plan
or change its scope substantially.

1. **True perspective in Skia.** Does `SKMatrix44` (or `SKCanvas.Concat44`)
   on Avalonia 12.0.3's bundled SkiaSharp give us real foreshortening (far
   features narrow, near features widen) instead of the east-west stretching
   from the old `SKMatrix` 3×3 hack? The 2.5D-tilt spike memory
   [[reference_glsl_matrix_transpose]] is GL-specific; we need a Skia-side
   answer. **Test plan:** small standalone control, tilt a textured quad
   with `SKMatrix44.MakePerspective` × `MakeRotate`, compare side-by-side to
   what the GL spike rendered at the same pitch.

2. **Skia GPU texture upload cost on iPad.** Memory
   [[imagery-png-render-cost]] flagged ~30 FPS / 4-5 ms of overhead in 2D for
   on-imagery view. That measurement was via `DrawImage` of a pre-decoded
   `SKImage`. **Test plan:** baseline a fresh measurement on the spike branch
   *post-pivot* — does it still cost 30 FPS, or has the situation changed
   with the PERF-05 fixes? Same scenarios (S5, S6) from the existing
   protocol.

3. **Coverage bitmap as `SKImage` vs `SKBitmap.SetPixels` per frame.** The
   coverage map is a 50 MB RGB565 bitmap that updates as the vehicle drives.
   The GL path wrote it as a `GL_TEXTURE_2D` and re-uploaded on dirty.
   `SKImage` from raw pixel memory could be a wrap (zero-copy) or a copy
   (50 MB / frame is catastrophic). **Test plan:** prototype a 50 MB RGB565
   `SKImage.FromRawPixels` and measure upload cost per frame on iPad and
   Android tablet.

4. **`OnAnimationFrameUpdate` rate when the visual is hidden.** Today's S1-2D
   data showed the hidden GlMapControl still rendering at the same rate as
   the visible 2D control (both 40 FPS, contending). Need to confirm
   `RegisterForNextAnimationFrameUpdate` truly pauses when `IsVisible=false`,
   or if we need lifecycle add/remove of the visual from the parent.

If (1) fails — `SKMatrix44` doesn't give real perspective — the pivot is
*dead* and we revisit options 2-4 from the GL spike's closing summary
(accept 32 FPS, file-and-wait, or hardcore Avalonia fork). **Don't start
Phase 1 work until (1) is answered yes.**

## Architecture

```
MapVisualHandler : CompositionCustomVisualHandler        ← runs on render thread
├── Receives MapRenderState snapshots via OnMessage (same as today)
├── OnAnimationFrameUpdate() → Invalidate() → re-arm next frame
└── OnRender(ImmediateDrawingContext)
    ├── Skia lease (ISkiaSharpApiLeaseFeature)
    ├── SKCanvas.Concat44(perspectiveMatrix)    ← AOG camera as SKMatrix44
    ├── DrawCoverageTexture(SKImage from RGB565 backing buffer)
    ├── DrawGroundTexture(SKImage from tiled field imagery)
    ├── DrawBoundaries (cached SKPath)
    ├── DrawHeadland (cached SKPath)
    ├── DrawTracks (cached SKPath; AB lines, curves, recorded paths)
    ├── DrawVehicle (SKImage of tractor sprite, transformed)
    └── DrawTool (sections drawn from coverage detection bits)

MapControl : Control                              ← UI-thread Avalonia control
├── Holds the CompositionCustomVisual (parent of the handler)
├── Subscribes to MainViewModel + services (boundary, track, coverage, GPS)
├── On change → marshal snapshot to render thread via OnMessage
└── Toggle2D3DCommand → set pitch to 90° (top-down) or restore stored tilt
```

The 2D/3D distinction collapses into one control with a camera pitch knob.
"2D mode" becomes `pitch = 90°` (top-down orthographic-equivalent);
"3D mode" becomes anything < 90° with real perspective. This matches the
strategic direction in the project memory: top-down is the eventual sole
mode once 3D is proven.

`DrawingContextMapControl` stays alive only as the fallback until the new
control reaches parity. Then it gets deleted.

## Phases

### Phase 0 — Research (answer the open questions)

Three standalone-control spikes, each in its own file under
`Shared/AgOpenWeb.Views/Controls/Spikes/`, gitignored, removed when the
pivot lands:

- `PerspectiveSpike.cs` — `SKMatrix44` perspective test (question 1).
- `CoverageImageSpike.cs` — 50 MB RGB565 `SKImage` upload cost (question 3).
- `HiddenVisualSpike.cs` — does `RegisterForNextAnimationFrameUpdate` pause
  when invisible (question 4)?

**Exit criteria:** all three open questions have measured answers.
Question 2 is rolled into the post-Phase-1 baseline since we won't have
the new control in shape to measure imagery overlay until then.

### Phase 1 — Skeleton control, top-down only

`SkiaMapControl` + `SkiaMapVisualHandler`, derived from
`CompositionCustomVisualHandler` (mirror `MapCompositionHandler` shape from
`DrawingContextMapControl`). Implement `IMapControl`. Top-down only
(pitch = 90°, no perspective math yet). Renders: background fill, grid,
ground texture, boundary, headland, vehicle.

Side-by-side with `DrawingContextMapControl` and `GlMapControl` — same
side-by-side pattern as the spike branch today, toggled by a DiagFlag for
A/B comparison. Goal is to land the architecture, not yet to beat the 2D
path on parity.

**Exit criteria:** S1, S2 scenarios from the existing TESTING.md protocol
show the new control rendering correctly on iPad + Android, FPS in the
same ballpark as `DrawingContextMapControl` (within 10%). No regression
beyond the 24 FPS floor on Android tablet.

### Phase 2 — Add tracks, sections, coverage

Pull `DrawTrack`, `DrawSections`, `DrawCoverage` from
`DrawingContextMapControl` into the new handler. Coverage as `SKImage` from
the existing RGB565 backing buffer (per Phase 0 answer to question 3).

**Exit criteria:** S3 (AB track), S4 (curve), S5 (driving sections off),
S6 (driving sections on) all functionally equivalent to the 2D control.
FPS within 10% of 2D on iPad, within 10% on Android.

### Phase 3 — Real perspective via SKMatrix44

Wire the AOG camera model into `SKCanvas.Concat44(perspectiveMatrix)`.
`CameraPitch` slider produces real foreshortening. Top-down (pitch = 90°)
falls out as a special case.

**Exit criteria:** tilting the camera produces visually-correct
foreshortening (near features widen, far narrow). No east-west stretching
at any pitch < 90°. FPS at tilted view ≥ 95% of top-down FPS — perspective
is essentially free in Skia.

### Phase 4 — Delete `DrawingContextMapControl` and `GlMapControl`

Once the new control is at parity + has perspective working on iPad and
Android. Single map control in the app. The Toggle2D3DCommand becomes a
pitch toggle (90° ↔ stored tilt). Documentation updated.

**Exit criteria:** `git grep -i 'DrawingContextMapControl\|GlMapControl'`
returns nothing in `Shared/` or `Platforms/`. Single map control. The 24
FPS floor [[fps-floor]] is met on Android tablet for all of S1-S6.

## Risks and decision gates

- **Risk:** `SKMatrix44` perspective stretches like the old hack →
  pivot is dead, return to GL fork or accept 32 FPS. *Gate:* Phase 0
  question (1) must answer cleanly yes before any Phase 1 work.
- **Risk:** Coverage `SKImage` upload is per-frame copy of 50 MB → would
  crash performance everywhere. *Gate:* Phase 0 question (3) measures this;
  if it's a copy we need to find Skia's zero-copy primitive (`SKImage.FromPixels` with `SKImageCreationMode`) or revisit the coverage model.
- **Risk:** Performance gain doesn't materialize on iPad — `RegisterForNextAnimationFrameUpdate`'s 117 FPS was on a near-empty scene. With the full map workload it might still throttle in another way. *Gate:* Phase 1 exit criteria; if iPad isn't comfortably above the floor with the trivial scene, we know full parity won't get there either.

## Out of scope

- Full 3D terrain (memory [[no-3d-rendering]]).
- ISOBUS VT/TC (memory [[isobus-out-of-scope]]).
- Avalonia fork to disable the commit throttle.

## Scope changes vs. the original GL plan

| Original GL plan | This pivot |
|---|---|
| `OpenGlControlBase` + Silk.NET.OpenGLES | `CompositionCustomVisualHandler` + Skia GPU draws |
| GLSL shaders | SKShader / SKPath / SKImage |
| Per-platform `RenderingMode` shims for OpenGL | Avalonia 12 defaults; no shims |
| `glTexImage2D` coverage upload | `SKImage.FromPixels` |
| 2D path retired in Phase 6 conditional on top-down GL beating 2D on Android | 2D path retired in Phase 4 since "2D path" IS the new control with pitch=90° |
| Cross-platform OpenGL ES validation matrix | Cross-platform Skia validation — Avalonia handles backend (Metal on Apple, OpenGL elsewhere) |
