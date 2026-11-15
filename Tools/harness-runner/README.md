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

Each container has its own network, so runs side by side don't share UDP 9999/8888. They do share
the CPU, and a starved run misses its watchdogs and reports it (a false `AUTOSTEER-DROPPED`,
2026-11-10, a build beside an unlimited run). So a whole harness gets `--cpus 4` and a shard
`--cpus 2`, and `runner-compare.sh` starts at most the host's threads over that many at once (12 on
the 6-core Mac mini: three whole runs or six shards), the rest waiting for a slot (`CPUS`, `MAX_RUNS`
to change them); a run started by hand needs `--cpus 4` too.

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
