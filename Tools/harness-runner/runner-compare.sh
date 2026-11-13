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
set -euo pipefail
[ $# -ge 3 ] || { sed -n 5,16p "$0"; exit 2; }
fix="$1"; base="$2"; proj="$3"; shift 3
ctx="${RUNNER_CONTEXT:-runner}"
name="cmp-$(basename "$proj" .Tests | sed 's/^AgOpenWeb\.//')-$(date +%m%d-%H%M%S)"
out="${OUT_DIR:-./harness-out/$name}"
here="$(cd "$(dirname "$0")" && pwd)"
dk() { docker --context "$ctx" "$@"; }

envs=(-e "CONFIG=${CONFIG:-Release}")
while IFS='=' read -r k _; do envs+=(-e "$k"); done < <(env | grep '^AGOPENWEB_' || true)

# Each run is held to CPUS cores (default 4), so up to MAX_RUNS (default 3) share the host's 12
# threads without starving one another: a starved run misses its watchdogs and reports false
# findings (a false AUTOSTEER-DROPPED, 2026-11-10, a build beside an unlimited run). This
# comparison starts two.
cpus="${CPUS:-4}"; max="${MAX_RUNS:-3}"
busy=$(dk ps --filter ancestor=aow-harness:latest -q | wc -l | tr -d ' ')
[ $((busy + 2)) -le "$max" ] || { echo "The host already runs $busy harness container(s) of $max; wait for one to finish."; exit 1; }

for side in fix base; do
  ref=$fix; [ $side = base ] && ref=$base
  dk run -d --cpus "$cpus" --name "$name-$side" -v aow-cache:/cache "${envs[@]}" aow-harness:latest "$ref" "$proj" "$@" >/dev/null
  echo "== $side ($ref): $name-$side"
done
problems=0
for side in fix base; do
  code=$(dk wait "$name-$side")
  mkdir -p "$out/$side"
  dk cp "$name-$side:/out/." "$out/$side" >/dev/null
  dk rm "$name-$side" >/dev/null
  result=$(grep -hE 'Passed!|Failed!' "$out/$side/test.log" 2>/dev/null | head -1 || true)
  echo "== $side finished (exit $code): $result"
  # A side whose tests failed may have stopped before its jobs ran: its report is no comparison.
  case "$result" in
    *Passed!*) ;;
    *) echo "!! $side did not pass: read $out/$side/test.log before trusting the comparison"; problems=1 ;;
  esac
done

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
