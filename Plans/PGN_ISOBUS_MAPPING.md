# AgOpen PGN → ISOBUS Data Layer Mapping

**Status:** discussion artifact — not a decision, not a plan to implement.
**Date:** 2026-06-01
**Purpose:** Support the "redo the AgOpen PGNs" debate by mapping every current
AgOpenGPS message onto the ISO 11783 / J1939 / NMEA 2000 application layer,
including how AgOpen-proprietary config/tuning maps into the standard's
**proprietary PGN** space. Numbers flagged ⚠️ in earlier drafts have been
verified against live sources (see [Sources](#sources)).

---

## 1. The core idea

A **PGN is an application-layer definition** — the parameter group number plus
its field layout, scaling, and units. It is *independent of CAN as a transport*.
AgOpen already proves this: it borrowed the word "PGN" and ships single-byte
identifiers (`0xFE`, `0xFC`, …) inside a custom UDP frame.

The proposal: keep UDP transport, but adopt the **real ISO/J1939/N2K PGN data
definitions** for everything that has a public equivalent, and place AgOpen's
proprietary config/tuning in the standard's reserved **Proprietary A / B** space.
This makes the whole protocol ISO-*shaped* without requiring CAN hardware or a
certified stack.

### What changes vs. what disappears

| Concern | Result over UDP |
|---|---|
| 18-bit PGN, source address, destination, priority | Carried explicitly in the UDP envelope header (§2) |
| **TP/ETP multi-packet** (CAN's >8-byte segmentation) | **Disappears** — a UDP datagram holds a full PGN payload (e.g. 24-byte Pin Config, 33-byte Section Dimensions) in one shot |
| Address claiming (J1939-81) | Largely moot — addressing is IP:port; carry NAME/SA only if device identity matters |
| Bus arbitration / priority | Informational only — no CSMA arbitration on UDP (AgOpen already lives with this) |

### What this does **not** give you

- **Not on-the-wire ISOBUS.** A real CAN implement/VT won't see UDP. Bridging to
  a physical ISOBUS requires a **UDP↔CAN gateway**, which re-introduces address
  claiming + TP/ETP — confined to that one box. That seam aligns with the project
  boundary: live ISO 11783 over CAN is another group's domain (AgValoniaGPS stays
  file-level / ISOXML).
- **No formal ISOBUS certification** — conformance is CAN-bound.
- **Proprietary content stays opaque** to other vendors (by definition). You gain
  a legitimate, collision-free home + reuse of the standard's structure, not
  interop for the private data.

---

## 2. Proposed UDP envelope

AgOpen's current frame, widened to carry the J1939 header fields explicitly, then
the **verbatim ISO PGN payload**:

```
0x80 0x81 | Prio | PGN (18-bit, 3 bytes) | SA | DA | Len | <standard ISO PGN data…> | CRC
```

- **SA** (source address) ← AgOpen's existing `Src` byte. Module address map:
  AgIO/app, Steer = 126, Machine = 123, IMU = 121, GPS = 124, Tool = 122.
- **DA** (destination) used for PDU1 / Proprietary A (addressed writes); ignored
  for PDU2 broadcast.
- **Prio** informational.
- Payload bytes follow the standard PGN's SPN order / scaling / units exactly.

---

## 3. Public ISO / NMEA 2000 PGNs — the interop layer

These AgOpen messages have standardized equivalents. Adopt the standard payload
layout verbatim. **All PGN numbers in this table are verified** (§Sources).

| AgOpen message | AgOpen id | Direction | ISO / N2K target | PGN (dec / hex) |
|---|---|---|---|---|
| Main Antenna (lat/lon, fast) | D6 / 214 | GPS→App | Position, Rapid Update | 129025 / 0x1F801 |
| Main Antenna (full fix: lat/lon/alt/sats/fix/age) | D6 / 214 | GPS→App | GNSS Position Data | 129029 / 0x1F805 |
| …DOP portion | D6 / 214 | GPS→App | GNSS DOPs | 129539 / 0x1F903 |
| …speed + course-over-ground | D6 / 214 | GPS→App | COG & SOG, Rapid Update | 129026 / 0x1F802 |
| Heading (true / dual) | D6/214, D3/211 | →App | Vehicle/Vessel Heading | 127250 / 0x1F112 |
| IMU roll/pitch/yaw | D6/214, D3/211 | IMU→App | Attitude | 127257 / 0x1F119 |
| IMU yaw-rate / gyro | D6/214, D3/211 | IMU→App | Rate of Turn | 127251 / 0x1F113 |
| **Steer Data** (curvature command + engage) | FE / 254 | App→Steer | **Ag Guidance System Command (GSC)** | **44288 / 0xAD00** |
| **From AutoSteer** (actual curvature, limit/avail status) | FD / 253 | Steer→App | **Ag Guidance Machine Info (GMI)** | **44032 / 0xAC00** |
| **Tool Steering** (XTE / command) | E9 / 233 | App→Tool | GSC (tool source address) | 44288 / 0xAD00 |
| **From Tool Steer** (actual) | E6 / 230 | Tool→App | GMI (tool source address) | 44032 / 0xAC00 |
| Machine Data → speed field | EF / 239 | App→Machine | Wheel-Based Speed & Distance | 65096 / 0xFE48 |
| (alt. radar/GPS speed) | — | — | Ground-Based Speed & Distance | 65097 / 0xFE49 |
| (alt. selected/commanded speed) | — | — | Machine Selected Speed | 61474 / 0xF022 |
| (alt. commanded speed setpoint) | — | — | Machine Selected Speed Command | 64835 / 0xFD43 |
| Machine Data → `hydLift` (rear hitch raise/lower) | EF / 239 | App→Machine | Rear Hitch State (cmd = SPN 1869 set-point inside) | 65094 / 0xFE46 |
| (front hitch, if used) | — | App→Machine | Front Hitch State (SPN 1873 set-point inside) | 65092 / 0xFE44 |
| Section state (on/off) | EF/239, E5/229, EA/234 | both | **ISO 11783-10 TC process data** — not a single PGN | see §7 |

> **Guidance curvature encoding (GSC & GMI):** bytes 0–1 `uint16` little-endian,
> resolution **0.25 mm⁻¹/bit**, offset **−8032 km⁻¹** → physical = (raw × 0.25) − 8032.
> GSC byte 2 bits 0–1 = curvature command status (steer-intent). GMI byte 2/3/4
> carry mechanical lockout, readiness, limit status, and exit-reason codes.

> **Hitch:** there is **no separate hitch-command PGN**. The raise/lower command
> is SPN 1869 "Rear Hitch Position Command (Set Point)" carried *inside* the Rear
> Hitch State PGN (65094). Position 0 % (down) → 100 % (up) at 0.4 %/bit.

---

## 4. Manufacturer code & proprietary prefix (applies to §5–§6)

AgOpen is **not** being registered with the ISO/SAE NAME authority. For
collision-avoidance only, we adopt a **notional 11-bit manufacturer code**:

> **`AGOPEN_MFG_CODE = 2017 (0x07E1)`**
> Chosen as AgOpenGPS's inception year; fits the J1939 NAME 11-bit Manufacturer
> Code field (range 0–2047). **Notional — not ISO-assigned.** If a real shared
> bus ever needs it, swap this constant; nothing else changes.

Per J1939 best practice, every proprietary payload leads with the manufacturer
code so multiple vendors sharing the proprietary PGNs don't collide. Over UDP this
prefix costs a few bytes with no 8-byte frame limit to worry about.

**Proprietary A prefix** (one shared PGN 0xEF00 → needs a function selector):

| Byte | Field |
|---|---|
| 0 | Manufacturer Code LSB (`0xE1`) |
| 1 | Manufacturer Code MSB, low 3 bits (`0x07`) |
| 2 | **Function selector** (see §5 table) |
| 3…n | message payload |

**Proprietary B prefix** (each message has its own PGN → no function byte):

| Byte | Field |
|---|---|
| 0 | Manufacturer Code LSB (`0xE1`) |
| 1 | Manufacturer Code MSB, low 3 bits (`0x07`) |
| 2…n | message payload |

---

## 5. Proprietary A — addressed config writes (PGN 61184 / 0xEF00)

PDU1, **destination-specific** (App → one named module). No public ISO equivalent
— this is the device tuning that lives inside the ECU in the certified world. The
**function selector** (prefix byte 2, §4) disambiguates each message. Allocation
is blocked by module:

| Range | Owner |
|---|---|
| `0x00`–`0x0F` | Steer module |
| `0x10`–`0x1F` | Machine module |
| `0x20`–`0x2F` | Tool module |
| `0x30`–`0x3F` | Network / infrastructure |
| `0x40`–`0xFF` | Reserved (future) |

| AgOpen message | AgOpen id | Dest module (SA) | **Function byte** |
|---|---|---|---|
| Steer Settings (gainP / PWM / countsPerDeg / offset / ackerman) | FC / 252 | Steer (126) | `0x01` |
| Steer Config (set0 / pulseCount / minSpeed) | FB / 251 | Steer (126) | `0x02` |
| Machine Config (raise/lower time, hydEnable, users) | EE / 238 | Machine (123) | `0x10` |
| Pin Config (24 pin assignments) | EC / 236 | Machine (123) | `0x11` |
| Section Dimensions (16 widths + count) | EB / 235 | Machine (123) | `0x12` |
| Tool Settings (invert / driver / Danfoss bits) | E7 / 231 | Tool (122) | `0x20` |
| Subnet Change | C9 / 201 | all | `0x30` |
| Scan request | CA / 202 | all | `0x31` |
| Subnet Reply | CB / 203 | all | `0x32` |
| Hello / heartbeat | C8 / 200 | all | better mapped to J1939 **Address Claimed** 60928/0xEE00 + **Request** 59904/0xEA00 |

> Pin Config (24 B) and Section Dimensions (33 B) exceed 8 bytes — on CAN they'd
> need TP/ETP; over UDP they fit in one datagram (after the 3-byte prefix). This
> is the concrete payoff of keeping UDP transport.

---

## 6. Proprietary B — broadcast telemetry / misc (PGN 65280–65535 / 0xFF00–0xFFFF)

PDU2, broadcast, 256 PGNs available to manufacturers. For diagnostic / raw / UI
traffic with no public home. AgOpen claims the contiguous block **0xFF80–0xFF8F**
("AgOpen telemetry block"); the manufacturer-code prefix (§4) disambiguates from
other vendors who happen to use the same PB PGN.

| AgOpen message | AgOpen id | **PB PGN (dec / hex)** |
|---|---|---|
| Raw WAS value (ToAutosteer) | F9 / 249 | 65408 / `0xFF80` |
| Steer sensor diag (From Autosteer 2) | FA / 250 | 65409 / `0xFF81` |
| PWM display / steer diag | (in FD) | 65410 / `0xFF82` |
| Hardware text message (duration / color / text) | DD / 221 | 65411 / `0xFF83` |
| Nudge by machine | DE / 222 | 65412 / `0xFF84` |
| From Machine (relay states) | ED / 237 | 65413 / `0xFF85` (or TC Actual Work State, §7) |
| L/R section speed | E5 / 229 tail | 65414 / `0xFF86` |
| _(reserved)_ | — | 65415–65423 / `0xFF87`–`0xFF8F` |

---

## 7. Section control — not a PGN, a process-data model

AgOpen's section bits (`Machine Data` SC1to8/9to16, `64 sections` E5,
`Switch Control` EA) do **not** map to a dedicated PGN. ISO 11783-10 **Task
Controller** carries section state as **process-data variables** keyed by **DDIs**
(Data Dictionary Identifiers), all transported over a single PGN.

**Transport:** Process Data Message (PDM), **PGN 51968 / 0xCB00** (PDU1,
destination-specific). Byte 1 = 4-bit command + 4-bit element number; bytes 2–3 =
DDI; bytes 4–7 = value.

**Section-control DDIs (verified against isobus.net):**

| Role | DDI (dec) | DDI (hex) | Meaning |
|---|---|---|---|
| **Section Control State** (master enable) | 160 | 0xA0 | 0 = manual/off, 1 = auto (implement accepts commanded states) |
| **Setpoint Condensed Work State (1–16)** | 290 | 0x122 | App commands sections; 2 bits/section (00 off / 01 on / 10 error / 11 no-change) |
| …family up to **(241–256)** | 305 | 0x131 | 16 contiguous DDIs, one per 16-section block |
| **Actual Condensed Work State (1–16)** | 161 | 0xA1 | Implement reports back; 2 bits/section |
| …family up to **(241–256)** | 176 | 0xB0 | 16 contiguous DDIs |
| **Setpoint Work State** (overall on/off command) | 289 | 0x121 | added 2012 as commanded counterpart to 141 |
| **Actual Work State** (overall on/off) | 141 | 0x8D | 1 = working, 0 = not |

**Mapping recipe:**
- Master enable ↔ DDI 160 (auto vs manual).
- Command sections (App→implement) ↔ DDI 290+ (Setpoint Condensed Work State).
- Read actual states ↔ DDI 161+ (Actual Condensed Work State).
- All transported in PGN 51968 / 0xCB00.
- Fallback rule: a setpoint-capable working set must fall back to the Actual Work
  State DDIs when connected to a TC that doesn't support setpoints.

> **No runtime "number of sections" DDI exists.** Section count is structural —
> described in the Device Descriptor Object Pool (DDOP), where each section is a
> device element with its own geometry. Derive the count from DDOP structure, not
> a process value. (Nearest DDI: 159 / 0x9F "Number of Sub-Units per Section".)

> This is a larger structural shift than the 1:1 rows in §3 — it's "stand up a
> Task Controller / DDOP," not "renumber a message." Call it out as its own work
> item in the debate.

---

## 8. Open items before any implementation

- **GMI bit-field boundaries** (bytes 3–4: limit status / exit reason) are
  version-dependent in AgIsoStack; verify against ISO 11783-7 text if bit-exact
  parsing is required.
- **SPN-level hitch fields** (1868–1877) are medium-confidence; PGNs are high.
- **Condensed Work State interior DDIs** (163–175, 292–304) are inferred from the
  confirmed contiguous range; spot-check against the isobus.net DDI export for a
  compliance-grade build.

---

## Sources

Verified 2026-06-01 via web research (cross-checked ≥2 independent sources each):

- **AgIsoStack++** (Open-Agriculture) — `can_general_parameter_group_numbers.hpp`,
  `isobus_guidance_interface.cpp`, `isobus_speed_distance_messages.cpp`:
  https://github.com/Open-Agriculture/AgIsoStack-plus-plus
- **ISOBUS Data Dictionary / PGN database** (VDMA) — isobus.net:
  - Guidance PGNs & Process Data PGN: https://www.isobus.net/isobus/pGNAndSPN
  - DDI entities (160, 161, 141, 289, 290, 305): https://www.isobus.net/isobus/dDEntity/
  - DDI export: https://www.isobus.net/isobus/site/exports?view=export
- **AgIsoStack API docs** (Machine Selected Speed Command):
  https://delgrossoengineering.com/isobus-docs/
- ISOBUS PGN/SPN reference gist: https://gist.github.com/thxmxx/df5cd77846d6bc46da396e202db54e11

**Confirmed against tentative claims:**
- GSC = **44288 / 0xAD00**, GMI = **44032 / 0xAC00** — confirmed.
- Wheel-Based Speed = **65096 / 0xFE48**, Ground-Based = **65097 / 0xFE49** — confirmed.
- **Correction:** Machine Selected Speed is **61474 / 0xF022** (not in the 65xxx
  range); Machine Selected Speed Command = **64835 / 0xFD43**.
- Rear Hitch State = **65094 / 0xFE46**, Front Hitch State = **65092 / 0xFE44**;
  raise/lower is a set-point SPN inside, not a separate command PGN.
- Process Data PGN = **51968 / 0xCB00**; section control via Condensed Work State
  DDIs (Actual 161+, Setpoint 290+), master enable DDI 160.

> One discarded source: a CSS Electronics PGN-list fetch returned shifted/incorrect
> speed & hitch numbers; values above use the AgIsoStack source + isobus.net as the
> authoritative tiebreaker.
