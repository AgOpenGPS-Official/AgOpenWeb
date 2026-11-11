# Scenario harnesses: faster to run, faster to work with

**Status:** in progress, 2026-11-11. The way of working and #771 are in place; #772 and the items
in section 4 are open.

## 1. Why this plan

On 2026-11-10 fixing the nightly findings went slowly, and most of the time went into harness runs:

- **A night cost about 500 runner-minutes**, about 1 h 40 min side by side.

  | Harness | Minutes |
  |---|---|
  | Vehicle & Tool settings sweep | 97 |
  | Rig job | 79 |
  | Field job (careful + careless) | 75 |
  | Working day | 41 |
  | Path job | 35 |
  | Dump replay | 29 |
  | Turn intervention | 27 |
  | Everything else together | 120 |

- **One engine, five nightlies.** Field, rig, path, turn intervention and imperial job all run
  `JobRun` and grade the same spray and turn checks, about 58 % of the night. One cause showed up
  as the same finding in several reports and was judged several times.
- **Every job was a whole field.** One rig job is about 60 simulated minutes. Checking one pass end
  meant rerunning all of it, 5–10 minutes a run. J1, J8 and P1 were rerun about 10 times in one
  day, each time to read one more number.
- **The slow harnesses ran every night**, whether or not anything they cover had changed.
- **Runner** (the harness Mac mini) hung after a macOS update, and runs fell back to the main Mac,
  where they competed with the app and with each other.

## 2. How we work (in force)

These need no code. They cut the most time.

1. **A unit test first.** Reproduce each finding as a unit test before touching the harness. The
   theme 4 fixes each came down to a test that runs in milliseconds and fails on develop.
2. **Read before rerunning.** Read the saved report, pictures and traces of the run that found it.
   A rerun is for a question the saved run can't answer.
3. **One confirming run per fix**, a short run where one fits (section 3), the whole job last.
4. **Runs go to Runner, never to the main Mac.** Runner is disposable and only for harnesses. When
   it breaks, restart OrbStack, reboot it, or rebuild the image, without asking. Run the fix and
   its base side by side on Runner, from the same image.
5. **Don't build next to a running harness.** A starved process misses its watchdogs. It gave a
   false `AUTOSTEER-DROPPED` on Runner.
6. **Before asking for a merge,** dispatch the nightlies on the branch on GitHub and compare their
   reports with the night's report on develop.

## 3. Done

**#771 (merged 2026-11-11, Worker redeployed as version `533025b1`):**

- **Short runs** (`JobRun`):
  - `AGOPENWEB_SCENARIO_TURNS=N` stops each job as its (N+1)th U-turn starts;
  - `AGOPENWEB_SCENARIO_MINUTES=N` stops it after N simulated minutes, for laps and pivots, which
    make no U-turns;
  - `AGOPENWEB_SCENARIO_FROM_PASS=K` starts each job on pass K, to rework the passes where a
    finding was (`FROM_PASS=28 TURNS=4`);
  - the whole-field checks (finished, left undone) are skipped, and the report says it was a short
    run.

  J1 with two turns runs in 43 s instead of several minutes.
- **One nightly for the five job harnesses.** `job-scenarios.yml` replaces the five workflows. The
  six jobs (field careful, field careless, path, rig, turn, imperial) still run side by side on
  their own runners. A last job writes one combined report listing each finding once, with every
  harness and job it was seen in (`Tools/combine-job-reports.py`). On a two-turn run, 39
  `SPRAYED-IN-HEADLAND` lines became one entry. Manual inputs: `only`, `turns`, `from_pass`.
- **Slow harnesses only when their code changed.** The settings sweep (97 min), working day (41),
  update (17) and dump replay (29) run only when a commit of the last 25 hours touched the paths
  they cover, and every Sunday (`.github/scripts/nightly-fresh.sh`, the paths in each workflow).
  That saves up to about 180 runner-minutes on a quiet night. The Vehicle & Tool operators job
  still runs every night.

A first two-turn run of `job-scenarios.yml` (run 42946668334) took: field careful 4 min, field
careless 11, rig 8, imperial 3, turn 7, and path 26 (see 4.2).

**Runner** is back: a Docker context `runner` on the main Mac reaches its OrbStack (linux/amd64,
as GitHub's runners). The image `aow-harness:latest` fetches any branch, commit or `pr/<n>` from a
GitHub mirror in a volume, so Runner needs no checkout:
`run-harness <ref> <test project> [dotnet test args]`, reports in `/Users/chris/aow-out/<name>`.

## 4. Open

### 4.1 Short runs: the turn the run stopped in (#772)

The first two-turn run failed in the turn job. The run stops as turn N+1 starts, so T4's check,
which needs turn 3 finished, reported a harness failure, and in T11/T12 the manual tap that started
the cut-off turn counted as refused. #772 drops the cut-off turn from judging and turns a check that
waits for a turn the run didn't reach into a note. Open, CI to check, then merge.

### 4.2 A `minutes` input for the path job

The path job's lap and pivot jobs make no U-turns, so `turns` doesn't shorten them: 26 of the 33
minutes of the two-turn run. `job-scenarios.yml` needs a `minutes` input passed as
`AGOPENWEB_SCENARIO_MINUTES`, so a manual short run is short for every harness.

### 4.3 The Runner image in the repo

The Dockerfile and `run-harness.sh` live only in a session's scratch folder, so a lost session
loses the way to rebuild Runner. Move them to `Tools/harness-runner/` with a short README:
- build the image on Runner;
- run one harness;
- run a fix and its base side by side.

### 4.4 Comparing a fix with its base

Every fix is judged against its base, and today that comparison is done by reading two reports
side by side. A script that takes two job reports and lists the findings gone, new and unchanged
(with their numbers, such as m² missed) would make the before/after check one step. It can share
the parsing in `combine-job-reports.py`.

### 4.5 Fail loudly on a busy port

On the main Mac an AgOpenWeb app held UDP 9999. A harness run beside it showed valves shut for up to
192 s, which the same jobs on GitHub didn't. The rig should check its ports at the start and stop
with a clear harness failure when one is taken, instead of producing findings.

### 4.6 Memory findings across machines

The working day's memory figures swing by about 50 MB between runs and machines, against a 40 MB
growth threshold. On Runner #768 showed `NATIVE-MEMORY-GROWS` (49 MB) while develop on the same
machine ended higher (255 MB against 208 MB resident). Decide with the memory theme whether the
threshold should be relative to the run's own early baseline, or the finding needs two runs before
it is a Bug.

### 4.7 Check the saving after a week

After a week of nightlies on the new workflows, add up the runner-minutes per night (weekday and
Sunday) and the time from a finding to a merged fix, and compare them with section 1.

## 5. Not in this plan

- What the harnesses cover and which gaps to close: [HARNESS_IMPROVEMENT_PLAN.md](HARNESS_IMPROVEMENT_PLAN.md).
- Making the app itself faster (`CONTROL-TICK-OVER-10-MS`, memory): the performance and memory
  themes of the nightly findings.
