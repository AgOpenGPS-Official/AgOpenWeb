# Next-session prompt — PERF-05 Phase 2c #6: render-thread audit

Paste the section below into a new session.

---

## Goal

Continue the PERF-05 perf audit on the AgOpenWeb app. Phase 1 (instrumentation), Phase 2a-b (investigation), and Phase 2c #1+#2 (display-tick fix) shipped on branch `perf-05/instrumentation` and are awaiting review as PR #406. The **next item from the priority list on issue #403 is the render-thread audit** (Phase 2c #6) — measure where the render thread's remaining ~82% CPU goes, identify which fixes have meaningful impact, then implement them.

## What we already know

The Time Profiler trace from `Plans/perf_data/2026-05-20/instruments/2026-05-20_134912_time_profiler_s5.trace` (iPad S5, sim driving + sections off + field loaded + panel closed, post-fix Phase 2c #2 build) shows the render thread (`tid_7603`, the Avalonia compositor) at ~82% of one core. Top inclusive time on the render thread:

| % render | Where |
|---|---|
| 30.2% | `ServerCompositionTarget.RenderRootToContextWithClip` |
| 29.4% | `ServerCompositionVisual.Render` (per-visual; **scales with visual tree size**) |
| 10.5% | `MapCompositionHandler.OnRender` (our map control) |
| 10.4% | `ServerCompositionDrawListVisual.RenderCore` |
| 8.5% | `Avalonia.Skia.Metal.SkiaMetalGpu.SkiaMetalRenderSession.Dispose` |
| 7.3% | `Avalonia.Skia.DrawingContextImpl.DrawRectangle` (panel / HUD backgrounds) |
| 3.3% | `SkiaSharp.SKCanvas.DrawRoundRect` (panel borders, buttons) |

**Empirical signal** that supports the visual-tree-size hypothesis: in the Phase 1 capture run we accidentally left the simulator panel open during one S6 capture (`S6_panel_open.log`). That single panel cost ~8 FPS on iPad — confirming that open panels carry real per-frame render-thread cost from the compositor walking their visual subtree.

## Suspect areas, in rough priority order

1. **Panel decoration overhead** — `DrawRectangle` 7.3% + `DrawRoundRect` 3.3% = 10.6% of render thread for panel backgrounds/borders. The status bar alone has nested `Border` + `Rectangle` + `TextBlock` for each readout. Could nested decorations collapse into fewer composited visuals?
2. **`SkiaMetalRenderSession.Dispose` at 8.5%** — looks like a per-frame Skia GPU resource session is being constructed and torn down each frame. May be amortizable.
3. **Visual tree size** — `ServerCompositionVisual.Render` at 29.4% scales linearly with the number of visuals the compositor walks per frame. Likely small-element-heavy regions: status bar, button strip, simulator panel (when open), Section Control panel.
4. **What our `MapCompositionHandler.OnRender` (10.5%) is doing** — `[RenderBudget]` shows track render `trk=` is the biggest item inside this when an AB line is active. The drafted `DrawTrackSk` paint-caching fix on the spike branch would help, but only by ~3-4% of render thread total. Lower priority than #1-#3.

## How the audit pattern works (already in place)

- **PERF-05 plan**: `Plans/PERF_05_SUBSYSTEM_CHURN_AUDIT.md`
- **Layered tooling**: `Stopwatch` + `GC.GetAllocatedBytesForCurrentThread()` for cheap structural measurements (Phase 1 style); Xcode Instruments Time Profiler for focused attribution (Phase 2b style).
- **DiagFlags markers**: file-presence flags in `Shared/AgOpenWeb.Models/Diagnostics/DiagFlags.cs`. Each subsystem's instrumentation is off by default; turn on by pushing a marker file to the app's Documents folder.
- **Marker push**: `Plans/perf_data/2026-05-20/push-markers.sh` (iPad via `xcrun devicectl device copy to --domain-type appDataContainer`; Android via `adb shell run-as <pkg> cp`).
- **Instruments capture**: `Plans/perf_data/2026-05-20/instruments-trace-ipad.sh` — attaches Time Profiler by process name, 30 s window, saves to `instruments/<timestamp>_time_profiler_s5.trace`. Override template via `TEMPLATE='Allocations' bash …` or `TEMPLATE='Metal System Trace' bash …`.
- **Scenario protocol**: `Plans/perf_data/2026-05-20/TESTING.md` — repeatable S1–S8 captures with marker windows.
- **Analysis writeup**: `Plans/perf_data/2026-05-20/ANALYSIS.md` (the audit log so far — read §8 and §10 for the most recent state).

## Suggested first moves for the render audit

Per the user's anti-deferral pattern, don't assume — measure. In order:

1. **Read the existing trace** before doing anything new. Pull the breakdown from `Plans/perf_data/2026-05-20/instruments/2026-05-20_134912_time_profiler_s5.trace` and look at the *callers* of `DrawRectangle` and `DrawRoundRect` on the render thread. Use the `python3` regex extraction pattern from the prior session (see ANALYSIS.md §10 — `inclusive` aggregation by frame-name prefix). Goal: find out *which* control/visual is invalidating + redrawing those rectangles every frame.
2. **Then** decide whether the fix is (a) reducing visual count in a specific panel, (b) caching/amortizing `SkiaMetalRenderSession`, or (c) something else the trace points at. The user wants data-driven decisions — measure first.
3. **For SkiaMetalRenderSession.Dispose** specifically — search the Avalonia source / `Avalonia.Skia.Metal` namespace for who owns and recycles the session. The 8.5% per-frame teardown is suspicious for a value that should be amortized.
4. **Don't propose any "fast vs slow" framings** with a bypass option (memory `feedback_no_workaround_paths`). Don't propose throttling cadences (memory `project_autosteer_decoupled_from_gps` — control cadences are intentional). Don't propose "later" or "deferred" work (memory `feedback_no_later_deferred`).

## Key constraints / memories to know

- iOS testing is on a **physical iPad Pro 12.9" 2nd gen**, never the simulator. Build with `-r ios-arm64`. The user has corrected this repeatedly.
- Control cadences (100 Hz machine, 50 Hz steer, 10 Hz GPS sensor, dead-reckoned display) are **architectural choices**, not perf bugs. Don't throttle them.
- The user maintains the AiO firmware and is a senior systems thinker. Match the conversation level.
- Multi-fix sessions go on a feature branch from the start (don't pollute develop).
- Build to verify compile, wait for user to confirm fix works, then one commit + push (not per-iteration).
- Honor user config without floors — built-in min/clamp values on user-visible settings are wrong.
- When iterating on the iPad, the test scenario S5 = simulator driving + sections off + field loaded + simulator panel closed. The panel-open variant adds ~8 FPS of cost so always confirm it's closed before measuring.
- Cycle pattern that worked well last session: user runs scenario for ~30 s, types `SN go` / `SN done` to mark window, then I slice the syslog/logcat stream into a per-scenario log file.

## Test devices currently set up

- **iPad**: UDID `d2fcb0323a90ad2954ab501f2603cd7573d99b2a`, bundle `com.agopenweb.ios`. Phase 2c #2 build installed. Documents/AgOpenWeb/ has all 8 PERF markers present.
- **Android**: serial `R52TB090VAK`, package `com.agopenweb.android`. Same build state.

## Branch / PR state

- `perf-05/instrumentation` — 18 commits ahead of develop, PR #406 open. Wait for review before merging.
- For new render-audit work, **branch from `perf-05/instrumentation`** (so the instrumentation infrastructure is still in place — the audit needs it). Don't branch from develop until #406 merges.

## What I'd start with concretely

```bash
# 1. New branch from PR head
git checkout perf-05/instrumentation && git pull
git checkout -b perf-05/render-thread-audit

# 2. Re-aggregate the existing trace by *immediate caller* of DrawRectangle on render thread
#    to figure out which visual is the source. Use the python3 pattern from
#    ANALYSIS.md §10 but modify to walk one frame deeper.

# 3. Capture an Allocations trace if Time Profiler points at "everywhere"
TEMPLATE='Allocations' bash Plans/perf_data/2026-05-20/instruments-trace-ipad.sh
```

Then iterate: data → hypothesis → fix → re-measure, the same loop the rest of this audit ran on.
