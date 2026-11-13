# Field job scenarios: the careful and the careless operator

An end-to-end harness for a whole job: AgOpenWeb drives a field from the first pass to the
last with AutoSteer, automatic U-turns and automatic sections, and the harness measures what
the implement really did on the ground.

The whole backend runs in the test process, as the Desktop head boots it, on a temporary
`AGOPENWEB_DATA` folder, with its real UDP service on port 9999 (the same rig as the machine
module harness). Every step is a command exactly as the web client sends it.

Three things stand around it:

- **A real machine** (`TruthMachine.cs`, from the Vehicle & Tool harness): a bicycle model at
  the pivot axle steered by a motor with its speed and lag, the implement on its real linkage
  (mounted, or trailed on a towed axle), the antenna on the cab. Its RTK fixes go to AgOpenWeb
  at 10 Hz, and the steer angle AgOpenWeb sends the steer module (PGN 254) steers it back.
- **An emulated machine module** (`EmulatedSprayerModule.cs`, the Sprayer_ESP32 firmware's PGN
  handling) on 127.0.0.1:8888: it keeps PGN 235/236/238 in its flash, answers hellos, and opens
  the relays PGN 239 (and 229 above 16 sections) asks for, shutting them 2 s after the last one.
  An implement with more than 16 sections drives relay i from section i, as the Seeder_ESP32.
  Behind each relay is a **valve with its own time**: product reaches the nozzles 1.0 s after
  the relay closes and stops 0.5 s after it opens (`JobSpec.ValveOnS` / `ValveOffS`, AgOpenGPS's
  default look-ahead, which is what it assumes a sprayer takes). Look-ahead is there to cover
  that time, so the careful operator sets it to the valves' (`LookAheadMatchesValves`); C8 and
  C16 don't.
- **The truth coverage** (`TruthGrid.cs`): a 0.2 m grid over the field. Each fix, every section
  whose valve is open sweeps the strip between its previous and current ends into the grid.
  The sweep follows the nozzles, not the relays: since #714 the relays rightly lead the screen
  by the look-ahead. So the grid holds where chemical really went, counted per pass, and knows for each cell
  whether it lies inside the boundary, inside the headland line, or inside an obstacle.

A job (`JobRun.cs`): AgOpenWeb boots fresh; the operator types the Vehicle & Tool settings
and the relay functions, imports the field from KML (the five real field shapes of
`TestData/fields.json`, with or without their obstacles), picks the AB line, makes the headland
from the whole boundary, switches section control in the headland and U-turns on, starts a job,
puts the tractor at the first pass facing in, engages AutoSteer and sets the sections to Auto.
From there AgOpenWeb drives. The run ends when AgOpenWeb reports the end of the field (or
runs out of the field, or never gets there).

At the end (`Jobs.Judge`), the truth grid is compared with the field and with AgOpenWeb's own
coverage map (`ICoverageMapService.IsPointCovered`), and the implement's ends on each pass with
its neighbours':

| Check | Finding |
|---|---|
| Ground never sprayed between neighbouring passes, widest strip (wedges at the pass ends after AgOpenWeb said "Headland too narrow for this U-turn" are a note: the turn leaves its pass inside the headland line) | `MISSED-STRIPS` |
| The field reported done with ground left (inside the worked passes, or on a side of the field / in passes skipped), or never reported done | `FIELD-LEFT-UNDONE`, `FIELD-NOT-FINISHED` |
| The operator took over before any U-turn (a turn blocked or missed at the first end): the job never got past its first pass, whatever the coverage checks say about nothing sprayed | `JOB-NEVER-TURNED` |
| "End of field reached" said, then a U-turn onto another pass after all (the operator drives on) | `END-OF-FIELD-TOO-EARLY` |
| Sprayed outside the boundary, inside an obstacle, inside the headland further past its line than a section's width (a section is in the headland only with both ends in it, as AgOpenGPS, so where a pass meets the line at an angle up to a section's width lands past it) | `SPRAYED-OUTSIDE-BOUNDARY`, `SPRAYED-IN-OBSTACLE`, `SPRAYED-IN-HEADLAND` |
| AgOpenWeb's map painted where no valve was open, or blank where one was | `MAP-SHOWS-UNSPRAYED`, `SPRAYED-NOT-ON-MAP` |
| AgOpenWeb's area covered once against the truth | `APP-AREA-OFF` |
| Sprayed twice beyond the overlap asked for | `DOUBLE-SPRAYED` |
| A U-turn that took the tractor past the boundary (an implement end past it is a note: *Distance from boundary* keeps the tractor inside, not the implement, as in AgOpenGPS, kept on purpose) | `TURN-OUTSIDE-BOUNDARY` |
| U-turns missed or cancelled, AutoSteer dropping out | `TURN-MISSED`, `AUTOSTEER-DROPPED` |
| The first U-turn arrow pointing at the edge the operator started from | `FIRST-TURN-TOWARD-EDGE` |
| The screen's sections and the nozzles disagreeing | `VALVES-NOT-FOLLOWING`, `SECTION-WITHOUT-RELAY` |
| AgOpenWeb found no U-turn that fits and asked the operator to take over (the job stops there, as at a missed turn's warning; a turn blocked by a hard boundary or an obstacle is a note) | `TURN-REFUSED` |
| The tractor more than 25 m out of the field with no such warning | `RAN-OFF` |

Two operators:

- **Careful** (`CarefulOperator.cs`): the Case Puma 180 CVX with a UM982 and TM171; the 8 m
  mounted sprayer in 8 sections on Olival (with and without its obstacles), Quinta Torta and
  Tapada da Casa; the trailed 19-section seed drill on Baetas; a trailed 21 m cultivator on
  Couto Ribeira. Headland twice the implement wide, "Off outside boundary" on.
- **Careless** (`CarelessOperator.cs`): the same jobs with one thing wrong or done at the wrong
  moment. C8 sets look-ahead 1 s longer than the valves; C16 sets it to 0 s with the same
  valves (AgOpenWeb's default before it took AgOpenGPS's 1.0 s / 0.5 s).

When AgOpenWeb says "No U-turn fits inside the boundary at this end - take over manually" or
"U-turn blocked: ... take over manually", or that a U-turn was missed ("U-turn missed: ... Take
over manually"; "No U-turn can be made from the headland on this pass. Take over manually at the
end of the field", #761), the operator takes over and the job stops, as a person would
(`TURN-REFUSED` when no turn fits; a blocked turn is the app refusing a swing into a hard boundary
or an obstacle, as it must (#698), noted; a missed turn is `TURN-MISSED`, expected where the
operator caused it, as T10's AutoSteer off in the headland). A warning for "the end of the field" comes mid-pass: the operator
drives the pass to its end (the implement out of the cultivated area with none ahead, or the
tractor at the boundary) and takes over there. The ground left from there is the consequence of
that one finding, not `FIELD-NOT-FINISHED` too. A tractor that drives on after such a warning is
AgOpenGPS behaviour (leaving the field does not switch AutoSteer off, #494), so `RAN-OFF` is a
run-off with no warning. Before, AutoSteer was left to drive on and the job ended as a run-off.

## Running

```bash
dotnet test Tests/AgOpenWeb.FieldJobScenarios.Tests/ --filter "FullyQualifiedName~FieldJobScenarioTests"
```

A job is a field driven at about 12x real time: a few minutes for the small field, tens of
minutes for the whole set (the careful operator's jobs about 80 minutes on a GitHub runner, J6
with its 82 obstacles most of it). CI only builds it; the full run is nightly on develop
(`.github/workflows/job-scenarios.yml` with the path, rig, turn intervention and imperial jobs, also run by hand from Actions), the careful and the
careless operator as two jobs side by side, each with its own report. The report goes
to `field-job-scenarios-report.md` in the test output folder, or to `AGOPENWEB_SCENARIO_REPORT`.

**One run at a time per machine.** The emulated module binds UDP 8888, so two runs side by side
hear each other's section commands and both measure wrong valves without an error. A second run
of any fixture here (field, path, rig, turn or imperial jobs) fails at once on a lock file
(`agopenweb-field-job-scenarios.lock` in the temp folder) that every fixture takes.

| Variable | Effect |
|---|---|
| `AGOPENWEB_SCENARIO_ONLY=J1,J3` | only these jobs |
| `AGOPENWEB_SCENARIO_OPERATOR=careful` or `careless` | one operator |
| `AGOPENWEB_SCENARIO_PACE=8` | real milliseconds per simulated fix (0.1 s): 8 is about 12x |
| `AGOPENWEB_SCENARIO_TRACE=path.log` | every step and pass to this file while the run goes |
| `AGOPENWEB_SCENARIO_VERBOSE=0` | quiet console |
| `AGOPENWEB_SCENARIO_DUMP=folder` | a picture of each job (PPM: field, headland, obstacles, sprayed ground, the track) |
| `AGOPENWEB_SCENARIO_DEBUGLOG=YouTurn` | AgOpenWeb's debug log lines of these categories in the timeline |

The tests fail only when the harness itself breaks. The findings are the product: the report
lists each kind once, most serious first, then each job with what was measured and its
timeline.

### Short runs: confirm a fix in a minute

A whole job is 5 to 80 minutes. To confirm a fix on the first passes, or on the passes where a
finding was:

```bash
AGOPENWEB_SCENARIO_TURNS=2 AGOPENWEB_SCENARIO_ONLY=J1 AGOPENWEB_SCENARIO_OPERATOR=careful \
  dotnet test Tests/AgOpenWeb.FieldJobScenarios.Tests/ -c Release --filter "FullyQualifiedName~FieldJobScenarioTests"
```

`AGOPENWEB_SCENARIO_TURNS=N` stops each job as its (N+1)th U-turn starts (N turns and the pass after
them worked); `AGOPENWEB_SCENARIO_MINUTES=N` after N simulated minutes (the lap and pivot jobs, which
have no U-turns). The job ends as an early end does: the report says "a short run", and the checks
that need the whole field (finished, left undone) are not made; the rest are judged on the ground
covered so far. J1 with two turns: 43 s.

To rework the passes where a finding was, start there: `AGOPENWEB_SCENARIO_FROM_PASS=28` puts the
tractor at the start of pass 28 (AgOpenWeb's pass number, as the report's turn lines and findings
give it), driven the way the whole job drives it (the passes alternate from the first), with the
passes before it unworked; `AGOPENWEB_SCENARIO_TURNS=4` then stops after pass 32. Rig J8 from pass
-8 with two turns: 42 s. What depends on the passes before (a strip against the pass before 28, the
coverage there) needs the start a pass or two earlier. Jobs whose passes don't alternate one by one
(skip rows) start the right way only on a pass the whole job reaches after an even number of turns. On GitHub, `job-scenarios.yml`'s `only` and `turns` inputs
do the same. Work out a finding with a unit test first; a short run confirms the fix in the real
backend, the whole nightly last.

## Path jobs: curves, laps, a water pivot, contour, a recorded path

`PathJobScenarioTests` runs the same rig on lines that are not straight (`PathJobs.cs`), with
the careful operator, the Case Puma and the 8 m mounted sprayer:

| Job | Field | Line |
|---|---|---|
| P1 | Olival | Boundary Curve: the outer boundary as a closed loop, no headland, worked lap by lap from the outside in |
| P2 | Olival | Bnd. Curve along the west boundary (vertices 20 to 33, the narrow south-western end), headland 16 m, U-turns |
| P3 | Pivot | Water pivot from the tower's lat/lon |
| P4 | Pivot | Water pivot recorded by driving a third of the outer circle |
| P5 | Pivot | Contour: the outer circle by hand, then Contour and AutoSteer |
| P6 | Olival | Contour |
| P7 | Olival | Record Path by hand, then play it back with AutoSteer |

`Pivot` is made up, not a real field: a circle of 150 m round a pivot tower whose 4 m pad is an
obstacle (`ScenarioFields.Pivot`).

Each line has its own pass frame (`PassFrame.cs`: straight, polyline, circle), so the checks
measure across and along the passes as on an AB line. Where AgOpenWeb makes no U-turn (a closed
loop, a pivot, contour) the operator (`PathOperators.cs`) taps "move one pass" (or, on contour,
takes the wheel and moves a width in) at the end of each lap, waits for AutoSteer to settle on
the new pass, and stops when no lap is left: when the ground deep enough for the next lap is
gone, or falls into pieces one closed line cannot go round (`DepthGrid`). What no lap reaches is
counted apart (left for an AB line or by hand), and so are the laps' corners, where an inner lap
pulls away from the outer one. AgOpenWeb's clock runs on the job's simulated time (`SimClock.cs`):
contour rebuilds its line every 2 s of it. When AgOpenWeb warns that the contour line ahead can't
be driven as a lap (it turns back tighter than the rig turns, runs onto ground already sprayed, or
ends: where the field narrows), the operator takes over, drives that stretch of the lap by hand and
engages again past it. Driving by hand, the operator looks ahead on the line that keeps its
distance from every side of the reference, so a sharp corner is driven round, not overshot.

Findings these jobs add:

| Check | Finding |
|---|---|
| The implement off the pass it should be on (the reference offset by whole widths) | `OFF-ITS-PASS` |
| "Move one pass" refused, or AutoSteer never settling on the next lap | `LAP-CHANGE-REFUSED`, `LAP-CHANGE-NOT-SETTLED` |
| The pivot circle away from the tower or the field's edge | `PIVOT-CIRCLE-OFF` |
| AgOpenWeb's lap line running out of the field (checked before each lap, as the operator sees it) | `LAP-LINE-LEAVES-FIELD` |
| Contour not engaging, its line lost, or AutoSteer taking the tractor off the lap it follows | `CONTOUR-NOT-ENGAGED`, `CONTOUR-LINE-LOST`, `CONTOUR-LEFT-THE-LAP` |
| A recorded route not played, not followed, or not finished | `RECORDED-PATH-NOT-PLAYED`, `RECORDED-PATH-NOT-FOLLOWED`, `RECORDED-PATH-NOT-DONE` |
| With no headland, the strip along the boundary never sprayed | `EDGE-NOT-SPRAYED` |

```bash
dotnet test Tests/AgOpenWeb.FieldJobScenarios.Tests/ --filter "FullyQualifiedName~PathJobScenarioTests"
```

About half an hour for the seven jobs; nightly on develop (`.github/workflows/job-scenarios.yml`, its `path` job).
The same variables pick jobs (`AGOPENWEB_SCENARIO_ONLY=P1,P3`), trace and dump; the report goes
to `path-job-scenarios-report.md`, or to `AGOPENWEB_SCENARIO_REPORT`.

## Rig jobs: an implement to one side, a curve driven, skip rows

`RigJobScenarioTests` runs the field job with the rigs and tracks testers use and the jobs above
do not (`RigJobs.cs`), with the careful operator:

| Job | Field | Rig and line |
|---|---|---|
| J8 | Olival | 3 m mower mounted at the back, shifted 2.5 m right; AB line, the first pass along the edge with the mower on the fence side |
| J9 | Olival | 3 m trailed mower, its cutting width 2.5 m left of the drawbar |
| J10 | Quinta Torta | The field's curve driven by hand (Curve > Record going in, Finish going out), then worked from it with the 8 m sprayer |
| J11 | Couto Ribeira | Trailed 21 m cultivator, headland 42 m, radius 10 m, skip rows on (alternative, 1 row) |
| J12 | Olival | J8's mower on the edge pass: the mower in the cultivated area, the tractor in the headland, for one pass |
| J13 | Olival | Renato's Case Maxxum 135 (42°) and 8 m front sprayer in 5 unequal sections (2/1/2/1/2 m), with the suggested-settings page's U-turn values (radius 4 m, distance 6.3 m) |
| J14 | Baetas | The same tractor with a semi-mounted 3 m no-till drill (trailed for AgOpenWeb, the discs ahead of its wheels, a 5 cm gap between passes), Keyhole turns, headland 18 m |
| J15 | Quinta Torta | A combine (pivot on the front axle, rear-wheel steering) with a 6 m header |
| J16 | Olival | A 6 m seed drill towed behind its seed tank (TBT: tractor, tank, drill) |
| J17 | Tapada da Casa | A trailed manure spreader, a 12 m bout centred 4 m behind its axle |
| J18 | Olival | A 5 m trailed disc harrow on wheels in the middle of its frame |
| J19 | Olival | A 6-furrow mounted reversible plough (2.7 m) at 7 km/h: it keeps to one side of the field whichever way it is driven, which AgOpenWeb's tool offset can't say |
| J20 | Olival | The J14 tractor and settings (Keyhole, radius 4.3 m) with a 3.2 m seeding roller towed from a bar on the lift arms, which swings 60° each way before it stops |
| W1 | Baetas | The 8 m sprayer with the skip button on *ignore worked tracks* (the snake order), 1 row: the narrow end leaves passes too short to land on, so the order's next pass sometimes has no room and the nearest open pass is taken |
| W2 | Olival, hard boundary | The same mode on J7's hard fence (some ends fit no turn); a careless operator taps the U-turn arrow mid-pass and the row button (2 rows) after the seventh turn |

On these jobs the implement is also measured against the pass it should be on (`OFF-ITS-PASS`,
from 30 s after each turn), and where each U-turn's exit leg put it: with an implement to one side
the tractor's own lines are not evenly spaced (8 m and then -2 m apart for J8), so a turn built
from the wrong side lands beside the pass and AutoSteer pulls the implement across in the headland.

| Check | Finding |
|---|---|
| A U-turn's exit leg more than a quarter of a pass (1 m at least) beside the pass the implement then works | `TURN-EXIT-OFF-PASS` |
| The curve AgOpenWeb keeps away from where the implement was driven | `RECORDED-CURVE-SHIFTED` |
| W1, W2: a U-turn onto a pass already worked, with skip worked passes on | `TURN-ONTO-WORKED-PASS` |
| On the edge pass, no U-turn: AutoSteer driving on past the boundary with no warning (Safety), or AgOpenWeb's warning and the operator taking over at the pass's end (Bug) | `EDGE-PASS-NO-TURN` |
| A reversible plough shown across from where it works (no setting for an offset that keeps to one side of the field) | `REVERSIBLE-PLOUGH-NO-SETTING` |
| Trailed and TBT, every job of the harness: the hitch angle (tractor to drawbar) reaching the angle where the drawbar touches a rear tyre, from the tractor's hitch, track and tyre size (`RearTyreRadius`, `RearTyreWidth`) and the drawbar's width where it passes the tyres (`DrawbarWidth`, 0.6 m by default for an A-frame), seen from above with the tyre's whole radius; or the implement's own swivel stop (`SwivelStopDeg`) when that comes first | `DRAWBAR-HITS-WHEEL` |

```bash
dotnet test Tests/AgOpenWeb.FieldJobScenarios.Tests/ --filter "FullyQualifiedName~RigJobScenarioTests"
```

About an hour and a half; nightly on develop (`.github/workflows/job-scenarios.yml`, its `rig` job). The report goes to
`rig-job-scenarios-report.md`, or to `AGOPENWEB_SCENARIO_REPORT`.

## Turn interventions: an operator who acts during a U-turn

`TurnInterventionScenarioTests` runs J1 (Olival, the 8 m sprayer) with an operator who does one
thing while a U-turn is planned or under way (`TurnInterventions.cs`), the reporters' pattern:

| Job | What the operator does |
|---|---|
| T1, T2, T3 | Changes the U-turn radius (8 to 12 m), the distance from boundary (2 to 6 m) or the style (Sagitta to Keyhole), 10 s into the second turn |
| T4 | Sets skip rows during a turn (#585): the next turn must skip, onto the side not yet worked |
| T5 | Sets skip rows with the turn already planned (#410, #577): the turn must go two passes over, to the side it was going |
| T6 | Taps the U-turn arrow twice before a turn: the turn must be as it was |
| T7 | Nudges the line 50 cm during a turn (#93) |
| T8 | Reverses 7 m in the middle of a turn (#588), then drives on |
| T9 | Reverses out of a turn just finished (#588) |
| T10 | Switches AutoSteer off half way round a turn and on again (AgOpenWeb drops the turn; re-engaging should build it again) |
| T11 | U-turns off: every turn made with the manual buttons (#272, #163), on a 26 m headland the turn fits in from the headland line; a manual turn that would leave the field is refused (as an automatic one, #698), so one that leaves it is `MANUAL-TURN-LEFT-FIELD` (Safety) |
| T12 | A manual U-turn in the middle of a pass, then automatic turns (the rest of that pass is left on purpose) |

Each turn is watched from the action to 15 s after it ends: the path AgOpenWeb follows, the side
it turns to, the pass the implement lands on.

| Check | Finding |
|---|---|
| The turn dropped, or its path moved more than 1 m within 25 m of the tractor | `TURN-LOST`, `TURN-CHANGED-MID-TURN` |
| The turn going to the other side, or landing on another pass than planned (or than the change asks for); skip rows turning back over passes already worked | `TURN-SIDE-CHANGED`, `TURN-LANDED-ELSEWHERE`, `SKIP-NOT-APPLIED`, `SKIP-TURNS-BACK` |
| Reversing: the turn made again, or the pass lost | `TURN-REPEATED`, `PASS-LOST-REVERSING` |
| Manual turns refused, handing back to the line earlier than the automatic ones (4 m of arc left, more than 10° beyond it), or landing elsewhere | `MANUAL-TURN-REFUSED`, `MANUAL-TURN-ENDS-EARLY`, `MANUAL-TURN-WRONG-PASS` |
| After a manual turn mid-pass, the automatic turns not going on to the next pass | `PATTERN-LOST` |

```bash
dotnet test Tests/AgOpenWeb.FieldJobScenarios.Tests/ --filter "FullyQualifiedName~TurnInterventionScenarioTests"
```

About half an hour; nightly on develop (`.github/workflows/job-scenarios.yml`, its `turn` job). The report
goes to `turn-intervention-scenarios-report.md`.

## Imperial units: the same job typed in feet and inches

AgOpenWeb keeps and sends everything in metric; imperial is a conversion in the web client, made
when a value is typed and when a box is filled (`js/core.js`, #65). `ImperialJobScenarioTests`
(`ImperialJobs.cs`) opens the real web client of the job's own backend in headless Chromium
(Playwright), switches it to imperial in App Settings, and types every setting that has a unit
into its own box, as the box shows values (feet to two decimals, inches to one); the headland in
the field builder's box. Settings without a unit go as config.set, as the page's buttons send them.
The browser is closed before the job starts; the job is then driven as every field job.

| Job | What the operator does |
|---|---|
| U0 | J1 in metric: the reference |
| U1 | J1 typed in feet and inches |
| U2 | J1 in imperial with centimetres typed into the inch boxes (100 for a 1 m section) |

| Check | Finding |
|---|---|
| A value typed in imperial never reaching AgOpenWeb, or kept as another length | `IMPERIAL-VALUE-LOST`, `IMPERIAL-VALUE-OFF` |
| A box showing another number than was typed, once AgOpenWeb has echoed it | `IMPERIAL-BOX-CHANGES` |
| The job typed in imperial not coming out as the metric one (unsprayed share, U-turns, pass spacing, headland, the module's section widths) | `IMPERIAL-JOB-DIFFERS` |
| The machine module's section widths (PGN 235) not the ones AgOpenWeb keeps | `MODULE-WIDTHS-DIFFER` |
| A width in the wrong unit taken with nothing on the screen questioning it | `WRONG-UNIT-ACCEPTED` |

```bash
pwsh Tests/AgOpenWeb.FieldJobScenarios.Tests/bin/Debug/net10.0/playwright.ps1 install chromium   # once
dotnet test Tests/AgOpenWeb.FieldJobScenarios.Tests/ --filter "FullyQualifiedName~ImperialJobScenarioTests"
```

`AGOPENWEB_CHROMIUM=/path/to/chrome` uses another Chromium. About 20 minutes; nightly on develop
(`.github/workflows/job-scenarios.yml`, its `imperial` job). The report goes to `imperial-job-scenarios-report.md`.
