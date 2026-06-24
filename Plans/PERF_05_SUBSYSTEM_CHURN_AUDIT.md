# PERF-05: Subsystem Churn Audit

Systematic, subsystem-by-subsystem measurement pass to characterize where the
app actually spends CPU and allocator time on each platform, so we can rank
fixes by largest *unjustified* time sink — not by gut feel.

**Status:** Draft. Pre-instrumentation.
**Parent strategy:** [`PERFORMANCE_STRATEGY.md`](PERFORMANCE_STRATEGY.md)
**Umbrella issue (churn fixes):** GitHub #403 (renaming as part of this work)

## Why this exists

Recent iPad performance investigation found a single allocation-churn site
(`DrawingContextMapControl.DrawTrackSk` re-allocates SKPaint / SKPathEffect /
SKFont every frame, ~4 ms/frame on iPad Pro 2nd gen for one AB line).
Issue #403 had already identified a separate churn site (upstream `Track`
re-allocation each GPS tick defeating the GL `ReferenceEquals` gate).

These are two instances of the same pattern: per-frame or per-tick allocation
that platform A's allocator masks but platform B's exposes. Fixing them
one-at-a-time as they surface during feature work is the deferral trap —
the bill comes due with interest. This plan flips the order: instrument
everything once, analyze the whole picture, then fix in priority order from
a single umbrella issue.

## Goals

1. For each subsystem listed below, get **CPU time per cycle** and **allocator
   bytes per cycle** at 1 Hz emission cadence on both iPad and Android.
2. For each subsystem, judge whether the measured cost is *reasonable for the
   task it performs* (necessary compute) or *disproportionate / scaling with
   frequency instead of input* (churn).
3. Produce a prioritized fix list — entries land on issue #403 in order of
   "largest unjustified cost first."

Explicit non-goals:
- Not fixing anything in this plan. Fixes are individual follow-ups linked
  from #403.
- Not adding always-on perf telemetry. Markers stay off in shipped builds.
- Not chasing 60 FPS. The strategy doc's 24 FPS floor / ~47 FPS ceiling
  framing still holds.

## Subsystems

Seven subsystems on the first pass, in measurement priority order. Each gets
its own DiagFlags marker (so a single marker file flips it on, no rebuilds).

| # | Subsystem | Code entry point | Measurement marker |
|---|---|---|---|
| 1 | 2D render path | `DrawingContextMapControl.OnRender` | `.perf_render_2d` (existing `.log_render_timing` covers part of this) |
| 2 | State mirroring | `MainViewModel.SendStateToHandler` / render-pull tick | `.perf_state_mirror` |
| 3 | GPS pipeline | `GpsService` → `PositionEstimator` → snapshot apply | `.perf_gps_pipeline` |
| 4 | Guidance | `TrackGuidanceService` + `YouTurnGuidanceService` | `.perf_guidance` |
| 5 | Coverage | `CoverageMapService.AddCoveragePoint` / cell mark / paint | `.perf_coverage` |
| 6 | UDP | `IUdpCommunicationService` packet parse + send | `.perf_udp` |
| 7 | AutoSteer | `IAutoSteerService` zero-copy pipeline | `.perf_autosteer` |

GL render path is intentionally excluded — it lives only on
`spike/angle-silk-opengl-eval`, not develop, and the spike is parked. When
the spike resumes, the same instrumentation pattern applies to
`GlMapControl.OnOpenGlRender` (groundwork already done in the parked
branch's `e9e3aee` commit).

## Standard instrumentation pattern

Same shape across every subsystem so analysis tooling is uniform.

```csharp
// At the start of the cycle (e.g. method entry)
long t0 = Stopwatch.GetTimestamp();
long a0 = GC.GetAllocatedBytesForCurrentThread();

// ... actual work ...

// At the end
_perfCycleTicks += Stopwatch.GetTimestamp() - t0;
_perfCycleAllocs += GC.GetAllocatedBytesForCurrentThread() - a0;
_perfCycleCount++;

// 1-Hz emission on the same wall-clock window each subsystem uses
if (windowElapsed >= 1.0 && _perfCycleCount > 0)
{
    double ticksPerUs = Stopwatch.Frequency / 1_000_000.0;
    Console.WriteLine(
        $"[<Subsystem>-PERF] cycles={_perfCycleCount} " +
        $"us/cycle={(_perfCycleTicks / ticksPerUs / _perfCycleCount):F1} " +
        $"alloc/cycle={(_perfCycleAllocs / _perfCycleCount):F0}B " +
        $"total_us={_perfCycleTicks / ticksPerUs:F0} " +
        $"total_alloc={_perfCycleAllocs}B");
    _perfCycleTicks = _perfCycleAllocs = _perfCycleCount = 0;
}
```

For render subsystems we already have per-section buckets — keep those, just
add the allocation delta around the whole frame.

For subsystems that have a "natural cycle" (GPS pipeline = per fix; coverage
= per AddCoveragePoint; UDP = per packet), the cycle count is the natural
event count, not frames.

## Test scenarios

Each scenario produces one row of data per subsystem per platform. Hold each
scenario for ~30 seconds so the 1-Hz emission gives a stable average. Run
on both iPad Pro 2nd gen and Samsung Android tablet — both are the
perf-floor devices the strategy doc names.

| ID | Scenario | What it stresses |
|---|---|---|
| S1 | App open, no field | Composition + render baseline only |
| S2 | 330ha field loaded, idle | + coverage bitmap, boundary, headland render path |
| S3 | S2 + active AB track | + track render (the iPad finding) |
| S4 | S2 + active curve track (~500 points) | + per-point polyline render cost |
| S5 | Simulator driving, sections off | + GPS pipeline, state mirror at 10 Hz |
| S6 | Simulator driving, sections on | + coverage write per tick |
| S7 | Headland turn execution | + YouTurn compute on UI thread |
| S8 | Real GPS attached, sections off | + UDP RX + PGN parse at full rate |

## Data collection

Markers pushed without rebuild on each platform:

- **iPad (devicectl):**
  ```
  xcrun devicectl device copy to \
    --device <UDID> --domain-type appDataContainer \
    --domain-identifier com.agopenweb.ios \
    --source <marker-file> --destination Documents/AgOpenWeb/<marker>
  ```
- **Android (adb):**
  ```
  adb push <marker-file> /storage/emulated/0/Documents/AgOpenWeb/<marker>
  ```
  (Verify path matches `Environment.SpecialFolder.MyDocuments` on Android.)

Logs streamed off-device:
- **iPad:** `idevicesyslog -u <UDID> -m "PERF"`
- **Android:** `adb -s <serial> logcat | grep PERF`

Each measurement run captures all 7 markers active simultaneously. Output is
one log file per (platform, scenario) — 16 files total (2 platforms × 8
scenarios) — saved to `Plans/perf_data/<date>/<platform>/<scenario>.log`.

## Analysis framework

For each (subsystem, scenario, platform) cell, judge against three rules:

1. **Necessary compute** — time scales with input size (e.g. coverage paint
   time grows with new cells/sec), allocator bytes scale with input. ✅ Leave
   alone.
2. **Bounded fixed cost** — small constant per cycle, doesn't scale with
   anything. ✅ Leave alone unless the constant itself is suspect.
3. **Churn** — time or allocations grow with cycle *frequency* (not input),
   OR allocate objects per cycle that could trivially be cached. ❌ Fix.

The smoking-gun pattern for churn: a subsystem that allocates the same shape
of object every cycle, with the same configuration, that could be a single
cached instance. `SKPaint`, `SKPathEffect`, `SKFont`, model snapshots like
`Track`, DTOs like `MapRenderState`, etc.

For each ❌ row, prioritize by `iPad us/cycle × cycles/sec` (worst-case
floor-device cost per second). That number goes on #403 as one task.

## Outputs

1. **Raw data:** `Plans/perf_data/<date>/<platform>/<scenario>.log` (gitignored
   for size; summarized in #403 comments).
2. **Analysis table** appended to `PERFORMANCE_STRATEGY.md` under a new
   "iPad characterization" section with per-subsystem time and alloc/cycle.
3. **Prioritized churn fix list** added as task comments on issue #403, each
   with a recommended fix shape (cache field, instance pool, etc.).
4. **Updates to memory:**
   - `reference_test_devices.md` — current FPS readings for both devices
   - New memory note if any subsystem analysis surfaces a recurring pattern
     beyond simple caching.

## Tooling — layered approach

Logging is Phase 1. Native profilers are held in reserve for Phase 2,
deployed only at suspects the logging surfaces. Profilers are
complementary, not alternatives.

- **Phase 1 — DiagFlags logging (this plan).** Cheap (<1% overhead), runs
  unattended, comparable across platforms and runs, ships invisibly. Gives
  the structural picture: which subsystem, how much time, how much
  allocation per cycle.
- **Phase 2 — Native profilers (focused).** Used only on subsystems Phase 1
  flags as suspect.
  - **Xcode Instruments → Allocations** — confirms the exact call stack of
    a churn site Phase 1 identifies.
  - **Xcode Metal System Trace** — for the "outside-our-render-code" GPU /
    compositor gap on iPad (the one place pure logging can't see).
  - **Android Studio Memory Profiler** — Android equivalent, typically with
    cleaner .NET symbol resolution than Instruments.
  - **`dotnet-trace`** — managed-side EventPipe trace if Mono symbol
    mangling in Instruments blocks reading a stack.

We don't set up profiler infrastructure or capture sessions until Phase 1
points us at specific suspects to investigate.

## Out of scope (do not chase in this audit)

- Anything already debunked in `PERFORMANCE_STRATEGY.md` ("Debunked
  hypotheses" section): panel transparency, animation loop, individual
  sub-millisecond draw ops.
- GL spike work — that's parked on `spike/angle-silk-opengl-eval` and
  resumes after the iPad reversal is addressed.
- Any "while we're in there" refactors. This pass is measurement + a
  prioritized churn fix list; the fixes themselves happen on individual PRs
  linked from #403.
