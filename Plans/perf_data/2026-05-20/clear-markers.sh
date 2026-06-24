#!/usr/bin/env bash
# PERF-05 Phase 1 marker cleanup — removes the 7 marker files from both
# devices so the app stops emitting *-PERF lines. Run after the capture.
#
# Apple's devicectl doesn't have a direct "rm file" — instead we push an
# empty marker over to overwrite, then the trick is that DiagFlags only
# checks File.Exists, so we actually need to delete. easier route on iOS:
# uninstall + reinstall the app. For now we instead leave the markers and
# accept that subsequent launches will keep PERF emission on until the next
# reinstall (cheap on iPad).
#
# Android we can delete directly via adb shell rm.

set -euo pipefail

IPAD_UDID="d2fcb0323a90ad2954ab501f2603cd7573d99b2a"
IPAD_BUNDLE="com.agopenweb.ios"
ANDROID_SERIAL="R52TB090VAK"

MARKERS=(
  .log_render_timing
  .perf_state_mirror
  .perf_gps_pipeline
  .perf_guidance
  .perf_coverage
  .perf_udp
  .perf_autosteer
)

echo "Android — deleting marker files via adb shell rm"
for m in "${MARKERS[@]}"; do
  adb -s "$ANDROID_SERIAL" shell "rm -f /storage/emulated/0/Documents/AgOpenWeb/$m" || true
  echo "  rm $m"
done

echo
echo "iPad — devicectl does not expose remote rm cleanly. To clear iPad markers:"
echo "  1. Stop the app on the iPad."
echo "  2. Reinstall: mlaunch --installdev … (overwrites the bundle but"
echo "     not the data container)."
echo "  3. Or fully uninstall + reinstall via the Home screen / Xcode if you"
echo "     want a clean Documents/AgOpenWeb."
echo
echo "Practical: leave iPad markers in place until the next reinstall — the"
echo "perf logging is harmless when no analyst is reading it."
