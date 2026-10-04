#!/usr/bin/env python3
"""Follow an AiO v26 board's log over its web API and print the lines that match.

    board_log.py BOARD_IP SECONDS [PATTERN="RTCM:"]

The firmware logs "RTCM: N packets from ..." at intervals: the datagrams it took off its
UDP socket since the last line. Summed over a run and compared with what the forwarder sent,
that shows whether the board lost any (it keeps only the newest datagram between two polls of
the socket: Firmware_Teensy_AiO_26 issue 32).
"""
import re, sys, time, urllib.request

board, seconds = sys.argv[1], float(sys.argv[2])
pattern = re.compile(sys.argv[3] if len(sys.argv) > 3 else "RTCM:")
entry = re.compile(r'"timestamp":(\d+),[^{}]*?"message":"([^"]*)"')
seen, total, end = set(), 0, time.time() + seconds
while time.time() < end:
    try:   # the reply is sometimes cut short; take the entries that are whole
        text = urllib.request.urlopen("http://%s/api/logs/data" % board, timeout=3).read().decode("utf-8", "replace")
    except Exception:
        time.sleep(0.5); continue
    for stamp, message in entry.findall(text):
        if stamp in seen or not pattern.search(message):
            continue
        seen.add(stamp)   # one line per board timestamp: a cut reply can garble a repeat
        m = re.search(r"RTCM: (\d+) packets", message)
        total += int(m.group(1)) if m else 0
        print("%.1f board+%.1fs %s" % (time.time(), int(stamp) / 1000.0, message), flush=True)
    time.sleep(0.4)
print("total RTCM packets counted by the board: %d" % total)
