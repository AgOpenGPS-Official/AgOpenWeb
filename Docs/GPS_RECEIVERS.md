# GPS receivers

How a receiver's position reaches AgOpenWeb, what the app decodes, and how to set a
receiver up. The design and its reasons are in `Plans/GPS_RECEIVER_SENTENCES_PLAN.md`.

## One transport: UDP

The app does not open serial ports on any head. A receiver either has a network port of
its own (Bynav T1-FD, Septentrio mosaic, UB9A0) or sits behind a serial-to-Ethernet
bridge: the AiO board (building `$PANDA` / `$PAOGI`, or in passthrough), a Teensy or ESP32
doing nothing but serial-to-UDP, or the HAT daemon. The app side is the same for all of
them.

The app listens on three UDP ports:

| Port | Use |
|---|---|
| 9999 | The module port: everything an AiO board sends, and a receiver with its own network port |
| 2211 | GPS1 (Ace's convention), for a bridge that keeps two receivers apart |
| 2222 | GPS2 |

The System Data card (Network IO → GPS → More) names the source as `address · port`.

### Bridge contract

For HAT, Teensy and ESP32 firmware that forwards a receiver's serial output:

1. **Receiver → app.** Send to UDP 9999 (or 2211 / 2222) on the app's subnet, broadcast or
   unicast. Each datagram holds one or more **whole** lines, `$…*hh\r\n` (or a `#…*crc`
   log), never a partial line. Preferred: one line per datagram, sent as soon as the line is
   complete, which is what the AiO passthrough does.
2. **App → receiver.** Listen on UDP 2233 and write every datagram's bytes to the receiver's
   serial port unchanged. These are the RTCM corrections: datagrams of at most 512 bytes,
   at least 10 ms apart (`Plans/RTCM_FORWARDING_PLAN.md`), so a 1 KB buffer and a
   460800 baud link are enough.

A bridge that batches an epoch's burst into one datagram is fine. A bridge that cuts a line
across two datagrams is tolerated (the app joins the pieces) but it is a bug in the bridge;
the card's "Lines joined" counter points at it.

## What the app decodes

Every accepted sentence or sentence set yields one fix per receiver epoch. The app only
decides *when* a fix is used, never *what* is in it: nothing is paired across sentences
from different devices on the host (that is AgIO's GGA + IMU-PGN problem, which no
non-real-time host can do deterministically).

| Family | Sentences | From | Heading | Roll | HDOP / age |
|---|---|---|---|---|---|
| AiO | `$PANDA` (single antenna + IMU), `$PAOGI` (dual antenna) | AiO board, Ace Nav | IMU, or dual antenna | IMU, or antenna baseline | yes |
| KSXT | `$KSXT` | Bynav T1-FD, Unicore UM982 (direct or via passthrough) | dual antenna | baseline pitch, when fixed | no: both read "—" |
| Standard set | `GGA` or `GNS`, plus `VTG`, `HPR`, `HDT`, `THS` in any combination | UM982, Septentrio, F9P via a bridge | `HPR`/`HDT`/`THS` from the receiver (`GP`, `GN`…); from an IMU talker (`IN`, `HE`) it is the IMU heading | `HPR` pitch, when fixed | from `GGA`/`GNS` |
| INSPVAX | `#INSPVAXA`, `#INSPVAA` (long header) or `%INSPVAXSA`, `%INSPVASA` (short header) | Unicore UM981 (RTK + INS, single antenna) via a bridge | the INS azimuth | the INS roll | no: both read "—" |

### The standard set: epochs

A receiver printing `GGA`+`VTG`+`HPR` spreads one fix over three sentences. The app groups
them by the receiver's epoch: sentences carrying a UTC (`GGA`, `GNS`, `HPR`) belong to the
epoch with that UTC; sentences without one (`VTG`, `HDT`, `THS`) attach to the open epoch.

- **Warm-up.** For the first three epochs the fix is emitted when the next epoch starts
  (one epoch of lag), while the app learns which sentences the receiver prints per epoch.
- **Learned.** From then on the fix is emitted the moment the last sentence of the set
  arrives: no timer, no lag. The status bar shows the family as the receiver prints it,
  e.g. `GGA+VTG+HPR`.
- **A lost sentence.** If a member never comes, the next epoch's start closes the epoch
  with that member absent: heading invalid, roll 0, speed 0 for that fix (never a value
  carried over from the previous epoch). The card counts it under "Incomplete epochs".
- **Reconfigured.** If the receiver's set changes, it is relearned after three alike epochs.

`VTG`'s true track is only used through the fix-to-fix heading, like a single-antenna
`$PANDA` without an IMU. `HPR`'s pitch is the vehicle's roll with the antennas across the
cab (the AgOpenGPS convention); it is taken only when the heading solution is RTK fixed.

### INSPVAX

The UM981 fuses GNSS and its own IMU and prints one `#INSPVAXA` per epoch: position,
velocity, roll, pitch and azimuth. The app takes it as one fix: azimuth as the heading (no
IMU fusion on the host), speed from the north and east velocities, roll and pitch from the
INS. The fix quality follows the position type (`INS_RTKFIXED` 4, `INS_RTKFLOAT` 5,
`INS_PSRDIFF` 2, `INS_PSRSP` 1), and is none while the INS status is `INS_INACTIVE`. The
frame's CRC-32 is checked; a mismatch counts as a bad checksum.

### What the System Data card shows

| Row | Meaning |
|---|---|
| GPS rate | Fixes per second |
| Missed sentences | Fixes that did not come, judged from the learned rate |
| Not accepted | Lines the parser refused, with how many failed the checksum (a corrupted or mis-split line: look at the bridge) and how many are sentences this build does not decode (look at the receiver's output set) |
| Lines joined | Lines a bridge cut across datagrams and the app joined |
| Bytes dropped | Bytes in GPS text that belonged to no line |
| Incomplete epochs | Standard-set epochs closed with a learned member missing |
| Source | The sender's address and the port |

## Setting up a receiver

### Bynav T1-FD / Unicore UM982, dual antenna

Reviewed against the Unicore N4 reference manual R1.4 on a T1-FD (2026-10-09):

```
CONFIG HEADING FIXLENGTH
CONFIG HEADING LENGTH 160 20     # baseline in cm, then the tolerance in cm; measure it
CONFIG HEADING OFFSET 90         # antennas across the cab: the receiver applies this itself
CONFIG RTK TIMEOUT 60            # the only stale-corrections guard on $KSXT, which has no age field
KSXT 0.1                         # one sentence per epoch, or:
GPGGA 0.1
GPVTG 0.1
GPHPR 0.1
```

With `HEADING OFFSET` set on the receiver, the app's Dual Heading Offset stays 0. The
receiver's `SMOOTH` settings do their own filtering; the app adds none.

### Unicore UM981, single antenna with INS

```
INSPVAXA 0.1
```

Through the AiO passthrough or any bridge; the `#` frame passes unchanged.

### A receiver on a USB or serial port, no board

`Tools/serial-to-udp.py` is the reference bridge: it reads the port, sends each whole line
as one datagram to the app's GPS port, and writes what arrives on 2233 back to the port.
With the app on the same machine:

```
pip install pyserial
Tools/serial-to-udp.py /dev/tty.usbserial-1420 --baud 115200
```

Add `--to 192.168.5.255:9999` when the app runs on another host. `Tools/nmea-capture.py`
beside it records what the receiver prints, which is what a bug report or a new receiver
family needs.

### An AiO board

Nothing to set up: the board builds `$PANDA` (with its IMU) or `$PAOGI` (dual antenna) and
sends one per epoch to 9999. With "GPS-UDP passthrough" on, the board forwards the
receiver's own sentences instead, and the app decodes those as above.
