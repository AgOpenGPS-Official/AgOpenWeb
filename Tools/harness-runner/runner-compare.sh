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
# once as the host's cores allow: a job runs on one core, so the wait is the longest job, not the
# sum of them (SHARDS=0 runs the list in one container, as a run without ONLY does). The base side
# is cached on the host by commit and settings (RESULT_CACHE in run-harness.sh; BASE_CACHE=0 to
# run it again): a second fix against the same base runs the fix side only.
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

# The shards: the codes in AGOPENWEB_SCENARIO_ONLY, or "all" for one container a side.
only="${AGOPENWEB_SCENARIO_ONLY:-}"
shards=()
if [ -n "$only" ] && [ "${SHARDS:-1}" != 0 ]; then
  for c in $(echo "$only" | tr ',' ' '); do shards+=("$c"); done
else
  shards=(all)
fi

# Each run is held to CPUS cores, so runs share the host's threads without starving one another:
# a starved run misses its watchdogs and reports false findings (a false AUTOSTEER-DROPPED,
# 2026-11-10, a build beside an unlimited run). A whole harness gets 4, a shard (one job, about
# one core) 2; at most MAX_RUNS (default the host's threads over CPUS) run at once, the rest wait.
ncpu=$(dk info --format '{{.NCPU}}')
if [ "${shards[0]}" = all ]; then cpus="${CPUS:-4}"; else cpus="${CPUS:-2}"; fi
max="${MAX_RUNS:-$(( ncpu / cpus ))}"; [ "$max" -ge 1 ] || max=1

start() { # side code
  local side=$1 code=$2; shift 2
  local ref=$fix; [ "$side" = base ] && ref=$base
  local args=(run -d --cpus "$cpus" --name "$name-$side-$code" -v aow-cache:/cache "${envs[@]}")
  if [ "$code" != all ]; then args+=(-e "AGOPENWEB_SCENARIO_ONLY=$code"); elif [ -n "$only" ]; then args+=(-e AGOPENWEB_SCENARIO_ONLY); fi
  [ "$side" = base ] && [ "${BASE_CACHE:-1}" != 0 ] && args+=(-e RESULT_CACHE=1)
  dk "${args[@]}" aow-harness:latest "$ref" "$proj" "$@" >/dev/null
}

runs=()
for code in "${shards[@]}"; do runs+=("fix/$code" "base/$code"); done
for item in "${runs[@]}"; do
  waited=0
  while :; do
    busy=$(dk ps --filter ancestor=aow-harness:latest -q | wc -l | tr -d ' ')
    [ "$busy" -lt "$max" ] && break
    [ $waited = 1 ] || { echo "-- the host runs $busy of $max harness containers; $item waits for a slot"; waited=1; }
    sleep 15
  done
  start "${item%/*}" "${item#*/}" "$@"
  echo "== $item started: $name-${item%/*}-${item#*/}"
done

problems=0
for item in "${runs[@]}"; do
  side=${item%/*}; code=${item#*/}; cname="$name-$side-$code"
  exit_code=$(dk wait "$cname")
  dest="$out/$side"; [ "$code" = all ] || dest="$out/$side/$code"
  mkdir -p "$dest"
  dk cp "$cname:/out/." "$dest" >/dev/null
  dk logs "$cname" 2>&1 | grep -E '^== ' > "$dest/run.log" || true
  dk rm "$cname" >/dev/null
  result=$(grep -hE 'Passed!|Failed!' "$dest/test.log" 2>/dev/null | head -1 || true)
  note=""; grep -q '^== cached' "$dest/run.log" && note=" (cached)"
  echo "== $item finished (exit $exit_code)$note: $result"
  # A side whose tests failed may have stopped before its jobs ran: its report is no comparison.
  case "$result" in
    *Passed!*) ;;
    *) echo "!! $item did not pass: read $dest/test.log before trusting the comparison"; problems=1 ;;
  esac
done

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
