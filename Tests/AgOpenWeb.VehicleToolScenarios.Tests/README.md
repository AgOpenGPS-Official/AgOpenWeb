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

## The settings sweep

`SettingsSweep.cs` flips every guidance, AutoSteer, machine, tool and vehicle setting the
operator can change, one at a time, on the careful Puma with its 8 m sprayer, and checks that
something measurable changed: the steer angle AgOpenWeb computes, the bytes of a PGN it sends
to the modules, the sections, or the pass. A setting that changes nothing is a finding
(`SETTING-NO-EFFECT`), whatever the reason, so a setting added tomorrow and never connected is
caught too. Each setting has a probe (`SweepProbes.cs`) that drives what it is about: a weave
across the line (steering; from a single antenna with a drifting IMU, or with none, for the
heading settings), a 6° side slope (roll), a reverse (dual, and single antenna with an IMU),
2 and 20 km/h, an RTK drop, a nudge, a manual U-turn, a free drive across the passes, section
strips worked and driven back over (also 0.2 m to the side), the steer module's switches. Each
probe is driven three times with the setting at its base value (the noise), then with the
setting changed; a change has to stand out of the noise, and a section switch is compared with
the same switch in the base runs (the switch off entering worked ground moves up to 4 m
between identical runs, the switch on leaving it hardly at all). A section that switched a
different number of times between the base runs can't be compared switch by switch: its row
says so ("noisy") and it is not judged. After each change the sweep reads the value back from
the store; one the app refused or clamped (`VehicleToolLimits`, a range) is reported as
`SWEEP-FLIP-REFUSED` with the app's reason, and its probe is not judged (it would read as no
effect). The probes run faster than real time, except the free drive (the pass is picked again
at most every 0.66 s of the wall clock) and either side of each end of a section strip (the
look-ahead and turn-off times are counted on the wall clock): the distance the look-ahead on,
look-ahead off and turn-off delay cover at 8 km/h, plus 5 m for the pose estimator's speed to
settle, and at least 15 m. A trailed implement comes up to each strip over 50 m in real time,
so its hitch has settled. A setting in seconds may still show a larger effect than on the
tractor, and the sweep asks only whether it acts. A strip that paints less than the
implement's width is reported on its own (`SECTIONS-PAINT-SHORT`).

The settings come from the source, not a list: every key the host's config bridge handles
(`ApplyConfigSet`'s case labels, `or` and `when` included) and every key the web client sends
(`data-key`, `data-cfg`, `data-cfgnum`, `data-tgl`, `cfgSend`, and quoted `'prefix.key'`
literals with a prefix the bridge knows, such as the wizards' `wzNum` and `wzTgl` keys; a
literal the host routes as a command is not a setting). A key nobody sweeps is reported
(`SETTING-NOT-SWEPT`), and so is a key the client sends that the host ignores
(`SETTING-IGNORED-BY-HOST`). A scan that finds no `ApplyConfigSet`, or fewer than 100 keys in
it, is a `HARNESS` finding rather than an empty list. Display-only settings are in the table
too, expected to change nothing. `PgnTap.cs` keeps a copy of every datagram the real UDP
service sends and answers as a steer module (PGN 253: the wheel angle, the switches), sent to
port 9999 on the loopback so it takes the whole receive path (the steer module counts as
connected).

The rig reports no network interfaces, so everything AgOpenWeb sends to the modules goes to
127.0.0.1:8888 only, never to the LAN: a module on the bench (an AiO saves steer and machine
settings to its EEPROM) gets nothing. The tap records every datagram before it leaves. UDP
port 9999 must be free (no AgOpenWeb or other harness running on the machine).

The report gets one table, a row per setting: from and to, the probe, what was expected,
what changed, and the verdict.

## Host-loop budget

`HostLoopBudgetTests.cs` holds the host loop and the 10 Hz broadcast to a work budget with a
field open (`TestCategory=HotPath`, run by CI's `hot-path-budgets` job): a 300 x 400 m field
from a KML file with a 24 m headland, 40 more fields with their jobs on the disk, the tractor
standing, then driving a pass with AutoSteer and the sections on. The web server's loop is off;
the test runs each broadcast itself after each fix and counts what it cost (`CostMeter.cs`:
polygon edges, coverage lookups, bytes allocated, and on Linux the files read), and the rig's
dispatcher counts every work item on the host loop the same way. A broadcast that reads a file
with nothing changed on the disk fails (#604), as does a host-loop item that reads one while
driving (#169). With no network interfaces in the rig, the Status frame's list of IP
addresses reads nothing, so any read is the broadcast's own. It prints each phase's counts and
the host loop's work by name. About 16 s.

## Running

```bash
dotnet test Tests/AgOpenWeb.VehicleToolScenarios.Tests/ -c Release --filter "FullyQualifiedName~VehicleToolScenarioTests"
```

About 80 minutes in Release (careful about 9, careless about 15, the settings sweep about 50, measured before the third base run and the wider real-time stretches: expect it longer). CI only builds it; the full run is nightly on develop (`.github/workflows/vehicle-tool-scenarios.yml`, also run by hand from Actions), with the report kept as an artifact and in the run's summary. The report goes to `vehicle-tool-scenarios-report.md` in the test output
folder, or to `AGOPENWEB_SCENARIO_REPORT`. Other switches:

| Variable | Effect |
|---|---|
| `AGOPENWEB_SCENARIO_REPORT=path.md` | where to write the report |
| `AGOPENWEB_SCENARIO_OPERATOR=careful` | only one operator (`careful` or `careless`), or only the settings sweep (`sweep`); a comma-separated list for several (the nightly runs `careful,careless` and `sweep` as two jobs side by side) |
| `AGOPENWEB_SCENARIO_ONLY=V-WB` | only drives whose code or name contains this (`settings`, `profiles` for those checks); in the sweep, only settings or probes containing it (`Slope\|Deadzone`: either) |
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
