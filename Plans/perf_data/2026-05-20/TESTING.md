# PERF-05 Phase 1 Capture — Test Script (2026-05-20)

Driven by **[../../PERF_05_SUBSYSTEM_CHURN_AUDIT.md](../../PERF_05_SUBSYSTEM_CHURN_AUDIT.md)**.
Branch: `perf-05/instrumentation`.

Devices:
- **iPad Pro 12.9" 2nd gen** (UDID `d2fcb0323a90ad2954ab501f2603cd7573d99b2a`,
  bundle `com.agvaloniaagps.ios`)
- **Samsung Android tablet R52TB090VAK** (package
  `com.agvaloniaagps.android`)

Output: one log file per (platform, scenario) at
`Plans/perf_data/2026-05-20/<platform>/<scenario>.log`. The whole `*.log` set
is gitignored — summary goes on #403.

---

## Pre-flight (Claude drives)

1. ✅ Build iOS + Android on `perf-05/instrumentation` (done — both green).
2. Install fresh build on both devices.
3. Push 7 marker files to both devices via
   `Plans/perf_data/2026-05-20/push-markers.sh` (created alongside this doc).
4. Launch the app on iPad and Android.
5. Start syslog capture on both devices, filtered for `PERF` / `RenderBudget`,
   written to:
   - `Plans/perf_data/2026-05-20/ipad/_stream.log`
   - `Plans/perf_data/2026-05-20/android/_stream.log`
6. Verify the `[DiagFlags]` startup line appears in each stream — confirms
   all 7 markers were read.

After pre-flight Claude says **"ready — run S1 on both devices and tell me
'S1 done'"**.

---

## Per-scenario protocol

For every scenario:

1. **Claude** says: *"Ready to start SN — go."*
2. **You** put both devices into the SN state described below.
3. **You** type `SN go` in chat to mark the window start.
4. **You** hold the state, untouched, for **~30 seconds**.
5. **You** type `SN done` in chat to mark the window end.
6. **Claude** reads the stream logs, slices the window between your two
   timestamps, writes the slice to `…/<platform>/<scenario>.log`, and reports
   the per-subsystem numbers.

A 30-second hold gives ~30 emissions per subsystem at 1 Hz — enough for
stable averages.

---

## Scenarios

### S1 — App open, no field
- App freshly launched.
- No field opened.
- Do not interact. Phone-down both devices.

Expected baseline: pure composition + render-vehicle-only cost. Nothing else
should be active. Useful as the "everything else" floor.

### S2 — Field loaded, idle
- Open the **330 ha field**.
- No track active, no AB / curve set.
- Vehicle parked. No simulator.
- Hold steady.

Adds boundary + headland + coverage-bitmap-present (but no painting) to S1.

### S3 — Active AB track
- S2 state, plus: tap A, drive forward a few meters (sim off, so move manually
  by drag if needed — just need an A and a B point set), tap B.
- Active AB line visible on screen.
- No simulator running.
- Hold steady.

Adds the `DrawTrackSk` path with per-frame `SKPaint` allocs — the leading
suspect from the earlier iPad investigation.

### S4 — Active curve track (~500 points)
- S2 state, plus: start a curve recording, drive a short meandering loop in
  the simulator (or drag) until ~500 points are recorded, then stop recording.
- Curve becomes the active track.
- Simulator stopped after recording.
- Hold steady.

Probes per-point polyline rendering cost vs. the AB-line 2-point case.

### S5 — Simulator driving, sections off
- S2 state, plus: enable simulator, set ~10 km/h, drive straight.
- All sections OFF.
- No coverage being painted.
- Let it drive for the full 30 s window.

Adds GPS pipeline + state mirror at simulator rate. No coverage write.

### S6 — Simulator driving, sections on
- S5 state, plus: turn all sections ON. Coverage paints behind the vehicle.
- Continue driving.

Adds `CoverageMapService.AddCoveragePoint` per-tick cost on top of S5.

### S7 — Headland turn execution *(optional first pass)*
- S6 state, plus: AutoSteer engaged on an AB line that aims into the
  headland boundary. Let YouTurn fire.
- Capture covers approach + the turn + first few meters of the new pass.

YouTurn compute is on the UI thread (per `PERFORMANCE_STRATEGY.md` item #2);
this is the only scenario where `[YouTurnGuidance-PERF]` should report
non-zero cycles. May be hard to set up cleanly — skip on first pass if
needed and treat as a follow-up run.

### S8 — Real GPS attached, sections off *(optional first pass)*
- Connect the AiO board over UDP. Real GPS streams in at ~10 Hz.
- Simulator off.
- Vehicle stationary or slow-moving.
- No field load required (or use S2's 330 ha field).

Probes the real UDP RX + `AutoSteerService.ProcessGpsBuffer` path. Needs the
physical AiO hardware connected to the network the iPad/Android can reach;
skip if not set up today.

---

## Post-flight

1. Stop syslog captures.
2. Confirm scenario log files exist:
   ```
   Plans/perf_data/2026-05-20/ipad/S1.log
   Plans/perf_data/2026-05-20/ipad/S2.log
   …
   Plans/perf_data/2026-05-20/android/S1.log
   …
   ```
3. Claude builds the analysis table, posts it as a comment on #403, and
   appends an "iPad characterization" section to `PERFORMANCE_STRATEGY.md`
   per the plan's Outputs section.
4. Remove marker files from both devices so a stray production run won't
   keep emitting `*-PERF` lines:
   ```
   Plans/perf_data/2026-05-20/clear-markers.sh
   ```

---

## What the logs look like

Per-subsystem 1-Hz emit format (same across all subsystems for analysis
uniformity):

```
[RenderBudget]        frames=N ground=Xms/YB grid=Xms/YB cov=Xms/YB bnd=Xms/YB trk=Xms/YB veh=Xms/YB tot_alloc=…B/frame zoom=…
[StateMirror-PERF]    cycles=N us/cycle=X alloc/cycle=YB total_us=… total_alloc=…B window=…s
[GpsPipeline-PERF]    cycles=N us/cycle=X alloc/cycle=YB …
[TrackGuidance-PERF]  cycles=N us/cycle=X alloc/cycle=YB …
[YouTurnGuidance-PERF] cycles=N us/cycle=X alloc/cycle=YB …
[Coverage-PERF]       cycles=N us/cycle=X alloc/cycle=YB …
[UdpRx-PERF]          packets=N us/packet=X alloc/packet=YB …
[UdpTx-PERF]          sends=N us/send=X alloc/send=YB …
[AutoSteerRx-PERF]    cycles=N us/cycle=X alloc/cycle=YB …
[AutoSteerTx-PERF]    cycles=N us/cycle=X alloc/cycle=YB …
```

Necessary-vs-churn judgement applied per the audit plan's "Analysis
framework" section.
