#!/bin/bash
# AgOpenWeb
# Copyright (C) 2026 AgOpenWeb Contributors
# SPDX-License-Identifier: Apache-2.0
# Run one harness on a fix and on its base side by side on the harness host, then compare.
#
#   runner-compare.sh <fix ref> <base ref> <test project folder> [dotnet test args...]
#
#   AGOPENWEB_SCENARIO_ONLY=J1,J8 AGOPENWEB_SCENARIO_TURNS=2 \
#     Tools/harness-runner/runner-compare.sh fix/coverage-sections develop \
#     Tests/AgOpenWeb.FieldJobScenarios.Tests --filter FullyQualifiedName~CarefulOperator
#
# Refs are branches, commits or pr/<number>, fetched from GitHub on the host: push first. Every
# AGOPENWEB_* variable set here, and CONFIG (default Release), go to both runs. The containers run
# on the Docker context RUNNER_CONTEXT (default "runner"); their /out folders are copied to
# OUT_DIR (default ./harness-out/<name>), and Tools/compare-reports.py writes compare-<report>.md there
# for each report. It exits 1 when a side did not pass, a report is on one side only, or there is none.
#
# The jobs AGOPENWEB_SCENARIO_ONLY names run one per container (a shard), on each side, as many at
# once as the host's threads allow, counting every comparison running on it (CPU_BUDGET): a job runs on one core, so the wait is the longest job, not the
# sum of them (SHARDS=0 runs the list in one container, as a run without ONLY does). The base side
# is cached on the host by commit and settings (RESULT_CACHE in run-harness.sh; BASE_CACHE=0 to
# run it again): a second fix against the same base runs the fix side only.
#
# Each container is copied to OUT_DIR and removed as soon as it ends, so the host keeps only the
# runs in progress. RUN_TIMEOUT (minutes, default 90) stops a run that hangs; MIN_FREE_GB (default
# 10) holds the next run back while the host's disk is that full.
set -euo pipefail
[ $# -ge 3 ] || { sed -n 5,23p "$0"; exit 2; }
fix="$1"; base="$2"; proj="$3"; shift 3
ctx="${RUNNER_CONTEXT:-runner}"
name="cmp-$(basename "$proj" .Tests | sed 's/^AgOpenWeb\.//')-$(date +%m%d-%H%M%S)"
out="${OUT_DIR:-./harness-out/$name}"
here="$(cd "$(dirname "$0")" && pwd)"
dk() { docker --context "$ctx" "$@"; }

envs=(-e "CONFIG=${CONFIG:-Release}")
while IFS='=' read -r k _; do [ "$k" = AGOPENWEB_SCENARIO_ONLY ] || envs+=(-e "$k"); done < <(env | grep '^AGOPENWEB_' || true)
# The jobs run at 2 ms per fix, about 50x real time (the nightly's 8 ms is 12x, for GitHub's two
# cores): measured 2026-11-15 on seven jobs at 8, 4 and 2, every report's pace check at 0 lag and
# every finding the same. A shard that lags is reported below.
[ -n "${AGOPENWEB_SCENARIO_PACE:-}" ] || envs+=(-e AGOPENWEB_SCENARIO_PACE=2)

# The shards: the entries of AGOPENWEB_SCENARIO_ONLY (comma-separated), or "all" for one container
# a side. An entry is a job code (J1) or name=selector, the selector passed as the shard's ONLY and
# the name used for its container and folder: the Vehicle & Tool sweep's selectors hold "|" and "."
# (sweep-type=tool.type, sweep-module=Module|Approach), which a container name cannot.
only="${AGOPENWEB_SCENARIO_ONLY:-}"
shards=()
if [ -n "$only" ] && [ "${SHARDS:-1}" != 0 ]; then
  IFS=',' read -r -a entries <<< "$only"
  for e in "${entries[@]}"; do shards+=("${e%%=*}"); done
else
  shards=(all)
fi
selector() { # the ONLY a shard runs with
  local e; for e in "${entries[@]}"; do [ "${e%%=*}" = "$1" ] && { echo "${e#*=}"; return; }; done
}

# Each run is held to CPUS cores, so runs share the host's threads without starving one another:
# a starved run misses its watchdogs and reports false findings (a false AUTOSTEER-DROPPED,
# 2026-11-10, a build beside an unlimited run). A whole harness gets 4, a shard (one job, about
# one core) 2; at most MAX_RUNS (default the host's threads over CPUS) run at once, the rest wait.
ncpu=$(dk info --format '{{.NCPU}}')
if [ "${shards[0]}" = all ]; then cpus="${CPUS:-4}"; else cpus="${CPUS:-2}"; fi
# The host's threads are shared by every comparison running on it, not shared out per comparison: a
# container starts when the threads of all the harness containers on the host plus its own fit in
# CPU_BUDGET (default the host's), so several comparisons run at once and fill the host without
# starving a run. MAX_RUNS still caps this comparison's own containers when set.
budget="${CPU_BUDGET:-$ncpu}"
max="${MAX_RUNS:-0}"

start() { # side code
  local side=$1 code=$2; shift 2
  local ref=$fix; [ "$side" = base ] && ref=$base
  local args=(run -d --cpus "$cpus" --name "$name-$side-$code" -v aow-cache:/cache "${envs[@]}")
  if [ "$code" != all ]; then args+=(-e "AGOPENWEB_SCENARIO_ONLY=$(selector "$code")"); elif [ -n "$only" ]; then args+=(-e AGOPENWEB_SCENARIO_ONLY); fi
  [ "$side" = base ] && [ "${BASE_CACHE:-1}" != 0 ] && args+=(-e RESULT_CACHE=1)
  dk "${args[@]}" aow-harness:latest "$ref" "$proj" "$@" >/dev/null
}

# A container is copied and removed the moment it ends, not when the comparison ends: each one
# leaves its clone and build in the host's disk (0.8 GB, a web client one 2.8 GB), and a comparison
# of 72 shards that kept them all filled the host's 80 GB at 15:17 on 20 Nov 2026; every shard
# started after that hung, held the slots, and the comparison waited 21 hours. So the host holds
# only the runs in progress. A run past RUN_TIMEOUT minutes (default 90) is stopped and reported,
# and no run starts while the host has less than MIN_FREE_GB (default 10) free. The comparison's
# containers go with it when it is stopped (Ctrl-C, a killed script).
timeout_s=$(( ${RUN_TIMEOUT:-90} * 60 ))
min_free_kb=$(( ${MIN_FREE_GB:-10} * 1024 * 1024 ))
# The runs in progress: a file each (side.code) holding when it started (macOS's bash 3.2 has no
# associative arrays).
running=$(mktemp -d)
cleanup() {
  local left; left=$(dk ps -aq --filter "name=^$name-" 2>/dev/null || true)
  [ -z "$left" ] || dk rm -f $left >/dev/null 2>&1 || true
  rm -rf "$running"
  [ -z "$ticket" ] || rm -f "$ticket"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

problems=0
collect() { # side/code [stopped]
  local item=$1 why=${2:-} side=${1%/*} code=${1#*/}
  local cname="$name-$side-$code" dest="$out/$side"
  [ "$code" = all ] || dest="$out/$side/$code"
  mkdir -p "$dest"
  local exit_code
  exit_code=$(dk inspect -f '{{.State.ExitCode}}' "$cname" 2>/dev/null || echo "?")
  dk cp "$cname:/out/." "$dest" >/dev/null 2>&1 || true
  dk logs "$cname" 2>&1 | grep -E '^== ' > "$dest/run.log" || true
  dk rm -f "$cname" >/dev/null 2>&1 || true
  rm -f "$running/${side}.${code}"
  local result note=""
  result=$(grep -hE 'Passed!|Failed!' "$dest/test.log" 2>/dev/null | head -1 || true)
  grep -q '^== cached' "$dest/run.log" && note=" (cached)"
  if [ -n "$why" ]; then echo "!! $item $why"; problems=1; return; fi
  echo "== $item finished (exit $exit_code)$note: $result"
  # A side whose tests failed may have stopped before its jobs ran: its report is no comparison.
  case "$result" in
    *Passed!*) ;;
    *) echo "!! $item did not pass: read $dest/test.log before trusting the comparison"; problems=1 ;;
  esac
  # The job harness's pace check: AgOpenWeb's position behind the fixes, in fixes. A run that
  # fell behind was starved of CPU (or paced too fast) and its findings are noise.
  # Only the job harness has the line: elsewhere grep finds nothing, which is no lag (and must not
  # end the script under pipefail, as it did on the GPS/NTRIP harness, 2026-11-15).
  local lag
  lag=$( (grep -h "harness pace check" "$dest"/*.md 2>/dev/null || true) | sed -E 's/.*\| *([0-9]+) \/ .*/\1/' | sort -n | tail -1)
  [ -z "$lag" ] || [ "$lag" = 0 ] || { echo "!! $item: AgOpenWeb ran $lag fix(es) behind the harness (the pace check): a starved run, its findings are noise; raise AGOPENWEB_SCENARIO_PACE or lower MAX_RUNS"; problems=1; }
}
# The runs of this comparison that have ended are collected; one past its time is stopped first.
reap() {
  local f item cname state now
  now=$(date +%s)
  for f in "$running"/*; do
    [ -e "$f" ] || continue
    item=$(basename "$f"); item="${item%%.*}/${item#*.}"
    cname="$name-${item%/*}-${item#*/}"
    state=$(dk inspect -f '{{.State.Status}}' "$cname" 2>/dev/null || echo gone)
    if [ "$state" = exited ] || [ "$state" = dead ]; then collect "$item"
    elif [ "$state" = gone ]; then echo "!! $item: its container is gone"; rm -f "$f"; problems=1
    elif [ $(( now - $(cat "$f") )) -gt "$timeout_s" ]; then
      dk stop -t 10 "$cname" >/dev/null 2>&1 || true
      collect "$item" "ran over ${RUN_TIMEOUT:-90} min and was stopped: read its test.log"
    fi
  done
}
# Threads held by the harness containers running on the host now (their --cpus).
used_cpus() {
  local ids; ids=$(dk ps --filter ancestor=aow-harness:latest -q)
  [ -n "$ids" ] || { echo 0; return; }
  dk inspect -f '{{.HostConfig.NanoCpus}}' $ids | awk '{ s += $1 } END { printf "%d", s / 1000000000 }'
}
# Two comparisons must not both see room for their next container at the same moment: the check
# and the start are made under a lock on this machine (every comparison is started from it). A lock
# older than 5 minutes was left by a comparison that was killed. Two comparisons can find such a lock
# at once, and the second then removes the lock the first has just taken: the first's release finds
# nothing, which must not end it (set -e ended it with an empty log, 28 Nov 2026, the Field
# comparison of a round started after a killed one). At worst one container starts beside another
# in the same moment, one over the budget until the next ends.
lock=${TMPDIR:-/tmp}/aow-runner-slot.lock
release_lock() { rmdir "$lock" 2>/dev/null || true; }
take_lock() {
  until mkdir "$lock" 2>/dev/null; do
    [ -n "$(find "$lock" -maxdepth 0 -mmin +5 2>/dev/null)" ] && release_lock
    sleep 2
  done
}
# The run that has waited longest, across every comparison on this machine, has its threads kept for
# it: a ticket each in the queue folder (when it began to wait, its comparison, its threads), and a
# run that is not the oldest starts only if it leaves room for the oldest. Without it a whole harness
# (4 threads) waited behind the job harness's shards (2 each), which took every pair of threads as
# it came free (round 5's web client waited 50 minutes, 28 Nov 2026). Only the oldest is waited for,
# so the shards fill what is left: strictly first-come (only the oldest may start) left threads
# idle behind a 4-thread run and sent every shard, and every base result from the cache, to the
# back of the queue after each start (the final round's job harness, 9 jobs of 59 in 55 minutes).
# A ticket whose comparison is gone (killed) is removed by the next one to look.
queue=${TMPDIR:-/tmp}/aow-runner-queue
mkdir -p "$queue"
ticket=""
kept_for_oldest() { # threads to leave free for the oldest waiting run: 0 when it is this one
  local t p oldest
  for t in "$queue"/*; do
    [ -e "$t" ] || continue
    p=$(basename "$t" | cut -d- -f2)
    kill -0 "$((10#$p))" 2>/dev/null || rm -f "$t"
  done
  oldest=$(ls "$queue" | sort | head -1)
  if [ -z "$oldest" ] || [ "$oldest" = "$(basename "$ticket")" ]; then echo 0; else echo "$oldest" | cut -d- -f3; fi
}
free_kb() { dk run --rm --entrypoint df aow-harness:latest -Pk / 2>/dev/null | awk 'NR==2 {print $4}'; }

runs=()
for code in "${shards[@]}"; do runs+=("fix/$code" "base/$code"); done
for item in "${runs[@]}"; do
  waited=0
  ticket="$queue/$(date +%s)-$(printf %07d $$)-$cpus-$name-${item%/*}.${item#*/}"
  touch "$ticket"
  while :; do
    reap
    take_lock
    used=$(used_cpus); mine=$(ls "$running" | wc -l | tr -d ' ')
    keep=$(kept_for_oldest)
    if [ $(( used + cpus + keep )) -le "$budget" ] && { [ "$max" = 0 ] || [ "$mine" -lt "$max" ]; }; then
      free=$(free_kb); free=${free:-0}
      [ "$free" -ge "$min_free_kb" ] && break
      [ $waited = 2 ] || { echo "-- the host has $(( free / 1024 / 1024 )) GB free, under ${MIN_FREE_GB:-10}; $item waits"; waited=2; }
    else
      [ $waited = 1 ] || { echo "-- the host's harness containers hold $used of $budget threads ($keep kept for a run waiting longer); $item ($cpus) waits"; waited=1; }
    fi
    release_lock
    sleep 15
  done
  start "${item%/*}" "${item#*/}" "$@"
  rm -f "$ticket"; ticket=""
  release_lock
  date +%s > "$running/${item%/*}.${item#*/}"
  echo "== $item started: $name-${item%/*}-${item#*/}"
done
left() { ls "$running" | wc -l | tr -d ' '; }
while [ "$(left)" -gt 0 ]; do reap; [ "$(left)" -eq 0 ] || sleep 10; done

# The shards' reports, one per job, merged into one findings list per report name and side
# (Tools/combine-job-reports.py), so the comparison reads as a whole run's; each job's full report
# stays in its shard's folder. A code that wrote no report on either side is not a job of this
# project (or the ref is from before it): a problem, not a quiet run.
if [ "${shards[0]}" != all ]; then
  for code in "${shards[@]}"; do
    [ -n "$(ls "$out"/fix/"$code"/*.md "$out"/base/"$code"/*.md 2>/dev/null)" ] || { echo "!! $code: no report on either side (not a job of this project?)"; problems=1; }
  done
  for side in fix base; do
    for r in $( (cd "$out/$side" && ls */*.md 2>/dev/null | xargs -n1 basename | sort -u) || true); do
      parts=()
      for code in "${shards[@]}"; do [ -f "$out/$side/$code/$r" ] && parts+=("=$out/$side/$code/$r"); done
      python3 "$here/../combine-job-reports.py" "$out/$side/$r" "${parts[@]}"
    done
  done
fi

# Every report on either side, compared by name: a project with several fixtures writes one each
# (Tests/ScenarioShared/ReportPath.cs). A report on one side only is a problem, not a quiet run.
# A ref from before that wrote the one report.md; it is paired with the other side's report
# when that side has exactly one.
reports() { (cd "$out/$1" && ls *.md 2>/dev/null) || true; }
for side in fix base; do
  other=base; [ $side = base ] && other=fix
  mine=$(reports $side); theirs=$(reports $other)
  [ "$mine" = report.md ] || continue
  # Its fixtures all wrote that one file, the last one run over the others: only a run of one
  # fixture (a --filter) left the report of the jobs asked for.
  tests=$(grep -hoE 'Total: +[0-9]+' "$out/$side/test.log" 2>/dev/null | grep -oE '[0-9]+' | head -1 || true)
  if [ "${tests:-0}" != 1 ]; then
    echo "!! $side is from before per-fixture reports and ran ${tests:-no} tests into its one report.md: rerun with a --filter for one fixture"
    problems=1
  elif [ -n "$theirs" ] && [ "$theirs" != report.md ] && [ "$(echo "$theirs" | wc -l | tr -d ' ')" = 1 ]; then
    mv "$out/$side/report.md" "$out/$side/$theirs"
    echo "== $side is from before per-fixture reports: its report.md is compared with $theirs"
  fi
done
names=$( (reports fix; reports base) | sort -u)
[ -n "$names" ] || { echo "!! no report on either side; see $out/*/build.log and test.log"; problems=1; }
for r in $names; do
  if [ ! -f "$out/base/$r" ] || [ ! -f "$out/fix/$r" ]; then
    side=base; [ -f "$out/fix/$r" ] && side=fix
    echo "!! $r: only on the $side side"; problems=1; continue
  fi
  cmp="$out/compare-${r%.md}.md"
  python3 "$here/../compare-reports.py" "$out/base/$r" "$out/fix/$r" "$cmp"
  echo "== $r"
  sed -n 1,40p "$cmp"
done
echo "== $out"
[ $problems -eq 0 ] || { echo "!! the comparison is incomplete: see the lines marked !! above"; exit 1; }
