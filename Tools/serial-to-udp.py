#!/usr/bin/env python3
"""Serial-to-UDP bridge for a GPS receiver on a USB or serial port.

The reference implementation of the bridge contract in Docs/GPS_RECEIVERS.md, for a bench
or a laptop with a receiver on a USB port and no AiO board:

  receiver → app:  every whole line the receiver prints ($…*hh and Unicore #…*crc logs,
                   with their CR LF) is sent as ONE UDP datagram to the app's GPS port, as
                   soon as the line is complete. Nothing is batched, nothing is cut.
  app → receiver:  every datagram that arrives on UDP 2233 (the RTCM corrections the app
                   forwards from NTRIP) is written to the port unchanged.

Usage:
    Tools/serial-to-udp.py /dev/tty.usbserial-1420                 # 115200 baud → 127.0.0.1:9999
    Tools/serial-to-udp.py COM5 --baud 460800
    Tools/serial-to-udp.py /dev/ttyUSB0 --to 192.168.5.255:9999    # the app on another host (broadcast)
    Tools/serial-to-udp.py /dev/ttyUSB0 --no-rtcm                  # one way only

Needs pyserial (`pip install pyserial`). Run `Tools/nmea-capture.py` beside it to record
what the receiver prints; with `--to 127.0.0.1:9999` the app on this machine and the
capture tool both receive every datagram. Binary bytes between lines (UBX, RTCM echoed
by the receiver) are dropped and counted.
"""
import argparse
import socket
import sys
import threading
import time

try:
    import serial  # pyserial
except ImportError:  # pragma: no cover
    serial = None

LINE_LEADS = (ord('$'), ord('#'), ord('%'))
MAX_LINE = 1024


class LineFramer:
    """Cuts a byte stream into whole lines: from $, # or % to the next LF (CR LF kept).
    Bytes that belong to no line are dropped and counted; a line longer than MAX_LINE is
    garbage and dropped too."""

    def __init__(self) -> None:
        self.buf = bytearray()
        self.dropped = 0

    def feed(self, data: bytes):
        self.buf += data
        while True:
            start = -1
            for i, b in enumerate(self.buf):
                if b in LINE_LEADS:
                    start = i
                    break
            if start < 0:
                self.dropped += len(self.buf)
                self.buf.clear()
                return
            if start > 0:
                self.dropped += start
                del self.buf[:start]
            end = self.buf.find(b'\n')
            if end < 0:
                if len(self.buf) > MAX_LINE:
                    self.dropped += len(self.buf)
                    self.buf.clear()
                return
            line = bytes(self.buf[:end + 1])
            del self.buf[:end + 1]
            if len(line) > MAX_LINE:
                self.dropped += len(line)
                continue
            yield line


def serial_to_udp(port, sock, dest, stats, stop):
    framer = LineFramer()
    while not stop.is_set():
        data = port.read(port.in_waiting or 1)
        if not data:
            continue
        for line in framer.feed(data):
            try:
                sock.sendto(line, dest)
                stats['lines'] += 1
            except OSError as e:
                stats['send_errors'] += 1
                stats['last_error'] = str(e)
        stats['dropped'] = framer.dropped


def udp_to_serial(rtcm_sock, port, stats, stop):
    rtcm_sock.settimeout(0.5)
    while not stop.is_set():
        try:
            data, _ = rtcm_sock.recvfrom(4096)
        except socket.timeout:
            continue
        except OSError:
            break
        try:
            port.write(data)
            stats['rtcm_datagrams'] += 1
            stats['rtcm_bytes'] += len(data)
        except Exception as e:  # serial write failed
            stats['last_error'] = str(e)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('port', help='serial port (/dev/tty.usbserial-…, /dev/ttyUSB0, COM5)')
    ap.add_argument('--baud', type=int, default=115200)
    ap.add_argument('--to', default='127.0.0.1:9999', help='where the lines go, ip:port (default 127.0.0.1:9999; a .255 address broadcasts)')
    ap.add_argument('--rtcm-port', type=int, default=2233, help='UDP port to listen on for corrections (default 2233)')
    ap.add_argument('--no-rtcm', action='store_true', help='do not forward corrections to the receiver')
    args = ap.parse_args()

    if serial is None:
        print('pyserial is not installed: pip install pyserial', file=sys.stderr)
        return 2
    host, _, port_text = args.to.rpartition(':')
    dest = (host or '127.0.0.1', int(port_text))

    try:
        ser = serial.Serial(args.port, args.baud, timeout=0.05)
    except Exception as e:
        print(f'cannot open {args.port}: {e}', file=sys.stderr)
        return 1

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    stats = {'lines': 0, 'dropped': 0, 'send_errors': 0, 'rtcm_datagrams': 0, 'rtcm_bytes': 0, 'last_error': ''}
    stop = threading.Event()
    threads = [threading.Thread(target=serial_to_udp, args=(ser, sock, dest, stats, stop), daemon=True)]
    rtcm_sock = None
    if not args.no_rtcm:
        rtcm_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        rtcm_sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        rtcm_sock.bind(('', args.rtcm_port))
        threads.append(threading.Thread(target=udp_to_serial, args=(rtcm_sock, ser, stats, stop), daemon=True))
    for t in threads:
        t.start()

    print(f'{args.port} @ {args.baud} → udp {dest[0]}:{dest[1]}'
          + ('' if args.no_rtcm else f'; udp :{args.rtcm_port} → {args.port}') + '  (Ctrl-C to stop)', file=sys.stderr)
    try:
        while True:
            time.sleep(5)
            print(f'{stats["lines"]} lines sent, {stats["dropped"]} bytes dropped, '
                  f'{stats["rtcm_datagrams"]} correction datagrams ({stats["rtcm_bytes"]} B) written'
                  + (f'; last error: {stats["last_error"]}' if stats['last_error'] else ''), file=sys.stderr)
    except KeyboardInterrupt:
        pass
    finally:
        stop.set()
        for t in threads:
            t.join(timeout=1)
        ser.close()
        sock.close()
        if rtcm_sock:
            rtcm_sock.close()
    return 0


if __name__ == '__main__':
    sys.exit(main())
