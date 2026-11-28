#!/bin/bash
# AgOpenWeb
# Copyright (C) 2026 AgOpenWeb Contributors
# SPDX-License-Identifier: Apache-2.0
# Every harness on GitHub's runners for a branch, against the nightly runs on its base.
#
#   github-compare.sh start <branch>                 dispatch the eleven harness workflows on it
#   github-compare.sh compare <branch> [out dir]     when they have ended: compare their reports
#
# compare takes each workflow's latest run on the branch and its runs on BASE_BRANCH (default
# develop) at the commits in BASE_SHAS (space-separated, a prefix is enough; default the latest
# NIGHTS, 2, runs there; during the refactor freeze name F's runs, as a run before F may have run a
# harness that has changed since), downloads their reports and lists, per report and per base night,
# findings new, gone and in both (Tools/compare-reports.py). Two base nights show the noise: a
# finding new against one and not the other is the spread, not the fix. The runs use the nightly's
# settings (pace 8 on two cores), so they compare with the nightly and not with the harness host.
# WORKFLOWS (space-separated, the workflow's name without -scenarios.yml: vehicle-tool working-day)
# limits both to those: run-round.sh sends its GitHub part this way.
set -uo pipefail
[ $# -ge 2 ] || { sed -n 5,17p "$0"; exit 2; }
cmd=$1; branch=$2
here="$(cd "$(dirname "$0")" && pwd)"; repo="$(cd "$here/../.." && pwd)"
if [ -n "${WORKFLOWS:-}" ]; then
  workflows=""
  for k in $WORKFLOWS; do
    [ -f "$repo/.github/workflows/$k-scenarios.yml" ] || { echo "!! no workflow $k-scenarios.yml"; exit 2; }
    workflows="$workflows $k-scenarios.yml"
  done
else
  workflows=$(cd "$repo/.github/workflows" && ls *-scenarios.yml)
fi

if [ "$cmd" = start ]; then
  for w in $workflows; do gh workflow run "$w" --ref "$branch" && echo "started $w on $branch"; done
  exit 0
fi

out="${3:-./harness-out/github-$(echo "$branch" | tr / -)-$(date +%m%d-%H%M)}"
mkdir -p "$out"; out="$(cd "$out" && pwd)"
base_branch="${BASE_BRANCH:-develop}"
bad=0
for w in $workflows; do
  key=${w%.yml}
  fixrun=$(gh run list --workflow "$w" --branch "$branch" --limit 1 --json databaseId,status,conclusion \
    --jq '.[0] | "\(.databaseId) \(.status) \(.conclusion)"')
  set -- $fixrun
  if [ "${2:-}" != completed ]; then echo "!! $key: no ended run on $branch (${2:-none})"; bad=1; continue; fi
  [ "${3:-}" = success ] || { echo "!! $key: the run on $branch ended $3"; bad=1; }
  mkdir -p "$out/$key/fix"
  gh run download "$1" -D "$out/$key/fix" -p '*report*' -p '*combined*' >/dev/null 2>&1
  [ -n "$(find "$out/$key/fix" -name '*.md' 2>/dev/null)" ] || { echo "!! $key: the run on $branch left no report (skipped?)"; bad=1; continue; }
  bases=$(gh run list --workflow "$w" --branch "$base_branch" --limit 20 --json databaseId,headSha,status \
    --jq '.[] | select(.status == "completed") | "\(.databaseId) \(.headSha)"')
  n=0
  while read -r id sha; do
    [ -n "$id" ] || continue
    if [ -n "${BASE_SHAS:-}" ]; then
      hit=0; for s in $BASE_SHAS; do [[ "$sha" == "$s"* ]] && hit=1; done; [ $hit = 1 ] || continue
    fi
    n=$((n + 1)); [ $n -le "${NIGHTS:-2}" ] || break
    d="$out/$key/base-${sha:0:8}"; mkdir -p "$d"
    gh run download "$id" -D "$d" -p '*report*' -p '*combined*' >/dev/null 2>&1
    # A report is matched by its artifact and file both: the Vehicle & Tool parts upload reports
    # of one file name, each in its own artifact.
    for fixmd in $(cd "$out/$key/fix" && find . -name '*.md' | sed 's#^\./##'); do
      basemd="$d/$fixmd"
      [ -f "$basemd" ] || { echo "!! $key: $fixmd has no base report at ${sha:0:8}"; bad=1; continue; }
      r=$(dirname "$fixmd"); [ "$r" != . ] || r=$(basename "$fixmd" .md)
      cmpmd="$out/$key/compare-$r-${sha:0:8}.md"
      python3 "$repo/Tools/compare-reports.py" "$basemd" "$out/$key/fix/$fixmd" "$cmpmd" >/dev/null
      line=$(grep -hE '^[0-9]+ new, [0-9]+ gone' "$cmpmd" | head -1)
      printf '%-28s %-44s %s\n' "$key" "$r vs ${sha:0:8}" "$line"
      [[ "$line" == "0 new"* ]] || bad=1
    done
  done <<< "$bases"
  [ $n -gt 0 ] || { echo "!! $key: no base run on $base_branch${BASE_SHAS:+ at $BASE_SHAS}"; bad=1; }
done
exit $bad
