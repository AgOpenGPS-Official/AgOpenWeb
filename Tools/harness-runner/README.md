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
- `runner-compare.sh`: one harness on a fix and on its base side by side, the two `/out` folders
  copied back, and `Tools/compare-reports.py` listing the findings new, gone and in both, for each
  report by name (a project with several fixtures, the field job harness's six, writes one each:
  `Tests/ScenarioShared/ReportPath.cs`; a fixture with none of `AGOPENWEB_SCENARIO_ONLY`'s jobs writes
  none). It exits 1, with the reason marked `!!`, when a side did not pass, a report is on one side
  only or there is none: a comparison that read the wrong report looked like a quiet run (2026-11-13).

Each container has its own network, so runs side by side don't share UDP 9999/8888. They do share
the CPU, and a starved run misses its watchdogs and reports it (a false `AUTOSTEER-DROPPED`,
2026-11-10, a build beside an unlimited run). So every run gets `--cpus 4`, and at most three run at
once on the 6-core, 12-thread Mac mini: a comparison (two) and one more. `runner-compare.sh` does both
(`CPUS`, `MAX_RUNS` to change them); a run started by hand needs `--cpus 4` too.

## Set up

```bash
docker context create runner --docker host=ssh://<user>@<host>   # once, on the development machine
docker --context runner build -t aow-harness:latest Tools/harness-runner
```

The host needs `docker` on the PATH of SSH sessions (on macOS with OrbStack, `/usr/local/bin` in
`~/.zshenv`).

## Run

```bash
# A fix against develop: J1 and J8, two U-turns each (Docs: AGOPENWEB_SCENARIO_* in CLAUDE.md)
AGOPENWEB_SCENARIO_ONLY=J1,J8 AGOPENWEB_SCENARIO_TURNS=2 \
  Tools/harness-runner/runner-compare.sh fix/my-branch develop \
  Tests/AgOpenWeb.FieldJobScenarios.Tests --filter FullyQualifiedName~FieldJobScenarioTests

# One run by hand
docker --context runner run -d --cpus 4 --name j1 -v aow-cache:/cache -e CONFIG=Release \
  -e AGOPENWEB_SCENARIO_ONLY=J1 aow-harness:latest develop Tests/AgOpenWeb.FieldJobScenarios.Tests
docker --context runner wait j1 && docker --context runner cp j1:/out ./j1 && docker --context runner rm j1
```

`AGOPENWEB_*` variables and `CONFIG` (`runner-compare.sh` defaults it to Release) pass through to
the run.
