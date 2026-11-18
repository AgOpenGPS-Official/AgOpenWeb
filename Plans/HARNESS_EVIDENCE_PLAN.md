# Scenario harnesses: fix from evidence, not from reruns

**Status:** in progress, 2026-11-17. The rule is in force. Evidence capture is built for the job
harnesses (PR #873); the first nightly with findings has to show that a dump is enough to fix
from. The other harnesses are not wired yet.

## 1. Why this plan

[HARNESS_SPEED_PLAN.md](HARNESS_SPEED_PLAN.md) made the runs shorter. They were still inside the
debug cycle: a fix was merged after a short fix-vs-base run on Runner, and a run was started again
to read one more number. On 2026-11-17 a slow control tick was being looked for in the working day
harness, 40 minutes a run, when one or two slow ticks would have said as much as a hundred.

Last night's runs (2026-11-17), wall time:

| Harness | Minutes | Clock |
|---|---|---|
| Vehicle & Tool settings sweep | 100 | sped up |
| Job: rig | 84 | sped up |
| Job: field careless / careful | 42 / 34 | sped up |
| Working day | 40 | sped up |
| Dump replay | 29 | sped up |
| Job: path / turn | 27 / 26 | sped up |
| Vehicle & Tool operators | 27 | sped up |
| Web client | 23 | real time |
| GPS and NTRIP | 21 | real time |
| Steer wizard | 12 | real time |
| Field, machine module, ISOBUS | about 5 each | real time |

A rerun did two jobs: it got more data, and it confirmed the fix. With testers neither needs the
tester again: the bug report dump has the data, the issue is recreated from it, and the fix is
tested against it. This plan does the same for harness findings.

## 2. The rule (in force, Chris 2026-11-17)

This replaces point 6 of the speed plan's "How we work" (the short fix-vs-base run as merge gate).

1. **The harnesses run overnight, on develop.** They are not a gate for a fix.
2. **A finding comes with its evidence**: the app's own bug report dump, saved by the harness
   beside the report (section 3).
3. **A fix is made from the evidence and proven by a unit test built from it**, failing on develop
   and passing on the fix. Then the unit suites, then merge. The nightly on develop is the check
   the next morning; a regression it shows gets its own fix PR.
4. **When the evidence is not enough to write the test, extend what the harness saves.** Don't
   rerun to look. A short run by hand (`ONLY`, `TURNS`, `FROM_PASS`, on Runner) is for the rare
   question the dump can't be made to answer.
5. **A change to a harness itself still gets a run of that harness**, short and named, on Runner.
6. **Timing findings** (a slow tick, a stall) are not deterministic: one run without a slow tick
   doesn't show it is gone, and a unit test can't hold it. The fix is made from what was captured
   at the moment (which lock, which thread, how long); the nightly's numbers over several days
   confirm it.

Still as before: one theme at a time, one PR per theme; runs never on a development machine.

## 3. Evidence capture

### Built: the job harnesses (PR #873)

`Tests/ScenarioShared/Evidence.cs`, linked into `AgOpenWeb.FieldJobScenarios.Tests` (its field,
path, rig, turn and imperial fixtures all go through `Jobs.RunAsync`).

A job with a finding writes `evidence/<job>.zip` beside the report, at the end of the job with the
field and the job still open (`Jobs.KeepEvidenceAsync`):

- **The app's dump** (`DebugDumpService.Capture` on the host loop, `WriteDump` off it, the job's
  coverage saved first, as the app's bug report does): `appsettings.json`, `configuration.json`,
  `runtime_state.json`, `logs.txt`, `gps_data_log.csv` (the last 5 minutes), `turn_path.json`
  (the last turn), `field/` with the job's coverage tiles.
- **Added by the harness**, under `attachments/`:
  - `gps_data_log_whole_scenario.csv`: the whole job. The app's recorder keeps 3000 fixes, so
    the harness takes the new rows every 2000 fixes (`Evidence.PullGpsLog`, from `JobRun.SendFix`).
  - `log_whole_scenario.txt`: the whole job's log (`Evidence.LogProvider`, UTC as the GPS log).
  - `turns.json`: every U-turn as planned (the path, the pass before, where the tractor was).
  - the picture of the job (`.ppm`); red marks the cells a Bug check counted.
  - **The harness's own data**, for a finding that proves to be the harness's doing (added after
    J19 on 2026-11-18, whose finding could only be placed by working it out again):
    `truth_track.csv` (the real machine, the module's relays and nozzles, the screen's and
    AgOpenWeb's sections and tool, fix by fix; `Evidence.Row`), `truth_grid.bin` + `.json` (the
    harness's 0.2 m grid: real spray, zones, the cells each area check counted, AgOpenWeb's screen
    and mapping sweeps) and `harness_trace.txt` (`Evidence.Trace`, from `Journal.Trace`).
- **`user_notes.txt`**: the findings, what was measured, the checks and the timeline.

`AGOPENWEB_SCENARIO_EVIDENCE=all` keeps evidence for every job, `0` for none. The nightly keeps
the zips as the `job-scenarios-evidence-<key>` artifacts; on Runner they are in `/out/evidence/`.
The rig now logs to the app's in-memory log, as the heads do (`FieldJobRig`), or `logs.txt` is
empty. The recorder, the app's log and the whole-scenario log are the process's, so they are
cleared when a job's `Evidence` is made.

Checked: J1 on Runner, `TURNS=2`, `PACE=2`, `EVIDENCE=all`: 20 s, pace lag 0, a 300 KB zip with
every part filled.

### Built: the working day (PR #874)

Timing, so the evidence is not a dump: what the slow moment waited in, captured at the moment.
`Tests/AgOpenWeb.WorkingDayScenarios.Tests`:

- **Each slow control tick** (over 10 ms) carries in the report and the live log:
  - the lock waits that overlapped it, named when the lock is a field of the backend's services
    (`ContentionWatch`, the runtime's contention events);
  - the tick thread's own CPU time, the times it waited and was preempted (`ThreadUsage`,
    `getrusage`), and the bytes it allocated;
  - what a watchdog saw the thread wait in once the tick passed 15 ms: its system call, the futex
    and the kernel wait (`TickWatch`), and the first time, every busy thread in the process by name.
- **`working-day-live.log`** beside the report, flushed per line: the day's events, slow cycles
  and ticks, lock waits of 5 ms or more.
- **`AGOPENWEB_DAY_UNTIL=<event>`** and **`AGOPENWEB_DAY_STOP_ON=tick[:N]|cycle[:N]`** end the day
  3 s after the event or the Nth slow moment; the report says it stopped early and leaves the
  whole-day trends unjudged. 10–18 minutes instead of 45.
- The report counts the control ticks that allocate.

Shown on the first finding: the 100 ms ticks at a reconnect were the coverage snapshot's lock
(`_coverageLock`, 101 ms, held by the snapshot's thread); the fix's unit test came from that.
Getting there took five short runs, two of them lost to a theory the counters didn't support and
one to a bug in the contention watch (a pointer payload read with `Convert`, so every wait was
dropped). Lessons for the other harnesses' timing evidence: name the lock and the holder first,
before counting anything else, and check a new watch reports something on the host it runs on.

### Not shown yet

- **That a dump is enough to write the unit test from.** The checked run had no findings. Take the
  first nightly finding with a dump, write its test from the dump alone, and note here what was
  missing.
- **Dump replay.** `AgOpenWeb.DumpReplayScenarios.Tests` reads zips from `AGOPENWEB_DUMP_DIR`
  (as private dumps: `AGOPENWEB_REPLAY_PRIVATE=1`, a local run). It should take these; not tried.
- **Moments in the middle of a job.** The runtime state is the end of the job. Earlier moments are
  in the whole-job logs and `turns.json` only. If a finding needs the state at its moment, add a
  dump there (`Evidence.SaveAsync` takes a moment's name), a few per job at most: saving the
  coverage in mid-job is something a run without evidence doesn't do.

## 4. Open

In this order. Each harness: link `Evidence.cs`, add the two log providers to its rig, make an
`Evidence` per scenario, save when the scenario has a finding, name the zip in the report, keep
`evidence/` as an artifact in its workflow.

1. **Prove the format on a real finding** (above) before copying it.
2. **Vehicle & Tool** (operators and sweep) and **field scenarios**: guidance and U-turns, the
   same kind of finding as the jobs.
3. **Steer wizard, machine module, ISOBUS, GPS and NTRIP**: real time, short scenarios, so one
   dump at the end of a scenario with a finding is close to the moment. Add what each emulated
   module saw (its PGN log; the caster's and the receiver's for GPS and NTRIP).
4. **Web client**: the dump plus the screenshots it already takes, and the browser's console.
5. **Update and dump replay**: the data folder as it was read, which a dump only partly holds.
6. **Working day**: built (PR #874), see section 3.

## 5. For a run by hand: see it, stop it, keep it

Less pressing now that runs are overnight, still wanted for the run by hand. From a read of all
eleven harnesses on 2026-11-17:

- **Nothing shows while a run goes.** Ten harnesses (all but the working day, which has its live
  log since #874) keep their findings in memory and write the
  report in the last lines of the test; their trace goes to `Console.WriteLine`, which NUnit holds
  until the test ends. `run-harness.sh` cuts the test output down to `Passed!|Failed!`, so
  `docker logs -f` shows nothing. *Wanted:* one live log for all (the working day's `LiveLog`
  moved to `Tests/ScenarioShared`): a line when a scenario starts, ends (with its time) and when
  a finding is made, passed through by `run-harness.sh`.
- **A run that is stopped keeps nothing**, except in the job harness (report after each job) and
  the working day. *Wanted:* the report written after every scenario, everywhere.
- **Only the working day stops when what is looked for has happened.** *Wanted:* one setting for
  all, `AGOPENWEB_SCENARIO_STOP_ON=<finding code>[:N]`, the report marked as stopped early.
- **Workflow inputs.** Only the job and dump replay workflows take `only`; the working day's
  workflow has no input for `until` or `stop_on`.
- **Per-scenario times are not in the reports**, so which fixed waits to cut is not known (GPS and
  NTRIP has several of 45 to 120 s). *Wanted:* a "slowest scenarios" table in each report.

## 6. Not in this plan

- **A shorter night.** Splitting the rig fixture's 13 jobs and the sweep's 137 settings over
  runners, and running the real-time harnesses' scenarios side by side, would cut the night from
  100 minutes to the longest single job. With the runs overnight and out of the debug cycle that
  buys little; take it up if the night starts to run into the morning.
