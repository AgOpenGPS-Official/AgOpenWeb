# Dump replay scenarios: the bug reports, again, on today's build

A harness that takes the bug report dumps people sent (main menu › Bug Report, or Send Report)
and runs them again on the current build: item 3.4 of `Plans/HARNESS_IMPROVEMENT_PLAN.md`.
A dump has everything the reporter's app had at the moment they pressed the button: their
Vehicle & Tool settings, their field folder with the active job's coverage, the track they were
on, and the last five minutes of the GPS log at 10 Hz. So a report that was fixed can be
checked again every night, and a new build is tried on real fields, real machines and real
operators instead of made-up ones.

The whole backend runs in the test process, as the Desktop head boots it, on a temporary
`AGOPENWEB_DATA` folder, with its real UDP service on port 9999 and a steer module stub
answering it (the same rig as the field job harness). Every step the operator takes is a
command exactly as the web client sends it.

## What it does with each dump

**Set-up** (`DumpReplay.SetUpAsync`), as the reporter had it:

- Vehicle & Tool: every value of the dump's `configuration.json` that the web client has a
  `config.set` key for is typed in (`ReporterSettings.cs`); the few without a key are set in the
  configuration store. Settings a dump does not carry keep AgOpenWeb's default, and the report
  lists them (`configuration.json` is a summary; see *Limits*).
- The field folder is put in the fields folder as it was on the reporter's disk, a few fixes
  are sent from where the GPS log starts, and the operator opens it: Field › Resume job
  (`field.resumeJob`) when the dump has a job, else Field › Open. The field is the reporter's
  own, made by them through the app; the harness never builds geometry itself.
- The Offset Fix (drift) the reporter had is typed in (`offset.set`), the reporter's track is
  selected (`track.select`), and the headland, skip rows and U-turns are switched as the dump's
  runtime state and log say.
- The passes: the GPS log has the reporter's pivot, pass number and distance from the line on
  every row with guidance, so where their build put each pass is known to the centimetre. On an
  AB line, if this build puts them half a working width away (a track made before AgOpenGPS's
  pass model came in, where the reference line is the edge between two passes), the operator
  moves the line back with Ref Nudge's half-width button (`track.refHalfNudgeRight`) and the
  report says so (`TRACK-PASSES-MOVED-HALF`).

**Replay** (`DumpReplay.ReplayAsync`): each row of `gps_data_log.csv` becomes the fix the
reporter's receiver gave. The log holds the pivot after AgOpenWeb's antenna transform and the
fused heading; the harness turns it back into the antenna position (the inverse of
`AntennaToPivotTransform`, with the reporter's antenna height, offset and roll) and feeds it to
the GPS service with the receiver's own heading, roll, fix, correction age and sentence type,
one step after the NMEA parser. The 100 Hz control loop is ticked between fixes, and the rows
are paced by the wall clock (AgOpenWeb's GPS-lost, guidance and hello watchdogs run on it).
The operator does what the reporter did: AutoSteer on and off when theirs went (`autosteer.toggle`),
the sections to Auto on the first engage when a job with coverage was being worked, the manual
U-turn button where their log has `[ManualYouTurn] Triggering` (`youturn.manualLeft/Right`), and,
where their build moved to another pass with AutoSteer on and no U-turn (a lateral move or the
steer switch, which the dump does not record), the lateral button (`track.snapRight/Left`) or
AutoSteer off and on.

What this build decides on every row is put next to what the reporter's build decided
(`ReplayJudge.cs`): the pose, the line, the steer angle, U-turns, AutoSteer, the alarms the
operator heard. The reporter's tractor was steered by their build, so once this build guides on
another line than theirs did (for 3 s), the guidance comparisons stop there and the report says
from when; the take-over is the closed-loop test.

**Take-over** (`TakeOver.cs`): a replay can only say what AgOpenWeb decided on the path the
reporter drove; it can't say where AgOpenWeb would have taken them. So AgOpenWeb boots again
with the same set-up, the reporter's own fixes are fed for 5 s up to a moment on a line about
25 s before their first headland (else the first moment with AutoSteer on), and from there a
simulated machine with the reporter's geometry (the field job harness's `TruthMachine`: a
bicycle model steered by a motor with its lag, the implement on its linkage) carries on at the
reporter's median speed, kept between 6 and 12 km/h, steered by the angle AgOpenWeb sends, with
AutoSteer and U-turns on. It drives for 4 minutes, or until two U-turns are done, or until it
leaves the field.

## Findings

| Check | Finding | Kind |
|---|---|---|
| An exception, an error logged, the host loop not answering for 30 s, state changed off the loop | `EXCEPTION`, `ERROR-LOGGED`, `HOST-LOOP-HANG`, `STATE-OFF-LOOP` | app defect |
| The reporter's field does not open, its job does not resume, its track is missing or is not the one selected | `FIELD-DID-NOT-OPEN`, `JOB-DID-NOT-RESUME`, `TRACK-NOT-FOUND`, `TRACK-NOT-SELECTED` | app defect |
| The field itself: the boundary crosses itself, the headland line or an obstacle lies outside the boundary | `FIELD-BOUNDARY-SELF-CROSSING`, `FIELD-HEADLAND-OUTSIDE`, `FIELD-OBSTACLE-OUTSIDE` | app defect |
| A steer angle, cross-track or heading that is not a number | `NOT-A-NUMBER` | app defect (safety) |
| The heading turning more than 10° while the machine stands and the receiver's heading holds (#157) | `HEADING-MOVES-AT-STANDSTILL` | app defect |
| The pass number or the cross-track error jumping between two fixes on a line | `PASS-JUMP`, `XTE-JUMP` | app defect |
| The machine leaving the headland line on a line, moving forward, with no U-turn planned or running (now, whatever the reporter's build did) | `UTURN-MISSED` | app defect |
| Take-over: AutoSteer will not engage, or goes off by itself | `TAKEOVER-NO-AUTOSTEER`, `TAKEOVER-AUTOSTEER-DROPPED` | app defect |
| Take-over: a U-turn takes the tractor itself past the boundary | `TURN-OUTSIDE-BOUNDARY` | app defect (safety) |
| Take-over: the tractor stays inside but an end of the implement swings past the boundary (*Distance from boundary* keeps the tractor inside, as in AgOpenGPS; the field job harness's finding too) | `TURN-IMPLEMENT-OUTSIDE` | parity with AgOpenGPS (improvement) |
| Take-over: a U-turn comes more than 1 m closer to the boundary than *Distance from boundary* asks (#544) | `TURN-CLOSER-THAN-SET` | app defect |
| Take-over: a U-turn still running after 2 minutes | `TURN-NOT-FINISHED` | app defect |
| Take-over: the machine leaves the field with AutoSteer on, no U-turn and no warning. When AgOpenWeb refused the turn and said so ("No U-turn fits ...", #698), that is the operator's cue and not a finding | `TAKEOVER-RAN-OFF` | app defect (safety) |
| The same alarm (U-turn, boundary, RTK lost) 4 times or more within 2 s (#570) | `ALARM-REPEATED` | app defect |
| The passes of the reporter's track lie half a working width from where their build put them (moved back with Ref Nudge for the replay), or some other distance | `TRACK-PASSES-MOVED-HALF`, `TRACK-PASSES-MOVED` | parity with AgOpenGPS (expected for old tracks) / app defect |
| This build's pivot is more than 10 cm (median) from the reporter's: the replay is not faithful, so its other comparisons are weak | `REPLAY-POSE-DIFFERS` | harness artifact |
| The harness itself failed on a dump | `HARNESS` | harness artifact |

Each finding names one of: app defect, harness artifact, parity with AgOpenGPS, enhancement
(plan section 5). The report's top table gives each dump's verdict *then* (the reporter's build,
from their GPS log) and *now* (this build), so a fix shows as a change in that column, and a
regression too.

## The dumps

`TestData/dumps.json` lists them: the issue, the reporter, what they reported, the PR that
closed it, where the zip is and its SHA-256. **No dump is in the repository**: a dump holds the
reporter's field, which is their farm, and NTRIP settings. The public ones are the zips the
reporters attached to their issues; they are downloaded once into a cache folder and checked
against their SHA-256. The ones the bug report service keeps (`agopenweb-dumps...workers.dev`)
are for maintainers only: a nightly run reaches them with a Cloudflare Access service token,
and without one they are skipped and listed as skipped in the report.

To add a dump: add its entry (its id is the issue number, with a letter when one issue has
several dumps), or first try it by putting the zip in `AGOPENWEB_DUMP_DIR`: a zip there that
the manifest does not name is replayed too.

## Running it

```bash
dotnet test Tests/AgOpenWeb.DumpReplayScenarios.Tests/
```

It takes about as long as the GPS logs plus 4 minutes a dump. The test fails only when the
harness breaks; the findings are in the report, `dump-replay-scenarios-report.md` in the test
output folder.

| Variable | What it does |
|---|---|
| `AGOPENWEB_SCENARIO_ONLY` | Only these dump ids, comma separated (`306,544a`) |
| `AGOPENWEB_SCENARIO_REPORT` | Where to write the report |
| `AGOPENWEB_DUMP_DIR` | A folder of zips named `<id>.zip`, used before downloading (and new zips replayed too) |
| `AGOPENWEB_DUMP_CACHE` | The download cache (default: the temp folder's `agopenweb-dump-cache`) |
| `AGOPENWEB_DUMPS_ACCESS_ID`, `AGOPENWEB_DUMPS_ACCESS_SECRET` | Cloudflare Access service token for the bug report service's dumps |
| `AGOPENWEB_REPLAY_PACE` | Replay speed (1 = the GPS log's own pace; above 1 is faster, and the watchdogs may then fire where they did not) |
| `AGOPENWEB_REPLAY_TAKEOVER` | `0` skips the take-over |
| `AGOPENWEB_TAKEOVER_SECONDS` | How long the take-over drives at most (default 240) |
| `AGOPENWEB_SCENARIO_PACE` | Real ms per take-over fix (default 100, real time) |
| `AGOPENWEB_SCENARIO_VERBOSE`, `AGOPENWEB_SCENARIO_TRACE` | Steps to the console, or to a file |

`dump-replay-scenarios.yml` runs it by hand, or nightly on `develop` once it is in the bug report
Worker's `NIGHTLY_WORKFLOWS` (`Tools/bugreport-worker/wrangler.toml`), with the dumps cached
between runs. Like every harness here it
binds UDP 9999: one harness per machine at a time (a second run of this one fails at once on
its lock file).

## Limits

- `configuration.json` is a summary of the profile, not the profile: Pure Pursuit or Stanley,
  the look-ahead speed factor, section control in the headland, section off outside the
  boundary, the machine's relay functions and a few more are not in it, so AgOpenWeb's defaults
  are used. Older dumps carry fewer settings still. The report lists what was missing for each
  dump. A dump that carried the whole vehicle and tool profile files would make the replay exact.
- The section master switch's state is not in a dump; the operator is assumed to have set the
  sections to Auto when a job with coverage was being worked.
- The GPS log holds the filtered roll; with a roll filter set, the replay filters it a second
  time. A dual receiver's heading is taken from `$PAOGI` rows, or from the receiver's own
  sentences when the profile says dual and no IMU heading is there.
- The take-over's machine has the reporter's measured geometry but not their steering motor,
  their ground or their hand on the wheel: it shows where this build would steer a machine of
  that shape from that place, not what the reporter's tractor would have done.
