# PERF-05 — Analysis (2026-05-20)

> **Status: audit complete.** All three phases (1 instrumentation,
> 2a-b investigation, 2c #1+#2 fix) closed. Headline result: **iPad
> S5 43 → 64 fps (+49%)**, main-thread CPU −44%, render-thread CPU
> −12%. Remaining `#403` candidates (`DrawTrackSk` paint caching,
> `DrawVehicleSk` paint caching, `Track` instance pooling,
> render-thread audit) are tracked there for future iteration.

## Phase 1 — Instrumentation pass

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
4. **Cut per-cycle allocation in `Coverage.AddCoveragePoint`** — at
   1,616 cycles/s with 1.06 KB/cycle that's 1.7 MB/s of pure
   allocator pressure. The cadence itself is intentional: control
   loop runs at 100 Hz (matches firmware `taskAutosteer`), steer
   packet exchange at 50 Hz, GPS at 10 Hz, on-screen tractor position
   dead-reckoned between fixes. See
   `Plans/Completed/UNIFIED_CONTROL_LOOP_PLAN.md` and
   `Plans/Completed/UNIFIED_PACKET_PROTOCOL.md` — do NOT propose
   collapsing back to GPS rate. Fix the *per-call* cost instead: pool
   the intermediate quad/cell tuples, reuse rasterization buffers,
   avoid `Dictionary` lookups that allocate boxed keys.
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

## 7. Phase 2a — `ApplyGpsCycleResult` instrumentation result

Hypothesis going in: the iPad "+13 ms outside OnRender" at S5 from
Phase 1 lives inside `ApplyGpsCycleResult` (UI-thread bridge from the
background GPS cycle to State updates).

Result: **partial confirmation.** `ApplyGpsCycle` is a real allocator
contributor and the largest single UI-thread allocator subsystem, but
its **direct** CPU cost only explains ~1 ms/frame of the iPad outside
gap — not the full ~14 ms.

### Clean S5 (panel closed, sim driving, sections off) on Phase 2a build

| Bucket | µs/cycle | KB/cycle | Rate | CPU ms/s | KB/s | ms/frame |
|---|---|---|---|---|---|---|
| iPad RenderBudget inside | — | 5.4 | 43 fps | 270 | 232 | 6.3 |
| **iPad `ApplyGpsCycle-PERF`** | **1,299** | **9.37** | **30** | **39** | **281** | **0.9** |
| iPad StateMirror | 179 | 1.78 | 20 | 4 | 36 | 0.08 |
| iPad GpsPipeline (bg) | 696 | 3.38 | 30 | 21 | 101 | — |
| iPad AutoSteerTx | 393 | 1.02 | 100 | 39 | 102 | 0.9 |
| iPad UdpTx | 178 | 0.50 | 210 | 37 | 105 | 0.9 |
| **Android `ApplyGpsCycle-PERF`** | **1,517** | **8.87** | **19** | **30** | **177** | **0.5** |

Frame-time accounting on iPad at S5 (Phase 2a):
- frame budget: 1000/43 = **23.3 ms/frame**
- inside `OnRender`: **6.3 ms**
- `ApplyGpsCycle` (UI thread): **0.9 ms**
- `StateMirror` + `UdpTx` + `AutoSteerTx` on UI thread: **~1.9 ms**
- **unaccounted "outside": ~14 ms/frame** ← still missing

### What we learned that IS actionable

1. **`ApplyGpsCycleResult` allocates 9.37 KB/cycle × 30 Hz = 281 KB/s.**
   Biggest single UI-thread allocator subsystem. Worth pooling the
   snapshot/DTO objects it produces even though it's not the +13 ms
   answer.
2. **Android `ApplyGpsCycle` is *slower per cycle* than iPad**
   (1,517 µs vs 1,299 µs) but lower CPU/s because Android runs at
   19 Hz vs iPad 30 Hz. This **confirms the GpsPipeline rate
   asymmetry from Phase 1 is real and propagates downstream** — same
   asymmetry in `ApplyGpsCycle` cycle rate.
3. **Per-cycle allocation is the same shape on both platforms**
   (9.37 KB vs 8.87 KB). The churn is in the code, not the platform.

### What's still missing (Phase 2b → Instruments)

The remaining ~14 ms/frame iPad outside cost can't be attributed to
any of the now-9 instrumented subsystems. Suspect bucket:

- **Avalonia binding / composition response** to the `PropertyChanged`
  cascade fired *inside* `ApplyGpsCycle`'s bracket — the property
  setter call is synchronous (inside our timing), but the
  binding's effect on layout/composition is scheduled and runs on
  the next compositor frame (outside our timing).
- **GC pauses** from accumulated allocator pressure on the UI thread:
  ApplyGps 281 + AutoSteerTx 102 + UdpTx 105 + StateMirror 36 + render
  frame allocs 232 = **~756 KB/s flowing through the UI thread alone.**
- **Skia → Metal handoff** per present, which pure C# logging can't
  see.

Phase 2b — Xcode Instruments Time Profiler on iPad during S5 — should
isolate this. Setup script:
[`instruments-trace-ipad.sh`](instruments-trace-ipad.sh).

## 8. Phase 2b — Xcode Instruments Time Profiler on iPad during S5

Captured 2026-05-20 12:04 via [`instruments-trace-ipad.sh`](instruments-trace-ipad.sh).
Trace bundle at `instruments/2026-05-20_120411_time_profiler_s5.trace`
(gitignored, 20 MB). 30-second window with S5 state held on iPad.

### Where the main thread spends its time

Main thread received **7,037 CPU samples** over 30 s = ~7.0 s of main-thread
CPU = **~23% of one core constantly busy**. Top inclusive-time call sites
(% of all main-thread samples; any frame in stack counts):

| % main | Where | What |
|---|---|---|
| **22-23%** | `Avalonia.Media.TextFormatting.TextLayout` ctor / `CreateTextLines` / `FormatLine` / `ShapeTextRuns` / `FetchTextRuns` / `BidiAlgorithm` / `LineBreakEnumerator` | **Text layouts being re-created every GPS cycle** for TextBlock-bound properties |
| 16% | `CommunityToolkit.Mvvm.ObservableObject.OnPropertyChanged` (×2) | `PropertyChanged` event firing |
| 15% | `Avalonia.Utilities.WeakEvents...OnEvent` + `WeakEvent.Subscription.OnEvent` | Weak-event dispatch to binding subscribers |
| 14.9% | `MainViewModel.ApplyGpsCycleResult` | Phase-2a-instrumented bracket (matches the 39 ms/s measured at 30 Hz) |
| 8% | `Avalonia.Markup.Xaml.MarkupExtensions.CompiledBindings.InpcPropertyAccessor.OnEvent` / `SendCurrentValue` | INPC binding accessor |
| 6.5% | `Avalonia.Data.Core.BindingExpression.OnNodeValueChanged` / `ConvertAndPublishValue` | Avalonia binding pipeline |
| 6% | `Avalonia.Utilities.WeakHashList.GetAlive` | Binding subscriber list enumeration |
| 5.6% | `MainViewModel.set_GpsToPgnLatencyMs(double)` | One specific text-bound property setter |
| 4.2% | `MainViewModel.set_Latitude(double)` | Another text-bound property setter |

### The actual call chain on iPad main thread

```
ApplyGpsCycleResult (15%)
  → set_Latitude / set_Heading / set_GpsToPgnLatencyMs / … (each ~3-5%)
    → ObservableObject.OnPropertyChanged (16% total)
      → WeakEvent dispatch (15%)
        → CompiledBindings.InpcPropertyAccessor.OnEvent (8%)
          → BindingExpression.ConvertAndPublishValue (6.5%)
            → TextBlock re-render
              → TextLayout.CreateTextLines (23%)
                → BidiAlgorithm, LineBreakEnumerator, ShapeTextRuns
```

### What this confirms / resolves

The Phase 1 finding "iPad +13 ms/frame outside OnRender when simulator
runs" is **TextLayout cost driven by TextBlocks bound to high-frequency
properties**. At 43 fps the 23% text layout cost translates to
**~5 ms/frame** — about a third of the +14 ms iPad-outside gap, just
from text re-shaping. The remaining ~9 ms/frame is the rest of the
binding cascade (WeakEvent dispatch, BindingExpression, INPC accessor
chain), all of which scale with the trigger rate.

This is "death by a thousand cuts" — no single function is more than
~23% of main thread, but the cascade adds up to dominate. The fix lever
is **reducing the trigger rate**, not chasing Avalonia internals.

### Render-thread (tid_7803) is the real bottleneck — pegged at ~93% CPU

The main thread analysis above shows ~23% CPU. But the **render thread**
captured **27,822 samples = ~27.8 s of CPU in a 30-second window =
~93% of one core constantly busy**. That's the actual ceiling we're
hitting, not the main thread.

Top inclusive time on tid_7803 (Avalonia compositor / Skia / Metal):

| % render | Where | What |
|---|---|---|
| 40% | `NSRunLoop` / `CADisplayLink` + `ServerCompositor.RenderCore` overhead | Compositor framework — drives the render loop |
| 27% | `ServerCompositionTarget.RenderRootToContextWithClip` | Walk the visual tree, render the root |
| 26.5% | `ServerCompositionVisual.Render` (each visual) | Per-visual render; **scales with visual tree size** |
| 12.9% | `ServerCompositionDrawListVisual.RenderCore` | Drawing the draw lists per visual |
| **9.5%** | **`Avalonia.Skia.DrawingContextImpl.DrawRectangle`** | Rectangle draw — panel/HUD backgrounds |
| 7.7% | `ServerCompositionCustomVisual.RenderCore` | Custom-visual host (our `DrawingContextMapControl`) |
| **7.6%** | **`MapCompositionHandler.OnRender`** | Our map render (matches PERF logging) |
| 7.2% | `SkiaMetalRenderSession.Dispose` | Skia render-session teardown |
| **5.8%** | **`SKCanvas.DrawRoundRect`** | Rounded rectangles — panel borders, buttons |
| 5.7% | `SKCanvas.Flush` / `GRContext.Flush` | Skia → Metal command submission |

### What this reframes

- **Our map render is only 7.6% of render-thread CPU.** Even if every
  `DrawTrackSk` / `DrawVehicleSk` allocation churn fix landed at 100%
  effectiveness, render thread would still be ~85% pegged.
- **Panels / HUD render together are ~15% of render-thread CPU**
  (rectangle 9.5% + rounded rectangle 5.8%). Dovetails with the
  empirical "simulator panel open costs ~8 fps" finding — open panels
  add visuals the compositor must walk every frame.
- **`SkiaMetalRenderSession.Dispose` is 7.2%** — a per-frame Skia
  resource session is being torn down. May be amortizable.
- **The +14 ms iPad-outside gap has two layers**: ~5 ms from
  main-thread TextLayout (PropertyChanged cascade → text reshape), and
  the rest from render-thread work the main thread is implicitly
  blocked on or that drives the next frame's wait.

### Phase 2c fix candidates from this trace

In priority order:

1. **Throttle GPS-display text-bound properties to ~5 Hz.** Speed,
   lat/lon, heading, latency — none of these need to update visually
   at 30 Hz. A 200 ms throttle is invisible to the eye and would cut
   text layout cost ~6×. Quick fix: add a small DispatcherTimer-driven
   mirror that updates the display-bound copies of these properties.
2. **Compare-and-skip identical values in `ApplyGpsCycleResult`
   setters.** Several setters call `CommunityToolkit.Mvvm.SetProperty`
   which short-circuits on reference-equal — but on `double` setters
   this never short-circuits because every double bit-pattern differs
   from the last. Add `Math.Abs(new - old) < epsilon` checks for
   display-only properties.
3. **Audit which TextBlocks bind to high-frequency properties** and
   replace direct bindings with `Formatted` properties that update from
   the throttled tick instead of the raw GPS tick. Pairs well with #1.
4. **Pool snapshot DTOs in `ApplyGpsCycleResult`** — 9.4 KB/cycle is
   real GC pressure (281 KB/s on iPad). Even if the cascade is cheaper
   after #1, less garbage = fewer GC pauses contributing to "outside"
   time.
5. **The earlier-priority items still hold**: `DrawTrackSk` paint
   caching (lift from spike branch), `DrawVehicleSk` paint caching,
   cache `Track` instance upstream (#403 original finding). These are
   per-frame allocation churn fixes, independent of the binding
   cascade.
6. **Render-thread audit — biggest long-term lever.** With render
   thread pegged at ~93% CPU and the map only 7.6% of that, the win
   isn't in the map render code — it's in reducing what the
   compositor has to walk every frame:
   - Audit which panels are *always rendered* even when not visually
     active (the +8 fps "simulator panel open" finding suggests open
     panels carry real per-frame cost).
   - Look at the rectangle / rounded-rectangle hot draws (15% combined
     of render thread). Could nested `Border` + `Rectangle` panel
     decorations be replaced with fewer composited visuals?
   - Investigate the 7.2% `SkiaMetalRenderSession.Dispose` — should
     the render session be amortized across frames instead of torn
     down each one?

## 9. Phase 2c #1 implementation plan — decouple display from GPS arrival

The earlier framing of "throttle GPS-display setters to 10 Hz" was the
wrong shape. **GPS is just another sensor feeding the position
estimator at its own 10 Hz arrival rate** — see the cadence design in
[`UNIFIED_CONTROL_LOOP_PLAN`](../../Completed/UNIFIED_CONTROL_LOOP_PLAN.md)
and [`UNIFIED_PACKET_PROTOCOL`](../../Completed/UNIFIED_PACKET_PROTOCOL.md).
Control runs at 100 Hz consuming dead-reckoned pose; nothing should be
*gated on GPS sensor arrival*.

The mistake we're correcting: `ApplyGpsCycleResult` directly sets
`MainViewModel.Latitude / Longitude / Easting / Northing / Heading /
_speed / RollDegrees / FixQuality` from each GPS cycle's `result`.
Each setter fires `PropertyChanged` → Avalonia binding cascade →
TextBlock re-render → TextLayout recompute (Phase 2b finding). At sim
30 Hz iPad rate that's 30 cascades/s; at real 10 Hz it's 10/s. Either
way the architecture couples display refresh rate to sensor arrival —
the same coupling we removed for control logic.

### Fix shape

- **`State.Vehicle` is already the system of record** — it has
  `Latitude`, `Longitude`, `Easting`, `Northing`, `Heading`, `Speed`,
  `FixQuality`, `SatelliteCount` as `ObservableObject` properties,
  written by `ApplyGpsCycleResult` via `State.Vehicle.UpdateFromGps`.
- **Add a 10 Hz display tick** (separate `DispatcherTimer` independent
  of any sensor or control-loop tick) that reads from `State.Vehicle`
  (plus a small cache for fields not yet on `State.Vehicle`, e.g.
  `RollDegrees`) and sets the MainViewModel display properties.
- **Remove the direct display-property setters from
  `ApplyGpsCycleResult`** — keep `State.Vehicle.UpdateFromGps(...)`
  (that's the canonical state update; control loop and renderer need
  it at sensor rate). Drop the redundant `MainViewModel.Latitude = …`
  / `MainViewModel.Heading = …` etc. block.

Net effect: MainViewModel display PropertyChanged fires at exactly
10 Hz regardless of sensor or sim rate. Bindings unchanged.
TextLayout cost drops from ~30 Hz × per-property cascade to 10 Hz ×
per-property cascade — 3× reduction on iPad sim, 0 cost on real
10 Hz GPS (matched rate).

### Out of scope for this PR

- `GpsToPgnLatencyMs` setter (5.6% main-thread CPU in Phase 2b trace)
  is written from AutoSteer 100 Hz tick, not from GPS cycle. Same
  disease, separate fix — follow-up.
- `StatusMessage` updates from `ApplyGpsCycleResult` are
  event-driven (not every cycle) and don't need throttling.
- Tool/hitch position setters drive the map render path, not text;
  leave alone.

## 10. Phase 2c results — unified 5 Hz status-display tick

Two PRs landed:
- **Phase 2c #1** (`91b6919`): decouple GPS-sourced display properties
  from `ApplyGpsCycleResult`; sample from `State.Vehicle` at a 10 Hz
  display tick.
- **Phase 2c #2** (`d38eb00`): generalize to a single 5 Hz status tick
  covering *every* MainViewModel property bound to the top status bar,
  regardless of upstream source rate. Folded in `GpsToPgnLatencyMs`
  (was written at 100 Hz from `OnAutoSteerStateUpdated`). Architectural
  rule established: new status diagnostics use the cache → tick
  pattern, no direct PropertyChanged firings at source rate.

### Instruments confirmation (3 Time Profiler traces compared)

Captured on iPad Pro 12.9" 2nd gen in S5 state (sim driving, sections
off, panel closed), 30-second window each. Numbers below are %
of main-thread samples.

| Function | Phase 2b | Phase 2c#1 | Phase 2c#2 | Total Δ |
|---|---|---|---|---|
| **TextLayout.CreateTextLines** | 23% | 18.6% | **7.2%** | **−69%** |
| TextLayout ctor | 23% | 18.6% | 7.2% | −69% |
| BidiAlgorithm.Process | ~3% | 1.9% | 0.7% | −77% |
| **set_GpsToPgnLatencyMs** | 5.6% | 5.9% | **0.5%** | **−91%** |
| set_Latitude | 4.2% | 3.3% | 2.2% | −48% |
| InpcPropertyAccessor.OnEvent | 8% | 7.7% | **3.9%** | −51% |
| OnPropertyChanged | 16% | 15.1% | 12.9% | −19% |
| WeakEvents dispatch | 15% | 15.1% | 12.0% | −20% |

**Main-thread CPU**: 23.4% → 19% → **13%** (total: −44%).
**Render-thread CPU**: ~93% → ~89% → **~82%** (total: −12%).

The render thread benefits structurally: less binding work means less
invalidation means fewer per-frame visual tree walks. Absolute Skia
draw call counts:

| Render-thread draw call | Phase 2b | Phase 2c#2 | Δ samples |
|---|---|---|---|
| `DrawRectangle` (panel/HUD bg) | 2,643 | 1,810 | −833 (~−31%) |
| `DrawRoundRect` (panel borders) | 1,614 | 814 | −800 (~−50%) |

The 3.2× reduction in TextLayout cost (23% → 7.2%) closely matches the
trigger-rate ratio: GPS-sourced setters 30 Hz → 5 Hz (6×) and
AutoSteer-sourced 100 Hz → 5 Hz (20×), weighted by their original
contributions. The model predicted, the data confirmed.

### S5 frame budget result (iPad)

| Metric | Phase 2a baseline | **Phase 2c#2** | Δ |
|---|---|---|---|
| fps | 43 | **64** | +21 (+49%) |
| frame time | 23.3 ms | 15.6 ms | −7.7 ms |
| inside OnRender | 6.3 ms | 5.5 ms | −0.8 ms |
| **outside OnRender** | **17.0 ms** | **10.1 ms** | **−6.9 ms** |
| ApplyGpsCycle µs/cycle | 1,299 | 781 | −40% |
| StateMirror µs/cycle | 185 | 135 | −27% |
| Allocator throughput (UI thread) | ~770 KB/s | ~520 KB/s | ~−32% |

Phase 1's *"+13 ms iPad outside cost when simulator runs"* mystery is
now ~50% recovered (17 → 10 ms outside). The remaining ~3 ms is the
unavoidable composition/Skia/Metal cost for the visual tree the
compositor must still walk every frame — that's Phase 2c #6 territory
(render-thread audit).

### Android S5 (vsync-bound)

Vsync ceiling at 61 fps hides the visible win, but `ApplyGpsCycle` CPU
dropped 63% (1,517 → 557 µs/cycle). Battery / thermal benefit,
indirect responsiveness gain (more slack for other UI work).

### What Phase 2c #1+#2 actually shipped

1. **A single architectural rule**: every status-bar-bound MainViewModel
   property follows the cache → tick pattern. No future diagnostic
   can re-introduce a 100-Hz PropertyChanged storm by accident.
2. **`MainViewModel.cs` / `MainViewModel.GpsHandling.cs` /
   `MainViewModel.Guidance.cs` / `MainViewModel.ApplyResults.cs`**:
   `_statusTickTimer` (5 Hz), `OnStatusTick`, `_latestRollDegrees`,
   `_latestGpsToPgnLatencyMs`. Setter calls removed from sources.
3. **iPad recovers +21 fps in S5**, well above the 24 fps product floor.
4. **Cross-platform**: Android benefits proportionally; ApplyGpsCycle
   CPU dropped 63% there too.

## 11. Bugs filed during this audit

- **#404** — Android `AutoSteerService` not running (no PGN TX, breaks
  manual section painting)
- **#405** — `CoverageMapService.ClearAll()` wipes `_activeSections`,
  requires section-toggle cycle to resume painting after Delete
  Applied Area
- **#403** — Umbrella churn issue (this analysis populates it)
