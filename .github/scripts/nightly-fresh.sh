#!/bin/bash
# AgOpenWeb
# Copyright (C) 2026 AgOpenWeb Contributors
# SPDX-License-Identifier: Apache-2.0
# Should this nightly harness run? Writes run=true|false to $GITHUB_OUTPUT.
#
#   nightly-fresh.sh <nightly true|false> [path ...]
#
# - Not a nightly (a manual dispatch): always.
# - No commit on the branch in the last 25 hours: no.
# - With paths (the slow harnesses that rarely change, #harness-speed): only when a commit in the
#   last 25 hours touched one of them, and every Sunday (UTC) whatever changed, so nothing goes
#   more than a week unrun. Without paths: every night with a commit.
# Needs the last day's history (actions/checkout with fetch-depth of a few hundred).
set -euo pipefail
nightly="$1"; shift
out() { echo "run=$1" >> "$GITHUB_OUTPUT"; [ -n "${2:-}" ] && echo "$2" >> "$GITHUB_STEP_SUMMARY"; exit 0; }
[ "$nightly" != "true" ] && out true
last=$(git log -1 --format=%ct HEAD)
[ $(( $(date +%s) - last )) -ge 90000 ] && out false "No commit on develop in the last 25 hours; nothing to run."
[ $# -eq 0 ] && out true
[ "$(date -u +%u)" = "7" ] && out true "Sunday: the weekly full run."
changed=$(git log --since="25 hours ago" --format=%h -- "$@" | head -20)
[ -n "$changed" ] && out true "Run: commits in the last 25 hours touched what this harness covers ($(echo $changed | tr '\n' ' '))."
out false "Nothing this harness covers changed in the last 25 hours (paths: $*); it runs again when something does, and every Sunday."
