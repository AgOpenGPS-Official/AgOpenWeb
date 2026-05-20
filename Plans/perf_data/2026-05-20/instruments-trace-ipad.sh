#!/usr/bin/env bash
# PERF-05 Phase 2b — capture an Xcode Instruments Time Profiler trace from
# the iPad app while it's running. Use this to chase the ~14 ms/frame
# iPad "outside-OnRender" cost that pure logging couldn't attribute.
#
# Prerequisites:
#   - iPad connected, paired, developer-mode on.
#   - AgValoniaGPS.iOS running on iPad, ideally already in the S5 state
#     (sim driving, sections off, panel closed) before this script starts
#     the recording window.
#   - xctrace available (ships with Xcode command-line tools).
#
# Usage:
#   1. Manually put the iPad into the S5 state and hold it.
#   2. Run this script. It records for 30 s, then saves a .trace bundle.
#   3. Open the saved .trace in Instruments.app to inspect.
#
# Notes on reading the trace:
#   - Focus on the main / UI thread during the recording window.
#   - Look for big chunks of time in Avalonia binding evaluation,
#     PropertyChanged invocation, Skia draw, or Metal command submission.
#   - Symbols from Mono/.NET will be partly mangled — function names like
#     `wrapper_managed_to_native_…` are common. The C# method name is
#     usually embedded in the mangled name; ignore the wrapper prefix.
#   - For allocation tracking, swap "Time Profiler" for "Allocations" below.

set -euo pipefail

IPAD_UDID="d2fcb0323a90ad2954ab501f2603cd7573d99b2a"
PROCESS="AgValoniaGPS.iOS"   # xctrace --attach matches by process name
TEMPLATE="${TEMPLATE:-Time Profiler}"
DURATION="${DURATION:-30s}"

OUT_DIR="$(cd "$(dirname "$0")" && pwd)/instruments"
mkdir -p "$OUT_DIR"
SAFE_TEMPLATE="$(echo "$TEMPLATE" | tr ' ' '_' | tr '[:upper:]' '[:lower:]')"
TRACE="$OUT_DIR/$(date +%Y-%m-%d_%H%M%S)_${SAFE_TEMPLATE}_s5.trace"

echo "Recording template:  $TEMPLATE"
echo "Recording duration:  $DURATION"
echo "Attaching to process: $PROCESS on iPad $IPAD_UDID"
echo "Trace will be saved:  $TRACE"
echo "  (the app must already be running on the iPad — start with S5 state held)"
echo

xcrun xctrace record \
  --template "$TEMPLATE" \
  --device "$IPAD_UDID" \
  --attach "$PROCESS" \
  --output "$TRACE" \
  --time-limit "$DURATION" \
  --no-prompt

echo
echo "Done. Open in Instruments:"
echo "  open \"$TRACE\""
echo
echo "Alternate templates worth trying once Time Profiler points at a suspect:"
echo "  --template 'Allocations'    (per-allocation stacks, ~2-3x slowdown)"
echo "  --template 'Metal System Trace' (GPU command timing)"
