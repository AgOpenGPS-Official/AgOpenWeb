# RTCM forwarding: forward messages, not bytes

**Status:** proposed 2026-10-03. Phase 1 implemented 2026-10-04 (`RtcmFramer`, `RtcmStreamStats`).
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
2. **Backlog rule: newest observations, all station data.** Observation messages (legacy
   1001–1004 / 1009–1012 and MSM 1071–1137) belong to an epoch. When a newer epoch has arrived,
   older epochs that have not started sending are dropped. Every other message is kept and sent
   in order; of several queued copies of one such type, only the newest is kept. This replaces
   both the one-second age rule and the 10,000-byte clear. A hard cap stays as a memory guard.
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
8. **PR #247 is not merged.** Phase 2 removes the rule it tunes. Its author's observation (a
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

### Phase 2: whole messages, epoch-aware backlog
- `RtcmQueue` replaces the byte queue; the age rule and the backlog clear go.
- De-chunk before framing.
- Tests: a backlog of several epochs sends only the newest plus every station message; a
  message in flight is completed; nothing invalid is forwarded; the #169 resume burst (one huge
  read after a pause) ends with one epoch on the wire, not ten.
- **Behaviour change:** bytes that are not valid RTCM are no longer forwarded.

### Phase 3: byte-budget pacing
- Catch-up sending within the minimum gap; tests drive a clock that wakes the sender late.
- Depends on nothing in the firmware, but firmware #32 widens the margin on v26.

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
3. Minimum gap for catch-up: 10 ms assumes the v26 loop polls well inside that. Confirm on the
   bench, or wait for firmware #32.
4. Should messages the receiver cannot use (for example ephemeris 1019/1020 on a caster that
   sends them) be forwarded? Proposed: yes, forward everything valid; the app is not the place
   to second-guess the caster.

## Out of scope

- Serial or USB connection to a receiver.
- NTRIP caster selection, sourcetable browsing, GGA upload policy.
- Firmware changes beyond issue #32.
