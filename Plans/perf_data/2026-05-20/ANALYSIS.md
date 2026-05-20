# PERF-05 Phase 1 — Analysis (2026-05-20)

Captured: iPad Pro 12.9" 2nd gen + Samsung Android tablet R52TB090VAK.
Branch: `perf-05/instrumentation`.
Scenarios: S1 (app open, no field) · S2 (field loaded) · S3 (+AB line) ·
S5 (simulator driving, sections off) · S6 (sim driving, sections on).
S4, S7, S8 skipped this pass.

## 1. Frame-budget summary (medians per scenario)

| Scenario | iPad fps | iPad inside | iPad outside | Android fps | Android inside | Android outside |
|---|---|---|---|---|---|---|
| S1 app open | 117 | ~1.7 ms | **~6.8 ms** | 61 | ~1.5 ms | ~14.9 ms |
| S2 field loaded | 101 | ~1.4 ms | **~8.5 ms** (+1.7 vs S1) | 61 | ~1.8 ms | ~14.6 ms (flat) |
| S3 + AB line | 70 | 5.5 ms | ~8.8 ms | 61 | 4.9 ms | ~11.5 ms |
| S5 sim driving | 36 | 5.7 ms | **~22.1 ms** (+13.3 vs S3) | 54 | 5.5 ms | ~13 ms (flat) |
| S6 sim + sections on | 47 | 2.5 ms\* | ~18.7 ms | 59 | 2.3 ms\* | ~14.6 ms |

\* S6 zoom dropped to 0.21 (camera-follow zoomed out during sim driving),
which reduces visible geometry — trk drops from 4.2 ms to 0.4 ms. Not a
real "track cost fix," just less geometry on screen.

## 2. Three systematic asymmetries

### A. Presentation model — iPad renders unbounded; Android locks to vsync

- iPad at idle (S1) hits **117 fps**; Android caps at **61 fps**.
- Android is vsync-locked at the display refresh; when inside+outside
  fits inside 16.7 ms, the render thread sleeps the slack.
- iPad presents as fast as the Avalonia + Metal pipeline allows. **Every
  millisecond of cost shows up as fewer frames** rather than slack
  absorbed into vsync wait.

Implication: comparing platforms by raw FPS misleads. **iPad's FPS
sensitivity to load is the consequence of unbounded presentation, not
inherently slower rendering.** Adding 5 ms of work drops iPad 20+ fps;
Android absorbs the same 5 ms into vsync slack until 16.7 ms is gone.

This is the single biggest reason "Android beats iPad in FPS" looks
worse than it is. iPad's absolute work-per-frame is competitive (often
*better*) — it just doesn't get to coast.

### B. AutoSteer pipeline is iPad-only — driving most of iPad's "extra" CPU and TX load

Across **every** scenario:

|  | iPad | Android |
|---|---|---|
| AutoSteerTx | 101 cycles/s × 487 µs × 1,017 B | **0 cycles/s** |
| UdpTx | 212 sends/s × 226 µs × 500 B | 10 sends/s × 514 µs × 496 B |
| AutoSteerRx | (not exercised — sim path) | 0 |
| Coverage (S6) | 1,616 cycles/s × 34 µs × 1,058 B | **0** (section state never reached) |

Filed as **#404** (Android AutoSteerService not running). The
*correctness* downstream of this is that manual section painting is
broken on Android. The *perf* downstream is that **iPad pays ~110 ms
CPU/s + ~200 KB/s allocator churn for a pipeline Android isn't even
running.** Until #404 lands the perf comparison is structurally
asymmetric.

### C. iPad "outside-OnRender" explodes when simulator runs — Android stays flat

The interesting finding from this audit. Going from S3 (AB line idle)
to S5 (simulator driving, sections off):

|  | iPad | Android |
|---|---|---|
| Frame time | 14.3 → **27.8 ms** (+13.5) | 16.4 → 18.5 ms (+2.1) |
| Inside OnRender | 5.5 → 5.7 ms (+0.2 — basically flat) | 4.9 → 5.5 ms (+0.6) |
| **Outside OnRender** | 8.8 → **22.1 ms (+13.3)** | 11.5 → 13.0 ms (+1.5) |

iPad pays **+13 ms/frame outside OnRender** the moment the simulator
starts, while Android pays +1.5 ms. The new work the simulator
introduces is:

- `GpsPipeline.ProcessCycle` on a background thread — iPad **30 Hz** ×
  566 µs = 17 ms/s CPU (off the UI thread)
- A `Dispatcher.UIThread.Post(() => ApplyGpsCycleResult(result))` per
  cycle — **NOT instrumented in Phase 1.**

`ApplyGpsCycleResult` is the bridge between the background GPS cycle
and the UI thread state mirror. It:
- Mirrors `Track` references (#403's original finding)
- Sets `State.Vehicle`, `State.Guidance`, `State.YouTurn`
- Fires `PropertyChanged` cascade

**Hypothesis**: PropertyChanged from `ApplyGpsCycleResult` triggers
Avalonia binding evaluation that costs significantly more on iOS Metal
than on Android. On iPad this drops 30 frames/s into composition wait;
on Android it slides into vsync slack invisibly.

This is the **#1 Phase 2 target** — instrument `ApplyGpsCycleResult` +
Avalonia binding pipeline + Xcode Instruments Time Profiler during S5
to see exactly where the +13 ms is going.

## 3. GpsPipeline rate asymmetry — 30 Hz iPad vs 20 Hz Android

Same simulator, same scenario, different cycle rate:

|  | iPad | Android |
|---|---|---|
| GpsPipeline cycles/s (S5) | 30 | 20 |

Worth investigating where the simulator's emission cadence diverges per
platform. Could be a `DispatcherTimer` vs `Timer` choice, a different
backing clock, or platform-specific throttling.

Not a churn issue per se but explains why iPad takes a bigger hit from
the simulator — it gets 50% more cycles to process.

## 4. Churn candidates — ranked by iPad us/s × cycles/s (≈ CPU cost) + KB/s allocator

Combined sources of CPU + allocator pressure under steady state (S6
worst case, iPad). **Worst-offender first.**

| Site | Rate | us/cycle | KB/cycle | CPU ms/s | KB/s | Notes |
|---|---|---|---|---|---|---|
| Vehicle marker render (all scenarios) | 47–117 fps | ~700 µs | 3.5 KB | 32–82 | 165–410 | Fires every frame regardless of state; biggest *quiet* churn |
| `Coverage.AddCoveragePoint` (S6) | 1,616 | 34 µs | 1.06 KB | 55 | 1,709 | Rate driven by AutoSteer 100 Hz × 16 sections; not GPS rate |
| `DrawTrackSk` SKPaint/PathEffect/Font churn (S3+) | 47–70 fps | 4.2 ms | 1.78 KB | 195–294 | 84–125 | Original iPad smoking gun |
| `GpsPipeline.ProcessCycle` (sim on) | 30 | 566–890 µs | 3.39 KB | 17–27 | 102 | Track instance allocation churn (#403 original finding) |
| `AutoSteerTx` (iPad only) | 101 | 487 µs | 1.02 KB | 49 | 103 | Platform-asymmetric (#404) |
| `UdpTx` (iPad only at high rate) | 212 | 226 µs | 500 B | 48 | 106 | Driven by AutoSteer; collapses to 10 Hz on Android |
| `StateMirror` (`OnRenderPullTick`) | 20 | 147–230 µs | ~2.0 KB | 3–5 | 38 | Throttled to 20 Hz both platforms |
| Frame-level allocations (RenderBudget tot_alloc) | 47–117 fps | — | 3.7–5.7 KB | — | 175–660 | Includes vehicle + track + grid; subset of above |

**iPad steady-state alloc churn at S6 ≈ ~2.3 MB/s.** That's enough to
keep gen-0 GC running constantly; some of the "outside" cost on iPad
is likely GC pause time.

## 5. Concrete findings → fix candidates (for #403)

In rough priority order (biggest iPad relief, smallest blast radius):

1. **Cache `SKPaint`/`SKPathEffect`/`SKFont` in `DrawTrackSk`** — fixes
   the iPad track regression confirmed in S3 (4.2 ms → expected ~1 ms).
   Already drafted on the spike branch; can lift the diff over.
2. **Cache `SKPaint` in `DrawVehicleSk`** — same anti-pattern, fires
   every frame, costs ~3.5 KB/frame. ~165–410 KB/s avoided depending
   on FPS.
3. **Instrument `ApplyGpsCycleResult` + UI-thread binding cascade** —
   not a fix, but the Phase-2 measurement that unblocks #C above. iPad
   +13 ms/frame outside is the biggest single recoverable cost in the
   whole audit.
4. **Decouple `Coverage.AddCoveragePoint` cadence from
   `AutoSteerTx`** — 1,616 paints/s is ~3× more than GPS-fix rate
   requires. Painting at GPS rate (~30 Hz × 16 sections = 480/s) would
   cut coverage CPU + churn ~3×.
5. **Cache `Track` instance upstream of `GpsCycleResult`** — the
   original #403 finding. Confirmed still present: 3.39 KB/cycle ×
   30 Hz = 102 KB/s of pure churn.
6. **Investigate `GpsPipeline` rate asymmetry** (iPad 30 vs Android
   20). One platform is wrong relative to the other.

## 6. What we did not measure but should (Phase 2 scope)

- `ApplyGpsCycleResult` UI-thread work (the biggest gap)
- S4 (curve track with ~500 points) — polyline cost scaling
- S7 (headland turn) — YouTurnGuidance cost (didn't fire this run; need
  a field with a headland)
- S8 (real GPS) — `AutoSteerRx` zero-copy path; UDP RX at real-world
  rate
- Avalonia composition / Skia → Metal handoff cost (Xcode Instruments
  territory, not pure logging)

## 7. Bugs filed during this audit

- **#404** — Android `AutoSteerService` not running (no PGN TX, breaks
  manual section painting)
- **#405** — `CoverageMapService.ClearAll()` wipes `_activeSections`,
  requires section-toggle cycle to resume painting after Delete
  Applied Area
- **#403** — Umbrella churn issue (this analysis populates it)
