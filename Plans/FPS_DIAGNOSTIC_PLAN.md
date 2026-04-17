# FPS Diagnostic Plan

## Purpose

Characterize the mobile rendering cost of AgValoniaGPS on the Android tablet
(historical 11 FPS with a field open) by isolating each contributor to the
frame budget. Produce a data table that tells us, per scenario, how many FPS
each of {coverage bitmap, per-draw-op work, panel overlays, compositor
invalidation, binding churn} consumes.

**This plan does not design fixes.** Design comes after measurement. Every
experiment here answers one question; no experiment anticipates a solution.

## Scope

### In scope
- Android tablet perf characterization (baseline device: see `reference_test_devices.md` memory).
- Render-path costs: draw ops, panel overlays, compositor invalidation frequency, binding updates.
- Thermal effects over a 5-minute sustained session.

### Out of scope
- iOS (revisit once Android is characterized).
- Desktop (already known: 60 FPS on M4, not the bottleneck we care about).
- Any implementation of replacements (coverage rewrite, vector layers, etc.).
- Pan/zoom/touch latency — separate concern.

## Prior learnings to validate

From the abandoned `feature/vector-map-spike` branch, preliminary numbers:

- Empty map, no panels, cold start: ~30 FPS (degraded to 23 FPS after multiple APKs, possibly thermal or accumulated diagnostic overhead)
- Field open, coverage OFF, no panels: ~27 FPS
- Field open, coverage ON, no panels: ~11 FPS
- Field open, coverage ON, sim panel open: 6 FPS
- Field open, coverage OFF, sim panel open (semi-transparent bg): 11 FPS
- Field open, coverage ON, sim panel open (opaque bg): 8 FPS

Derived rough costs (to be re-validated cleanly):
- Coverage bitmap blit: ~16 FPS
- Semi-transparent panel over field: ~13 FPS extra vs opaque
- Opaque panel over field: ~3 FPS
- Field rendering beyond coverage: ~3 FPS

These are noisy single-sample numbers. The diagnostic harness must produce
averaged, variance-aware measurements before any of them are treated as facts.

## Methodology

### Measurement discipline
1. **Cold start every measurement.** `adb shell am force-stop` + relaunch.
2. **Report variance, not single numbers.** Log FPS at 1 Hz for at least 30 s per scenario, report mean ± stddev.
3. **One variable per test.** Hold everything else constant (flags, field state, panel state, zoom).
4. **Record the environment.** Device thermal state, battery level, brightness, ambient temperature if relevant.
5. **Run every scenario at least twice, ideally at different thermal states** (cold after 10 min off, warm after 5 min session).

### Test environment
- Primary device: Samsung Android tablet (model `R52TB090VAK`).
- Fixed orientation and screen brightness per session.
- NTRIP connection disabled during measurement (background work variable).
- Simulator-driven GPS only; no real GPS receiver.
- Same test field for all field-open scenarios (Hale County, 32.59053, -87.18021).

## Infrastructure to build first

Build all of this **before any measurement runs**. Rebuild cost per scenario must be zero.

### 1. Diagnostic flag registry

File-presence flags in the app's `Documents/AgValoniaGPS/` directory. Read once
at process startup (no per-frame cost). Each flag toggles one render-path
variable. Relaunch to change state; no rebuild.

Flags:
- `.skip_coverage_draw` — skip the coverage bitmap blit at render time
- `.skip_boundary_draw` — skip boundary/headland polygon rendering
- `.skip_tracks` — skip all track rendering
- `.skip_ground_texture` — skip ground texture draw
- `.skip_grid` — skip grid lines
- `.skip_vehicle` — skip vehicle/tool/sections
- `.panels_opaque` — force all floating panel backgrounds to opaque
- `.hide_all_panels` — force every panel `IsVisible=false` regardless of state
- `.disable_animation_frame_update` — stop the compositor handler from requesting every frame; only invalidate on state change
- `.log_send_state_frequency` — count `SendStateToHandler` calls/sec and emit to logcat

Each flag logs its state once at startup: `[DiagFlags] skip_coverage_draw=true ...`.

### 2. Logcat FPS sink

Emit to logcat every 2 seconds:
```
[FPS] avg=N.N min=N.N max=N.N stddev=N.N n=M over 2000ms
```

Computed from the existing `_compositorFrameCount` in `MapCompositionHandler`.
This lets measurements be harvested via `adb logcat` without the user reading
the HUD. It also gives us variance, which single HUD readings don't.

### 3. Render-op timing (phase 2, optional)

Wrap each `Draw*` call in `MapCompositionHandler.OnRender` with a stopwatch.
Emit once per second:
```
[RenderBudget] total=N.Nms coverage=N.Nms boundary=N.Nms tracks=N.Nms ... other=N.Nms
```

Helps identify which specific draw op dominates when coverage is off.
Lower priority — may not be needed if flag-based isolation is sufficient.

### 4. Automated scenario runner

A shell script that:
1. Force-stops the app.
2. Toggles the requested flag files via `adb shell run-as`.
3. Relaunches the app, waits for first state.
4. Sends the app through a scripted sequence (open field → open panel → etc.) via `adb shell input` or a debug-only IPC endpoint.
5. Captures logcat `[FPS]` output for 30-60 s per scenario.
6. Writes results to a CSV.

This may be overkill for early scenarios. Revisit after manual runs to decide.

## Baseline scenarios

Measure these in order. Each is a separate cold-start run.

| # | Scenario | Flags | Expected signal |
|---|----------|-------|----------------|
| B1 | Post-install, no field, no panels | none | Hardware + framework ceiling |
| B2 | Same after 5 min sustained session | none | Thermal delta vs B1 |
| B3 | B1 with `disable_animation_frame_update` | that flag | Is compositor loop self-limiting? |
| B4 | Field open (Hale Co.), no panels, no flags | none | Field-open baseline |
| B5 | B4 after 5 min sustained session | none | Thermal under field load |

## Isolation matrix

For each draw-op flag, measure field-open-no-panels FPS vs B4:

| Test | Flag on | Δ vs B4 reveals |
|------|---------|-----------------|
| I1 | skip_coverage_draw | Coverage blit cost |
| I2 | skip_boundary_draw | Boundary+headland cost |
| I3 | skip_tracks | Track rendering cost |
| I4 | skip_ground_texture | Ground texture cost |
| I5 | skip_grid | Grid draw cost |
| I6 | skip_vehicle | Vehicle/tool/sections cost |
| I7 | all I1–I6 on | Pure compositor/empty-map cost with field data loaded |

Each Δ isolates one draw op's contribution.

## Panel matrix

Measure with field open (per B4):

| Test | Scenario | Δ vs B4 reveals |
|------|----------|-----------------|
| P1 | Sim panel open, default (semi-transparent) | Full current panel cost |
| P2 | Sim panel open with `panels_opaque` | Opaque panel cost |
| P3 | P1 − P2 | Transparency-specific cost |
| P4 | Field Ops panel open, default | Is it panel-specific or universal? |
| P5 | Multiple panels open | Linear or nonlinear accumulation? |
| P6 | Panel open with `skip_coverage_draw` | Coverage × panel interaction |

## Compositor frequency tests

| Test | Scenario | Reveals |
|------|----------|---------|
| C1 | Default compositor invalidation | Baseline |
| C2 | `disable_animation_frame_update` | Are we forcing unnecessary redraws? |
| C3 | `log_send_state_frequency` for 30 s | Is state push rate reasonable? |

If C2 shows a big jump, the `Invalidate()` + `RegisterForNextAnimationFrameUpdate()` loop in `MapCompositionHandler` is forcing frames even when nothing changed.

## Exit criteria

The plan is complete when we have, in a spreadsheet:

1. Mean ± stddev FPS for every scenario above, twice (cold and warm).
2. A decomposition table answering: "for Scenario X, coverage contributes A FPS, panel transparency B FPS, draw-op Y C FPS, other D FPS."
3. A ranked list of FPS-recovery opportunities, largest first, with confidence intervals.

Only then do we start designing fixes.

## What comes after

Once we have clean data, the follow-up work falls into two tracks:

1. **Panel/compositor track** — if transparency × map complexity is the main cost, fix all panels uniformly. Small surgical change, high yield.
2. **Coverage representation track** — if coverage blit is confirmed as the biggest single cost, design the replacement (thick strokes, swath polygons, retained geometry). This is the larger redesign.

Both may be warranted; data tells us the order.
