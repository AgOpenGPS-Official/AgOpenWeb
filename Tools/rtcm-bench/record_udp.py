#!/usr/bin/env python3
"""Record every UDP datagram arriving on a port, with its arrival time.

    record_udp.py PORT OUT.bin

Port 2233 hears the RTCM the app broadcasts (a host receives its own broadcast); port 9999
hears the modules' NMEA and PGNs beside the app (both bind with address reuse).
Record format: little-endian double time, uint16 length, then the datagram.
"""
import socket, struct, sys, time

s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
for opt in (socket.SO_REUSEADDR, socket.SO_REUSEPORT, socket.SO_BROADCAST):
    s.setsockopt(socket.SOL_SOCKET, opt, 1)
s.bind(("", int(sys.argv[1])))
out = open(sys.argv[2], "wb")
while True:
    d, _ = s.recvfrom(4096)
    out.write(struct.pack("<dH", time.time(), len(d)) + d); out.flush()
