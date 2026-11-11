#!/bin/bash
# AgOpenWeb
# Copyright (C) 2026 AgOpenWeb Contributors
# SPDX-License-Identifier: Apache-2.0
# run-harness <ref> <test project folder> [extra dotnet test args...]
# The code: the repo mounted read-only at /repo, or else GitHub through a mirror kept in the
# /cache volume (refreshed under a lock, so parallel runs share it). <ref> is a branch, a commit,
# or pr/<number> (a pull request's head, forks included). Reports go to /out
# (AGOPENWEB_SCENARIO_REPORT and the working day's AGOPENWEB_DAY_REPORT default to /out/report.md);
# AGOPENWEB_* pass through with -e.
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
export AGOPENWEB_SCENARIO_REPORT="${AGOPENWEB_SCENARIO_REPORT:-/out/report.md}"
export AGOPENWEB_DAY_REPORT="${AGOPENWEB_DAY_REPORT:-/out/report.md}"
# A failed build stops here with its errors.
if ! dotnet build "$proj" -c "${CONFIG:-Debug}" --verbosity minimal > /out/build.log 2>&1; then
  grep -E "error" /out/build.log | sort -u | head -20
  echo "== build failed (/out/build.log)"; exit 1
fi
dotnet test "$proj" -c "${CONFIG:-Debug}" --no-build --nologo "$@" 2>&1 | tee /out/test.log | grep -E "Passed!|Failed!" || true
