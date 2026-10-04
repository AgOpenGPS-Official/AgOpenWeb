#!/usr/bin/env python3
"""Stand-in RTCM forwarder: caster -> UDP 2233, paced like the app, with datagram loss.

    forward.py CASTER_HOST CASTER_PORT MOUNT CREDS_FILE DEST_IP SCHEDULE_JSON LOG

Run it with AgOpenWeb's NTRIP off. It sends 256-byte datagrams 25 ms apart to DEST_IP:2233
(a module address for unicast, or x.x.x.255 for broadcast) and drops a share of them on a
schedule, to measure what datagram loss between the app and the module does to the receiver.
SCHEDULE_JSON is a list of [start_s, duration_s, loss] with loss 0..1; outside it nothing is
dropped. CREDS_FILE holds "user:password".

GAP_MS (default 25) is the gap between datagrams; HOLD_S holds the stream back for that
many seconds first and then sends the backlog at that gap, to see whether a module keeps up
with closely spaced datagrams.

With FIXED_FOR=30 in the environment the schedule starts once the receiver has been RTK
Fixed for that many seconds (it gives up after FIXED_TIMEOUT, default 900 s). The process
exits 20 s after the last episode.
"""
import base64, json, os, random, socket, sys, time
from rtcm import FixWatch

host, port, mount, creds, dest = sys.argv[1], int(sys.argv[2]), sys.argv[3], open(sys.argv[4]).read().strip(), sys.argv[5]
schedule, log = json.loads(sys.argv[6]), open(sys.argv[7], "w")
random.seed(1)


def note(msg):
    log.write("%.3f %s\n" % (time.time(), msg)); log.flush()


tcp = socket.create_connection((host, port))
tcp.sendall(("GET /%s HTTP/1.1\r\nHost: %s\r\nNtrip-Version: Ntrip/2.0\r\nUser-Agent: NTRIP rtcm-bench\r\n"
             "Authorization: Basic %s\r\nConnection: keep-alive\r\n\r\n"
             % (mount, host, base64.b64encode(creds.encode()).decode())).encode())
head = b""
while b"\r\n" not in head:
    head += tcp.recv(1)
if b"200" not in head.split(b"\r\n")[0]:
    sys.exit("caster refused: %r" % head)
tcp.setblocking(False)
udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
udp.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)

gap = float(os.environ.get("GAP_MS", "25")) / 1000.0
hold = float(os.environ.get("HOLD_S", "0"))
gate = float(os.environ.get("FIXED_FOR", "0"))
timeout = float(os.environ.get("FIXED_TIMEOUT", "900"))
watch = FixWatch() if gate > 0 else None
began, t0, queue, next_send, last = time.time(), None, bytearray(), 0.0, None
sent = dropped = 0
end = max((s + d for s, d, _ in schedule), default=0) + 20
while True:
    now = time.time()
    if t0 is None:
        if watch is None or watch.fixed_for() >= gate:
            t0 = now
            note("schedule " + json.dumps([[s, "loss %d%% %ds" % (round(p * 100), d), s + d] for s, d, p in schedule]))
            note("connect dest=%s (waited %.0f s for RTK Fixed)" % (dest, now - began))
        elif now - began > timeout:
            sys.exit("receiver not RTK Fixed for %.0f s within %.0f s (fix %d)" % (gate, timeout, watch.fix))
    elif now - t0 > end:
        note("done (sent %d dropped %d)" % (sent, dropped)); break
    try:
        d = tcp.recv(4096)
        if d == b"":
            break
        queue.extend(d)
    except BlockingIOError:
        pass
    loss = 0.0
    for s, dur, p in schedule:
        if t0 is not None and s <= now - t0 < s + dur:
            loss = p
    if t0 is not None and loss != last:
        note("loss -> %.2f (sent %d dropped %d)" % (loss, sent, dropped)); last = loss
    if queue and now >= next_send and now - began >= hold:
        chunk = bytes(queue[:256]); del queue[:256]
        if random.random() < loss:
            dropped += 1
        else:
            udp.sendto(chunk, (dest, 2233)); sent += 1
        next_send = now + gap
    time.sleep(0.002)
