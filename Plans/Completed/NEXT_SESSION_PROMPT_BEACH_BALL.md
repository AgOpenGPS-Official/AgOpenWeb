# Next session — Desktop close+reopen beach ball

Branch: `spike/angle-silk-opengl-eval` (push state at `265820f` as of session end).

## The problem

On Desktop, with the `.use_skia_map` marker on, this sequence **reliably**
produces a 2-3 second UI thread freeze (macOS beach ball):

1. Open `Res Test` field (367 ha, has a background PNG, AB line set,
   auto sections + auto u-turn + auto-steer all enabled).
2. Start the simulator (stationary).
3. Close the field.
4. Re-open the same field.
5. Engage AutoSteer / accelerate.
6. → 2-3 s freeze on first non-stationary GPS cycle.

It does **not** fire on the initial open of the same field — only after a
close + reopen cycle. The user is suspicious (rightly) that anything
running constantly (UDP, GPS pipeline, render thread) can be the cause,
since those would freeze on initial open too. Something specific to the
close + reopen lifecycle is the real trigger.

PERF-05 captured the freeze directly:

```
[StateMirror-PERF]    cycles=5  alloc/cycle=147B   window=2.99s   ← 2.99s with only 5 cycles
[TrackGuidance-PERF]  cycles=17 alloc/cycle=336B   window=3.39s
[GpsPipeline-PERF]    cycles=5  alloc/cycle=3414B  window=2.99s
[ApplyGpsCycle-PERF]  cycles=5  alloc/cycle=4382B  window=2.99s
```

Normally each PERF emitter shows `cycles=31 window=1.03s`. The stretched
windows mean the UI thread was blocked for ~3 seconds and the
instrumented cycles barely ran.

## What's been ruled OUT this session

All four hypotheses below were tested in turn; the freeze persists
through each fix. **The fixes are still good and shipped** because they
each address real per-frame allocation churn — they just don't fix THIS.

1. **SendStateToHandler coalescing** (commit `b59cae5`) — ApplyGpsCycle
   alloc/cycle 10,909 B → 832 B (-92%). Real win. Freeze still hits.
2. **CompositeBackgroundIntoBitmap async** (commit `76f416f`) — moves
   ~700 ms per-cell loop off UI thread. Freeze still hits.
3. **`_detectionBits`/`_displayPixels` array reuse** (commit `265820f`,
   part 2) — skips ~73 MB LOH alloc per re-open. Freeze still hits.
4. **PNG decode cache retention** across `CloseFieldAsync`'s
   `ClearBackground` (commit `265820f`, part 3) — skips ~80 MB LOH
   re-decode per re-open. Freeze still hits.
5. **UDP `BeginSendTo` → `SendTo`** (commit `265820f`, part 1) —
   AutoSteerTx alloc/cycle 49,000 B → 240-1,296 B (-97%). Real win.
   Freeze still hits.

Cumulative: roughly 5 MB/s less LOH churn during driving + ~150 MB less
LOH alloc per close+reopen. Heap pressure is way down. The freeze is
unchanged. **This means the cause isn't accumulated LOH garbage
triggering a Gen2 collection** — that was the wrong hypothesis four
times running.

## What to do first

Stop guessing. Add direct GC instrumentation and measure. Concrete
next step:

In `MainViewModel.ApplyResults.cs` (where the `[ApplyGpsCycle-PERF]`
window emit lives, around line 100ish — search for
`[ApplyGpsCycle-PERF]`), capture Gen0/Gen1/Gen2 collection counts and
heap size **at the start and end of the perf window**. Emit the deltas
in the log line.

Something like:

```csharp
// At window start (when accumulator resets):
_perfAgcGen0Start = GC.CollectionCount(0);
_perfAgcGen1Start = GC.CollectionCount(1);
_perfAgcGen2Start = GC.CollectionCount(2);
_perfAgcHeapStart = GC.GetTotalMemory(false);

// At emit time:
int gen0Delta = GC.CollectionCount(0) - _perfAgcGen0Start;
int gen1Delta = GC.CollectionCount(1) - _perfAgcGen1Start;
int gen2Delta = GC.CollectionCount(2) - _perfAgcGen2Start;
long heapDelta = GC.GetTotalMemory(false) - _perfAgcHeapStart;
// ... add to the existing Console.WriteLine
```

Decisive outcome:

- If the stretched window shows `gen2Delta=+1` (or more), **GC is the
  cause** and we know to chase what's keeping memory alive across close.
  The likely suspects are HashSet/Dictionary state in singletons
  (`CoverageMapService._newCells`, `_cellCountPerZone`,
  `_activeSections`, etc.) that grow on use, `Clear()` on close but
  retain backing arrays — but those are guesses; trust the measurement.

- If the stretched window shows `gen2Delta=0` and `heapDelta` is small,
  **GC is innocent** and the freeze is something else entirely — likely
  candidates worth checking next:
  - Lock contention (some service taking a lock the UI thread waits on).
  - Synchronous file I/O on the open path (the Avalonia composition
    layer or a service touching disk).
  - Dispatcher post backlog (many work items queued from the close +
    reopen, drained synchronously when the UI thread next yields).
  - Avalonia.Skia surface rebuild or texture upload that's
    serializing on the UI thread.

## State that's already in place

- `~/Documents/AgValoniaGPS/.use_skia_map` marker is present (Phase 3
  perspective is the active 2D control).
- All six PERF-05 markers are enabled in
  `~/Documents/AgValoniaGPS/`:
  `.perf_state_mirror`, `.perf_gps_pipeline`, `.perf_apply_gps_cycle`,
  `.perf_guidance`, `.perf_coverage`, `.perf_autosteer`. (NOT
  `.perf_udp` — not necessary, UDP cost is now negligible.)

## Where to bring the user back in

After adding the GC instrumentation, ask the user to reproduce the
beach ball one more time and paste the surrounding `[ApplyGpsCycle-PERF]`
lines. Look for the stretched window and the `gen2Delta` value on that
line. Report the finding; pick the next-step branch based on it.

## Don't re-do

- Don't propose more allocation-cap fixes without a measurement showing
  Gen2 is firing. Five rounds of allocation hypotheses have been wrong;
  the GC measurement is now required gate before any further alloc work.
- Don't touch the Phase 3 perspective code — that work is shipped and
  verified on all three platforms. The beach ball is a separate concern
  not introduced by Phase 3.
- Don't try to A/B test by removing `.use_skia_map` — GL is parked half-
  built ([[gl-compositionvisual-pivot]]); it won't render PNG / coverage.

## Phase 3 status

**Shipped and verified** on iPad / Android / Desktop:
- 50 / 57 / 60 FPS in 3D driving with sections + coverage. All ≥ 2× the
  24 FPS floor.
- All S1-S7 visual scenarios passed (tap-to-create-AB ray-cast,
  near-plane clip on all polylines, grid + boundary + tracks + coverage,
  HeadingUp rotation at tilt).
- See `Plans/GL_MAP_PIVOT_PLAN.md` for exit criteria; all met.

## Open follow-ups (separate from the beach ball)

- Task #13: zoom button CanExecute (UX polish — buttons stay active at
  visual cap).
- Task #16: Phase 3 perf audit (claw back FPS, especially grid AA in 3D).

## What to load at session start

1. This file.
2. `Plans/PERF_05_SUBSYSTEM_CHURN_AUDIT.md` and
   `Plans/perf_data/2026-05-20/ANALYSIS.md` — the existing perf
   instrumentation framework.
3. `Shared/AgValoniaGPS.ViewModels/MainViewModel.ApplyResults.cs` —
   where to add the GC instrumentation.
4. Memory: [[test-devices]], [[fps-floor]], [[no-quick-fixes]],
   [[root-cause-over-symptom]] — applicable.
