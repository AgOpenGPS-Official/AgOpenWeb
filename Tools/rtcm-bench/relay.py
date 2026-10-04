#!/usr/bin/env python3
"""TCP relay between AgOpenWeb and an NTRIP caster that misbehaves on a schedule.

    relay.py CASTER_HOST CASTER_PORT SCHEDULE_JSON LOG [LISTEN_PORT=2102]

SCHEDULE_JSON is a list of [start_s, kind, duration_s, param], timed from the first
connection: kind "stall" holds everything from the caster and releases it in one burst;
"trickle" lets it through at `param` bytes per second. Point the app's NTRIP profile at
127.0.0.1:LISTEN_PORT. What TCP hides from the app (loss, retransmits) reaches it exactly
like this: a pause, then a burst.

With CHUNKED=1 the relay answers "HTTP/1.1 200 OK" with Transfer-Encoding: chunked and
sends the caster's stream as chunks whose boundaries fall inside RTCM messages, to check
that the app decodes a chunked reply.

With FIXED_FOR=30 in the environment the schedule is timed from the moment the receiver has
been RTK Fixed for that many seconds, not from the first connection.
"""
import json, os, socket, sys, threading, time
from rtcm import FixWatch

host, port, schedule, log = sys.argv[1], int(sys.argv[2]), json.loads(sys.argv[3]), open(sys.argv[4], "w")
listen = int(sys.argv[5]) if len(sys.argv) > 5 else 2102
start = [None]
chunked = os.environ.get("CHUNKED") == "1"
gate = float(os.environ.get("FIXED_FOR", "0"))
watch = FixWatch() if gate > 0 else None
began = [None]


def note(msg):
    log.write("%.3f %s\n" % (time.time(), msg)); log.flush()


def begin():
    start[0] = time.time()
    note("schedule " + json.dumps([[s, "%s %ds" % (k, d), s + d] for s, k, d, _ in schedule]))
    note("connect (waited %.0f s for RTK Fixed)" % (start[0] - began[0]))


def mode():
    if start[0] is None:
        if watch.fixed_for() >= gate:
            begin()
        return "ok", 0
    t = time.time() - start[0]
    for s, kind, dur, param in schedule:
        if s <= t < s + dur:
            return kind, param
    return "ok", 0


def serve(client):
    caster = socket.create_connection((host, port))
    if began[0] is None:
        began[0] = time.time()
        if watch is None:
            begin()
    if start[0] is not None:
        note("connect")

    def upstream():  # request and GGA, app -> caster
        try:
            while True:
                d = client.recv(4096)
                if not d:
                    break
                caster.sendall(d)
        except OSError:
            pass

    held, lock, done = bytearray(), threading.Lock(), [False]

    def downstream():
        header = b"" if chunked else None
        try:
            while True:
                d = caster.recv(4096)
                if not d:
                    break
                if header is not None:   # swap the caster's reply header for a chunked one
                    header += d
                    end = header.find(b"\r\n\r\n")
                    cut = end + 4 if end >= 0 else (header.find(b"\r\n") + 2 if header.startswith(b"ICY 200") and b"\r\n" in header else -1)
                    if cut < 0:
                        continue
                    d, header = header[cut:], None
                    with lock:
                        held.extend(b"HTTP/1.1 200 OK\r\nNtrip-Version: Ntrip/2.0\r\nTransfer-Encoding: chunked\r\nContent-Type: gnss/data\r\n\r\n")
                if chunked:
                    d = b"".join(b"%x\r\n%s\r\n" % (len(d[i:i + 500]), d[i:i + 500]) for i in range(0, len(d), 500))
                with lock:
                    held.extend(d)
        except OSError:
            pass
        done[0] = True

    threading.Thread(target=upstream, daemon=True).start()
    threading.Thread(target=downstream, daemon=True).start()
    last, budget, tick = "ok", 0.0, time.time()
    try:
        while not done[0]:
            now = time.time()
            kind, param = mode()
            if kind != last:
                note("%s -> %s (held %d bytes)" % (last, kind, len(held))); last = kind
            if kind == "stall":
                tick = now; time.sleep(0.01); continue
            with lock:
                n = len(held)
            if n:
                take = n
                if kind == "trickle":
                    budget += (now - tick) * param
                    take = min(n, int(budget))
                    budget -= max(take, 0)
                if take > 0:
                    with lock:
                        out = bytes(held[:take]); del held[:take]
                    client.sendall(out)
            tick = now; time.sleep(0.005)
    except OSError as e:
        note("end %r" % e)
    note("close"); client.close(); caster.close()


srv = socket.socket()
srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
srv.bind((os.environ.get("LISTEN_HOST", "127.0.0.1"), listen)); srv.listen(4)   # 0.0.0.0 for an app on another device
while True:
    conn, _ = srv.accept()
    threading.Thread(target=serve, args=(conn,), daemon=True).start()
