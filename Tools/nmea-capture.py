#!/usr/bin/env python3
"""Capture what a GPS receiver or bridge sends over UDP, datagram by datagram.

Listens on the GPS ports (9999, and Ace's 2211/2222) and writes every datagram that
looks like text (`$`, `#` or `%` first byte) to a capture file, one line per datagram:

    <seconds since start>\t<source ip:port>\t<datagram bytes, line ends escaped>

Datagram boundaries and arrival times are kept, which is what the parser tests need:
they replay a capture one datagram at a time, and a text log would hide how a bridge
chunked the stream. A datagram that starts with printable text but no `$` is the rest
of a line a bridge cut, and is written too. Binary PGNs (0x80 0x81 ...) are counted and
skipped; any other binary datagram (a stranger broadcasting to the port) is written as
`other <hex of its first 16 bytes>` so a rising "Bytes dropped" on the System Data card
can be traced to its sender.

Usage:
    Tools/nmea-capture.py out.txt                 # until Ctrl-C
    Tools/nmea-capture.py out.txt --seconds 60
    Tools/nmea-capture.py out.txt --port 2211     # one port only

Stop the app first: it binds 9999 too, and only one of you gets each datagram unless
SO_REUSEPORT is on, which it is here, so sharing usually works — but a capture taken
beside the running app is the honest one for a bug report anyway.

Captures go under Tests/AgOpenWeb.Services.Tests/Fixtures/nmea/ when they become test
fixtures (see Plans/GPS_RECEIVER_SENTENCES_PLAN.md, Phase 0).
"""
import argparse
import select
import socket
import sys
import time

DEFAULT_PORTS = (9999, 2211, 2222)
TEXT_LEADS = (ord('$'), ord('#'), ord('%'))


def open_socket(port: int) -> socket.socket:
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    if hasattr(socket, 'SO_REUSEPORT'):
        s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEPORT, 1)
    s.bind(('', port))
    s.setblocking(False)
    return s


def escape(data: bytes) -> str:
    # Printable ASCII as is; \r \n \t and anything else escaped so one datagram is one line.
    out = []
    for b in data:
        if b == 13:
            out.append('\\r')
        elif b == 10:
            out.append('\\n')
        elif b == 9:
            out.append('\\t')
        elif b == 92:
            out.append('\\\\')
        elif 0x20 <= b < 0x7F:
            out.append(chr(b))
        else:
            out.append('\\x%02x' % b)
    return ''.join(out)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('out', help='capture file to write')
    ap.add_argument('--port', type=int, action='append',
                    help='UDP port to listen on (repeatable; default 9999, 2211, 2222)')
    ap.add_argument('--seconds', type=float, default=0, help='stop after this long (default: Ctrl-C)')
    args = ap.parse_args()

    ports = tuple(args.port) if args.port else DEFAULT_PORTS
    socks = {}
    for p in ports:
        try:
            socks[open_socket(p)] = p
        except OSError as e:
            print(f'port {p}: {e}', file=sys.stderr)
    if not socks:
        return 1

    start = time.monotonic()
    counts = {'text': 0, 'pgn': 0, 'other': 0}
    sources = set()
    print(f'listening on {", ".join(str(p) for p in socks.values())}; writing {args.out}', file=sys.stderr)
    try:
        with open(args.out, 'w', encoding='ascii') as f:
            f.write(f'# nmea-capture {time.strftime("%Y-%m-%dT%H:%M:%S")} ports={",".join(str(p) for p in socks.values())}\n')
            while True:
                if args.seconds and time.monotonic() - start >= args.seconds:
                    break
                ready, _, _ = select.select(list(socks), [], [], 0.5)
                now = time.monotonic() - start
                for s in ready:
                    data, (ip, port) = s.recvfrom(2048)
                    if not data:
                        continue
                    if data[0] in TEXT_LEADS or data[0] in (10, 13) or 0x20 <= data[0] < 0x7F:
                        counts['text'] += 1
                        sources.add(f'{ip}:{port}->{socks[s]}')
                        f.write(f'{now:.4f}\t{ip}:{port}\t{escape(data)}\n')
                    elif len(data) > 1 and data[0] == 0x80 and data[1] == 0x81:
                        counts['pgn'] += 1
                    else:
                        counts['other'] += 1
                        f.write(f'{now:.4f}\t{ip}:{port}\tother {len(data)} bytes {data[:16].hex(" ")}\n')
                if counts['text'] and counts['text'] % 100 == 0:
                    f.flush()
    except KeyboardInterrupt:
        pass

    elapsed = time.monotonic() - start
    print(f'{elapsed:.1f} s: {counts["text"]} text datagrams written, '
          f'{counts["pgn"]} PGNs and {counts["other"]} other datagrams skipped', file=sys.stderr)
    for src in sorted(sources):
        print(f'  from {src}', file=sys.stderr)
    return 0


if __name__ == '__main__':
    sys.exit(main())
