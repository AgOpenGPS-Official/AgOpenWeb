# GPS receivers direct: parsing multi-sentence fixes in the app

**Status:** proposed 2026-10-09. Nothing implemented beyond the groundwork in #288 (decoder
dispatch, `$KSXT`).
**Prompted by:** #288 (`$KSXT` from a Bynav T1-FD through an AiO UDP passthrough) and the
machines behind it; a coming issue with Septentrio captures (GGA/VTG/HDT/AVR); the HAT and
ESP32 steer-only boards, which leave the receiver talking straight to the app.
**Related:** `Plans/RTCM_FORWARDING_PLAN.md` (the corrections half of the same link),
`Plans/IMU_HEADING_WIRING_PLAN.md` (how a heading reaches guidance), #157 (PANDA vs PAOGI
heading), the HAT software plan in the firmware repo.

## Why this changes shape

AgOpenGPS moved GPS handling onto the AiO board because Windows could not be trusted to pair
a GPS epoch with an IMU sample on time. The board reads both, builds one `$PANDA`/`$PAOGI`
per epoch, and the PC only has to decode a single sentence. AgOpenWeb inherited that:
`NmeaParserServiceFast` takes one datagram, expects one whole sentence, and produces one
`VehicleState`.

Two things have moved since:

- **Dual-antenna receivers carry the whole fix themselves.** A UM982, a Bynav T1-FD, a
  Septentrio mosaic-H report position, speed, heading and roll from the same epoch, with
  their own quality flags. There is no IMU to pair; there is nothing for a board to add.
- **The next boards are steer-only.** The Pi HAT and the ESP32 designs drive the wheel and
  read the switches; the receiver's serial stream goes to the app as-is (the HAT daemon or
  the board just bridges serial to UDP). Without the AiO's sentence builder, the app has to
  read what the receiver prints.

Receivers print their fix as either one proprietary sentence (`$KSXT`, `$GPHPD`) or as
several standard ones per epoch (`GGA` + `VTG` + `HPR` on a Unicore, `GGA` + `VTG` + `HDT` +
`AVR` on a Septentrio). The first kind is done (#288: one decoder per sentence). This plan is
about the second kind, and about the ingest path both kinds share once the sender is no
longer an AiO.

## The rule that stays

**One fix = one receiver epoch. The host decides when a fix is used, never what is in it.**

AgIO's `ParseNMEA` accumulates fields from whatever sentences have arrived and flushes on
the next position sentence. That works, but what ends up in a fix depends on how the
datagrams were chunked and when the thread ran: a `VTG` that lands after the `GGA` goes out
with the *next* epoch, so a fix can carry last epoch's speed and heading on one host and not
on another. On a non-real-time OS that is the only place non-determinism can leak into the
data, and it is the thing the AiO was invented to avoid.

So the assembler below groups sentences by the **receiver's epoch**, not by arrival, and
emits when the receiver's burst is complete. Everything downstream — fusion, the 100 Hz
pose estimator, section control — keeps seeing coherent fixes and does not change.

What the app will still not do: pair a GPS stream with a separate IMU stream. A
single-antenna receiver plus an IMU needs the pairing done where timing is controlled — the
AiO today, the HAT daemon under PREEMPT_RT tomorrow — and the app receives the result as
`$PANDA`. That is out of scope here and should stay out.

## Everything is IP

The app has one transport: UDP on the module subnet. It does not open serial ports on any
head, and this plan does not add that. Receivers either have an Ethernet/USB-network port
of their own (Bynav T1-FD, Septentrio mosaic, UB9A0) or sit behind a **serial-to-Ethernet
bridge** — the AiO's passthrough today, a Teensy or ESP32 doing nothing but
serial↔UDP tomorrow, the HAT daemon on the Pi. The bridge is a few dozen lines of firmware;
the app side is the same for all of them.

What the app already does for an address that only ever sends NMEA, with no hello PGN:
the GPS status dot follows the sentence flow (`GpsService.IsGpsDataOk`), the GPS address on
the Network IO page is "where the position sentences come from", and RTCM corrections are
unicast back to that address on port 2233 (`NtripClientService.ResolveRtcmDestination`).
So a bridge needs no module identity; it needs to do two things:

**Bridge contract** (goes into the HAT/ESP32 firmware docs and `Docs/GPS_RECEIVERS.md`):

1. *Receiver → app:* send to UDP 9999 on the app's subnet, broadcast or unicast. Each
   datagram holds one or more **whole** NMEA lines, `$…*hh\r\n`, never a partial line.
   Preferred: one line per datagram, sent as soon as the line is complete (the AiO
   passthrough does this). Lines not starting with `$` (Unicore `#` logs, binary) may be
   dropped or forwarded; the app ignores them.
2. *App → receiver:* listen on UDP 2233 and write every datagram's bytes to the receiver's
   serial port unchanged. Datagrams are ≤ 512 bytes with ≥ 10 ms between them
   (`Plans/RTCM_FORWARDING_PLAN.md`), so a 1 KB buffer and a 460800 baud link are enough.

A bridge that splits lines across datagrams is tolerated (the splitter below), but it is a
bug in the bridge, not a feature of the app.

## What arrives, and how

| Receiver | Sentences per epoch | Reaches the app via |
|---|---|---|
| AiO board (any receiver) | `$PANDA` or `$PAOGI` | UDP 9999, one sentence per datagram |
| Bynav T1-FD (Ethernet) | `$KSXT` | UDP, one per datagram |
| Bynav / Unicore UM982 via AiO passthrough | `$KSXT`, or `GGA`+`VTG`+`HPR` | UDP, one per datagram (seen on the bench, #288) |
| UM982 / F9P via HAT daemon, Teensy or ESP32 bridge | `GGA`+`VTG`+`HPR` (or `$KSXT`) | UDP; the bridge contract says whole lines |
| Septentrio mosaic-H (Ethernet / USB-net) | `GGA`+`VTG`+`HDT`+`$PTNL,AVR` (or `$PSSN,HRP`) | UDP; one or several per datagram |
| u-blox F9P pair via bridge | `GGA`+`VTG`+`HDT` (+`RELPOSNED` binary) | out of scope until someone asks |

Field references, checked against the vendor documents:

- **Unicore N4 reference manual R1.4** — `$KSXT` table 7-131 (done), `GPHPR` table 7-42
  (`utc, heading, pitch, roll, QF, sats, age, stnID`; QF on the GGA scale: 4 fix, 5 float),
  `GPHPD` table 7-46 (one-sentence position + heading + ENU velocity + baseline; marked
  "future firmware"). `CONFIG HEADING OFFSET` is applied by the receiver to `HPR`/`THS`
  and, as the T1-FD bench showed, to `$KSXT` as well: the app's `DualHeadingOffset` is then 0.
- **Trimble `$PTNL,AVR`** — AgIO handles two layouts (`Yaw,Tilt,Roll` and `Yaw,Roll`); roll
  only when the quality field says fixed. Septentrio's own `$PSSN,HRP` carries heading, roll,
  pitch and their standard deviations in one sentence. **Which the Septentrio users run, and
  the exact field order, comes from the captures requested in the open issue.**
- **AgIO `NMEA.Designer.cs`** (in `AgOpen_Snapshot`) — the de-facto field map for everything
  AgIO already supports; port field-for-field where the vendor document agrees.

## Design

### 1. Line assembly (ingest)

Today `UdpCommunicationService` forwards any datagram starting with `$` and the parser
requires the datagram to *be* one sentence. The AiO passthrough and the Bynav's own UDP
output happen to send one sentence per datagram; a serial bridge may batch an epoch's burst
into one datagram or cut a sentence across two.

- A `NmeaLineSplitter` per source address: splits a datagram on `\r`/`\n`/next `$`, feeds
  each complete sentence to the dispatcher, keeps a partial tail (bounded, 512 bytes; a
  longer tail is garbage and is dropped) for the next datagram from the same source.
- Zero-copy: the splitter hands out spans into the receive buffer; the tail is the only copy.
- The bridge contract above says whole lines; the splitter defends against a bridge that
  doesn't, and counts the repairs so Network IO can point at the bridge.

### 2. Dispatch (exists since #288)

`ParseIntoState` → frame check → sentence id → decoder. One-shot decoders fill a
`VehicleState` and return "fix complete". The assembler is one more decoder target; the
difference is that it returns "fix complete" only when an epoch closes.

| Sentence | Decoder | Emits |
|---|---|---|
| `$PANDA`, `$PAOGI`, `$KSXT` | one-shot (done) | immediately |
| `$GPHPD` | one-shot (Phase 4, when a firmware prints it) | immediately |
| `GGA`/`GNS`, `VTG`, `HPR`, `HDT`, `THS`, `$PTNL,AVR`, `$PSSN,HRP`, `GNTRA` | epoch assembler | when the epoch closes |

### 3. Epoch assembler

State: one open epoch (position, speed/track, heading, roll/pitch, quality fields, which
members have arrived) plus the **learned burst**: the set of sentence types the receiver
prints per epoch.

- **Epoch identity.** `GGA` opens an epoch with its UTC field. Sentences that carry UTC
  (`HPR`, `AVR`, `HRP`) must match it within a tolerance (half the epoch period); a mismatch
  means a dropped `GGA`, so the current epoch is closed as incomplete and a new one opened
  on the newcomer. Sentences without UTC (`VTG`, `HDT`, `THS`) attach to the open epoch.
- **Learning the burst.** During the first few epochs, record which sentence types occur
  between consecutive `GGA`s and in what order. After three identical epochs the set is
  "learned". Receiver output sets are configuration; they don't change at runtime, and if
  they do (user reconfigures) the learned set is relearned the same way the sentence
  monitor relearns its rate.
- **Emission.** The epoch is emitted when its last learned member arrives — not when `GGA`
  arrives. Before the set is learned, emit on the next `GGA` (one epoch of lag during
  warm-up only). If a member never comes (lost datagram), the next `GGA` closes the epoch
  with that member **marked absent**: heading invalid, roll 0, speed from the previous
  value is *not* carried over — the same contract as `$PANDA`'s 65535 sentinel.
- **Mapping to `VehicleState`** (fields the fusion and validator already use):
  - `GGA`: lat/lon (ddmm.mmmm → degrees), altitude, fix quality (GGA scale, as is),
    satellites, HDOP, **differential age** — the field `$KSXT` lacks.
  - `VTG`: speed (km/h field 7, else knots field 5) → m/s; true track → `Heading` only when
    no heading sentence is in the burst (single antenna; `HasDualHeading = false`).
  - `HPR` / `HDT` / `THS` / `AVR` yaw / `HRP` heading: `Heading`, `HasDualHeading = true`,
    `ImuValid = false`.
  - Roll: `HPR` pitch (antennas across the cab, the AgOpenGPS convention and what the
    `$KSXT` decoder does), `AVR` roll, `HRP` roll — **only when that sentence's own quality
    says fixed**; otherwise 0. Through `ApplyAhrsRollCalibration` like every other source.
  - Everything else (pitch, yaw rate) 0 unless the sentence has it.
- **Determinism check.** Given the same sequence of sentences, the assembler produces the
  same sequence of fixes regardless of how they were split into datagrams or when the thread
  ran. This is a unit test: feed a capture one sentence per call, then two per call, then
  split mid-sentence, and compare the emitted fixes.

### 4. Downstream

- `AutoSteerService.ProcessGpsBuffer` publishes a fix only when the decoder says "complete".
  The simulator path is unchanged.
- `GpsSentenceMonitor`: a slot per sentence type (it is a fixed small set), the rate measured
  per **emitted fix**, not per sentence, and "not accepted" split into "bad checksum",
  "unknown sentence", "incomplete epoch".
- `GpsSentenceType` gains `NmeaEpoch`; the System Data card and Network IO show the family
  as the receiver prints it ("GGA+VTG+HPR"), from the learned burst.
- The GPS log (`GpsDataRecorder`) records the family and, per fix, which members were
  present — that is what a reporter's dump needs to show a dropped `HPR`.
- Correction age: the `GGA` age fills the readouts that #288 blanked for `$KSXT`. A `$KSXT`
  receiver that also prints `GGA` could borrow the age from it; not in this plan unless a
  user wants it.

### 5. Configuration and UI

Nothing to select. The dispatcher recognises what arrives; the learned burst is the
"receiver profile". Settings that already exist and now matter for more users:

- `Connections.DualHeadingOffset` — 0 when the receiver applies its own offset (Unicore
  `CONFIG HEADING OFFSET 90`), 90 when it doesn't. Documented per receiver.
- `Ahrs.IsRollInvert` / `RollZero` — apply to receiver roll as they do to IMU roll.

One new setting is likely: **roll source** for dual-antenna receivers that print both a
pitch and a roll (`HPR`, `HRP`): "antenna baseline (across the cab)" — the default and the
AgOpenGPS convention — or "receiver roll" for INS-equipped units mounted along the vehicle.
Decide when the first along-mounted user appears; until then baseline pitch.

## Phases

**Phase 0 — captures and fixtures.** Two small scripts: `Tools/nmea-capture.py` listens on
:9999, keeps datagram boundaries and arrival times, and writes a text file the tests can
replay; `Tools/serial-to-udp.py` is the reference bridge (whole lines to 9999, 2233 back to
the port) for a bench with a USB receiver and no board. Captures wanted: UM982/T1-FD printing `GGA`+`VTG`+`HPR` through the AiO passthrough
(Chris's bench, switch the receiver's output set); Septentrio from the issue (wiring,
config export, pcap with cold start / stationary / turns / RTK loss / heading loss, one
`AVR` line pasted). Fixtures live under `Tests/AgOpenWeb.Services.Tests/Fixtures/nmea/`.

**Phase 1 — line splitter + monitor.** `NmeaLineSplitter`, the monitor's per-type slots and
per-fix rate, rejection reasons. No new sentences yet; `$PANDA`/`$PAOGI`/`$KSXT` must behave
exactly as before (existing tests are the proof). Ships on its own.

**Phase 2 — epoch assembler, Unicore set.** `GGA`/`GNS` + `VTG` + `HPR` (and `THS`). Bench
on the T1-FD/UM982. Includes the determinism test, the dropped-member test, the warm-up
test, and the "receiver reconfigured" test. Network IO shows the family.

**Phase 3 — Septentrio set.** `HDT` + `$PTNL,AVR` (both layouts) and `$PSSN,HRP`, from the
captures. Decide then whether to keep AgIO's Kalman smoothing on `AVR` roll (it is
smoothing, not a bug; default off, since Unicore's `SMOOTH` showed receivers do their own).

**Phase 4 — the long tail, by demand.** `$GPHPD` one-shot, `GNTRA` (UB482), SkyTraq `PSTI`,
F9P `HDT`-only pairs. Each is a decoder or an assembler member and a fixture; none changes
the structure.

**Docs, with Phase 2:** `Docs/GPS_RECEIVERS.md` — the bridge contract, supported sentence
sets, how each receiver reaches the app, per-receiver configuration recipes (the reviewed Bynav config: `HEADING FIXLENGTH`,
`LENGTH` in cm with a tight tolerance, `OFFSET 90`, `RTK TIMEOUT` 30–60 s because `$KSXT`
has no age field, `KSXT 0.1` or `GPGGA 0.1`+`GPVTG 0.1`+`GPHPR 0.1`), and the bridge
contract for HAT/ESP32 firmware.

## Open decisions

1. ~~Serial ports in the desktop head?~~ Decided 2026-10-09: no. Everything is IP; a
   receiver without a network port sits behind a bridge (AiO passthrough, Teensy/ESP32,
   HAT daemon). For a bench without any board, `Tools/serial-to-udp.py` (a few dozen
   lines) is the bridge.
2. **Roll source setting** (above) — defer until needed.
3. **`GGA` without any heading sentence** (single antenna, no IMU): accept it — heading from
   fix-to-fix as the fusion already does for `$PANDA` with the 65535 sentinel — or refuse
   and say "no heading source"? Recommendation: accept; the existing single-antenna path
   is exactly this case.
4. **Epoch timeout.** The assembler emits on membership, not time. Should a safety timer
   (half an epoch) close a stuck epoch so a receiver that stops printing `HPR` mid-session
   doesn't stall the fix? Recommendation: yes, but it closes the epoch *incomplete* — it
   never fills in values — so determinism holds for the data and only the lag varies.

## Still to check

- Whether the AiO passthrough forwards every line or only `$`-prefixed ones (Unicore's
  `#` logs would be noise on 9999 either way; the splitter drops non-`$` lines).
- UM982 `GPHPR` at 10 Hz alongside `GGA`/`VTG`: confirm the receiver emits them in a fixed
  order per epoch (expected `GGA, VTG, HPR`; the learned burst handles any order, but the
  fixture should show it).
- Septentrio: whether `HDT`'s heading already includes the user's antenna offset, and
  whether `AVR` quality 3 means the same as `HPR` QF 4.
