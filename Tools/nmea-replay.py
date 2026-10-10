#!/usr/bin/env python3
"""Replay a Tools/nmea-capture.py file onto the network, datagram by datagram.

Sends each text datagram of a capture to the app's GPS port at the moment it arrived in
the original, so the running app sees what the reporter's bench saw: the same sentences,
the same chunking across datagrams, the same gaps. Binary strangers (`other N bytes …`
lines) are skipped and counted; the capture only holds their first 16 bytes.

Usage:
    Tools/nmea-replay.py capture.txt                       # once, real time, → 127.0.0.1:9999
    Tools/nmea-replay.py capture.txt --to 192.168.5.255:9999   # the app on another host
    Tools/nmea-replay.py capture.txt --speed 4             # four times faster
    Tools/nmea-replay.py capture.txt --speed 0             # as fast as the socket takes it
    Tools/nmea-replay.py capture.txt --loop                # again and again, until Ctrl-C

The datagrams leave this host, so the System Data card's Source shows this machine, not
the receiver the capture came from. The capture's own source column is printed at the
end for the record. The receiver's clock in the sentences is the original one: a replay
is a reproduction of the stream, not a live receiver, and the app's "age" column shows
the capture's correction ages, not today's.

Test fixtures under Tests/AgOpenWeb.Services.Tests/Fixtures/nmea/ are captures in this
format, so any of them can be put on the bench:

    Tools/nmea-replay.py Tests/AgOpenWeb.Services.Tests/Fixtures/nmea/um982-gga-vtg-hpr.txt --loop
"""
import argparse
import socket
import sys
import time


def unescape(text: str) -> bytes:
    """Inverse of nmea-capture.py's escape(): \\r \\n \\t \\\\ and \\xhh back to bytes."""
    out = bytearray()
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c != '\\' or i + 1 >= n:
            out.append(ord(c))
            i += 1
            continue
        e = text[i + 1]
        if e == 'r':
            out.append(13); i += 2
        elif e == 'n':
            out.append(10); i += 2
        elif e == 't':
            out.append(9); i += 2
        elif e == '\\':
            out.append(92); i += 2
        elif e == 'x' and i + 3 < n:
            out.append(int(text[i + 2:i + 4], 16)); i += 4
        else:
            out.append(92); out.append(ord(e)); i += 2
    return bytes(out)


def read_capture(path: str):
    """Yields (seconds, source, payload or None for a binary stranger)."""
    with open(path, 'r', encoding='ascii', errors='replace') as f:
        for line in f:
            line = line.rstrip('\n')
            if not line or line[0] == '#':
                continue
            parts = line.split('\t', 2)
            if len(parts) < 3:
                continue
            seconds = float(parts[0])
            if parts[2].startswith('other '):
                yield seconds, parts[1], None
            else:
                yield seconds, parts[1], unescape(parts[2])


def parse_target(text: str):
    host, _, port = text.rpartition(':')
    if not host or not port.isdigit():
        raise argparse.ArgumentTypeError(f'expected host:port, got {text!r}')
    return host, int(port)


def replay(datagrams, sock, target, speed: float) -> tuple[int, int, float]:
    """Sends the datagrams at their original spacing divided by speed. Returns (sent, skipped, elapsed)."""
    start = time.monotonic()
    first = None
    sent = skipped = 0
    for seconds, _, payload in datagrams:
        if first is None:
            first = seconds
        if payload is None:
            skipped += 1
            continue
        if speed > 0:
            due = start + (seconds - first) / speed
            delay = due - time.monotonic()
            if delay > 0:
                time.sleep(delay)
        sock.sendto(payload, target)
        sent += 1
    return sent, skipped, time.monotonic() - start


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('capture', help='file written by Tools/nmea-capture.py')
    ap.add_argument('--to', type=parse_target, default=('127.0.0.1', 9999),
                    help='host:port to send to (default 127.0.0.1:9999)')
    ap.add_argument('--speed', type=float, default=1.0,
                    help='time multiplier: 1 is real time, 4 is four times faster, 0 is no pauses at all')
    ap.add_argument('--loop', action='store_true', help='replay again when the capture ends, until Ctrl-C')
    args = ap.parse_args()
    if args.speed < 0:
        ap.error('--speed must be 0 or more')

    datagrams = list(read_capture(args.capture))
    if not datagrams:
        print(f'{args.capture}: no datagrams', file=sys.stderr)
        return 1
    span = datagrams[-1][0] - datagrams[0][0]
    sources = sorted({src for _, src, _ in datagrams})

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    if args.to[0].endswith('.255') or args.to[0] == '255.255.255.255':
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)

    host, port = args.to
    print(f'{len(datagrams)} datagrams over {span:.1f} s from {", ".join(sources)}; '
          f'sending to {host}:{port}'
          + (f' at {args.speed:g}x' if args.speed > 0 else ' without pauses')
          + (', looping' if args.loop else ''), file=sys.stderr)
    passes = 0
    total_sent = total_skipped = 0
    try:
        while True:
            sent, skipped, elapsed = replay(datagrams, sock, args.to, args.speed)
            passes += 1
            total_sent += sent
            total_skipped += skipped
            if not args.loop:
                break
    except KeyboardInterrupt:
        pass
    finally:
        sock.close()

    print(f'{passes} pass{"es" if passes != 1 else ""}: {total_sent} datagrams sent, '
          f'{total_skipped} binary strangers skipped', file=sys.stderr)
    return 0


if __name__ == '__main__':
    sys.exit(main())
