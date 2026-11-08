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
  the valves PGN 239 (and 229 above 16 sections) asks for, shutting them 2 s after the last one.
  An implement with more than 16 sections drives relay i from section i, as the Seeder_ESP32.
- **The truth coverage** (`TruthGrid.cs`): a 0.2 m grid over the field. Each fix, every section
  whose valve is open sweeps the strip between its previous and current ends into the grid.
  So the grid holds where chemical really went, counted per pass, and knows for each cell
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
| Ground never sprayed between neighbouring passes, widest strip | `MISSED-STRIPS` |
| The field reported done with ground left, or never reported done | `FIELD-LEFT-UNDONE`, `FIELD-NOT-FINISHED` |
| Sprayed outside the boundary, inside an obstacle, inside the headland | `SPRAYED-OUTSIDE-BOUNDARY`, `SPRAYED-IN-OBSTACLE`, `SPRAYED-IN-HEADLAND` |
| AgOpenWeb's map painted where no valve was open, or blank where one was | `MAP-SHOWS-UNSPRAYED`, `SPRAYED-NOT-ON-MAP` |
| AgOpenWeb's area covered once against the truth | `APP-AREA-OFF` |
| Sprayed twice beyond the overlap asked for | `DOUBLE-SPRAYED` |
| A U-turn that took the tractor or an implement end past the boundary | `TURN-OUTSIDE-BOUNDARY` |
| U-turns missed or cancelled, AutoSteer dropping out | `TURN-MISSED`, `AUTOSTEER-DROPPED` |
| The first U-turn arrow pointing at the edge the operator started from | `FIRST-TURN-TOWARD-EDGE` |
| The screen's sections and the valves disagreeing | `VALVES-NOT-FOLLOWING`, `SECTION-WITHOUT-RELAY` |

Two operators:

- **Careful** (`CarefulOperator.cs`): the Case Puma 180 CVX with a UM982 and TM171; the 8 m
  mounted sprayer in 8 sections on Olival (with and without its obstacles), Quinta Torta and
  Tapada da Casa; the trailed 19-section seed drill on Baetas; a trailed 21 m cultivator on
  Couto Ribeira. Headland twice the implement wide, "Off outside boundary" on.
- **Careless** (`CarelessOperator.cs`): the same jobs with one thing wrong or done at the wrong
  moment.

## Running

```bash
dotnet test Tests/AgOpenWeb.FieldJobScenarios.Tests/ --filter "FullyQualifiedName~FieldJobScenarioTests"
```

A job is a field driven at about 12x real time: a few minutes for the small field, tens of
minutes for the whole set. CI only builds it; the full run is nightly on develop
(`.github/workflows/field-job-scenarios.yml`, also run by hand from Actions). The report goes
to `field-job-scenarios-report.md` in the test output folder, or to `AGOPENWEB_SCENARIO_REPORT`.

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
