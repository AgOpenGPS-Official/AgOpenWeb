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

**Both stay supported, as equals, with no mode switch.** The AiO board with its `$PANDA`/
`$PAOGI` is not the legacy path; it is the right answer for a single-antenna receiver with
an IMU, and for anyone who already has one. The IP-direct path is the right answer for a
dual-antenna receiver and a steer-only board. The dispatcher recognises whichever arrives;
the user never picks. A machine can even have both (an AiO on the steer side and a
T1-FD on Ethernet), and it just works, because every source is an address on the subnet.

| | AiO board | IP-direct (receiver port or bridge) |
|---|---|---|
| Fix arrives as | `$PANDA` / `$PAOGI` (current firmware, frozen); standard sentences + an IMU sentence (future firmware) | the receiver's own sentences |
| IMU | on the board; in `$PANDA` today, its own sentence tomorrow | none needed (dual antenna); a board IMU's sentence is paired here under the 50 ms bound |
| Module identity | hello PGNs: steer / machine / IMU / GPS dots and addresses | none for the receiver; steer/machine still from their boards |
| RTCM back to the receiver | unicast to the board's address, 2233 → serial | unicast to the sender's address, 2233 |
| App-side code path | one-shot decoder (unchanged by this plan) | one-shot decoder or epoch assembler |

Receivers print their fix as either one proprietary sentence (`$KSXT`, `$GPHPD`) or as
several standard ones per epoch (`GGA` + `VTG` + `HPR` on a Unicore, `GGA` + `VTG` + `HDT` +
`AVR` on a Septentrio). The first kind is done (#288: one decoder per sentence). This plan is
about the second kind, and about the ingest path both kinds share once the sender is no
longer an AiO.

## The rules

**1. One fix = one receiver epoch.** AgIO's `ParseNMEA` accumulates fields from whatever
sentences have arrived and flushes on the next position sentence, so a `VTG` that lands
after its `GGA` goes out with the *next* epoch and a fix can carry last epoch's speed on one
host and not on another. The assembler below groups the receiver's sentences by the
**receiver's epoch**, not by arrival, and emits when the burst is complete. Everything
downstream — fusion, the 100 Hz pose estimator, section control — keeps seeing coherent
fixes and does not change. This costs nothing: the sentences already belong together.

**2. IMU: latest reading, with a staleness bound.** Brian's field experience since Ace
(with the TM171 and other 100 Hz IMUs): syncing a specific IMU sample to a specific GNSS
epoch buys nothing a tractor can feel. At 100 Hz the latest reading is at most 10 ms old —
0.2° of heading in a 20°/s turn, nothing of roll — and over UDP on a LAN the IMU and GPS
datagrams land in the same socket in order, so the host adds no error the board wouldn't.
So the app **may** pair a separate IMU stream with the GPS epoch: take the latest IMU
reading if it is younger than **50 ms**, else mark the IMU invalid for that fix (the
`$PANDA` 65535 semantics). The bound is what matters — a stalled IMU must not silently
feed two-second-old roll — not the alignment. No time tags, no special firmware timing.

What AgIO got wrong was serial (16 ms USB latency timers, Windows buffering) stacked on
rule 1, not the IMU. Perfect is the enemy of good; rule 1 is the good.

## Everything is IP

The app has one transport: UDP on the module subnet. It does not open serial ports on any
head, and this plan does not add that. Receivers either have an Ethernet/USB-network port
of their own (Bynav T1-FD, Septentrio mosaic, UB9A0) or sit behind a **serial-to-Ethernet
bridge** — the AiO board itself (building `$PANDA`/`$PAOGI`, or in passthrough), a Teensy
or ESP32 doing nothing but serial↔UDP, the HAT daemon on the Pi. The AiO is the first and
most complete of these devices, not an exception to the model: it speaks the same two
ports, and adds module identity and IMU pairing on top. The app side is the same for all
of them.

What the app already does for an address that only ever sends NMEA, with no hello PGN:
the GPS status dot follows the sentence flow (`GpsService.IsGpsDataOk`), the GPS address on
the Network IO page is "where the position sentences come from", and RTCM corrections are
unicast back to that address on port 2233 (`NtripClientService.ResolveRtcmDestination`).
So a bridge needs no module identity; it needs to do two things:

**Bridge contract** (goes into the HAT/ESP32 firmware docs and `Docs/GPS_RECEIVERS.md`):

1. *Receiver → app:* send to UDP 9999 on the app's subnet, broadcast or unicast — or to
   2211 (GPS1) / 2222 (GPS2), Ace's convention, which the app listens on too. Each
   datagram holds one or more **whole** NMEA lines, `$…*hh\r\n`, never a partial line.
   Preferred: one line per datagram, sent as soon as the line is complete (the AiO
   passthrough does this). Lines not starting with `$` (Unicore `#` logs, binary) may be
   dropped or forwarded; the app ignores them.
2. *App → receiver:* listen on UDP 2233 and write every datagram's bytes to the receiver's
   serial port unchanged. Datagrams are ≤ 512 bytes with ≥ 10 ms between them
   (`Plans/RTCM_FORWARDING_PLAN.md`), so a 1 KB buffer and a 460800 baud link are enough.

A bridge that splits lines across datagrams is tolerated (the splitter below), but it is a
bug in the bridge, not a feature of the app.

## Prior art: Brian's Ace (2023)

[farmerbriantee/Ace](https://github.com/farmerbriantee/Ace), last commit March 2023: "a new
way to run AOG where the modules are small, and everything is on the udp network." Read from
`Hardware/Ace`, `Hardware/AceSteer` and `Hardware/PGN_ACE.xlsx`:

| Module | Address / port | Role |
|---|---|---|
| GPS1 (main antenna) | `.11`, NMEA to UDP **2211**; u-center over TCP 3311 | dumb NMEA source; **"Hello = na"** — no identity |
| GPS2 (dual antenna) | `.22`, UDP **2222**; TCP 3322 | the heading receiver (`RELPOSNED` planned, not finished) |
| Nav (Teensy 4.1 + BNO085 + WAS, PoE) | `.121` | listens on 2211/2222, pairs the GGA with the IMU 40 ms after the GGA, builds `$PANDA` → 9999; sends the WAS straight to the steer module (PGN 249, 100 Hz); answers hello as IMU (121) |
| AceSteer / EncSteer | `.126` | steer-only; PID on the board; WAS from Nav; 254/252 in, 253 out |

Port scheme, in his words: "22XX = GPS, 2211 = GPS1, 2222 = GPS2, 2233 = RTCM3 — easy to
remember." The Nav firmware refuses every other module PGN. AgIO stays the hub.

What this plan takes from it:

- **The receiver is an identity-less UDP source** — the same principle, independently
  arrived at. (Ace also paired the IMU on the Nav board, 40 ms after the GGA; Brian's later
  experience is that this isn't needed — rule 2 above.)
- **The 22xx ports.** The app listens on **2211 (GPS1) and 2222 (GPS2)** as well as 9999, so
  an Ace-style board or any bridge built to that convention works unchanged, and a second
  receiver has a name. RTCM is already 2233.
- **An Ace Nav module is a supported source today** (it emits `$PANDA`). The HAT daemon's
  GPS half is simpler still: NMEA over UDP in, NMEA out, plus the IMU's own sentence.
- **F9P-pair dual via UBX `RELPOSNED`** (his GPS2 / DualWithIMU board) moves from "out of
  scope until someone asks" to a named Phase 4 candidate: a binary member that joins the
  `GGA` epoch with heading and roll from the baseline.

Not taken: the fixed-IP table (a convention to document, not enforce), the Nav→Steer WAS
PGN 249 (only needed when the WAS ADC and the motor driver are on different boards), and
u-center TCP passthrough (a bridge feature; nothing for the app).

## Brian's second point: standard sentences, no packing

`$PANDA`/`$PAOGI` pack a whole fix into one sentence because the link to the PC was serial
and one line per epoch was the cheap way to keep the fields together. Over UDP that reason
is gone, and Brian's advice is that the app should just accept standard sentences.

Taken. Ace still sampled the IMU 40 ms after the GGA and wrote both into `$PANDA`; by rule
2 above that is no longer needed either. A board just prints what it has: the receiver's
sentences as they come, and its IMU's attitude at the IMU's own rate (or at the GPS rate —
either is fine under the 50 ms bound).

Consequences:

- **New firmware (HAT daemon, future AiO builds) emits standard sentences only:** the
  receiver's `GGA` + `VTG` passed through, plus an attitude sentence from the board's IMU,
  plus rate of turn. No new packed formats, no `$PANDA` v2, no timing rules for the IMU
  sentence. The AiO becomes a bridge that also contributes an IMU sentence, and the app
  treats it like a UM982 printing `HPR`: the two columns in the table above converge into
  one path.
- **`$PANDA`/`$PAOGI` are frozen.** Decoded for the installed base as they are; no fields
  added.
- **The source must be in the sentence.** Fusion treats a dual-antenna heading as ground
  truth and an IMU heading as something to fuse (#157). NMEA's way to say which is the
  **talker ID**: `$GN…`/`$GP…` from a receiver, `$IN…` (integrated navigation) or `$HE…`
  (gyro) from a board's IMU. Which attitude sentence to use — an `HPR`-shaped one
  (`utc, heading, pitch, roll, quality`), or `THS` + `XDR` (pitch/roll) + `ROT` — is a
  decision for the HAT firmware; the assembler only needs the talker ID. Decide with the
  first firmware that emits it (Phase 2b).

## Prior art: the AiO v26 firmware (ours)

`Firmware_Teensy_AiO_26/lib/aio_navigation/GNSSProcessor.*` and `NAVProcessor.cpp` are
the closest existing implementation of this plan, running on the installed boards:

- **One byte-wise framer for both families:** `$…*hh` (8-bit XOR) and Unicore `#…*xxxxxxxx`
  (CRC-32) in the same state machine. It parses `GGA`, `GNS`, `VTG`, `RMC`, `HPR`, `KSXT`,
  `#INSPVAA`/`#INSPVAXA` (UM981/UM982 INS units), `BESTGNSSPOS`, and UBX `RELPOSNED` from a
  second F9P. The `#` framer is therefore a port, not a research item.
- **"Look at the data to decide":** each parsed type sets a bit in `messageTypeMask`;
  `HPR`/`KSXT`/`RELPOSNED` set `hasDualHeading`, `INSPVA` sets `hasINS`; `selectMessageType()`
  picks `$PAOGI` if either is set, else `$PANDA`. No single/dual setting. That is what the
  learned burst below generalises. Its flags are sticky (nothing clears them), so a receiver
  that stops printing `HPR` keeps producing `$PAOGI` with the last heading; the assembler's
  "member absent → heading invalid" is the fix for that.
- **Emission is flush-on-position-message** (`GGA`/`GNS`/`KSXT`/`INSPVA` fire the send). If
  the receiver prints `HPR` after `GGA`, each `$PAOGI` carries the previous epoch's heading:
  100 ms at 10 Hz, ~2° in a 20°/s turn — more than the IMU jitter rule 2 waves through.
  The learned-burst emission avoids it at no latency cost. Whether it bites depends on the
  UM982's output order; the Phase 0 capture shows it.
- **IMU on `$PAOGI` is latest-reading** (pitch, yaw rate from `imuProcessor.getCurrentData()`),
  roll from the dual's pitch field — the same conventions as rule 2 and the `$KSXT` decoder.
- **Passthrough forwards every complete line, `#` ones included.** The app's UDP ingest
  drops anything not starting with `$`; it should take `#` lines once the framer exists.

## The UM981: `#` messages are a target, not a long-tail item

The UM981 is Unicore's single-antenna **RTK + INS** module (UM981-Auto reference manual
R1.0): the receiver fuses its own IMU with the GNSS fix and prints one message per epoch
with position, velocity and attitude — a receiver doing the Teensy's job, with no dual
antenna and no board IMU. Its fused fix only exists as a `#` log:

`#INSPVAXA,COM1,0,73.5,FINESTEERING,1695,309428.000,00000040,4e77,43562;INS_SOLUTION_GOOD,INS_PSRSP,51.11637873403,-114.03825114994,1063.6093,-16.9000,-0.0845,-0.0464,-0.0127,0.138023492,0.069459386,90.000923268,0.9428,…,3,0*e877c178`

- Body (table 2-11): INS status, position type, lat, lon, height, undulation, N/E/U
  velocity (m/s), **roll, pitch, azimuth**, their σ's, extended status, time since update.
  CRC-32 over everything between `#` and `*`.
- **Two header shapes.** The N4 modules use the short header (`#BESTNAVA,54,GPS,FINE,
  week,ms,…;`); the UM981 uses the NovAtel-style long one (`#INSPVAXA,COM1,0,73.5,
  FINESTEERING,week,secs,status,reserved,version;`). The framer splits at the `;` and does
  not count header fields; decoders index from the body. There is also a `%…SA` short
  variant (`%GYRATTSA,week,ms;…`) — accepted by the framer, not decoded.
- **Mapping** (as v26 `parseINSPVAXA` does it): position type `INS_RTKFIXED` → 4,
  `INS_RTKFLOAT` → 5, `INS_PSRDIFF` → 2, `INS_PSRSP` → 1, `INS` (dead reckoning only) → 6;
  speed = √(vN² + vE²); azimuth → `Heading` with `HasDualHeading = true` (the fused heading
  is ground truth for guidance, as v26 treats it); roll/pitch straight from the INS — this
  receiver reports true vehicle roll, no baseline-pitch convention. Attitude only when
  INS status is `INS_SOLUTION_GOOD` (3) or `INS_ALIGNMENT_COMPLETE` (7); `INS_ALIGNING`
  (1) and `INS_HIGH_VARIANCE` (2) give position without attitude, `INS_INACTIVE` (0)
  nothing. `INS_SOLUTION_FREE` (6, no GNSS) is a fix of quality 6 and the validator's
  minimum decides.
- Configuration that matters for users: `CONFIG INS ANGLE` (IMU mounting), `CONFIG
  IMUTOANT OFFSET` (lever arm to the antenna), `CONFIG INS TIMEOUT` (how long DR carries
  on without GNSS — the same stale-fix concern as `RTK TIMEOUT` on the T1-FD),
  `CONFIG INSDIRECTION`. Goes in `Docs/GPS_RECEIVERS.md`.

So the `#` framer (CRC-32, `;` split, both header shapes) and the `INSPVAX` one-shot decoder
move to **Phase 2**, beside the Unicore NMEA set. The v26 firmware is the reference for
both. The other `#` logs (`BESTNAV`, `UNIHEADING`, `RTKSTATUS`, `RTCMSTATUS`) stay
diagnostics-only, Phase 4.

## Target sentence set

**AgIO's set, plus `$KSXT` (done), `$GPHPR`, and `#INSPVAXA` for the UM981.** AgIO's `NMEA.Designer.cs` is the
field-tested map; `HPR` is the one sentence it predates (a UM982 printing standard
sentences uses it for attitude; AgIO's `HPD` is marked "future firmware" by Unicore).

| Sentence | Role | Phase |
|---|---|---|
| `$PANDA`, `$PAOGI` | whole fix from an AiO board | done, frozen |
| `$KSXT` | whole fix, Unicore/Bynav | done (#288) |
| `GGA` / `GNGGA`, `GNS` | position, fix quality, sats, HDOP, age — opens the epoch (v26 parses both) | 2 |
| `VTG` | speed, track | 2 |
| `$GPHPR` | heading, pitch (roll), quality — Unicore attitude | 2 |
| `HDT` | dual heading — Septentrio, F9P pairs, others | 3 |
| `$PTNL,AVR` | roll (+ heading) — Trimble format, printed by Septentrio; two layouts | 3 |
| `$PSSN,HRP` | Septentrio's own attitude, if the captures show it in use | 3 |
| `$GNTRA` | heading + roll — UB482 / ComNav | 4 |
| `$GPHPD` | whole fix, Unicore (when a firmware prints it) | 4 |
| `$PSTI,032/035/036` | SkyTraq baseline / attitude | 4 |
| UBX `RELPOSNED` | F9P pair heading, binary (Ace GPS2; v26 GPS2) | 4 |
| `#INSPVAXA` (`#INSPVAA`) | whole fix from a UM981 / Unicore INS unit, CRC-32; v26 parses them | 2, with the `#` framer |
| `#RTKSTATUSA`, `#RTCMSTATUSA`, `#UNIHEADINGA` | diagnostics only, for Network IO; never the fix | 4, optional |

Not targeted: `RMC` (AgIO has it commented out; `GGA`+`VTG` cover it), Hemisphere
`$PSAT,HPR` and NovAtel `#HEADINGA` until someone asks.

## What arrives, and how

| Receiver | Sentences per epoch | Reaches the app via |
|---|---|---|
| AiO board (any receiver) | `$PANDA` or `$PAOGI` | UDP 9999, one sentence per datagram |
| Bynav T1-FD (Ethernet) | `$KSXT` | UDP, one per datagram |
| Bynav / Unicore UM982 via AiO passthrough | `$KSXT`, or `GGA`+`VTG`+`HPR` | UDP, one per datagram (seen on the bench, #288) |
| Unicore UM981 (RTK + INS, single antenna) via bridge | `#INSPVAXA` (one fused fix per epoch) | UDP; `#` framing, CRC-32 |
| UM982 / F9P via HAT daemon, Teensy or ESP32 bridge | `GGA`+`VTG`+`HPR` (or `$KSXT`) | UDP; the bridge contract says whole lines |
| Septentrio mosaic-H (Ethernet / USB-net) | `GGA`+`VTG`+`HDT`+`$PTNL,AVR` (or `$PSSN,HRP`) | UDP; one or several per datagram |
| u-blox F9P pair via bridge (Ace GPS1/GPS2) | `GGA`+`VTG` + UBX `RELPOSNED` (binary) | UDP 2211 / 2222; Phase 4 |
| Ace Nav module | `$PANDA` | UDP 9999; supported today |

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

- A `NmeaLineSplitter` per source port: splits a datagram on `\r`/`\n`/next `$` or
  `#`, feeds each complete sentence to the dispatcher, keeps a partial tail (bounded, 512
  bytes; a longer tail is garbage and is dropped) for the next datagram from the same
  source. `#`/`%` lines go to the CRC-32 framer (Phase 2, ported from v26): split at `;`,
  body fields after it, either header shape; until then the parser reports them as unknown
  sentences, so the System Data card shows what the receiver prints.
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
| `#INSPVAXA` | one-shot, `#` framer (Phase 2) | immediately |
| `$GPHPD` | one-shot (Phase 4, when a firmware prints it) | immediately |
| `GGA`/`GNS`, `VTG`, `HPR`, `HDT`, `THS`, `$PTNL,AVR`, `$PSSN,HRP`, `GNTRA` | epoch assembler | when the epoch closes |

### 3. Epoch assembler

State: one open epoch (position, speed/track, heading, roll/pitch, quality fields, which
members have arrived) plus the **learned burst**: the set of sentence types the receiver
prints per epoch.

- **Epoch identity.** `GGA` opens an epoch with its UTC field. Receiver sentences that
  carry UTC (`HPR`, `AVR`, `HRP`) must match it within a tolerance (half the epoch period); a
  mismatch means a dropped `GGA`, so the current epoch is closed as incomplete and a new one
  opened on the newcomer. Receiver sentences without UTC (`VTG`, `HDT`, `THS`) attach to the
  open epoch. **IMU sentences are not epoch members**: they update a "latest IMU" slot with
  an arrival stamp, and the epoch takes that slot when it closes if the stamp is younger
  than 50 ms (rule 2).
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
  - `HPR` / `HDT` / `THS` / `AVR` yaw / `HRP` heading from a receiver talker (`GN`, `GP`):
    `Heading`, `HasDualHeading = true`, `ImuValid = false`. The same shape from an IMU talker
    (`IN`, `HE`): `ImuHeading`, `ImuValid = true`, `HasDualHeading = false` — the `$PANDA`
    semantics, by talker ID instead of by sentence name.
  - Roll: `HPR` pitch (antennas across the cab, the AgOpenGPS convention and what the
    `$KSXT` decoder does), `AVR` roll, `HRP` roll — **only when that sentence's own quality
    says fixed**; otherwise 0. Through `ApplyAhrsRollCalibration` like every other source.
  - Everything else (pitch, yaw rate) 0 unless the sentence has it.
- **Determinism check (receiver fields).** Given the same sequence of receiver sentences,
  the assembler produces the same fixes regardless of how they were split into datagrams or
  when the thread ran. A unit test: feed a capture one sentence per call, then two per
  call, then split mid-sentence, and compare. IMU fields are exempt by design (rule 2) and
  tested for the bound instead: a 60 ms-old reading is not used.

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

## Non-regression: what every phase must prove

1. **The AiO path is unaffected.** `$PANDA`/`$PAOGI` decoders are frozen; nothing in this
   plan changes them. Proof: the existing parser, fusion and pipeline tests pass unchanged,
   and a `$PANDA`/`$PAOGI` capture (Phase 0) replayed through the splitter produces
   `VehicleState`s identical to `ParseIntoState` on the raw datagrams. A one-line datagram
   goes through the splitter as the same span, no copy.
2. **Zero-copy, no allocation per datagram.** One-shot decoders and the assembler decode
   field spans straight into `VehicleState` numbers; no sentence text is kept; the `#`
   framer's CRC-32 runs over the span and the `;` split is an index. The only copy anywhere
   is the splitter's partial-line tail (≤ 512 bytes, only when a bridge splits a line).
   Proof: `[AutoSteerRx-PERF]`'s allocation counter (`ProcessGpsBuffer`) reads 0 bytes per
   datagram for every fixture, and stays in the perf tests.
3. **No added latency on the fix.** One-shot sentences are published on arrival as now.
   The assembler publishes when the receiver's burst is complete — the sentences of one
   epoch leave the receiver within a few ms of each other — never on a timer alone
   (the safety timer only closes an epoch *incomplete*).
4. **No mode switch, no new setting required.** A machine configured today keeps working
   without visiting a settings page. The only new setting in the plan (roll source) is
   deferred and defaults to today's behaviour.

## Phases

**Phase 0 — captures and fixtures.** Two small scripts: `Tools/nmea-capture.py` listens on
:9999, keeps datagram boundaries and arrival times, and writes a text file the tests can
replay; `Tools/serial-to-udp.py` is the reference bridge (whole lines to 9999, 2233 back to
the port) for a bench with a USB receiver and no board. Captures wanted: UM982/T1-FD printing `GGA`+`VTG`+`HPR` through the AiO passthrough
(Chris's bench, switch the receiver's output set); Septentrio from the issue (wiring,
config export, pcap with cold start / stationary / turns / RTK loss / heading loss, one
`AVR` line pasted). Fixtures live under `Tests/AgOpenWeb.Services.Tests/Fixtures/nmea/`.

**Phase 1 — line splitter + monitor + ports.** *Done (2026-10-09).* `NmeaLineSplitter`
(`Services/Gps`) cuts every GPS datagram into whole lines in front of the parser: one line per
datagram stays a span into the receive buffer; a batched burst gives each line in turn; a
line cut across datagrams is joined from a bounded tail kept per source port and counted
(`JoinedLines`), bytes that belong to no line are counted (`DroppedBytes`). `#` lines are
handed out and the parser names them unknown until the framer exists. The parser reports why
it refused a line (`NmeaParseResult`: bad frame, bad checksum, unknown sentence, bad fields)
and the monitor counts checksum and unknown apart. `UdpCommunicationService` listens on 2211
(GPS1) and 2222 (GPS2) besides 9999, and `AutoSteerService.ProcessGpsDatagram` carries the
source; the System Data card shows the source, the counters and the breakdown. Non-regression
held: the `$PANDA`/`$PAOGI`/`$KSXT` decoders are untouched, the allocation test covers the
splitter's one-line and joined paths at 0 bytes, and the same sentences give the same fixes
however they are chunked (`GpsIngestTests`).

**Phase 2 — epoch assembler, Unicore set; `#` framer + `INSPVAX`.** *Done (2026-10-09).*
`NmeaEpochAssembler` (`Services/Gps`) takes `GGA`/`GNS`, `VTG`, `HPR`, `HDT` and `THS`
(`HDT`/`THS` came in with it since they are the same shape as `HPR`): epochs by the
receiver's UTC, the burst learned from three alike epochs, emission on the last learned
member, an absent member marked absent and counted, relearning on a changed set, members
from an IMU talker to the IMU slot. The CRC-32 framer takes both header shapes and the
`#INSPVAXA` / `#INSPVAA` decoder is checked against the UM981 manual's example line (its
printed CRC). Tests: warm-up, learning, dropped member, speed not carried over, reconfigured
smaller and larger, roll rules, talker routing, GNS modes, chunking determinism through the
service, zero allocation on both paths. `Docs/GPS_RECEIVERS.md` written. Not yet: the GPS
log does not record which members were present per fix; bench on a UM982 / UM981 pending
captures (the v26 firmware's INSPVAXA decoder reads the velocities one field early, past the
undulation — worth fixing there).

**Phase 2b — board IMU as a latest-reading source.** The attitude sentence from a HAT/AiO
build, distinguished by talker ID: heading goes to `ImuHeading`/`ImuValid`, not to the dual
heading; taken under the 50 ms staleness bound. Done together with the first firmware that
emits it; the fixture comes from that board. This is what lets the HAT daemon skip `$PANDA`
altogether — and it has no timing rules to get right.

**Phase 3 — Septentrio set.** `HDT` + `$PTNL,AVR` (both layouts) and `$PSSN,HRP`, from the
captures. Decide then whether to keep AgIO's Kalman smoothing on `AVR` roll (it is
smoothing, not a bug; default off, since Unicore's `SMOOTH` showed receivers do their own).

**Phase 4 — the long tail, by demand.** UBX `RELPOSNED` from an F9P pair on 2222 (Ace's
GPS2; binary, joins the `GGA` epoch), `$GPHPD` one-shot, `GNTRA` (UB482), SkyTraq `PSTI`.
Each is a decoder or an assembler member and a fixture; none changes the structure.

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
