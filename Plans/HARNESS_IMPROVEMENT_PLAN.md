# Scenario harnesses: what the bug reports teach

**Status:** draft, 2026-11-08. For Chris and Renato. Not public yet.

## 1. Why this plan

Seven scenario harnesses run every night (field, steer wizard, vehicle & tool, machine module,
GPS/NTRIP, field job, ISOBUS). In their first two weeks they found a dozen real defects, and in
the last 24 hours they caught a regression (#720) and a long-standing U-turn failure (#721)
before any tester did.

All 107 `bug` issues filed since June 2026 were read against those seven harnesses, asking of each
one: *which harness should have seen this, and what was it missing?* The answer is consistent.
The harnesses drive the middle of a job well. Testers find their bugs at the edges of a job, when
they do something in the middle of an action, and in parts of the app no harness touches.

This plan lists the gaps, what closes each one, and an order. Each item is small; most are
additions to a harness that exists.

## 2. What the 107 reports say

| Where the bugs are | Count | Examples | Harness today |
|---|---|---|---|
| Web client only: menus, dialogs, buttons, touch | ~20 | #695, #683, #675, #621, #571, #539, #522, #498, #479, #465, #399 | none |
| U-turns on real fields | ~20 | #692, #588, #585, #577, #570, #544, #523, #418, #411, #306, #272, #163, #156, #71 | field job (AB lines only) |
| Sections and coverage | ~10 | #707, #679, #681, #676, #451, #325, #175, #173, #89 | field job, machine module |
| Steering settings that don't reach the module or steering | ~12 | #99, #101, #103, #105, #106, #108, #110, #112, #154, #170, #240, #241 | steer wizard (wizard only) |
| GPS, heading, IMU | ~8 | #396, #353, #313, #266, #225, #157, #158, #328 | GPS/NTRIP, vehicle & tool |
| Tracks: curves, boundary curves, pivots | ~6 | #694, #590, #492, #450, #365, #104 | none (every job is an AB line) |
| Settings, units, data loss | ~6 | #107, #104, #112, #65, #398, #176 | vehicle & tool (restart compare) |
| Tramlines | 4 | #451, #541, #675, #111 | none |
| NTRIP | 3 | #443, #285, #282 | GPS/NTRIP (now covers #285) |
| Platform, crashes, performance | 5 | #73, #28, #394, #169, #604 | hot-path budgets (pipeline only) |
| Two clients | 1 | #397 | none |

Three patterns stand out:

1. **Whole in-scope areas with no harness.** The web client, tramlines, curve tracks, manual
   U-turns, flags and recorded paths, hotkeys, imperial units, a second browser. Together they are
   about a third of all reports.
2. **Mid-action changes.** #585 (U-turn settings changed during a turn), #577 (skip rows changed
   resets the direction), #588 (reversing out of a turn), #272 (manual turn exits early), #93
   (accidental nudge). The careless operator makes most of its mistakes *before* the action.
3. **The harness rigs are not the testers' rigs.** No field job runs a tool with a side offset,
   yet offset tools gave #591, #446, #679, #174 and #283. Graeme's dumps are an offset rig with
   skipped rows.

Two things the harnesses did right, to keep on purpose:

- #721 was found only because the harness makes its AB line the way a tester does, a typed line
  with B 100 m from A, not a synthetic line laid across the field. Rule: build tracks, boundaries
  and headlands through the operator's own commands, never from geometry.
- `CORRECTIONS-UNUSED-SILENT` in the GPS/NTRIP harness is #285 (Renato, October) found again by
  machine. The harness learned from a report. That loop should be the habit: every confirmed bug
  gets the question "which harness, and what did it lack?"

## 3. The items

Each item: what to add, which reports it would have caught, size.

### 3.1 Field job: the testers' rigs and tracks

- **An offset-tool job** (the side-shifted mower and the trailed offset mower from `Machines.cs`
  are there already; no job uses them) with U-turns and sections. Would have caught #591, #446,
  #679, #174, #283.
- **A curve-track job**: a recorded curve as the pass. **A boundary-curve job**: the pass taken
  from the boundary. Would have caught #694, #450, #492, #365, and the pivot family (#590) if a
  pivot job is added later.
- **Skip rows and a wide tool** on one job: #410, #544 (both reported, both already in the
  careless list in part; make them careful-operator jobs so they run every night as normal use).

Size: a day. All of it is job definitions, not harness code.

### 3.2 Field job: mid-turn interventions

Extend the careless list with things done *during* a U-turn:

- change the radius, the distance from boundary, the style and the skip rows while the turn is
  executing (#585, #577);
- reverse during the turn, and reverse out of a turn just finished (#588);
- nudge during the turn (#93);
- swap the direction twice; disengage AutoSteer halfway round and re-engage;
- a **manual U-turn** from the buttons, at both ends and mid-pass (#272, #163; no harness makes one
  today).

Size: a day. The operator model has the hooks (C3 reverses, C5 toggles skip rows).

### 3.3 Vehicle & tool: a settings-effect sweep

The largest early class (#99, #101, #106, #110, #111, #112, #105) was settings that changed
nothing. The harness checks that settings *survive* a restart (416 values compared); nothing
checks each one *does something*. Add a sweep: for every guidance and AutoSteer setting, flip it
and assert a measurable change in the steer output, the PGN bytes, or the sections. One scenario,
one table in the report.

Size: a day. Catches the whole class for good, including new settings.

### 3.4 A dump-replay harness

Every bug report dump has the reporter's settings, field, tracks, coverage, and the last 5
minutes of GPS at 10 Hz (sentence type, pre-fusion heading, differential age). Today we read
them by hand. A harness that loads a dump and drives the pipeline from its GPS log, with the
field scenarios' invariants and the field job's checks switched on, makes every report a
permanent regression case, on real receiver data instead of the bicycle model. That is where
#157 (heading wrong when stopping), #266, #396 and #353 (IMU and roll) live.

Seed it with the dumps we have: Graeme's (#544, #570, #577, #585, #588, #590, #591, #692–#695),
gunicsba's (#679, #681, #683), Renato's (#306, #313, #325). The GPS/NTRIP harness already has the
rule "captures only, no synthesis" for receivers; this is the same rule for whole incidents.

Size: two to three days. The biggest item and the one with the longest life.

### 3.5 A web-client harness

About twenty reports are things only a browser shows. The headless recipe used to verify fixes
by hand (the Desktop head `--headless`, Playwright, `transport.send`) is the harness; it needs
the careful/careless operator model and a report. Drive the in-scope flows: tracks (create every
way, rename, hide, delete), boundary and headland, tram set-up, field open/close/resume, the
section bar, the U-turn buttons, and a second browser that must change nothing (#397).

Would have caught #675, #621, #683, #695, #522, #399, #219, #479, #146. Not touch (#465) or
mouse-wheel (#87): those need a device.

Size: three days for the first version; then it grows with each report, like the others.

### 3.6 Imperial units

Run the careful field job once in imperial, and assert the same physical result as the metric
run. #65 and #112 were unit conversions that only showed in imperial. Also a careless step: type
a value in the wrong unit and check what the module receives.

Size: half a day.

### 3.7 Hot-path budgets for the host loop

#604 (every job file parsed ten times a second) and #169 (screen freezes) are host-loop costs.
The budgets cover the GPS pipeline and the 100 Hz control tick, not `MainViewModel` and the
projectors. Extend the budget to one 10 Hz broadcast with a field open.

Size: half a day.

### 3.8 Harness realism checks

Two places where a harness may be asserting something its model can't support:

- **Valve delay.** `SPRAYED-IN-HEADLAND` appeared the night #714 sent the look-ahead request to
  the module early. The emulated sprayer opens its valves the instant it gets the bit, so the
  harness now measures an early *command* as early *product*. Give the emulated sprayer a valve
  time (AgOpenGPS testers set 0.5–1.5 s) and the look-ahead the harness configures; then the
  finding means something.
- **The operator who never takes over.** `RAN-OFF` after a refused turn is correct app behaviour
  since #698 (the sound, #716's message); the finding should say "no turn, operator warned" and
  stop the job, not count it as the app driving out.

Size: half a day. Do this first, before the next nightly report is read.

## 4. Order

| # | Item | Size | Why here |
|---|---|---|---|
| 1 | 3.8 realism: valve delay, refused-turn handling | ½ day | tomorrow's report is wrong without it |
| 2 | 3.1 offset-tool, curve, boundary-curve jobs | 1 day | the testers' rigs; three open reports in this area |
| 3 | 3.2 mid-turn interventions, manual U-turn | 1 day | the reporters' pattern |
| 4 | 3.3 settings-effect sweep | 1 day | closes the largest early class permanently |
| 5 | 3.4 dump-replay harness | 2–3 days | every past and future dump becomes a test |
| 6 | 3.6 imperial pass | ½ day | cheap, two known bugs |
| 7 | 3.7 host-loop budget | ½ day | cheap |
| 8 | 3.5 web-client harness | 3 days+ | the largest gap, the most new code |

Items 1–4 and 6–7 are additions to harnesses Renato built and knows; they can go in parallel
with his other work. Item 5 is a new project and needs the dump format read from `Tools/unpack-dump.sh`.
Item 8 is the most work and should start when the others are running.

## 5. Rules for every harness

- Build tracks, boundaries and headlands through the operator's commands, never from geometry.
- Field speeds (6–12 km/h) and real sim pace for anything timing-related.
- A finding names which of: app defect, harness artifact, parity with AgOpenGPS, enhancement.
- Every confirmed bug is matched to a harness; if none would have seen it, the report says so and
  an item goes on this list.
- Reports are read before PRs are written ([nightly-reports-before-PRs]).

## 6. Not in this plan

- Touch and mouse-wheel behaviour on devices (#465, #87): a device matrix, not a harness.
- Android and iOS startup (#73, #28): the heads' own CI.
- Real receivers and real modules on the bench: Chris's bench board and the HAT, by hand.
