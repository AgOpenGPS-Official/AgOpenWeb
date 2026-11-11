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
# OUT_DIR (default ./harness-out/<name>), and Tools/compare-reports.py writes compare.md there.
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
for side in fix base; do
  code=$(dk wait "$name-$side")
  mkdir -p "$out/$side"
  dk cp "$name-$side:/out/." "$out/$side" >/dev/null
  dk rm "$name-$side" >/dev/null
  echo "== $side finished (exit $code): $(grep -hE 'Passed!|Failed!' "$out/$side/test.log" 2>/dev/null | head -1)"
done
if [ -f "$out/base/report.md" ] && [ -f "$out/fix/report.md" ]; then
  python3 "$here/../compare-reports.py" "$out/base/report.md" "$out/fix/report.md" "$out/compare.md"
  sed -n 1,40p "$out/compare.md"
else
  echo "A report is missing; see $out/*/build.log and test.log."
fi
echo "== $out"
