# Update scenarios: does a season's work survive an update, a restart, a power cut?

An end-to-end harness for what AgOpenWeb writes to disk and reads back: the device settings
(`appsettings.json`), the last field, theme and camera (`appstate.json`), the vehicle and
implement profiles (`Vehicles/`, `Tools/`), the NTRIP casters (`NtripProfiles/`), and the fields
(`Fields/<name>/field.geojson`, its sidecars, the jobs and their coverage tiles). Checklist
13.13 of `Checklists/13-heads-installs.md` (an update keeps fields, profiles and settings), done
by a program every night instead of by a person once.

Two operators:

- **The upgrader** runs an **older AgOpenWeb release** for a season and then installs this
  build over it. The release's Linux x64 daemon bundle (`agopenweb-linux-x64.tar.gz` of the
  GitHub release) is downloaded once into a cache, started headless as a child process on a
  fresh `AGOPENWEB_DATA` folder, and driven over its WebSocket (`/ws`) with the very commands
  the web client sends (`control.acquire`, `profile.new`, `config.set|key:value`,
  `profile.save`, `ntrip.save`, `field.new`, `boundary.fromMapPoints`, `headland.wholeBoundary`,
  `track.drawStraight` / `track.drawCurve` + `track.drawPoint`, `flag.placeAt`,
  `field.startJob`, `section.master`, `sim.speedUp`…), with pauses as a person has. It is
  stopped with SIGTERM, as systemd stops it. Then **this build** starts in the test process on
  the same data folder (the rig of the other harnesses: the whole backend as the Desktop head
  boots it), reads everything back through the app's own services (the configuration store,
  the profile lists, the NTRIP profiles, every field opened with `field.openOnly`, every job
  resumed with `field.resumeJob`) and compares it with what was set. Then it restarts (no
  value may move), and finally the older release is started again on what this build wrote
  (a rollback after a bad update) and this build once more after that.
  One scenario per release (`U-26.7.1`, `U-26.7.2`, `U-26.7.3`), oldest first, and one where
  the power goes on the last release in the middle of a job (`UK-<tag>`).
- **The restarter** makes the same farm on **this build** and puts it through what happens to
  a cab computer: a plain restart with a second restart that must change nothing (`R1`); a
  power cut with a job running, no exit save (`R2`); `appsettings.json`, `appstate.json` and
  the profiles in use each cut to half their bytes, as a power cut mid-write leaves them (`R3`:
  the `.bak` copies must win); the data folder copied to another computer and the old one
  gone, so the absolute Fields path in `appsettings.json` is stale (`R4`); an AgOpenGPS 6.x
  field folder copied into `Fields/` by hand, before the first start and while the app runs
  (`R5`: `Field.txt`, `Boundary.txt`, `Headland.Txt`, `TrackLines.txt` with an AB line and a
  curve, `Flags.txt`, `Sections.txt` with 240 m² worked; `AgOpenGpsFiles.cs` writes them as
  AgOpenGPS does); and the whole season under a comma-decimal culture (pt-PT) on every thread,
  then read again under the invariant culture (`R6`, the globalization fence of
  `CONTRIBUTING.md`). `R8` moves a whole **AgOpenGPS 6.8.6 installation** to AgOpenWeb the way
  a farmer does it and keeps AgOpenGPS: `AgOpenGpsInstall.cs` writes a `Documents/AgOpenGPS`
  folder with the layout and setting names of a real 6.8.6 computer (tractors in
  `VehicleProfiles/`, implements in `ToolProfiles/`, U-turn and tram in
  `Environment/environment.xml`, the NTRIP caster in `AgIO/`, a combined `Vehicles/*.XML` left
  from before 6.8.2 with that day's values, and fields with every file AgOpenGPS writes:
  coverage, contour, recorded path, elevation, background image, KML, ISO-XML). Its fields and
  profile folders are copied into `Documents/AgOpenWeb`; each tractor, implement and setting
  is then looked for in this build (imported, read from the stale combined file, or not
  imported, per settings tab), the fields judged as in `R5` plus their other files, and the
  AgOpenGPS folder must be byte for byte as it was. With
  `AGOPENWEB_UPDATE_AOG_FIELDS=<dir>` the farmer's own AgOpenGPS field folders (or a whole
  AgOpenGPS folder, with `Fields/` inside, copied as `R8` copies it) are copied in and opened
  too (`R7`); only counts go in the report, the files stay on the computer.

## The farm

`Farm.Standard()` in `Farm.cs`: two tractors (Fendt 312 with 19 steering, antenna and
dimension settings, Valtra N134), two implements (a rear-mounted 8 m sprayer with 8 sections,
look-ahead, U-turn and tram settings; a trailed 3 m seed drill with 12 sections), the Fendt and
the sprayer as the pair in use; fifteen device settings (metric units, display, connections,
GPS, roll), language `pt`, two hotkeys, night mode, a saved camera; two NTRIP casters with
passwords (made-up hosts: the client's "cannot find caster" warning is expected and not
reported); three fields near 41.6°N 5.7°W: a 300 × 200 m rectangle with an 8 m headland, an
AB line, a five-point curve, two flags and a 20 s sprayed job, a pentagon with a diagonal AB
line and a flag, and a 180 × 260 m rectangle with an AB line. Every setting has the
`config.set` key it goes in by and the dump key it must read back under, so the comparison is
exact and the report names both.

What the app does by design is not a finding: an AB line is extended past the boundary at both
ends and a drawn curve is smoothed, so a track is judged by whether every point drawn lies on
the saved line (1 m for an AB line, 2.5 m for a curve); a boundary by its area; a flag by its
position (1 m) and colour; a job by whether it resumes with worked area; the settings by value,
with the app's own rules respected in the farm (the WAS offset after Invert WAS, a hitch length
as a positive distance, look-ahead off at most 0.8 × on and excluding the turn-off delay, a UI
scale from the steps 100/115/130/150).

## Findings

Each problem is a finding with a stable code (`UPDATE-TRACK-MOVED`, `POWERCUT-COVERAGE-LOST`,
`TRUNCATED-SETTINGS-LOST`, `MOVED-FIELDS-DIR-STALE`, `AOG-COVERAGE-NOT-IMPORTED`,
`ROLLBACK-OLD-BUILD-ERRORS`, `RESTART-DRIFT`…) and a severity: **DataLoss** (the farmer's work
is gone or changed), **Bug**, **Inconsistency**, **Robustness** (a crash, a hang, an error
logged, an old build that cannot start on new data), **Improvement**. The report
(`update-scenarios-report.md`) lists each kind once, most serious first, split into **New** and
**Already reported** (`KnownFindings.txt`: one code per line with where it was reported), then
every scenario's checks, the files the older build left, a timeline and the older build's last
console lines. Scenario codes and finding codes do not change from night to night, so two
reports diff.

A setting that does not come back after an update is laid at the right door: before this build
starts, the older release's profile files and `appsettings.json` are read as text
(`OldProfileFiles`). A value the release never wrote is `OLD-BUILD-SETTING-NOT-SAVED`; a file
whose own values contradict each other (a default section width next to sections of another
width) is `OLD-BUILD-FILE-INCONSISTENT`; a value the file holds and this build reads back
differently is `UPDATE-PROFILE-SETTING-CHANGED` or `UPDATE-DEVICE-SETTING-CHANGED`, the only
ones this build answers for. A `save_error.txt` left by any build is `<prefix>-SAVE-ERROR`.

The test itself fails only when the harness breaks (an unhandled exception, a release that
did not start, no control over its WebSocket): a finding is a result, not a failure.

## Running it

```bash
# Everything (about 15 minutes; the first run downloads three bundles of ~45 MB each):
dotnet test Tests/AgOpenWeb.UpdateScenarios.Tests/ --filter FullyQualifiedName~UpdateScenarioTests

# One operator, one scenario:
AGOPENWEB_SCENARIO_OPERATOR=restarter AGOPENWEB_SCENARIO_ONLY=R3 dotnet test Tests/AgOpenWeb.UpdateScenarios.Tests/
AGOPENWEB_SCENARIO_OPERATOR=upgrader AGOPENWEB_SCENARIO_ONLY=U-26.7.2 dotnet test Tests/AgOpenWeb.UpdateScenarios.Tests/
```

| Variable | What it does |
|---|---|
| `AGOPENWEB_SCENARIO_ONLY` | A scenario code (`R2`, `U-26.7.1`) or a prefix (`R`, `U`). |
| `AGOPENWEB_SCENARIO_OPERATOR` | `restarter` or `upgrader`; both when unset. |
| `AGOPENWEB_SCENARIO_REPORT` | Where the Markdown report goes (default: the test's work directory). |
| `AGOPENWEB_SCENARIO_VERBOSE` | Field names of the farmer's own fields in the report (`R7`). |
| `AGOPENWEB_SCENARIO_KEEP` | Keep the scenarios' data folders (`/tmp/aow-update-<code>-…`) to look at. |
| `AGOPENWEB_UPDATE_FROM` | Releases to update from, oldest first (default `v26.7.1,v26.7.2,v26.7.3`); `tag=/path/to/app` uses a published app folder instead of a download. |
| `AGOPENWEB_UPDATE_CACHE` | Where the bundles are kept (default: the temp folder's `agopenweb-releases`). |
| `AGOPENWEB_UPDATE_AOG_FIELDS` | A folder of AgOpenGPS field folders of your own, or your whole AgOpenGPS folder, for `R7`. |

The old build and this build use UDP port 9999 and web port 5174 one after the other, never at
once; nothing else of AgOpenWeb may run on the machine. Releases before 26.7 have no Linux
bundle (the Avalonia era) and are not run. `update-scenarios.yml` runs the harness nightly on
`develop` (06:41 UTC, when there is a new commit), caches the bundles, keeps the report as an
artifact and shows it in the run's summary; CI only builds the project.

## Files

| File | What |
|---|---|
| `UpdateScenarioTests.cs` | The one NUnit test: both operators into one report. |
| `UpgraderOperator.cs` | The older release, the season on it, SIGTERM or SIGKILL, this build, the restart, the rollback. |
| `RestarterOperator.cs` | `R1`–`R8` on this build. |
| `AgOpenGpsInstall.cs` | `R8`'s AgOpenGPS 6.8.6 folder: profiles, environment, AgIO, fields with every file. |
| `Farm.cs` | The farm and `FarmBuilder`, which makes it through any `ICommandSender` (the WebSocket or the in-process rig). |
| `OldRelease.cs` | The release bundle (download, cache), the headless child process (`OldProcess`), the WebSocket client with control (`WebClient`). |
| `Snapshot.cs` | What this build reads back (`Snapshot`) and the comparison (`Judge`). |
| `AgOpenGpsFiles.cs` | An AgOpenGPS 6.x field folder written from a field spec. |
| `UpdateRig.cs` | The in-process backend (as the other harnesses' rigs), with an exit save or a crash on dispose. |
| `Journal.cs`, `Kit.cs` | The report and small helpers. |
| `KnownFindings.txt` | Codes already reported, with where. |
