# Harness runner

The scenario harnesses in a Docker image, for a machine kept only for harness runs (ours is a Mac
mini running OrbStack, reached as the Docker context `runner`). It is disposable: when Docker hangs,
restart OrbStack, then reboot the machine, then rebuild the image. Don't fall back to running
harnesses on a development machine, where the app and other runs share its ports and its CPU.

- `Dockerfile`: Ubuntu 24.04 (linux/amd64 on an Intel host, as GitHub's runners), .NET as CI pins
  it, Node and Playwright's Chromium for the web client and imperial harnesses, a normal user.
- `run-harness.sh` (the entry point): `run-harness <ref> <test project> [dotnet test args]`. The
  ref is a branch, a commit or `pr/<number>`, fetched from GitHub into a mirror in the `aow-cache`
  volume, so the host needs no checkout: push first. The report, `build.log` and `test.log` go to
  `/out`.
- `runner-compare.sh`: one harness on a fix and on its base side by side, the `/out` folders
  copied back, and `Tools/compare-reports.py` listing the findings new, gone and in both, for each
  report by name (a project with several fixtures, the field job harness's six, writes one each:
  `Tests/ScenarioShared/ReportPath.cs`; a fixture with none of `AGOPENWEB_SCENARIO_ONLY`'s jobs writes
  none). It exits 1, with the reason marked `!!`, when a side did not pass, a report is on one side
  only or there is none: a comparison that read the wrong report looked like a quiet run (2026-11-13).
  - **Shards.** The jobs `AGOPENWEB_SCENARIO_ONLY` names run one per container on each side, as many
    at once as the host has cores for: a job runs on one core at about 12× real time, one after the
    other in one process, so six whole jobs in one container took an hour while the host idled
    (2026-11-15). Sharded, the wait is the longest job. Each shard's full report is in
    `<out>/<side>/<code>/`; per side the shards' findings are merged into one list per report name
    (`Tools/combine-job-reports.py`), which is what is compared. `SHARDS=0` runs the list in one
    container.
  - **The base is cached.** The base side runs with `RESULT_CACHE=1`: a run that passed is kept in
    the `aow-cache` volume by commit, project, configuration, test arguments and `AGOPENWEB_*`
    settings, and the next comparison against the same base with the same settings copies it instead
    of running (`(cached)` in the output; `BASE_CACHE=0` runs it again). The fix side always runs.
  - **The build is cached.** `run-harness.sh` keeps each build (bin/ and obj/ of the tree, one tar)
    in the volume by commit, project and configuration, the newest 15: the shards of one comparison
    build once, under a lock, and the others take it; a later run of the same commit (a debug run,
    a second comparison) skips the build. NuGet packages live in the volume too, restored by one
    container at a time (two restores into one empty folder break each other). The volume is the
    harness host's to fill: `docker --context runner volume rm aow-cache` empties it.

  - **Pace.** The jobs run at `AGOPENWEB_SCENARIO_PACE=2` (2 ms per fix, about 50× real time) unless
    set; the nightly's 8 ms (12×) is for GitHub's two cores. Measured 2026-11-15 on seven jobs at
    8, 4 and 2: every report's pace check at 0 lag, every finding the same. A shard whose report
    shows AgOpenWeb behind the fixes fails the comparison as starved (`!!`).

  - **Named shards.** An entry of `AGOPENWEB_SCENARIO_ONLY` may be `name=selector`: the selector is
    the shard's ONLY, the name its container and folder (`sweep-type=tool.type`; a selector may hold
    `|` and `.`, a container name cannot).
- **A whole round** (every harness, a fix against its base): `run-round.sh <fix> <base> [out]`
  runs each harness once, in one of two places, so a round takes about 40 minutes. GitHub takes
  Vehicle & Tool, the working day, GPS/NTRIP and Update (`GITHUB_PART`), dispatched on the pushed
  branch at the fix and compared with their nightly runs (`BASE_SHAS`, default the base commit;
  `GITHUB_BASES` gives one workflow a base of its own). The host takes the rest, all the comparisons
  at once, the job harness in the nightly's groups and one container a job, the base side from its
  cache when it has run before. The host is six cores: the fix side of the whole round needs about
  90 minutes of it packed perfectly (round 4 took 2 h 18 on it alone), and GitHub runs its part on
  runners of its own. A line per comparison is printed at the end. `GITHUB_PART=` runs everything on
  the host.
- **A round on AWS spot** (`spot-round.sh`): the same round on one 64-core spot instance, started for
  the round and terminated when it ends, fix and base on the same machine and nothing on GitHub.
  `spot-round.sh setup` once (a key pair, a security group, a launch template, and an EBS volume
  holding Docker's data: the image, the base results and the build caches, kept between rounds),
  then `spot-round.sh run <fix> <base> [out]`; `status` and `down` show and end an instance. SSH is
  opened to this machine's address only, the instance shuts itself down after `SPOT_MAX_HOURS`
  (default 4) whatever happens, and a spot instance taken back mid-round leaves its containers
  marked `!!`: run the round again. About a dollar a round; still as long as its longest piece,
  the working day (about 40 minutes at its pace). The AWS permissions it needs are in its header. Vehicle & Tool, 2.5 hours in one
  piece, runs as its two operators and its settings sweep in the shards of `sweep-shards.txt`
  (selected by the harness's own rule, so the base commit's harness runs them too; the longest about
  40 minutes). `sweep-shards.py check` stops the round when the shards do not split both sides'
  sweep tables, every row in one shard; the Vehicle & Tool workflow runs the same shards as its
  matrix. `github-compare.sh start <branch>` dispatches the eleven workflows on the branch (or those
  `WORKFLOWS` names), and `github-compare.sh compare <branch>` compares their reports with the
  nightly runs on develop (`BASE_SHAS` names the base commits). Both exit 1 on anything new, gone or
  marked `!!`.

Each container has its own network, so runs side by side don't share UDP 9999/8888. They do share
the CPU, and a starved run misses its watchdogs and reports it (a false `AUTOSTEER-DROPPED`,
2026-11-10, a build beside an unlimited run). So a whole harness gets `--cpus 4` and a shard
`--cpus 2`, and the host's threads are shared by every comparison running on it, not shared out per
comparison: `runner-compare.sh` starts a container when the `--cpus` of all the harness containers
there plus its own fit in `CPU_BUDGET` (default the host's 12 on the 6-core Mac mini), checked under
a lock on the machine the comparisons are started from, so several comparisons run at once and the
host stays full (`CPUS` changes a container's share, `MAX_RUNS` caps one comparison's containers);
a run started by hand needs `--cpus 4` too and counts against the budget. The run that has waited
longest, across every comparison (a ticket each in `$TMPDIR/aow-runner-queue`), has its threads kept
for it, and the others start only in what is left: without it a whole harness waiting for 4 threads
was passed by the shards, which took each pair of threads as it came free (round 5's web client
waited 50 minutes, 28 Nov 2026); strictly first-come instead left threads idle and the job harness
crawling (the final round's first try).

They share the disk as well: a container keeps its clone and build (about 0.8 GB, a web client one
2.8 GB) until it is removed. `runner-compare.sh` copies each one's `/out` and removes it as soon as
it ends, so the host holds only the runs in progress; a comparison that kept them all to the end
filled the 80 GB disk on 20 Nov 2026, and every run started after that hung for 21 hours. A run
past `RUN_TIMEOUT` minutes (default 90) is stopped and reported, no run starts while the host has
under `MIN_FREE_GB` (default 10) free, and a comparison that is stopped (Ctrl-C) takes its
containers with it. Remove a container started by hand when its run is done (below).

## Set up

```bash
docker context create runner --docker host=ssh://<user>@<host>   # once, on the development machine
docker --context runner build -t aow-harness:latest Tools/harness-runner
```

The host needs `docker` on the PATH of SSH sessions (on macOS with OrbStack, `/usr/local/bin` in
`~/.zshenv`).

## Run

```bash
# A fix against develop: J1 and J8, two U-turns each, one container per job and side
# (Docs: AGOPENWEB_SCENARIO_* in CLAUDE.md). Name the jobs the fix touches and keep TURNS small:
# the nightly on develop is the full check.
AGOPENWEB_SCENARIO_ONLY=J1,J8 AGOPENWEB_SCENARIO_TURNS=2 \
  Tools/harness-runner/runner-compare.sh fix/my-branch develop Tests/AgOpenWeb.FieldJobScenarios.Tests

# One run by hand
docker --context runner run -d --cpus 4 --name j1 -v aow-cache:/cache -e CONFIG=Release \
  -e AGOPENWEB_SCENARIO_ONLY=J1 aow-harness:latest develop Tests/AgOpenWeb.FieldJobScenarios.Tests
docker --context runner wait j1 && docker --context runner cp j1:/out ./j1 && docker --context runner rm j1
```

`AGOPENWEB_*` variables and `CONFIG` (`runner-compare.sh` defaults it to Release) pass through to
the run.
