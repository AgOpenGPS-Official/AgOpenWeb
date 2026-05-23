# Next session — start Phase 1 of the GL map pivot

Branch: `spike/angle-silk-opengl-eval` (push state at `fa210fb` as of session end).
Plan: [Plans/GL_MAP_PIVOT_PLAN.md](GL_MAP_PIVOT_PLAN.md)

## Current state

Phase 0 of the pivot plan is COMPLETE. All three open questions
answered with on-device measurements:

| Question | Answer |
|---|---|
| Q1 SKMatrix44 perspective | **Works** — Desktop + iPad Pro 2nd gen, verified visually with foreshortening and trapezoid ground plane |
| Q3 50 MB RGB565 SKImage upload | **2.18 ms/frame on iPad** (4.0 ms Desktop M4 — iPad faster due to unified memory) |
| Q4 hidden visual idle | **IsVisible=false doesn't pause**, Children.Remove does |

Three commits landed on the branch:

- `fb5a348` — Pivot plan + Q1 PerspectiveSkiaSpike (F9)
- `1765d07` — Q3 CoverageUploadSpike (F10)
- `fa210fb` — Q4 HiddenVisualSpike (F4)

All three spike controls live in
`Shared/AgValoniaGPS.Views/Controls/Spikes/` and are wired to F-key
toggles in `Platforms/AgValoniaGPS.Desktop/Views/MainWindow.axaml.cs`
for visual A/B testing on Desktop. They are NOT mounted on iOS or
Android (one-shot Q1/Q3 iPad verifications used a temporary swap in
MainView that was reverted same session).

## Architectural decisions already made

Don't re-litigate these — they are baked into the plan and verified by
data:

1. **Pivot target is `CompositionCustomVisualHandler` + Skia GPU.** Not
   GpuInterop (also throttled), not OpenGlControlBase (the original
   bug), not Avalonia Metal control (no such public API in 12.0.3).
   Memory: [[gl-compositionvisual-pivot]].
2. **Apply 4x4 to canvas via `SKMatrix44.Matrix` collapse → SKCanvas.SetMatrix(SKMatrix)`.** Not
   `canvas.Concat(SKMatrix44)`. The .Matrix property handles the
   row-vector → column-vector transposition correctly. Memory:
   [[skiasharp-skmatrix44]].
3. **Build mvp in System.Numerics, implicit cast to SKMatrix44.** No
   hand-built ctors needed (column-major confusion bit us).
4. **Mode toggle uses `parent.Children.Add/Remove`, NOT `IsVisible`
   binding.** Q4 proved IsVisible leaves the animation handler firing.
   Memory: [[visibility-toggle-rule]].

## Phase 1 starting point

From the plan, Phase 1 is "skeleton control, top-down only":

> `SkiaMapControl` + `SkiaMapVisualHandler`, derived from
> `CompositionCustomVisualHandler` (mirror `MapCompositionHandler` shape
> from `DrawingContextMapControl`). Implement `IMapControl`. Top-down
> only (pitch = 90°, no perspective math yet). Renders: background fill,
> grid, ground texture, boundary, headland, vehicle.

> Side-by-side with `DrawingContextMapControl` and `GlMapControl` — same
> side-by-side pattern as the spike branch today, toggled by a DiagFlag
> for A/B comparison.

> Exit criteria: S1, S2 scenarios from the existing TESTING.md protocol
> show the new control rendering correctly on iPad + Android, FPS in the
> same ballpark as DrawingContextMapControl (within 10%).

Two implementation notes for Phase 1:

- **Reference the existing `MapCompositionHandler` inside
  `DrawingContextMapControl.cs`** (around line 3223). It's already
  doing the CompositionCustomVisualHandler pattern correctly; the new
  handler is essentially a parallel rewrite using the lessons from
  Phase 0.
- **The mode toggle must use Children.Add/Remove on the parent Grid**
  (per memory [[visibility-toggle-rule]]). The existing toggle in
  MainWindow.axaml.cs / MainView.axaml.cs uses IsVisible binding —
  that pattern needs to be refactored as part of Phase 1.

## Bonus discovery — easy win available

Q4 also exposed that the current `GlMapControl` on the spike branch
keeps rendering at 40 fps even when 2D is the visible mode (the
"IsVisible doesn't pause" problem). Fixing the F3 toggle handler in
MainWindow + MainView to use Children.Remove/Add would recover the
2D path's 117 → 40 FPS regression on iPad TODAY, without waiting for
Phase 1 to land. Consider doing this as a small standalone commit
early in the next session before starting Phase 1 proper.

## What to bring into the new session

The session can be opened fresh — no need to recap Phase 0 history.
The key reference docs to load via Read at session start:

1. `Plans/GL_MAP_PIVOT_PLAN.md` — the plan
2. `Shared/AgValoniaGPS.Views/Controls/DrawingContextMapControl.cs`
   line 3223 onward — the existing MapCompositionHandler to mirror
3. Memory: [[gl-compositionvisual-pivot]], [[skiasharp-skmatrix44]],
   [[visibility-toggle-rule]] — all auto-load via MEMORY.md

Useful subset of memory entries to be aware of:

- [[fps-floor]] — 24 FPS minimum on the perf-floor hardware
- [[turns-are-critical]] — perf scenario that matters most
- [[no-shortcuts-mvvm]] — Phase 1 architecture should respect MVVM
- [[di-three-platforms]] — Desktop/iOS/Android each have their own DI
- [[one-push-per-fix]] — commit cadence
- [[clean-build-meaning]] — what "clean build" means here

## Constraints

- Don't add to the [[no-3d-rendering]] scope creep. Phase 1 is
  ground-plane only — 2.5D perspective comes later in Phase 3.
- Don't touch the GL spike code (`GlMapControl.cs`) during Phase 1.
  Leave it as the parked baseline. It gets deleted in Phase 4.
- Don't propose patching Avalonia. The pivot exists specifically to
  avoid that. Memory: [[no-workaround-paths]].
