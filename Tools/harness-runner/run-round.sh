#!/bin/bash
# AgOpenWeb
# Copyright (C) 2026 AgOpenWeb Contributors
# SPDX-License-Identifier: Apache-2.0
# Every harness, whole runs, a fix against its base, on the harness host: all the comparisons at
# once, each container waiting for room under the host's thread budget (runner-compare.sh), so the
# host is full until the last one ends instead of running one comparison at a time.
#
#   run-round.sh <fix ref> <base ref> [out dir]
#
# The job harness in the nightly's groups, one container per job (careful J1-J7, careless C1-C16,
# rig J8-J20, path P1-P7, turn T1-T12, imperial U0-U2); Vehicle & Tool as its two operators and its
# settings sweep in eight shards (below); the other nine harnesses one container a side.
# Each comparison writes <out>/<name>.log and its reports to <out>/<name>/; the summary at the end
# lists, per comparison, findings new, gone and in both, and every line marked !!.
# GitHub's runners can take the same round at the same time: github-compare.sh.
set -uo pipefail
[ $# -ge 2 ] || { sed -n 5,14p "$0"; exit 2; }
fix="$1"; base="$2"; out="${3:-./harness-out/round-$(date +%m%d-%H%M)}"
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
mkdir -p "$out"; out="$(cd "$out" && pwd)"
export RUN_TIMEOUT="${RUN_TIMEOUT:-240}"
J=Tests/AgOpenWeb.FieldJobScenarios.Tests
codes() { grep -ohE "\"$1[0-9]+\"" "$repo/$J/$2" | tr -d '"' | sort -u -V | paste -sd, -; }

cmp() { # name, then env assignments and runner-compare arguments
  local name=$1; shift
  ( cd "$repo" && echo "=== $name $(date +%H:%M)" \
    && env OUT_DIR="$out/$name" "$@" > "$out/$name.log" 2>&1; echo "=== $name exit $? $(date +%H:%M)" ) &
  sleep 5 # the containers of one comparison are started before the next looks for room
}
rc="$here/runner-compare.sh"
# Vehicle & Tool in pieces: whole it is about 2.5 hours (the settings sweep 1 h 45 of it, one group
# after another), the longest harness by far. The operators run apart, and the sweep in the shards
# of sweep-shards.txt (the longest about 40 minutes), checked against both sides' sweep tables
# first: a table that changed could leave a row in no shard.
V=Tests/AgOpenWeb.VehicleToolScenarios.Tests
tables=()
for ref in "$fix" "$base"; do
  t="$out/SettingsSweep-$(echo "$ref" | tr / -).cs"
  git -C "$repo" show "$ref:$V/SettingsSweep.cs" > "$t" || { echo "!! no SettingsSweep.cs at $ref (git fetch?)"; exit 2; }
  tables+=("$t")
done
python3 "$here/sweep-shards.py" check "${tables[@]}" || { echo "!! the sweep's shards do not split its table: fix sweep-shards.txt"; exit 2; }
SWEEP=$(python3 "$here/sweep-shards.py" only)
# The longest start first (the working day, about 40 minutes, cannot be split), so they are not left
# waiting for threads behind the short ones; the short ones fill the threads that are left.
cmp WorkingDay "$rc" "$fix" "$base" Tests/AgOpenWeb.WorkingDayScenarios.Tests
cmp vt-sweep    AGOPENWEB_SCENARIO_OPERATOR=sweep    AGOPENWEB_SCENARIO_ONLY="$SWEEP" "$rc" "$fix" "$base" $V
cmp vt-careful  AGOPENWEB_SCENARIO_OPERATOR=careful  "$rc" "$fix" "$base" $V
cmp vt-careless AGOPENWEB_SCENARIO_OPERATOR=careless "$rc" "$fix" "$base" $V
cmp WebClient "$rc" "$fix" "$base" Tests/AgOpenWeb.WebClientScenarios.Tests
cmp jobs-careful  AGOPENWEB_SCENARIO_OPERATOR=careful  AGOPENWEB_SCENARIO_ONLY="$(codes J CarefulOperator.cs)" "$rc" "$fix" "$base" $J --filter FullyQualifiedName~FieldJobScenarioTests
cmp jobs-careless AGOPENWEB_SCENARIO_OPERATOR=careless AGOPENWEB_SCENARIO_ONLY="$(codes C CarelessOperator.cs)" "$rc" "$fix" "$base" $J --filter FullyQualifiedName~FieldJobScenarioTests
cmp jobs-rig      AGOPENWEB_SCENARIO_ONLY="$(codes J RigJobs.cs)" "$rc" "$fix" "$base" $J --filter FullyQualifiedName~RigJobScenarioTests
cmp jobs-path     AGOPENWEB_SCENARIO_ONLY="$(codes P PathJobs.cs)" "$rc" "$fix" "$base" $J --filter FullyQualifiedName~PathJobScenarioTests
cmp jobs-turn     AGOPENWEB_SCENARIO_ONLY="$(codes T TurnInterventions.cs)" "$rc" "$fix" "$base" $J --filter FullyQualifiedName~TurnInterventionScenarioTests
cmp jobs-imperial AGOPENWEB_SCENARIO_ONLY="$(codes U ImperialJobs.cs)" "$rc" "$fix" "$base" $J --filter FullyQualifiedName~ImperialJobScenarioTests
for p in SteerWizard GpsNtrip Field DumpReplay Update Isobus MachineModule; do
  cmp "$p" "$rc" "$fix" "$base" "Tests/AgOpenWeb.${p}Scenarios.Tests"
done
wait

echo; echo "== Round: $fix against $base ($out)"
bad=0
for f in "$out"/*.log; do
  n=$(basename "$f" .log)
  line=$(grep -hE '^[0-9]+ new, [0-9]+ gone' "$f" | tail -1)
  marks=$(grep -c '^!!' "$f")
  printf '%-16s %s%s\n' "$n" "${line:-no comparison}" "$([ "$marks" = 0 ] || echo " ($marks marked !!)")"
  { [ "$marks" = 0 ] && [[ "$line" == "0 new, 0 gone"* ]]; } || bad=1
done
exit $bad
