#!/bin/bash
# AgOpenWeb
# Copyright (C) 2026 AgOpenWeb Contributors
# SPDX-License-Identifier: Apache-2.0
# Every harness, a fix against its base, split between the harness host and GitHub's runners, so
# a round takes about 40 minutes: each harness runs in one place, once.
#
#   run-round.sh <fix ref> <base ref> [out dir]
#
# GitHub runs the harnesses of GITHUB_PART (default vehicle-tool working-day gps-ntrip update, the
# workflows' names without -scenarios.yml; empty runs everything on the host), dispatched on the
# branch whose head is the fix (BRANCH, default the pushed branch at that commit) and compared with
# their nightly runs (github-compare.sh: BASE_BRANCH, default develop, at BASE_SHAS, default the
# base commit; GITHUB_BASES="vehicle-tool=<branch>@<shas> ..." gives a workflow a base of its own).
# The host runs the rest, every comparison at once, each container waiting for room under the
# host's thread budget (runner-compare.sh): the job harness in the nightly's groups, one container
# per job (careful J1-J7, careless C1-C16, rig J8-J20, path P1-P7, turn T1-T12, imperial U0-U2),
# the others one container a side; a base already run is taken from the host's cache.
# Each host comparison writes <out>/<name>.log and its reports to <out>/<name>/, the GitHub part
# <out>/github.log and <out>/github/; the summary at the end lists, per comparison, findings new,
# gone and in both, and every line marked !!.
#
# Why the split: the host is six cores (12 threads), and the round's fix side alone needs about
# 17 thread-hours there, 90 minutes if packed perfectly (round 4 took 2 h 18 on the host alone).
# GitHub runs each workflow on runners of its own, the Vehicle & Tool sweep in parallel shards, so
# its four longest take about 40 minutes there side by side; the job harness, 85 minutes on GitHub's
# two cores at the nightly's pace, is quicker on the host, about 30 minutes at its pace.
set -uo pipefail
[ $# -ge 2 ] || { sed -n 5,26p "$0"; exit 2; }
fix="$1"; base="$2"; out="${3:-./harness-out/round-$(date +%m%d-%H%M)}"
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
mkdir -p "$out"; out="$(cd "$out" && pwd)"
github_part="${GITHUB_PART-vehicle-tool working-day gps-ntrip update}"
on_github() { # the workflow a host comparison stands for is in GITHUB_PART
  local k
  case "$1" in
    vt-*) k=vehicle-tool ;; jobs-*) k=job ;; WorkingDay) k=working-day ;; WebClient) k=web-client ;;
    GpsNtrip) k=gps-ntrip ;; SteerWizard) k=steer-wizard ;; DumpReplay) k=dump-replay ;;
    MachineModule) k=machine-module ;; *) k=$(echo "$1" | tr '[:upper:]' '[:lower:]') ;;
  esac
  [[ " $github_part " == *" $k "* ]]
}
if [ -n "$github_part" ]; then
  fixsha=$(git -C "$repo" rev-parse "$fix^{commit}") || { echo "!! no commit $fix here (git fetch?)"; exit 2; }
  branch="${BRANCH:-$(git -C "$repo" branch -r --points-at "$fixsha" | sed -n 's#^ *origin/##p' | grep -v '^HEAD' | head -1)}"
  [ -n "$branch" ] || { echo "!! $fix is the head of no pushed branch: push it, or set BRANCH (GITHUB_PART= runs all on the host)"; exit 2; }
  head=$(git -C "$repo" ls-remote origin "refs/heads/$branch" | cut -c1-40)
  [ "$head" = "$fixsha" ] || { echo "!! origin/$branch is at ${head:0:8}, not $fix: GitHub would test another commit"; exit 2; }
  started=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  WORKFLOWS="$github_part" "$here/github-compare.sh" start "$branch" > "$out/github.log" 2>&1 \
    || { cat "$out/github.log"; echo "!! the GitHub part did not start"; exit 2; }
  echo "=== GitHub: $github_part on $branch $(date +%H:%M)"
fi
export RUN_TIMEOUT="${RUN_TIMEOUT:-240}"
J=Tests/AgOpenWeb.FieldJobScenarios.Tests
codes() { grep -ohE "\"$1[0-9]+\"" "$repo/$J/$2" | tr -d '"' | sort -u -V | paste -sd, -; }

cmp() { # name, then env assignments and runner-compare arguments
  local name=$1; shift
  on_github "$name" && return
  ( cd "$repo" && echo "=== $name $(date +%H:%M)" \
    && env OUT_DIR="$out/$name" "$@" > "$out/$name.log" 2>&1; echo "=== $name exit $? $(date +%H:%M)" ) &
  sleep 5 # the containers of one comparison are started before the next looks for room
}
rc="$here/runner-compare.sh"
# Vehicle & Tool in pieces: whole it is about 2.5 hours (the settings sweep 1 h 45 of it, one group
# after another), the longest harness by far. The operators run apart, and the sweep in the shards
# of sweep-shards.txt (the longest about 40 minutes), checked against both sides' sweep tables
# first: a table that changed could leave a row in no shard. Only the scenario test, as the
# workflow runs it: the project's hot-path budget test in every part ran ten times, on a busy host.
V=Tests/AgOpenWeb.VehicleToolScenarios.Tests
tables=()
on_github vt-sweep || for ref in "$fix" "$base"; do
  t="$out/SettingsSweep-$(echo "$ref" | tr / -).cs"
  git -C "$repo" show "$ref:$V/SettingsSweep.cs" > "$t" || { echo "!! no SettingsSweep.cs at $ref (git fetch?)"; exit 2; }
  tables+=("$t")
done
[ ${#tables[@]} = 0 ] || python3 "$here/sweep-shards.py" check "${tables[@]}" || { echo "!! the sweep's shards do not split its table: fix sweep-shards.txt"; exit 2; }
SWEEP=$(python3 "$here/sweep-shards.py" only)
# The longest start first (the working day, about 40 minutes, cannot be split), so they are not left
# waiting for threads behind the short ones; the short ones fill the threads that are left.
cmp WorkingDay "$rc" "$fix" "$base" Tests/AgOpenWeb.WorkingDayScenarios.Tests
cmp vt-sweep    AGOPENWEB_SCENARIO_OPERATOR=sweep    AGOPENWEB_SCENARIO_ONLY="$SWEEP" "$rc" "$fix" "$base" $V --filter FullyQualifiedName~VehicleToolScenarioTests
cmp vt-careful  AGOPENWEB_SCENARIO_OPERATOR=careful  "$rc" "$fix" "$base" $V --filter FullyQualifiedName~VehicleToolScenarioTests
cmp vt-careless AGOPENWEB_SCENARIO_OPERATOR=careless "$rc" "$fix" "$base" $V --filter FullyQualifiedName~VehicleToolScenarioTests
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

# The GitHub part: each workflow's run on the branch at the fix, started by this round (an earlier
# run of the same commit, cancelled, would pass for it), once it has ended, compared with its base runs.
bad=0
if [ -n "$github_part" ]; then
  echo "=== GitHub: waiting for $github_part $(date +%H:%M)"
  for k in $github_part; do
    until [ "$(gh run list --workflow "$k-scenarios.yml" --branch "$branch" --limit 1 --json headSha,status,createdAt \
        --jq ".[0] | select(.createdAt >= \"$started\") | \"\\(.headSha) \\(.status)\"")" = "$fixsha completed" ]; do sleep 60; done
  done
  echo "=== GitHub: ended $(date +%H:%M)"
  for k in $github_part; do
    bb="${BASE_BRANCH:-develop}"; bs="${BASE_SHAS:-$(git -C "$repo" rev-parse "$base^{commit}")}"
    for g in ${GITHUB_BASES:-}; do [ "${g%%=*}" = "$k" ] && { v=${g#*=}; bb=${v%%@*}; bs=${v#*@}; bs=${bs//,/ }; }; done
    WORKFLOWS=$k BASE_BRANCH=$bb BASE_SHAS="$bs" "$here/github-compare.sh" compare "$branch" "$out/github" >> "$out/github.log" 2>&1 || bad=1
  done
fi

echo; echo "== Round: $fix against $base ($out)"
for f in "$out"/*.log; do
  n=$(basename "$f" .log)
  line=$(grep -hE '^[0-9]+ new, [0-9]+ gone' "$f" | tail -1)
  marks=$(grep -c '^!!' "$f")
  [ "$n" = github ] && continue
  printf '%-16s %s%s\n' "$n" "${line:-no comparison}" "$([ "$marks" = 0 ] || echo " ($marks marked !!)")"
  { [ "$marks" = 0 ] && [[ "$line" == "0 new, 0 gone"* ]]; } || bad=1
done
[ -z "$github_part" ] || { echo "-- GitHub (each report against each base run):"; grep -E '^(!!|[a-z-]+-scenarios )' "$out/github.log"; }
exit $bad
