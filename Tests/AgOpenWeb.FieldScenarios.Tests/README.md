# Field scenarios: the careful and the careless operator

An end-to-end harness for the field functions: create a field, outer and inner boundaries
(drawn, driven, imported from KML), headland, AB lines, A+, curves, boundary curves, pivots,
edits, tram lines, flags, contour, jobs, close and reopen, copy a field, Drive In.

The whole backend runs in the test process, as the Desktop head boots it (DI, settings,
`MainViewModel` on the `HostLoopDispatcher`, `RemoteServerHost`, `RemoteServerWiring`), on a
temporary `AGOPENWEB_DATA` folder. Every step is a command exactly as the web client sends it
(`transport.send('id|arg')` → `server.CommandHandler`); the tractor is moved by feeding GPS
fixes into `IGpsService`. After every step the harness takes a snapshot of the field on the
host loop and checks it against the rules in `FieldInvariants.cs` (a boundary must not cross
itself, a headland must lie inside the field, a tram system must point to a track that
exists, nothing of a field may stay behind when it is closed, …).

Two operators:

- **Careful** (`GoodOperator.cs`): on each of five real field shapes (`TestData/fields.json`)
  builds everything the right way, with a different boundary method per field, and checks
  that each action did what it should and that close + reopen and From Existing give back
  the same field.
- **Careless** (`BadOperator.cs`): about 170 wrong actions in ten groups: commands with no
  field open, bad field names, impossible boundary shapes, half-finished recordings, bad
  headland values, bad tracks, bad tram settings, pulling a track or boundary out from under
  what depends on it, field and job mix-ups, names that differ only in capitals. For these
  the app must keep its data sound and should say why it refused.

Some careless values cannot be typed in the web client (its inputs check numbers); they are
sent anyway, because the host is the last line: any browser on the LAN can send any command.

## Running

```bash
dotnet test Tests/AgOpenWeb.FieldScenarios.Tests/ --filter "FullyQualifiedName~FieldScenarioTests"
```

About four minutes. CI only builds it; the full run is nightly on develop (`.github/workflows/field-scenarios.yml`, also run by hand from Actions), with the report kept as an artifact and in the run's summary. The report goes to `field-scenarios-report.md` in the test
output folder, or to `AGOPENWEB_SCENARIO_REPORT`. Other switches:

| Variable | Effect |
|---|---|
| `AGOPENWEB_SCENARIO_REPORT=path.md` | where to write the report |
| `AGOPENWEB_SCENARIO_ONLY=Olival` | only fields whose name contains this (the careless operator uses the first) |
| `AGOPENWEB_SCENARIO_VERBOSE=1` | every step and finding to the test output |

The tests fail only when the harness itself breaks, a step throws out of the harness, or the
host loop hangs. The findings are the product: the report lists each kind once, most serious
first, with the step that caused it, followed by every step's log.

`DiagTests.cs` holds explicit experiments used to confirm findings (headland on a field with
obstacles, heading after placing the tractor).

## Severity

| Severity | Meaning |
|---|---|
| Bug | wrong data or wrong behaviour the operator can hit (an exception, a field that changes on reopen, a headland outside the field) |
| Inconsistency | two parts of the app disagree (a tram system pointing to a renamed track, state written off the host loop) |
| Robustness | bad input accepted and kept (a bow-tie boundary, a NaN offset) |
| Improvement | it works, but the operator is not told, or could be helped (a refusal with no message, duplicate track names) |

## Field data

The five field shapes are Renato's (project files), moved together to a made-up place so
the repository does not show where the farm is. Shapes, sizes, obstacles and lines are kept.
