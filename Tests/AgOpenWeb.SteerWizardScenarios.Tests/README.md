# Steer wizard scenarios: the careful and the careless operator

An end-to-end harness for the steer wizard: every one of its fifteen steps, from the vehicle
type to Finish, then a drive on an AB line with AutoSteer to see that the result steers.

The whole backend runs in the test process, as the Desktop head boots it (DI, settings,
`MainViewModel` on the `HostLoopDispatcher`, `RemoteServerHost`, `RemoteServerWiring`), on a
temporary `AGOPENWEB_DATA` folder. Every step is a command exactly as the web client sends it
(`wizard.open`, `wizard.next`, `wizard.action|…`, `config.set|key:value`), and the harness reads
the Wizard frame the browser would get, so it sees what the operator sees. Two things stand in
for the outside world:

- **The steer module** (`EmulatedSteerModule.cs`): the AiO v4 firmware's behaviour, not a model
  that agrees with the host. PGN 252/251 applied as the firmware reads them, its P controller
  with min PWM and the low/high limit, the WAS formula on the host's settings (an inverted
  sensor subtracts the offset), the steer switch edge logic and the watchdog. Behind it, the
  steering: a sensor with its own counts per degree, centre and mounting direction, a motor that
  can be wired either way, break-away PWM, mechanical locks, and the operator's hands on the
  wheel. Only the network is replaced (`ModuleWire.cs`); every service above it is the real one.
- **The tractor** (`SteerWizardRig.cs`): a bicycle model driven by the real wheel angle at the
  operator's speed, reported as RTK fixes at 10 Hz from the antenna's position.

Each scenario starts from a fresh tractor whose truth (CPD, Ackermann, locks, wheelbase,
antenna) the operator doesn't know; at the end the settings are compared with that truth.

## Two operators

- **Careful** (`CarefulOperator.cs`): does every step in order, reads each result and only goes
  on when the step says it worked. The settings must then match the tractor, the profile must
  be saved, and AutoSteer on an AB line must stay on the line.
- **Careless** (`CarelessOperator.cs`): 23 stories of what goes wrong in a cab: Next to the end,
  nonsense numbers, Zero WAS with the wheels turned (and twice, and with the module unplugged),
  Zero Roll on a slope, the motor wired backwards (with the wheels left turned, and with the motor
  test skipped), hands on the wheel during the motor test, Back, ✕, Cancel or the steer switch in
  the middle of a test, the browser lost mid-test, Start pressed three times, AutoSteer pressed
  while the wizard is open, circles stopped after a quarter, driven the wrong way or with the
  wheel eased off, the wizard opened again while a circle is recording, and a GPS-only tractor.

Some careless values cannot be typed in the web client (its inputs check numbers); they are
sent anyway, because the host is the last line: any browser on the LAN can send any command.

## Safety watch

`SafetyWatch.cs` samples the wheels and the host 50 times a second for the whole scenario,
whatever the operator is doing, and raises a Safety finding when the motor moves the wheels
with neither Free Drive nor AutoSteer on, Free Drive stays on with the wizard closed, with no
browser in control or after its step was left, AutoSteer is engaged while the wizard is open,
the motor pushes against a lock for more than 2.5 s, the wheels run away from the commanded
angle (the motor direction is wrong), or the motor drives against the operator's hands.

## Running

```bash
dotnet test Tests/AgOpenWeb.SteerWizardScenarios.Tests/
```

About ten minutes. CI only builds it; the full run is nightly on develop
(`.github/workflows/steer-wizard-scenarios.yml`, also run by hand from Actions), with the report
kept as an artifact and in the run's summary. The report goes to `steer-wizard-report.md` in the
test output folder, or to `AGOPENWEB_SCENARIO_REPORT`. Other switches:

| Variable | Effect |
|---|---|
| `AGOPENWEB_SCENARIO_REPORT=path.md` | where to write the report |
| `AGOPENWEB_SCENARIO_ONLY=Careful\|backwards` | only scenarios whose name contains one of these (`\|` between them) |
| `AGOPENWEB_SCENARIO_VERBOSE=1` | every step and finding to the test output |

The test fails when the harness itself breaks or the safety watch raised anything: something
moved the wheels that nobody could stop, or that the operator didn't ask for. Every other
finding is for the report, which lists each kind once, most serious first, with the step that
caused it, followed by every step's log.

## Severity

| Severity | Meaning |
|---|---|
| Safety | the wheels moved and nobody commanded or could stop it, or they pushed against a lock or the operator's hands |
| Bug | a wrong setting the operator can end up with (a CPD from half a circle, a centre taken with the wheels turned, a value the step said it would check) |
| Inconsistency | two parts disagree (left and right read differently, a test result that is the Free Drive clamp, not the lock) |
| Robustness | bad input accepted and kept |
| Improvement | it works, but the operator is not told, or could be helped (Finish with no test done) |
