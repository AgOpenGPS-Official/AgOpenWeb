#!/usr/bin/env python3
"""Summarise a bench run directory.

    analyze.py RUN_DIR

Reads what is there: events.log (from relay.py or forward.py), rtcm.bin (record_udp.py on
2233), nmea.bin (record_udp.py on 9999) and app.log (timestamped app console). Prints the
episodes, the app's drop messages, the forwarded stream re-framed as the receiver sees it,
and the receiver's fix quality and differential age over time.
"""
import json, os, re, struct, sys
from rtcm import frame, is_observation

run = sys.argv[1]
path = lambda n: os.path.join(run, n)


def records(name):
    raw, i = open(path(name), "rb").read(), 0
    while i + 10 <= len(raw):
        t, n = struct.unpack_from("<dH", raw, i); i += 10
        yield t, raw[i:i + n]; i += n


events = [(float(l.split()[0]), l.split(" ", 1)[1].strip()) for l in open(path("events.log"))]
t0 = events[0][0]
print("== episodes (seconds after the first connection)")
for t, m in events[1:]:
    if not re.search(r"Broken pipe|close$", m):
        print("  %7.1f %s" % (t - t0, m))

# The episodes as scheduled: (label, start, end). The log's first line carries the schedule.
episodes = [(label, a, b) for a, label, b in json.loads(events[0][1].split(" ", 1)[1])]

if os.path.exists(path("app.log")):
    print("== app: drops")
    for l in open(path("app.log"), errors="replace"):
        if re.search(r"dropped|backlog", l, re.I):
            print("  %7.1f %s" % (float(l.split()[0]) - t0, l.split(" ", 1)[1].strip()[:140]))

if os.path.exists(path("rtcm.bin")):
    stream, times, count = bytearray(), [], 0
    for t, d in records("rtcm.bin"):
        stream += d; times += [t] * len(d); count += 1
    msgs, bad, skipped = frame(stream, times)
    print("== forwarded stream: %d datagrams, %d bytes; %d whole messages, %d checksum failures, %d bytes outside a message"
          % (count, len(stream), len(msgs), len(bad), len(skipped)))
    print("  %-16s %6s %6s %8s %7s %s" % ("window", "msgs", "obs", "crcFail", "skipB", "1005/1006"))
    windows = []
    for label, a, b in episodes:
        windows += [(label, a, b), (" 20s after", b, b + 20)]
    for label, a, b in windows:
        m = [x for x in msgs if a <= x[0] - t0 < b]
        print("  %-16s %6d %6d %8d %7d %d" % (label, len(m), sum(is_observation(x[1]) for x in m),
              sum(a <= x - t0 < b for x in bad), sum(a <= x - t0 < b for x in skipped),
              sum(x[1] in (1005, 1006) for x in m)))
    whole = [x for x in msgs if x[0] - t0 >= 0]
    by_type = {}
    for _, mtype, _ in whole:
        by_type[mtype] = by_type.get(mtype, 0) + 1
    print("  types: " + " ".join("%dx%d" % kv for kv in sorted(by_type.items())))
    # The burst after each episode: how much stale data went out before the fresh epoch.
    first_obs = min((x[1] for x in whole if is_observation(x[1])), default=None)
    for label, a, b in episodes:
        sent = sum(x[2] for x in msgs if b <= x[0] - t0 < b + 3)
        epochs = sum(1 for x in msgs if x[1] == first_obs and b <= x[0] - t0 < b + 3)
        print("  3 s after %-14s: %6d bytes forwarded, %d epochs of %s" % (label, sent, epochs, first_obs))

if os.path.exists(path("nmea.bin")):
    rows = []  # (t, fix, age)
    for t, d in records("nmea.bin"):
        if d[:6] in (b"$PAOGI", b"$PANDA"):
            f = d.decode("ascii", "replace").split(",")
            try:
                rows.append((t - t0, int(f[6]), float(f[10] or 0)))
            except (ValueError, IndexError):
                pass
    print("== receiver: %d position sentences, %.0f..%.0f s" % (len(rows), rows[0][0], rows[-1][0]))
    prev = None
    for t, fix, age in rows:
        if fix != prev:
            print("  %7.1f fix -> %d (age %.0f s)" % (t, fix, age)); prev = fix
    names = {1: "single", 2: "DGPS", 4: "Fixed", 5: "Float"}
    print("  %-16s %-22s %8s %s" % ("window", "fix quality seen", "max age", "seconds not Fixed"))
    windows = [("before", 0, episodes[0][1])] if episodes else []
    for label, a, b in episodes:
        windows += [(label, a, b), (" 30s after", b, b + 30)]
    for label, a, b in windows:
        w = [r for r in rows if a <= r[0] < b]
        if not w:
            continue
        seen = "/".join(names.get(x, str(x)) for x in sorted(set(r[1] for r in w)))
        print("  %-16s %-22s %8.0f %.0f" % (label, seen, max(r[2] for r in w),
              sum(1 for r in w if r[1] != 4) / max(1.0, len(w) / (b - a))))
