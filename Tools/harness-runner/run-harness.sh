#!/bin/bash
# AgOpenWeb
# Copyright (C) 2026 AgOpenWeb Contributors
# SPDX-License-Identifier: Apache-2.0
# run-harness <ref> <test project folder> [extra dotnet test args...]
# The code: the repo mounted read-only at /repo, or else GitHub through a mirror kept in the
# /cache volume (refreshed under a lock, so parallel runs share it). <ref> is a branch, a commit,
# or pr/<number> (a pull request's head, forks included). Reports go to /out
# (AGOPENWEB_SCENARIO_REPORT and the working day's AGOPENWEB_DAY_REPORT default to /out/, each report
# under its own name);
# AGOPENWEB_* pass through with -e. With RESULT_CACHE=1 a run that passed is kept in the /cache
# volume by commit, project, configuration, test arguments and AGOPENWEB_* settings, and a run
# with the same key copies it to /out instead of running: the base side of a comparison.
set -euo pipefail
ref="$1"; proj="$2"; shift 2
if [ -d /repo/.git ] || [ -f /repo/.git ]; then
  git clone -q /repo /work/src
  cd /work/src
else
  url="${REPO_URL:-https://github.com/AgOpenGPS-Official/AgOpenWeb.git}"
  mirror=/cache/mirror.git
  (
    flock 9
    [ -d "$mirror" ] || git clone -q --mirror "$url" "$mirror"
    git -C "$mirror" fetch -q --prune origin '+refs/heads/*:refs/heads/*' '+refs/pull/*/head:refs/pull/*/head'
  ) 9>/cache/.lock
  git clone -q "$mirror" /work/src
  cd /work/src
  case "$ref" in pr/*) git fetch -q origin "refs/pull/${ref#pr/}/head" && ref=FETCH_HEAD ;; esac
fi
git checkout -q "$ref"
echo "== $(git log --oneline -1)"
cache_dir=
if [ "${RESULT_CACHE:-0}" = 1 ]; then
  key=$( { echo "$(git rev-parse HEAD) $proj ${CONFIG:-Debug} $*"; env | grep '^AGOPENWEB_' | sort; } | sha1sum | cut -c1-16)
  cache_dir=/cache/results/$key
  if grep -qs 'Passed!' "$cache_dir/test.log"; then
    cp -R "$cache_dir/." /out/
    echo "== cached result $key of this commit and these settings"
    exit 0
  fi
fi
# Each report goes to /out under its own name (Tests/ScenarioShared/ReportPath.cs), so a project
# with several fixtures (the field job harness's six) keeps them all. A ref from before that file
# writes the one /out/report.md, as it always did.
if [ -f Tests/ScenarioShared/ReportPath.cs ]; then out_report=/out/; else out_report=/out/report.md; fi
export AGOPENWEB_SCENARIO_REPORT="${AGOPENWEB_SCENARIO_REPORT:-$out_report}"
export AGOPENWEB_DAY_REPORT="${AGOPENWEB_DAY_REPORT:-$out_report}"
# A failed build stops here with its errors.
if ! dotnet build "$proj" -c "${CONFIG:-Debug}" --verbosity minimal > /out/build.log 2>&1; then
  grep -E "error" /out/build.log | sort -u | head -20
  echo "== build failed (/out/build.log)"; exit 1
fi
dotnet test "$proj" -c "${CONFIG:-Debug}" --no-build --nologo "$@" 2>&1 | tee /out/test.log | grep -E "Passed!|Failed!" || true
# Kept only when it passed: a failed run is run again next time. Copied whole, then renamed into
# place, so a run reading the cache never sees a half-written result.
if [ -n "$cache_dir" ] && grep -qs 'Passed!' /out/test.log && [ ! -d "$cache_dir" ]; then
  mkdir -p /cache/results && rm -rf "$cache_dir.$$" && cp -R /out "$cache_dir.$$" && mv "$cache_dir.$$" "$cache_dir" 2>/dev/null || rm -rf "$cache_dir.$$"
  echo "== result kept as $key"
fi
