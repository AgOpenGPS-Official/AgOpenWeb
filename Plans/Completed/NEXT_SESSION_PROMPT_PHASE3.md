# Next session — start Phase 3 of the GL map pivot

Branch: `spike/angle-silk-opengl-eval` (push state at `80e13c6` as of session end).
Plan: [Plans/GL_MAP_PIVOT_PLAN.md](GL_MAP_PIVOT_PLAN.md)

## Where we are

Phases 1 and 2 are COMPLETE. SkiaMapControl is at functional parity with
DrawingContextMapControl in 2D mode on iPad:

| Phase | Lands | Status |
|---|---|---|
| 1 | Skeleton control, top-down only: bg fill, grid, ground texture, boundary, headland, vehicle | ✅ `9d0553d` |
| 2a | Tracks (AB/curve + markers/labels), tool/sections, MapClicked + ScreenToWorld | ✅ `f11ce56` |
| 2b | Coverage bitmap subsystem (Rgb565 + Bgra8888 + SKBitmap + SKImage snapshot), imagery composite, camera follow with snap-on-jump | ✅ `80e13c6` |

The `.use_skia_map` DiagFlag selects DCMC vs SkiaMap as the 2D control
at app startup. The flag file lives at
`/private/var/mobile/Containers/Data/Application/<UUID>/Documents/AgOpenWeb/.use_skia_map`
on the iPad — push it with `xcrun devicectl device copy to ... --domain-type appDataContainer --domain-identifier com.agopenweb.ios --destination Documents/AgOpenWeb/.use_skia_map`.

iPad FPS at each phase (idle scene, no driving):

- Phase 1 idle: ~113 FPS (vs DCMC ~110 — slight Skia win)
- Phase 2a idle / AB-line on: ~100 / ~90
- Phase 2b S6 (driving, coverage painting): ~55–58

24 FPS floor [[fps-floor]] cleared throughout.

## Phase 3 starting point

From the plan:

> ### Phase 3 — Real perspective via SKMatrix44
>
> Wire the AOG camera model into `SKCanvas.Concat44(perspectiveMatrix)`.
> `CameraPitch` slider produces real foreshortening. Top-down (pitch = 90°)
> falls out as a special case.
>
> **Exit criteria:** tilting the camera produces visually-correct
> foreshortening (near features widen, far narrow). No east-west stretching
> at any pitch < 90°. FPS at tilted view ≥ 95% of top-down FPS —
> perspective is essentially free in Skia.

Reference material:

- The Phase 0 perspective spike is at
  `Shared/AgOpenWeb.Views/Controls/Spikes/PerspectiveSkiaSpike.cs`.
  F9 toggles it on Desktop. It proves the math works; just need to plumb
  it into `SkiaMapVisualHandler.GetCameraTransform` and the
  `SendStateToHandler` pipeline.
- Memory: [[skiasharp-skmatrix44]] — row-vector, row-major. Use the
  `.Matrix` property to collapse the 4×4 to a 3×3 SKMatrix and pass that
  to `SKCanvas.SetMatrix`. Do NOT use `canvas.Concat(SKMatrix44)`.
- Memory: [[reference_glsl_matrix_transpose]] is GL-specific; doesn't
  apply here (we're staying in Skia for Phase 3).
- The AOG camera math (LookAt × PerspectiveFOV + pitch/zoom/pan/rotation
  wiring on `MainViewModel`) is already in place — we just feed it into
  `SKMatrix44` instead of a GL uniform.

Phase 3 will need to:

1. Remove the Phase 1 hardcode `CameraPitch = 0.0, Is3DMode = false` in
   `SkiaMapControl.SendStateToHandler` and pass the real values through.
2. Replace `GetCameraTransform` in `SkiaMapVisualHandler` with a
   perspective version that builds an `SKMatrix44` from the AOG camera
   model and feeds it to `SKCanvas.SetMatrix` via `.Matrix`. Top-down
   stays a special case (pitch = 0).
3. Wire `SetPitch` / `SetPitchAbsolute` / `Set3DMode` (currently no-ops
   in SkiaMap) to actually update the camera state and re-render.
4. The 2D/3D toggle (F3) currently swaps SkiaMap ↔ GlMapControl via
   `ApplyMapModeChildren`. After Phase 3, the 3D path stays in SkiaMap
   too — the toggle becomes a pitch animation in-place. Phase 4 deletes
   GlMapControl, but Phase 3 can leave GlMapControl in the host grid as
   a parked fallback (mode-toggle keeps swapping). Decide which.

## Known open items

- **Issue [#416](https://github.com/AgOpenGPS-Official/AgOpenWeb/issues/416) — Active curve track renders as short V** instead of tracing the full base curve. Reproduces on DCMC too — latent VM bug, not introduced by Phase 2. Filed and parked.
- **Phase 2 exit criterion: "FPS within 10% of DCMC"** — we measured
  SkiaMap S6 idle at ~55–58 FPS on iPad but never grabbed the DCMC S6
  number for direct comparison. Worth taking that reading early in the
  Phase 3 session so we have a baseline before adding perspective cost.
- **Android still on Phase 1 marker** — we paused Android testing
  mid-session per user's "iPad-first" preference. Push the latest APK
  to Android and verify Phase 2 parity holds there too. Per
  [[test-devices]] the Android tablet is the 24 FPS floor device, so
  it's the critical perf check.
- **Curve track corner smoothness** — SkiaMap uses `StrokeJoin.Round`
  for the active polyline, DCMC uses default `Miter`. Visual win for
  SkiaMap, kept on purpose. Mentioned in case a future-you wonders why
  they diverge.

## Don't re-do

- The bonus 2D-toggle fix (Children.Add/Remove) is in `de6598f`. Don't
  go back to `IsVisible` binding for the host-grid swap.
- Coverage code is duplicated DCMC ↔ SkiaMap. **Don't try to refactor
  into a shared `MapCoverageBitmap` class during Phase 3** — the user
  explicitly chose "port now, refactor in Phase 4 when DCMC dies."
  Memory: [[no-later-deferred]] applies to the refactor (don't ship it
  half-done) but the user has already committed to the porting path.

## Constraints

- Don't touch `GlMapControl.cs` — parked baseline, gets deleted in Phase 4.
- Don't introduce 3D scope creep ([[no-3d-rendering]]). Perspective tilt
  is in scope; terrain meshes etc. are not.
- Don't patch Avalonia. The pivot exists to avoid that. [[no-workaround-paths]].
- Launches from background tasks have been killing the user's testing
  session. **Do not launch the app via mlaunch/devicectl.** Only install,
  then the user taps to open.
- Per [[ios-physical-device]] always build with `-r ios-arm64`, never the
  simulator.
- iPad-only testing this session per user preference; only verify
  Android once Phase 3 feature work is solid.

## What to bring into the new session

The session can be opened fresh — no need to recap Phases 1–2. Load via
Read at session start:

1. `Plans/GL_MAP_PIVOT_PLAN.md` — the plan, see Phase 3 section.
2. `Shared/AgOpenWeb.Views/Controls/SkiaMapControl.cs` — `SendStateToHandler` (lines ~240–340) is where pitch/3D-mode flows in, and `GetCameraTransform` in the handler is what gets replaced.
3. `Shared/AgOpenWeb.Views/Controls/Spikes/PerspectiveSkiaSpike.cs` — Phase 0 reference impl.
4. Memory: [[gl-compositionvisual-pivot]], [[skiasharp-skmatrix44]],
   [[visibility-toggle-rule]], [[no-later-deferred]] — auto-load.
