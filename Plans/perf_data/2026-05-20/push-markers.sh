#!/usr/bin/env bash
# PERF-05 Phase 1 marker push — drops all 7 marker files into the app's
# Documents/AgValoniaGPS directory on both devices. Restart the app after
# (DiagFlags reads markers once at startup).

set -euo pipefail

IPAD_UDID="d2fcb0323a90ad2954ab501f2603cd7573d99b2a"
IPAD_BUNDLE="com.agvaloniaagps.ios"
ANDROID_SERIAL="R52TB090VAK"

MARKERS=(
  .log_render_timing      # subsystem 1 (existing flag, drives DrawingContextMapControl)
  .perf_state_mirror      # subsystem 2
  .perf_gps_pipeline      # subsystem 3
  .perf_guidance          # subsystem 4 (TrackGuidance + YouTurnGuidance)
  .perf_coverage          # subsystem 5
  .perf_udp               # subsystem 6 (UdpRx + UdpTx)
  .perf_autosteer         # subsystem 7 (AutoSteerRx + AutoSteerTx)
  .perf_apply_gps_cycle   # Phase 2a — UI-thread ApplyGpsCycleResult
)

STAGE=$(mktemp -d)
trap "rm -rf $STAGE" EXIT
for m in "${MARKERS[@]}"; do touch "$STAGE/$m"; done

echo "iPad → $IPAD_BUNDLE Documents/AgValoniaGPS/"
for m in "${MARKERS[@]}"; do
  xcrun devicectl device copy to \
    --device "$IPAD_UDID" \
    --domain-type appDataContainer \
    --domain-identifier "$IPAD_BUNDLE" \
    --source "$STAGE/$m" \
    --destination "Documents/AgValoniaGPS/$m" \
    --quiet
  echo "  pushed $m"
done

echo
echo "Android → shared MyDocuments at /storage/emulated/0/Documents/AgValoniaGPS/"
adb -s "$ANDROID_SERIAL" shell "mkdir -p /storage/emulated/0/Documents/AgValoniaGPS" >/dev/null
for m in "${MARKERS[@]}"; do
  adb -s "$ANDROID_SERIAL" push "$STAGE/$m" "/storage/emulated/0/Documents/AgValoniaGPS/$m" >/dev/null
  echo "  pushed $m"
done

echo
echo "Done. Restart the app on both devices so DiagFlags picks them up."
echo "Look for the [DiagFlags] line in each stream to confirm all 7 are true."
