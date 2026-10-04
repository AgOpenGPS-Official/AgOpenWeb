# RTCM forwarding: forward messages, not bytes

**Status:** proposed 2026-10-03. Phase 1 implemented 2026-10-04 (`RtcmFramer`, `RtcmStreamStats`); Phase 2 implemented 2026-10-04 (`RtcmQueue`, `ChunkedDecoder`); Phase 3 implemented 2026-10-04 (lateness credit in `RtcmQueue`).
**Prompted by:** PR #247 (stale-drop tuning in `RtcmPacer`) and reports of receivers stuck in
RTK Float / DGPS while NTRIP is connected. Reviewing that PR showed the drop rule is being tuned
at the wrong layer: the forwarder does not know where an RTCM message starts or ends.
**Related:** #169 (pacing, the AiO stalled on a burst), #216 (live module subnet), #218 (module
flapping on Wi-Fi during NTRIP), firmware issue
[Firmware_Teensy_AiO_26#32](https://github.com/AgOpenGPS-Official/Firmware_Teensy_AiO_26/issues/32).

## What happens today

`NtripClientService` reads the caster's TCP stream and hands each read to `RtcmPacer`, which
queues the raw bytes and releases them as 256-byte UDP datagrams, one every 25 ms, to
`<module subnet>.255:2233`. Two rules drop data: the whole queue is cleared past 10,000 bytes,
and bytes queued for over one second are discarded.

- **The forwarder is byte-blind.** A TCP read is not aligned to RTCM messages, so both drop
  rules can cut a message in half. The receiver discards a cut message on its checksum.
- **Every message is treated alike.** The station position (1005/1006) arrives every 10–30 s and
  an observation epoch every second; a backlog clear throws both away.
- **One datagram per timer wake.** A late timer (`Task.Delay(25)` taking 40–50 ms on Android or
  Windows) lowers throughput instead of catching up.
- **RTCM is broadcast.** A Wi-Fi access point repeats every broadcast over the air at its
  lowest rate, which costs airtime the steering traffic needs.
- **`Transfer-Encoding: chunked` is not handled.** The request is HTTP/1.1 with
  `Ntrip-Version: Ntrip/2.0`; a caster that answers chunked would have its chunk-size lines
  forwarded inside the stream. Not seen in a report yet; to be confirmed in Phase 1.
- **Little to diagnose with.** The log has byte totals. Nothing says which messages arrive,
  whether any were dropped and why, or how old the receiver thinks its corrections are.

## What the modules do with it

Read from the three official firmwares (AiO v26, AiO v4 RVC, AiO v4 I2C); not run on a board.

| | AiO v26 | v4 RVC | v4 I2C |
|---|---|---|---|
| UDP 2233, unicast accepted | yes | yes | yes |
| Parses RTCM | no | no | no |
| Datagram over 512 bytes | dropped whole | cut to 512 | overruns a 512-byte buffer |
| Receiver serial link | 460800 baud | 460800 baud | 460800 baud |
| Datagrams read per loop pass | one | one | one |
| UDP receive queue | one datagram (firmware #32) | library default | library default |

So: the module is a pipe that writes each datagram to the receiver's serial port. Message
integrity can only be protected in the app. 512 bytes is a hard ceiling. Datagrams need a gap
between them (v26 keeps only the newest one between polls). The serial link carries about
46 KB/s, four times what `RtcmPacer` assumes.

## What the receiver needs

1. Whole messages with a valid checksum.
2. A fresh observation epoch about once a second. A few seconds of age is fine; a missed epoch
   is harmless; a stale epoch sent ahead of a fresh one only adds delay.
3. Every station / antenna / bias message (1005, 1006, 1007, 1008, 1033, 1230). They are rare,
   and without the station position there is no RTK solution at all.

## Decisions

1. **The forwarder works on RTCM messages.** The TCP stream is framed in the app (preamble
   `0xD3`, 10-bit length, CRC-24Q). Only messages with a valid checksum are queued. Nothing is
   ever dropped except as a whole message.
2. **Backlog rule: newest observations, newest station data.** An observation message (legacy
   1001–1004 / 1009–1012 and MSM 1071–1137) replaces the unsent ones of its own type from an
   earlier epoch, matched on the epoch time in its header. A station message (1005–1008, 1013,
   1033, 1230) replaces the unsent one of its type. An ephemeris replaces an unsent
   byte-for-byte copy of itself. Everything else is kept and sent in order. This replaces both
   the one-second age rule and the 10,000-byte clear. A 64 KB cap stays as a memory guard.
   *As built: epochs are matched per message type on the header's epoch time, not on the MSM
   "multiple message" bit. That needs no trust in how a caster sets the bit, and keeps both
   halves of an MSM split in two.*
3. **Datagrams stay at 256 bytes** (AgIO's size, safe on all three firmwares). Datagram
   boundaries need not match message boundaries: the module concatenates them.
4. **Pacing is a byte budget, not a tick.** Nominal rate stays 10 KB/s. A sender that wakes
   late may send the datagrams it owes with a shorter gap (not under 10 ms, about 25 KB/s) until
   it has caught up. A normal epoch's first datagram still goes out at once.
5. **Unicast to the GPS module when its address is known**, broadcast otherwise. The address
   comes from what the app already sees (the scan reply from module 120, the source of the NMEA
   datagrams). A setting keeps "always broadcast" for setups where another device also needs
   the corrections. *Default to be confirmed by Chris: this changes where packets go.*
6. **The receiver's differential age is the measure of success.** It is already parsed from GGA
   field 13. It goes to the NTRIP panel and the bug report dump beside the forwarder's counters.
7. **No AgIO parity goal here.** AgIO is byte-blind too. Its datagram size and its pacing idea
   are kept because the firmwares were written against them.
8. **A stream that is not RTCM 3 is still forwarded.** If no RTCM 3 message is found in the
   first 4 KB of a session (RTCM 2, CMR or a raw receiver format on the mount point), the app
   logs it and forwards the bytes as they come, as it always did.
9. **PR #247 is not merged.** Phase 2 removes the rule it tunes. Its author's observation (a
   message cut mid-stream is lost) is the starting point of this plan and is credited in it.

## Design

```
caster TCP ──► (de-chunk) ──► RtcmFramer ──► RtcmQueue ──► paced sender ──► UDP 2233
                                  │              │              │
                                  └── counters ──┴── drops ─────┴── NtripStatus / log / dump
```

- **`RtcmFramer`** (pure, in `AgOpenWeb.Services`): fed arbitrary byte slices, emits complete
  messages `(type, bytes)`; counts bytes skipped while hunting for a preamble and checksum
  failures. A length field over the 1023-byte maximum, or a failed checksum, resyncs one byte on.
- **`RtcmQueue`** (pure, replaces `RtcmPacer`'s queue): holds whole messages and applies
  Decision 2. Epochs are delimited by the MSM "multiple message" bit, with the MSM epoch time as
  a cross-check and a short arrival gap as the fallback for legacy observation messages. A
  message that has started sending always finishes.
- **Paced sender**: fills datagrams of up to 256 bytes from the queue and applies Decision 4.
  Time comes from `IClock`, as today, so tests do not sleep.
- **Destination**: `IUdpCommunicationService` exposes the GPS module's address with its age;
  the sender asks per datagram, as `CurrentRtcmEndpoint()` does for the subnet today.
- **Status**: per message type (count, seconds since last), messages dropped by reason
  (superseded epoch, duplicate station message, memory guard), checksum failures, bytes skipped,
  queue delay of the last datagram, destination in use, differential age.

## Baseline with today's forwarder (2026-10-04)

Measured with the Phase 1 build on the bench: RTKBase caster on the LAN, AiO v26 board with an
F9P, wired Ethernet. A relay between the app and the caster held the stream back and released
it in one burst ("stall"), or let it through at half its rate ("trickle"). The app's datagrams
were recorded off the wire and re-framed, and the receiver's fix and differential age were
read from its position sentences. The scripts are in `Tools/rtcm-bench`.

| Episode | Pacer | Forwarded stream | Receiver |
|---|---|---|---|
| Steady stream, 1.26 KB/s | no drops | every message whole | RTK Fixed, age 1 s |
| Stall 2 s, 5 s | no drops; the backlog is sent, oldest first | whole | age follows the stall, fix held |
| Stall 15 s, 25 s | backlog clear drops ~12 KB per burst | one message cut per clear | Fixed throughout, age back to 1 s on resume |
| Stall 45 s | watchdog reconnects at 30 s; backlog clear on resume | one message cut | Fixed throughout, age peaked at 45 s |
| Trickle at 600 B/s for 30 s | nothing dropped until the release burst | corrections arrive late | Fixed, age rose to 16 s |

The module leg and long outages were measured with a stand-in forwarder
(`Tools/rtcm-bench/forward.py`: the app's pacing, unicast to the board, datagrams dropped on a
schedule), starting from RTK Fixed:

| Episode | Receiver |
|---|---|
| 2 %, 5 %, 10 % of datagrams lost, 60 s each | Fixed, age 1–2 s |
| 20 %, 40 % lost, 60 s each | Fixed, age up to 3 s |
| No corrections for 90 s | still reports Fixed, age 90 s |
| No corrections for 180 s | still reports Fixed, age 180 s; age back to 1 s on resume |

What this says:

- **The one-second age rule never fired**, in any episode. PR #247 changes that rule, so it
  would have changed nothing here.
- **The 10,000-byte backlog clear is the rule that fires**, on every stall of about 10 s or
  more, and each time it cuts a message in half. Decision 2 replaces it.
- **An F9P rides through a 45 s gap in corrections** and takes the first fresh epoch after a
  burst. Caster-side stalls of this size do not produce RTK Float on this hardware.
- So the Float reports are not explained by forwarding faults on either leg, on this
  hardware. Still to see: the reporters' own streams and receivers (Phase 1 dumps).
- The differential age works as the end-to-end measure. The v26 firmware reports it in whole
  seconds, and 0 both for "under a second" and for "no corrections".
- **Fix quality does not show a loss of corrections.** A stationary F9P kept reporting RTK
  Fixed for three minutes with nothing arriving. Only the differential age moved. Phase 5 (show
  the age, warn on it) matters more than its position in the list suggests.
- **The app already acts on the age.** `GpsFixQualityValidator` marks a fix invalid when the
  age passes `MaxDifferentialAge` (5 s, not settable in the web client). So a caster stall of
  more than five seconds reaches guidance through that check, while the receiver itself is
  still Fixed.
- **Datagram loss on the module leg is tolerated** far beyond anything a working network
  produces: 40 % loss cost two seconds of age.
- **Unicast works on the v26 board**: every stand-in run sent to the board's own address.
- **Time to RTK Fixed on this bench ran from 25 s to never (14 minutes)** on the same clean
  stream, whichever forwarder was used. The bench scripts therefore wait for Fixed before an
  episode. "Stuck in Float" can be the receiver's convergence, with forwarding intact.
- **Not explained:** while in Float with the app forwarding (broadcast), the fix dropped to
  DGPS for one second about every 31 s. No message was cut on the wire at those moments, and it
  did not happen in ten minutes of Float with the stand-in forwarder (unicast).

## Phases (one PR each)

### Phase 1: measure, change nothing
- `RtcmFramer` with unit tests (synthetic messages with real CRC-24Q, split at every byte
  offset, garbage between messages, corrupt checksum, oversize length).
- Run the framer beside the existing pacer: counters only, forwarding untouched.
- Extend the 5-second `[NTRIP]` health line and the bug report dump: message types seen with
  their rates, checksum failures, bytes skipped, differential age.
- Detect a chunked reply and log it. If any caster in use answers chunked, de-chunking moves
  into this phase; otherwise it is done in Phase 2.
- Add the receiver's differential age to `gps_data_log.csv` (last column), so it can be read
  against the fix quality over the five minutes before a report.
- **Outcome:** a dump from a reporter stuck in Float shows whether the stream is complete at
  the app (types, rates, 1005/1006 present) and how old the receiver says the corrections are.
- **Checked against a real caster (2026-10-04):** an RTKBase / ZED-F9P base on the LAN
  (RTKLIB caster, NTRIP 1 reply). 186 s, 1,664 messages, no checksum failures, no skipped
  bytes, no pacer drops. About 1.26 KB/s: MSM7 for five constellations plus legacy 1004/1012
  every second, base position 1005 every 10 s, 1006 and 1230 every 30 s.

### Phase 2: whole messages, epoch-aware backlog (done)
- `RtcmQueue` replaces `RtcmPacer`'s byte queue; the age rule and the backlog clear are gone.
- `ChunkedDecoder` removes chunked transfer encoding before framing.
- Tests: a backlog of several epochs sends only the newest plus the newest station data; a
  message in flight is completed; repeated ephemerides collapse; an MSM split in two keeps both
  halves; the memory guard; a chunked stream split at every byte.
- **Behaviour change:** bytes that are not valid RTCM are no longer forwarded, unless the
  whole stream is not RTCM 3 (Decision 8).

Bench, same caster, board and episodes as the baseline:

| | Baseline | Phase 2 |
|---|---|---|
| Messages cut on the wire, 5 minutes with stalls of 2 to 25 s and a slow link | one per backlog clear | none |
| Bytes sent in the 3 s after a 15 s stall | 10,089 | 6,162 |
| Bytes sent in the 3 s after a 25 s stall | 11,013 | 6,926 |
| Stale epochs sent ahead of the fresh one | up to the 10 KB backlog | none (one message already in flight finishes) |
| Base position after a stall | whatever survived the clear | always, as its newest copy |
| Chunked reply (relay wrapping the real stream) | one message lost per chunk boundary | every message whole |

Seen on the way: this caster sends each GLONASS ephemeris twice back to back, byte for byte.
The queue sends one, so the health line shows a handful of "not sent" messages every 30 s in
steady flow. Harmless.

### Phase 3: byte-budget pacing (done)
- `RtcmQueue` keeps the nominal rate of one datagram per 25 ms as an average. Time the sender
  spent late (a datagram was ready and due, and the sender had not come for it) is credit,
  spent by shortening the next gaps down to 10 ms. At most 250 ms of lateness is made up. An
  idle queue earns nothing, and the first datagram of an epoch still goes at once.
- The send loop spins out the short catch-up waits, because a timer cannot keep them.
- Tests: a sender whose 25 ms waits take 45 ms keeps the nominal average and never goes
  under the minimum gap; a two-second stall of the sender is made up only in part; an idle
  queue earns no credit.

Bench, AiO v26 (the board's own count of datagrams received, from its log, against the
count sent by `Tools/rtcm-bench/forward.py`):

| Gap between datagrams | Sent | Counted by the board |
|---|---|---|
| 10 ms, steady stream | 680 | 680 |
| 10 ms, a 15 s backlog released at once | 661 | 661 |
| 2 ms, a 15 s backlog released at once | 690 | 653 |

So 10 ms is safe on the v26 firmware as it is, and the loss at 2 ms is firmware issue #32
seen on the bench. On the Mac the app's own gaps stay at 21–27 ms: its timers are punctual,
so the catch-up path is exercised there only by the unit tests. Windows and Android, where
the timers are late, are still to be checked on a device.

### Phase 4: unicast
- GPS module address from `UdpCommunicationService`; fall back to broadcast when unknown or
  stale; setting for "always broadcast".
- Verify on a bench board that corrections arrive by unicast on all three firmwares before the
  default changes.

### Phase 5: show it
- NTRIP panel: differential age, message table, a plain warning when the station position or
  observations are missing ("No base position received in the last 60 s").
- New strings through `tr()` and `Tools/i18n-extract.py`.

## Verification

- **Unit:** framer, queue and pacing are pure and clock-driven (as `RtcmPacerTests` is today).
- **Headless:** the fake caster from the verify recipe (python socket on 127.0.0.1) replays a
  recorded MSM7 stream: steady, paused then resumed in one burst, with a corrupted message, and
  with a chunked reply. A UDP listener on 2233 re-frames what it receives and checks that every
  message is whole and that station messages all arrive.
- **Bench:** AiO v26 board with an F9P. Compare differential age and time to RTK Fixed before
  and after each phase, wired and through a Wi-Fi access point from a tablet.
- **Field:** a build to the reporters who see Float, with a dump before and after.

## Open questions

1. Unicast by default, or opt-in for the first release (Decision 5)?
2. Is there a setup where RTCM must reach a device other than the GPS module (a second
   receiver, a radio bridge)? If so the "always broadcast" setting is required, not optional.
3. ~~Minimum gap for catch-up~~ Settled on the bench: the v26 board loses nothing at 10 ms
   (Phase 3).
4. Should messages the receiver cannot use (for example ephemeris 1019/1020 on a caster that
   sends them) be forwarded? Proposed: yes, forward everything valid; the app is not the place
   to second-guess the caster.

## Out of scope

- Serial or USB connection to a receiver.
- NTRIP caster selection, sourcetable browsing, GGA upload policy.
- Firmware changes beyond issue #32.
