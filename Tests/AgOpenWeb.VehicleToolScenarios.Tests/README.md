# Vehicle & Tool scenarios: the careful and the careless operator

An end-to-end harness for the Vehicle & Tool settings: the tractor's measurements, the GPS
and roll setup, the implement (type, dimensions, sections, offsets), and the profiles that
keep them.

The whole backend runs in the test process, as the Desktop head boots it, on a temporary
`AGOPENWEB_DATA` folder (the same rig as `AgOpenWeb.FieldScenarios.Tests`). Settings go in
exactly as the web client sends them (`config.set|key:value`, `profile.*`).

A simulated **real machine** (`TruthMachine.cs`) drives it:

- a bicycle model at the pivot axle (the rear axle of a tractor, the front axle of a combine),
  steered by a motor with its speed and lag (Keya, Cytron, IBT-2, Danfoss valve);
- the implement on its real linkage: mounted front or rear, trailed (a towed axle following
  its drawbar), or tow-between with its tank;
- the antenna on the cab, which moves sideways when a cross slope tilts the machine;
- the receiver and IMU of the Boards simulator (`SystemSpec`): a UM982 or two F9Ps send
  `$PAOGI` with the dual heading and roll, an X20P or F9P sends `$PANDA` with the IMU's roll
  and heading (TM171, BNO085, BNO085 RVC) or none.

Its sentences go through AgOpenWeb's real parser, heading fusion, roll correction, guidance
and section control, and the steer angle AgOpenWeb computes steers the machine back. Every
field run (`FieldRun.cs`) makes a field with an AB line, starts a job with automatic
sections, types the operator's settings, and drives passes side by side with AutoSteer on,
turning by hand at the headland. Distances are measured on the real implement: how far it
ran from its pass line, the gaps and overlaps between passes, how far AgOpenWeb's implement
is from the real one, the steer angles sent, how long the sections were on.

Two operators:

- **Careful** (`CarefulOperator.cs`): five vehicles (`Machines.cs`: a Case Puma 180 CVX with
  a UM982, a vineyard tractor, a 300 hp tractor, an articulated 4WD, a combine) with the
  implements they pull (8 m mounted sprayer in 8 sections, 10 m trailed sprayer, 3/6/12 m
  front, rear, trailed and TBT, side-shifted mowers, hay rakes, a grain header), on flat
  ground and across a 6° slope, with every GPS, IMU and motor of the Boards simulator. Then
  every machine is saved as a profile, AgOpenWeb restarted, and each one loaded back.
- **Careless** (`CarelessOperator.cs`): about 45 mistakes driven on the real machine
  (measures in cm in a metre box, wrong signs, forgotten values, a dual antenna set up the
  wrong way, a roll sensor never calibrated or zeroed on a slope, section widths in metres,
  an overlap as wide as the boom, a trailed sprayer set as mounted, settings changed with
  AutoSteer on), about 60 impossible values typed into the settings, and profile names and
  picker buttons used the wrong way.

Some careless values cannot be typed in the web client as they are sent here; they are sent
anyway, because the host is the last line: any browser on the LAN can send any command.

## Running

```bash
dotnet test Tests/AgOpenWeb.VehicleToolScenarios.Tests/ --filter "FullyQualifiedName~VehicleToolScenarioTests"
```

About 25 minutes (careful about 9, careless about 15). CI only builds it; the full run is nightly on develop (`.github/workflows/vehicle-tool-scenarios.yml`, also run by hand from Actions), with the report kept as an artifact and in the run's summary. The report goes to `vehicle-tool-scenarios-report.md` in the test output
folder, or to `AGOPENWEB_SCENARIO_REPORT`. Other switches:

| Variable | Effect |
|---|---|
| `AGOPENWEB_SCENARIO_REPORT=path.md` | where to write the report |
| `AGOPENWEB_SCENARIO_OPERATOR=careful` | only one operator (`careful` or `careless`) |
| `AGOPENWEB_SCENARIO_ONLY=V-WB` | only drives whose code or name contains this (`settings`, `profiles` for those checks) |
| `AGOPENWEB_SCENARIO_VERBOSE=1` | the machine and AgOpenWeb side by side every 4 s, and every finding |

The test fails only when the harness itself breaks. The findings are the product: the
report lists each kind once, most serious first, then a table of every drive, the settings
and what the app kept of them, and every pass.

`SmokeTests.cs` holds an explicit single drive (the Puma with its sprayer) for working on
the harness; `VT_SLOPE`, `VT_GPS` and `VT_IMU` pick the slope and the receiver.

## Severity

| Severity | Meaning |
|---|---|
| Safety | AutoSteer takes the machine off its pass, swings it across the pass, or holds the wheels at the stop |
| Bug | wrong data or wrong behaviour the operator can hit (a careful setup off its line, a profile that comes back different) |
| Inconsistency | two parts of the app disagree (a value that changes when the profile is loaded again) |
| Robustness | an impossible value accepted and kept, with no word to the operator |
| Improvement | it works, but the operator is not told, or could be helped |

## What the harness does not model

Ground the machine slides on, a steering wheel angle sensor and its calibration (the steer
wizard's job), GPS noise and RTK float, curves and U-turns under AutoSteer. The passes are
straight; the headland turn is driven by hand.
