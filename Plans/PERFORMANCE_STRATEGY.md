# Performance Strategy

Umbrella document for all performance work on AgValoniaGPS. Captures what we
know from diagnostic measurement, ranks fixes by product impact, and acts as
the index for individual implementation plans.

**This document is strategic, not tactical.** It says *what* and *why*, not
*how*. Each prioritized item gets its own implementation plan when the work
begins. See "Linked implementation plans" at the end.

## Current state

- **Real-world field-open FPS on Android tablet: 47.2 FPS** (measured 2026-04-17 with real AiO board, field loaded, no panels open, after GPU cache bump + coverage snapshot fix)
- **24 FPS floor required** for smooth perceived motion (cinema threshold)
- **23 FPS above floor — product is shippable on this hardware.**
- Real-GPS ceiling is ~57 FPS; remaining 10 FPS gap is the fundamental cost of sampling a 50 MB texture each frame. Diminishing returns from here.

### User-facing perf knobs

Two existing `DisplayConfig` settings let the user trade visual fidelity for FPS
on slower devices — these are the right lever for hardware we can't make
faster, and the fixes below exist to make these controls actually useful:

- **`DisplayResolutionMultiplier`** (1.0 Ultra → 6.0 Minimum): scales the
  coverage display-bitmap cell size. At 1.5 "High" the bitmap is ~44% of Ultra;
  at 2.5 "Medium" it's ~16%. Detection grid stays at 0.1 m, so section-control
  semantics are unaffected. Lower = dramatically less per-frame sampling cost
  at wide zoom.
- **`FieldTextureVisible`** (bool): drops the ground texture entirely. Minimal
  cost after the single-draw fix, but one fewer texture in the cache.

iPad Pro 2nd gen test 2026-04-17: large 104 ha field at Ultra was ~17 FPS at
wide zoom. High + texture off → 30-37 FPS at the same zoom. Same app, same
code, user's choice.

### Journey

| Stage | Real-GPS field-open FPS | Recovery |
|---|---|---|
| Original (before any fixes) | 14.5 | — |
| + 128 MB Skia GPU cache (commit `9454cbe`) | 19.2 | +4.7 |
| + Coverage SKImage snapshot + SKSamplingOptions (commit `8a70eb1`) | **47.2** | **+28.0** |
| *Total* | | **+32.7** |

Both fixes were small: the cache bump was one line; the snapshot was ~30 lines. No architectural rewrites.

### What each fix did

**Skia GPU cache (128 MB)**: Avalonia's Skia default is 28 MB. Our coverage bitmap alone is ~50 MB. Non-coverage textures (ground tiling, vehicle, tool image) got evicted and re-uploaded every frame. Bumping the cache so everything fits eliminates that thrash. Helped non-coverage scenarios dramatically (+21 FPS for skip_coverage), less so baseline (coverage itself kept re-uploading for a different reason).

**Coverage SKImage snapshot**: The coverage bitmap is written to at GPS tick rate (10 Hz). Skia cannot GPU-cache a mutating bitmap — the cache slot is invalidated every write, so `DrawBitmap` re-uploaded the full 50 MB every frame regardless of cache size. Fix: snapshot the bitmap to an immutable `SKImage` at throttled cadence (every 200 ms). Between snapshots, the GPU caches the texture and `DrawImage` is effectively free. Trade-off: ~100 ms of visual lag on freshly-sprayed coverage, imperceptible in field work. Detection bitmap stays live (no lag for section control).

## Product requirement

Floor: **24 FPS** minimum during normal field operation. Below this, motion is
visibly jerky; guidance feels delayed; driver responsiveness degrades.
See `project_fps_floor.md` memory and `Plans/FPS_DIAGNOSTIC_PLAN.md` for
full rationale.

## Ceiling reality

Three measured ceilings on the Android tablet:

| Ceiling | FPS | What it is |
|---|---|---|
| No-input idle (sim off, no GPS) | 58 | Unrealistic — nobody uses the app like this |
| Simulator driven | 40 | Testing only |
| **Real GPS attached** | **~47** | **Real-world ceiling, measured** |

**60 FPS is not achievable with real GPS attached.** The PGN state-push pipeline
costs ~10 FPS off the top no matter what. Design around 24 FPS as the floor,
~47 as the practical ceiling, not 60.

## Confirmed findings

All numbers measured on Samsung Android tablet (R52TB090VAK) with real AiO
board streaming PGNs, field "test" open, simulator disabled, thermal-controlled
with laptop fan cooler.

### Costs (ordered by impact)

| Cost | FPS | % of ceiling | When it hits |
|---|---|---|---|
| Coverage bitmap blit | 22 | 47% | Every frame with a field open |
| Ground texture tiling | ~9 | 19% | Every frame; masked by coverage in baseline |
| State-push marshalling (UI→render thread) | ~10 | 21% | Every GPS tick (10 Hz real-world) |
| YouTurn path computation on UI thread | unmeasured spike | unknown | During headland turn approach |
| Panel overlay | 1–7.5 | 2–16% | When a panel is open; scales inversely with render weight |
| Individual small draws (boundary, tracks, vehicle, grid) | <1 each | noise | Every frame |

### Debunked hypotheses

These looked promising but measurement ruled them out. Don't re-chase:

- **Panel transparency is NOT a cost.** Measured 0.2 FPS delta between transparent and opaque backgrounds. Earlier spike's "13 FPS from opaque panels" was noise.
- **Compositor animation loop is NOT gratuitous.** Disabling per-tick `RegisterForNextAnimationFrameUpdate` gave 0 FPS recovery. Frames are driven by legitimate state changes.
- **Simulator-off 58 FPS is NOT the hardware ceiling for real-world reasoning.** It's a no-input idle state, not a product scenario.

## Prioritized fix order

Ranked by: (a) closes gap to 24 FPS floor, (b) addresses scenario that matters most, (c) effort.

### 1. ~~Coverage representation rewrite~~ — **DONE via snapshot approach**

- **Impact:** +28 FPS real-world (19.2 → 47.2), well above 24 floor
- **Approach:** SKImage snapshot at 200 ms cadence (commit `8a70eb1`). Didn't need a full representation rewrite — just a caching layer between the live bitmap and the render thread.
- **Status:** Done. Product is now shippable on this hardware.

### 2. YouTurn compute → background service — **UX-critical during turns**

- **Impact:** unmeasured steady-state (YouTurn only fires during autosteer + headland approach), but likely prevents FPS spikes during turns — the moment driver cognitive load is highest
- **Scenario:** headland turns — arguably the most important scenario in the app
- **Effort:** moderate-to-large — extract ~1,796 lines from `MainViewModel.YouTurn.cs` into a service that runs inside `GpsPipelineService.ProcessGpsCycle`. Phase 0 test harness first.
- **Product impact:** keeps UI responsive during the moment that matters most. Also fixes the MVVM violation flagged in the threading plan carve-out.
- **Status:** Not started. Needs own plan + test harness + turn-scenario benchmark.

### 3. ~~Ground texture rendering fix~~ — **done via GPU cache bump**

Was +9 FPS cost; the 128 MB cache bump eliminated it for free. Ground texture now fits in cache and renders essentially free. No further work needed.

### 4. ~~State-push pipeline refactor~~ — **largely eliminated by GPU cache bump**

Earlier decomposition attributed ~10 FPS to "state-push overhead." Post-cache-bump, real-GPS ceiling is 57.9 FPS vs. the no-input 58 FPS hardware ceiling — the "state-push overhead" is now essentially zero. The cost was cache thrash, not the pipeline itself. Deferred indefinitely.

### 5. Aggregated small-draw optimizations — **deprioritize, likely eliminated too**

Post-cache-bump ceiling equals all-skips ceiling (within 1 FPS). Draw ops are effectively free now that their textures stay cached. Not recommended unless a specific scenario reopens this.

## What we are NOT doing

Explicit non-goals, so we don't accidentally wander back into them:

- **Chasing 60 FPS.** Hardware + state-push pipeline caps us at ~47 with real GPS attached. 60 FPS is not a realistic target on this tablet.
- **Panel transparency changes.** Measured noise. Not a cost.
- **Compositor animation loop disabling.** Measured noise. Not a cost.
- **Individual draw-op micro-optimizations.** Below the noise threshold for this ceiling.
- **Vector map / OSM road layer for perf reasons.** Background map representation is not a bottleneck. (Could still be valuable as a *product feature* later, but not a perf lever.)
- **Bitmap blit optimization within current architecture.** The coverage blit cost is fundamental to the bitmap-based approach; the fix is a different representation, not a faster bitmap.

## Open questions / future diagnostics

Things we haven't measured yet that might matter:

- **Headland-turn FPS:** we have no number for FPS *during* an actual turn. Autosteer engaged, tractor crossing boundary, YouTurn path generation running. Would validate/invalidate priority #2's expected impact.
- **Coverage growth over time:** all measurements used a field with modest pre-existing coverage (4,910 cells loaded from disk). Does FPS degrade as more coverage accumulates over a multi-hour job?
- **Large field stress:** `test` field is small. A full 80-acre field with dense boundaries and tracks may show different costs.
- **iPad perf:** deferred pending Android characterization. Now that Android is characterized, revisit.
- **Coverage write cost during heavy section activity:** pipeline writes pixels to SKBitmap every tick when sections are on. Not measured in isolation.

## Methodology + raw data

Full methodology, diagnostic infrastructure details, raw measurement tables,
and debunked-hypothesis reasoning live in `Plans/FPS_DIAGNOSTIC_PLAN.md`.
That document is the measurement record; this document is the strategy.

## Linked implementation plans

Individual tactical plans are created as each prioritized item is picked up.

- [ ] `Plans/PERF_01_COVERAGE_REWRITE.md` — not yet drafted
- [ ] `Plans/PERF_02_YOUTURN_TO_SERVICE.md` — not yet drafted
- [ ] `Plans/PERF_03_GROUND_TEXTURE.md` — not yet drafted
- [ ] `Plans/PERF_04_STATE_PUSH_REFACTOR.md` — not yet drafted (may never be)
