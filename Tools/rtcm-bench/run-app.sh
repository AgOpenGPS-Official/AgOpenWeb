#!/bin/sh
# Run the headless app through the relay against a real caster and record everything.
#
#   run-app.sh RUN_DIR SCHEDULE_JSON SECONDS
#
# Environment:
#   CASTER_HOST, CASTER_PORT (2101), CASTER_MOUNT   the real caster
#   CASTER_CREDS   file holding "user:password"
#   AGOPENWEB_DATA scratch data root for the app (never your real Documents folder)
#   PYTHON         a python with Playwright installed (default python3)
#
# The app sends its steer and machine configuration to any module on the network when it
# starts, and the firmware saves it. Use a bench board.
set -e
here=$(cd "$(dirname "$0")" && pwd); repo=$(cd "$here/../.." && pwd)
run=$1; schedule=$2; secs=$3
: "${CASTER_PORT:=2101}" "${PYTHON:=python3}"
app="$repo/Platforms/AgOpenWeb.Desktop/bin/Debug/net10.0/AgOpenWeb.Desktop"
pkill -f "AgOpenWeb.Desktop" 2>/dev/null || true
mkdir -p "$run"
python3 "$here/relay.py" "$CASTER_HOST" "$CASTER_PORT" "$schedule" "$run/events.log" & relay=$!
python3 "$here/record_udp.py" 2233 "$run/rtcm.bin" & rec1=$!
python3 "$here/record_udp.py" 9999 "$run/nmea.bin" & rec2=$!
( "$app" --headless 2>&1 | python3 -u -c 'import sys,time
for l in sys.stdin: sys.stdout.write("%.3f %s" % (time.time(), l)); sys.stdout.flush()' > "$run/app.log" ) &
for i in $(seq 1 60); do curl -s -o /dev/null -m 1 http://localhost:5174/ && break; sleep 0.5; done
"$PYTHON" "$here/drive.py" "$secs" "$CASTER_MOUNT" "$CASTER_CREDS" || true
pkill -f "AgOpenWeb.Desktop" 2>/dev/null || true
kill $relay $rec1 $rec2 2>/dev/null || true
sleep 1
python3 "$here/analyze.py" "$run"
