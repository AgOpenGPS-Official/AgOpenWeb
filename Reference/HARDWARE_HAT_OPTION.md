# AgOpenWeb HAT Option — CM4 IO Board + AOW HAT

> **Status: DECIDED 2026-09-23 — the HAT is the plan of record for the prototype.** It is the simpler
> board to lay out and build, and sealing doesn't separate it from the AiO board (§1). The AiO board
> (`HARDWARE_AIO_BOARD.md`) is **parked**, not abandoned: it stays the candidate for a later
> single-board version, and the HAT proves its peripheral circuits first. The official Raspberry Pi
> **CM4 IO Board** is the host; the AOW-specific circuits go on a **40-pin HAT**.
>
> Sources: CM4 IO Board datasheet (`cm4io-datasheet.pdf`, §2.2, §2.12, §2.14 and the PSU/GPIO
> schematic sheets), the IO board KiCad design (`CM4IOv5.kicad_pcb`, for all coordinates) and the
> AiO netlist/issue docs.

---

## 1. Why consider it

Every hard, high-speed part of the AiO board is already done — and tested — on the IO board:

| AiO block | Risk on the AiO board | On the IO board |
|---|---|---|
| CM4 DF40 mezzanine (CN1/CN2) | hand stencil + assembly (S12b) | done |
| PCIe → M.2 (U5) | SI unverified until built | PCIe ×1 slot (NVMe via an M.2 adapter card) |
| Gigabit Ethernet (L1 magjack) | SI unverified until built | done |
| CM4 rails: U2 eFuse, U4 NVMe buck | | done |

Everything else on the AiO board is low-speed — SPI, UARTs, GPIO, analog — and fits a HAT that can
be 2-layer, cheap, and easy to rework. It also suits AgOpenGPS users who already own a CM4 + IO board.

**Sealing is not a differentiator (2026-09-23).** The IO board + HAT stack goes inside the enclosure,
and the HAT carries a board-mount header; a **ribbon cable runs from it to the panel-mount Deutsch
connector** (ATS13 or similar). The enclosure is sealed at the panel connector either way, so the
IO board's edge connectors don't matter. What remains in the AiO board's favour is size (one
184 × 119 mm board against a 160 × 90 mm board plus a HAT stack) and a single part to build. The HAT
is also a **stepping stone**: its peripheral circuits are the AiO board's, so a HAT build proves CAN,
the ADC, RS-232 and the key latch before committing to the DF40 board.

**Ribbon to the panel connector — design points:**

- **Power through the ribbon:** ~0.4 A at 12 V running, more on a cold start. IDC ribbon is ~1 A per
  28 AWG conductor: give constant 12 V, keyed 12 V (tiny) and GND **two or more conductors each**, and
  keep the vehicle fuses in the harness, not on the ribbon.
- **CAN:** each CANH/CANL pair on adjacent conductors, with a GND conductor between pairs (or
  twisted-pair ribbon). Keep the run short.
- **Protection stays on the HAT**, at the header where the ribbon lands (TVS, series resistors,
  SRV05-4), exactly as it sits behind J1 on the AiO board.
- **Vibration:** latched/keyed IDC headers (box headers with ejectors or a locking ramp) and strain
  relief at both ends.
- **Pinout:** reuse the AiO J1 pin assignment (`HARDWARE_AIO_NETLIST.md` §5.1) plus the keyed 12 V
  on pin 21 (S8c.7), so the harness is the same for either board.

## 2. Hosts

| Host | GPIO allocation | Power into the host | Notes |
|---|---|---|---|
| **CM4 IO Board** (primary) | identical to the AiO board (same CM4) | switched 12 V into **J20** (§4) | tested: S8c.8 checks 1–3 were run on this board |
| Pi 4B | identical (same BCM2711) | 5 V into header pins 2/4 | would need a 5 V converter on the HAT (DNP on the IO board build) |
| Pi 5 | **recheck** — RP1 maps UARTs, PCM and PWM differently | 5 V / 5 A into the header | U1-class 5 A converter at its limit; GPS on UART5 and the LED drive need rework |

**Recommendation if pursued: target the IO board only**, keeping the standard HAT footprint so a
Pi 4 variant stays possible later.

## 3. Mechanical

> **Source: Raspberry Pi's CM4 IO Board KiCad design** (`CM4IO-KiCAD.zip` → `CM4IOv5.kicad_pcb`, from
> datasheets.raspberrypi.com), pulled 2026-09-23. Supersedes the earlier photo-based notes.
> **Coordinates are in the HAT frame:** origin at the HAT's top-left corner = IO board KiCad
> (79.0, 104.0); x to the right, y **down**, seen from above. The HAT's left edge sits on the IO
> board's left edge (x 79.0), and its bottom edge is 0.5 mm past the IO board's bottom edge
> (y 160.0), so the HAT covers a corner of the IO board. EasyEDA with the origin at the bottom-left:
> y_editor = 56.5 − y.

### 3.1 Outline, holes and connectors

| Feature | HAT x, y (mm) | IO board KiCad x, y | Notes |
|---|---|---|---|
| Outline | 0–65 × 0–56.5 | 79.0–144.0 × 104.0–160.5 | standard HAT |
| Hole H3 | 3.5, 3.5 | 82.5, 107.5 | M2.5 (2.7 mm), header end |
| Hole H5 | 61.5, 3.5 | 140.5, 107.5 | header end, beside PoE J9 |
| Hole H2 | 3.5, 52.5 | 82.5, 156.5 | |
| Hole H4 | 61.5, 52.5 | 140.5, 156.5 | beside the RJ45 |
| **J8 pin 1** (3.3 V) | **8.37, 4.82** | 87.37, 108.82 | odd pins on the inner row, y 4.82 |
| J8 pin 2 (5 V) | 8.37, 2.28 | 87.37, 106.28 | even pins on the edge row, y 2.28 |
| J8 pin 39 / 40 | 56.63, 4.82 / 56.63, 2.28 | 135.63, 108.82 / 106.28 | pitch 2.54 along x |
| **J1 pin 1 `GLOBAL_EN`** | **18.04, 32.00** | 97.04, 136.00 | 1 × 3, runs in **−x** from pin 1 |
| J1 pin 2 GND | 15.50, 32.00 | 94.50, 136.00 | |
| J1 pin 3 `RUN_PG` | 12.96, 32.00 | 91.96, 136.00 | |

- The four HAT holes were confirmed: the two at the header ends are 58.0 mm apart, and each pair is
  49.0 mm apart across the board.
- **J1 is not on J8's 2.54 mm grid** (pin 1 is 9.67 mm, 27.18 mm from J8 pin 1). Place the HAT's
  1 × 3 socket **by coordinate**, not by snapping to the header.
- The HAT sockets go on the **bottom layer**: EasyEDA mirrors a bottom footprint, so check pin 1
  against the top-view coordinates above after placing. The J1 socket must be the **same height** as
  the 2 × 20 socket so both seat together.
- Standard stacking: 11 mm M2.5 standoffs, 2 × 20 female socket on the HAT, and a 3-pin male header
  soldered into the IO board's J1.

### 3.2 IO board parts under the HAT

The only parts on the HAT's underside are the two sockets (and the pins of any through-hole parts
on top — keep those, trimmed, away from J9).

| IO board part | HAT x, y extent (mm) | Clearance to the HAT (11 mm standoffs) |
|---|---|---|
| BT1 CR2032 holder (Keystone 3034) | 3.4–18.6 × 6.6–30.4 | low (≈ 5 mm): clears |
| J1 pads (+ fitted 3-pin header) | 11.7–19.3 × 30.7–33.3 | mates with the HAT socket |
| J22 HDMI0, J10 HDMI1 | centred at x 23.0 and 48.0, along the bottom edge (y ≈ 42–56, estimated — footprint has no courtyard) | receptacles clear; HDMI plugs may not — AOW runs headless |
| J9 PoE 2 × 2 header | 60.2–62.8 × 8.4–10.9 (pins) | pin tips ≈ 2.5 mm below the HAT: no bottom parts or pin ends there |
| J6 CSI/DSI I²C jumpers | 41.4–43.9 × −3.3…−0.8 (pins) | just outside the top edge |
| U3 RJ45 magjack | from x 64.8, y 35.7 down | **right against the HAT's right edge** (courtyard at x 64.8) and taller than 11 mm: don't grow right below y ≈ 35 |

### 3.3 Room to grow past 65 × 56.5 mm

| Direction | Room | Notes |
|---|---|---|
| Left (−x), down (+y) | none | IO board edges |
| **Up (−y)** | **≈ 20 mm** (to IO y ≈ 84) | over the J6 jumpers and J4 camera FFC (unused by AOW). **IO board hole H7 at HAT (11.0, −21.0)** could be a fifth standoff. The CSI/DSI FFC row at IO y ≈ 75 limits it. → **65 × ~76 mm**. **Best direction if the GPS module doesn't fit.** |
| **Right (+x)** | **up to ≈ 25 mm (measured on the board), with a notch for the Ethernet / USB** | extension x 65–90 at most, **y 0–35 only**. **A ceiling, not a target:** take only what the layout needs (likely 10–15 mm) and leave the rest as air around the CM4 heatsink. The notch clears U3 (the RJ45 courtyard starts at HAT y 35.7, and it is taller than the 11 mm spacing) and the USB stack beside it. The extension covers the edge of the CM4 module (low, ≈ 5–6 mm: clears) and stops **≈ 10 mm short of the CPU heatsink**. Under it: U1/U2 (USON) and Y1 (HC-49 SMD), all low. |

- **Combined:** up and right together give an L-shaped board of about 90 × 76 mm, minus the notch. The
  upward extension is **x 0–65 only**: to the right of that is the CM4 module.
- **The right extension is a 25 mm cantilever** with no hole under it: IO board H5 (61.5, 3.5) and
  H4 (61.5, 52.5) are the nearest supports. Keep the ribbon header, anything a cable pulls on and
  heavy parts near the holes, and put light, low parts (latch, passives) at the tip.
- **Heat:** the extension sits beside the CM4 heatsink with a 10 mm gap. Don't put the HAT's warm
  parts (P-FET, GPS module) on that end.
- **Servicing:** the CM4 can no longer be lifted out without first removing the HAT.

Growing in any direction gives up Pi 4 compatibility (§2).

### 3.4 GPS boards — EMAX UM981 and Unicore UM982EB

**DECIDED 2026-09-23: the HAT takes the EMAX UM981 or the Unicore UM982EB, and nothing else.**
These are the boards the author owns. Anyone wanting another receiver can remix the board. Only one
is fitted at a time. Both stack above the HAT on standoffs, both use UART5 (`GPS_TX` GPIO12 → module
RX, `GPS_RX` GPIO13 ← module TX), and both run from header 5 V.

#### 3.4.0 Combined footprint — copied from AgOpenGPS AiO v5.0i

AgOpenGPS AiO v5.0i (`PCB_v5.0i-LOCKED`, EasyEDA export 2026-09-23) already overlays both boards in
one area: header H981 (EMAX) and H14 (UM982EB), sharing one mounting hole and one UART net pair.
The coordinates below come from that export. **Frame:** origin at the shared hole, x along the
UM982EB's long axis toward its header, y across it, both as laid out on v5.0.

| Feature | x, y (mm) | Board | Notes |
|---|---|---|---|
| **Hole A** | **0.00, 0.00** | both | Ø 3.3, shared |
| Hole B | 0.00, 34.30 | UM982EB | Ø 3.3 |
| Hole C | 64.60, 0.00 | UM982EB | Ø 3.2 |
| Hole D | 64.60, 34.30 | UM982EB | **not on v5.0**, which has other parts there. Add it on the HAT: it clears H14 pad 28 by ~1.6 mm. |
| Hole E | 36.00, 0.00 | EMAX | Ø 3.6 on v5.0 (a board hole) |
| Hole F | 35.50, 36.00 | EMAX | Ø 3.6. This is the EMAX's odd hole (§3.4.1). |
| — | (0.00, 36.00) | EMAX | **no hole**: it would overlap hole B (1.7 mm apart), so the EMAX sits on 3 standoffs, as on v5.0 |
| **H981** 1 × 8, 2.54 mm | x −1.20; pin 1 at y 9.12, pin 8 at y 26.90 | EMAX | pad 1.6 / drill 0.9. Pin 1 GND, 2 5V, 7 TX1, 8 RX1 |
| **H14** 2 × 14, 2.0 mm | odd pins x 65.80, even x 63.80; pins 1/2 at y 4.30, 27/28 at y 30.30 | UM982EB | pad 1.5 / drill 0.89. Pin 6 VIN, 14/17/20/22 GND, 15 TX1, 16 RX1 |

- **Nets, as on v5.0:** H981.7 and H14.15 → `GPS_RX` (module TX); H981.8 and H14.16 → `GPS_TX`
  (module RX); H981.2 and H14.6 → 5 V; H981.1 and H14.14/17/20/22 → GND. v5.0 puts the supply on a
  3.3 V / 5 V selector (`SJ_BYNAV-UM`) for other receivers. The HAT drops it: 5 V only.
- **Size:** pads and holes cover about **69 × 40 mm**. The UM982EB above them is 46 × 71 mm (board
  edges at x −3.2…67.8, y −6.0…40.0 in this frame).

**Where it fits on the HAT.** Lying along the HAT's 65 mm width it doesn't fit: it's 69 mm long, and
the right extension (§3.3) is only 35 mm tall. **Turned so its long axis runs down the HAT (+y), it
fits** with the upward extension. HAT position = (22 + y, −12 + x):

| Frame point | HAT x, y |
|---|---|
| Hole A | 22.0, −12.0 |
| Hole B | 56.3, −12.0 |
| Hole C / D | 22.0, 52.6 / 56.3, 52.6 |
| Holes E / F | 22.0, 24.0 / 58.0, 23.5 |
| H981 pins 1–8 | x 31.1–48.9, y −13.2 |
| H14 pins | x 26.3–52.3, y 51.8 / 53.8 |
| UM982EB board | x 16.0–62.0, y −15.2…55.8 |

- **This clears the HAT's own through-holes:** no GPS pad or hole lands in the 2 × 20 socket band
  (y 1.4–5.7), the J1 socket is at x < 19, and hole D is 5.2 mm from HAT hole (61.5, 52.5).
- **It needs about 16 mm of the upward extension** (HAT y down to −16, §3.3). The right extension
  isn't needed for the GPS.
- The UM982EB antennas come out at the top (the y −15 edge). The EMAX antenna points down the HAT
  from its y ≈ 27–37 edge, hanging over the HAT, not the CM4.
- Parts on the HAT under the GPS board have ~9 mm of headroom (the socket height), which clears all
  the SMD parts.

#### 3.4.1 EMAX UM981 — 42 × 42 mm, single antenna

Source: the EasyEDA user part **"UM981 EMAX"** (contributor `mtz8302`, from the AgOpenGPS community),
exported 2026-09-23. It also comes as a "NO HOLES NO PADS" variant, which is only the outline.
Coordinates are in the GPS board's own frame: origin at its top-left corner, y down, seen from above.

| Feature | x, y (mm) | Notes |
|---|---|---|
| Outline | 0–42.0 × 0–42.0 | |
| Mounting holes | (3.0, 3.0), (3.0, 39.0), (39.0, 39.0), **(39.0, 3.5)** | Ø 3.1 mm (M3). **One hole is at y 3.5, not 3.0**: check it on a real board before relying on it. |
| Header | 1 × 8, 2.54 mm pitch, pin 1 at **(12.12, 40.20)**, pins in +x, pin 8 at (29.90, 40.20) | pad Ø 1.52 mm, drill 0.91 mm, 1.8 mm in from the bottom edge |
| Antenna connector | x 17.75–24.25, sticks out **10 mm above the top edge** | single antenna (UM981 is single-antenna, with a built-in IMU) |
| Second connector (probably USB) | x −3.0…3.5, y 26.5–35.5, sticks out **3 mm past the left edge** | outline only; identify it on the board |

| Pin | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 |
|---|---|---|---|---|---|---|---|---|
| Signal | GND | **5V** | EV (event in) | TX2 | RX2 | PPS | **TX1** | **RX1** |

- **It takes 5 V**, so it runs from header 5 V and needs no 3.3 V regulator on the HAT.
- **Only four pins connect:** GND, 5V, TX1 → `GPS_RX` (GPIO13) and RX1 ← `GPS_TX` (GPIO12), on
  UART5 as on the AiO board. EV, TX2/RX2 and PPS go to unconnected pads, because all 28 GPIOs are
  allocated. **Check the UART logic level:** the UM981 is 3.3 V, but confirm the board doesn't shift
  it to 5 V.
- **Mounting: stacked, not flat.** A 2.54 mm female header on the HAT for the 8 pins, plus **4 × M3
  standoffs** to the HAT at the hole positions. The board then sits above the HAT's low parts, and
  only the header and the four standoffs cost HAT area. The M3 holes take the vibration, not the
  pins. The stack is roughly IO board + 11 mm + HAT + ~11 mm + GPS board.
- **Orientation:** put the antenna edge at a HAT edge, away from the CM4 heatsink, with a pigtail to
  the enclosure panel. Keep the left-edge connector reachable if it's the USB used for configuration.

#### 3.4.2 Unicore UM982EB — 46 × 71 mm, dual antenna (heading)

Source: the EasyEDA user part **"UM982-NO-PADS"** (contributor `wildbuckwheat`, "UM982 Evaluation
Board"), exported 2026-09-23. Its pin labels were checked against the AiO board's **U16** footprint
(`UM982EB MODULE`, contributor `viluchinsk`, in `PCB_PCB_AOW-v2.0_2026-09-18.json`). All the pins the
AiO board uses agree: 6 VIN, 14/17/20/22 GND, 15 TX1, 16 RX1. Same frame as above: origin top-left,
y down, seen from above.

| Feature | x, y (mm) | Notes |
|---|---|---|
| Outline | 0–46.0 × 0–71.0 | U16's outline is 45.4 × 71.0. **Measure a real board.** |
| Mounting holes | (6.0, 3.2), (40.3, 3.2), (6.0, 67.8), (40.3, 67.8) | Ø 3.2 mm; pattern 34.3 × 64.6 (U16: 34.0 × 64.75, Ø 3.4). **Measure.** |
| Header | **2 × 14, 2.0 mm pitch** along the bottom edge, x 10.0–36.0; outer row y 69.0 (odd pins), inner row y 67.0 (even pins) | **pin 1 at (36.0, 69.0)**, pin 2 at (36.0, 67.0), pin 27 at (10.0, 69.0), pin 28 at (10.0, 67.0) |
| Antenna connectors | two, at about (15.5, 4.4) and (32.5, 4.4) | the part marks ~15 mm of "RF conn clearance" past the top edge |

| Pin | Signal | | Pin | Signal |
|---|---|---|---|---|
| 1 | MOSI | | 2 | CSN |
| 3 | CLK | | 4 | SDRY |
| 5 | LNA_PWR | | **6** | **VIN (5 V)** |
| 7 | MISO | | 8 | RX3 |
| 9 | RSTN | | 10 | RSV |
| 11 | EVENT | | 12 | RSV |
| 13 | TX3 | | **14** | **GND** |
| **15** | **TX1** → `GPS_RX` | | **16** | **RX1** ← `GPS_TX` |
| **17** | **GND** | | 18 | TX2 |
| 19 | RX2 | | **20** | **GND** |
| 21 | PVT_STAT | | **22** | **GND** |
| 23 | PPS | | 24 | RSV |
| 25 | RTK_STAT | | 26 | ERR |
| 27 | SDA | | 28 | SCL |

- **Connect the same four signals** as the EMAX board: VIN (6), GND (14/17/20/22), TX1 (15), RX1 (16).
  The rest go to unconnected pads.
- **Mounting:** a 2 × 14 2.0 mm female header plus 4 × M3 standoffs. At 46 × 71 mm it's bigger than a
  standard HAT (65 × 56.5), so it overhangs as a stacked board. Let it overhang into the room from
  §3.3, not over the CM4 heatsink. Two antenna leads to the panel.

#### 3.4.3 Not supported (remix territory)

v5.0 also takes the ArduSimple **Micro** (XBee size, 2 × 10 at 2.0 mm, 3.3 V, no holes) and the
ArduSimple **Arduino-format** simpleRTK2B, in the same area. Adding them would grow the GPS area to
about 80 × 52 mm and the HAT with it, and the author doesn't own either board. Left out; the v5.0
export has their positions if someone remixes the HAT.

## 4. Power path

The IO board takes **+12 V on J19 (barrel) or J20** — a 4-pin "Berg" floppy-style connector
(mating part TE 171822-4) carrying +5 V, +12 V and GND. Its on-board converter makes 5 V at 3 A for
the CM4; the 12 V bus goes straight to the PCIe slot and fan connector.

### 4.1 Chosen direction: the HAT switches protected 12 V into J20

```
vehicle constant 12 V ─ Q1 reverse-polarity + SMBJ24A ─ P-FET high-side switch ─ cable ─ IO board J20 +12 V
vehicle keyed 12 V ── key front end (S8c.7 block A) ──┐          ▲
header pin 1 (CM4 3.3 V) ── hold (block C) ───────────┴── latch ─┘
header pins 2/4 (IO board 5 V) ─► HAT peripherals (CAN, ADC VA, GPS, LEDs, WAS supply) + U3 3.3 V LDO
```

- **No IO board modification.** The alternative — the HAT feeding 5 V into J20 — needs L5 removed
  so the IO board's converter doesn't fight it (datasheet §2.2); rejected for that reason.
- **The HAT has no main converter**: U1 (TPS54560), U2 (eFuse) and U4 (NVMe buck) drop out. The HAT
  keeps U3 (its own 3.3 V LDO, from header 5 V) — the CM4's 3.3 V output has too little spare
  current to share.
- **J20 pinout:** standard floppy Berg is 1 = +5 V, 2/3 = GND, 4 = +12 V. **Verify against the IO
  board schematic before wiring.**

### 4.2 Adapting the S8c.7 latch

The logic is unchanged — **on = KEY OR HOLD** — but it drives a high-side P-FET instead of U1's `EN`:

- **Q5 (key) and Q6 (hold) pull the P-FET's gate low directly**, through a gate divider (e.g. 100 k
  gate → source, 47 k gate → Q5/Q6 drains, giving V<sub>GS</sub> ≈ −8 V at 12 V) with a 12 V zener
  gate → source for jump starts. **Q4, R86 and the `OFF_G` node go away.**
- **Soft start:** a gate–drain capacitor (10–47 nF, to be sized) so the P-FET doesn't slam the IO
  board's input capacitors.
- **"CM4 alive"** = header pin 1 or 17. On the IO board that is the CM4's own 3.3 V output, which
  **S8c.8 check 1 showed drops to 0 V at halt** with `POWER_OFF_ON_HALT=1`.
- Blocks A (key front end), C (hold), D (low-voltage cut-off) and the always-on LDO carry over as
  they are. Block D's divider moves to the protected 12 V node.
- Block E (restart) and the watchdog: §5.

### 4.3 Limits to design for

- **Input voltage — DECIDED 2026-09-23: reuse the AiO front end, no surge stopper.** The IO board is
  rated 7.5–28 V with PCIe and fan unused (datasheet §2.2), and the SMBJ24A clamps short spikes at up
  to ~39 V, so brief transients can exceed the rating. That is accepted: the AgOpenGPS AiO boards
  run on less input protection than this with no field problems, a 24 V jump start stays under 28 V,
  and tractor alternators have load-dump suppression. The front end is therefore Q1 (reverse-polarity
  P-FET) + SMBJ24A + the fuse in the harness, then the latch's P-FET switch (§4.2).
  *Considered and set aside:* an LT4363-2 surge stopper (LCSC C118131, output clamped at ~26 V, its
  SHDN pin driven by Q4 in place of the P-FET switch, ~$4–6). Revisit only if field units show
  input-related failures.
- **5 V budget: 3 A** from the IO board's converter for the CM4 (Raspberry Pi budget 9 W) **plus**
  the HAT: 3 × CAN (MCP251863), the GPS module, 4 × SK6812, the WAS sensor supply, U3's 3.3 V loads,
  and anything on the IO board's USB ports. Probably fine; add it up before committing.
- **Standby draw** (S8c.7 budget ≈ 180 µA) no longer includes U1's `EN` divider.

## 5. Restart and watchdog signals

The AiO board uses two CM4 signals the 40-pin header doesn't carry: `GLOBAL_EN` (block E's restart
pulse) and `RUN_PG` (U20's reset output). Two ways to reach them:

1. **J1 socket (preferred).** The IO board's **J1** — three pads, unpopulated: 1 = `GLOBAL_EN`,
   2 = GND, 3 = `RUN_PG` (datasheet table 3) — sits under the HAT. Solder a 3-pin header into J1 and
   give the HAT a matching socket. **Block E and U20 then stay exactly as on the AiO board**, and both
   were bench-tested through these very pads (S8c.8 checks 2 and 3).
2. **Power-cycle instead.** Use the latch to cut the 12 V for ~1 s: covers both the halted-with-key-on
   restart and a watchdog reset, with no connection beyond the header. Costs extra gating (the key
   path must be overridable for the cut) and a harder reset than `RUN_PG`. Needed for a Pi 4 host
   unless its own `RUN`/`GLOBAL_EN` pads are used — **check where those are on the Pi 4B**.

## 6. Signals on the 40-pin header

Same allocation as the AiO board (`HARDWARE_AIO_NETLIST.md` §7), **with the S4 swap applied**
(`LED_DATA` on GPIO21, `SW_REMOTE` on GPIO2 — the §7 table there predates S4).

| Header pin | GPIO | Net | | Header pin | GPIO | Net |
|---|---|---|---|---|---|---|
| 1 | — | 3.3 V (**CM4 alive**, block C) | | 2 | — | 5 V (HAT supply) |
| 3 | 2 | `SW_REMOTE` | | 4 | — | 5 V |
| 5 | 3 | `WDT_EN` | | 6 | — | GND |
| 7 | 4 | `RS232_2_TX` | | 8 | 14 | `CON_TX` |
| 9 | — | GND | | 10 | 15 | `CON_RX` |
| 11 | 17 | `CAN2_INT` | | 12 | 18 | `PWM_MOTA` |
| 13 | 27 | `CAN3_INT` | | 14 | — | GND |
| 15 | 22 | `WDT_WDI` | | 16 | 23 | `MOT_DIR` |
| 17 | — | 3.3 V | | 18 | 24 | `STEER_EN` |
| 19 | 10 | `SPI0_MOSI` | | 20 | — | GND |
| 21 | 9 | `SPI0_MISO` | | 22 | 25 | `NCS_CAN3` |
| 23 | 11 | `SPI0_SCLK` | | 24 | 8 | `NCS_CAN1` |
| 25 | — | GND | | 26 | 7 | `NCS_CAN2` |
| 27 | 0 | `RS232_1_TX` | | 28 | 1 | `RS232_1_RX` |
| 29 | 5 | `RS232_2_RX` | | 30 | — | GND |
| 31 | 6 | `PIEZO_PWM` | | 32 | 12 | `GPS_TX` |
| 33 | 13 | `GPS_RX` | | 34 | — | GND |
| 35 | 19 | `SW_WORK` | | 36 | 16 | `CAN1_INT` |
| 37 | 26 | `NCS_ADC` | | 38 | 20 | `SW_ENGAGE` |
| 39 | — | GND | | 40 | 21 | `LED_DATA` |

- **Not a spec-compliant HAT:** GPIO0/1 (pins 27/28) are the HAT ID-EEPROM bus, used here for
  RS-232 #1, so there is no ID EEPROM. `force_eeprom_read=0` is already required on the AiO board.
- The Linux console (`CON_TX`/`CON_RX`) can stay on a HAT header, or be dropped: the IO board has
  USB and HDMI for bring-up.

## 7. What carries over, what drops out

| Carries over unchanged | Changes | Drops out |
|---|---|---|
| 3 × MCP251863 + X1, termination jumpers | S8c.7 latch: drives a P-FET, not U1 `EN` (§4.2) | U1 5 V buck + L2, D1 |
| U21 ADC (VA = header 5 V, still ratiometric with the WAS supply) | input protection: ≤ 28 V to J20 (§4.3) | U2 eFuse |
| U12 SP3232 + RS-232 TVS | block E / U20: via a J1 socket (§5) | U4 NVMe buck, U5 M.2 |
| GPS slot (U16 or P2) | power LED: no `PI_LED_nPWR` on the header — drop or drive from 5 V | L1 magjack, CN1–CN3 |
| switch inputs, steering outputs, SK6812 chain, piezo | J1 field connector → board-mount IDC header + ribbon to a panel Deutsch (§1) | H3 USB/rpiboot header (IO board has micro-USB) |
| U3 3.3 V LDO, U20 watchdog, SW1 reset | | |

## 8. Open questions

1. ~~**HAT or AiO as the plan of record?**~~ **Decided 2026-09-23: the HAT, for the prototype.** The
   AiO board is parked as a possible later single-board version.
2. ~~Measure the HAT hole positions~~ — **done 2026-09-23 from the KiCad design** (§3).
3. ~~GPS module placement~~ — **decided 2026-09-23: EMAX UM981 or Unicore UM982EB only, using
   v5.0's combined footprint, turned along the HAT's y axis with ~16 mm of upward extension** (§3.4.0). Still to check on real boards: the EMAX fourth hole and left-edge connector, the UM982EB
   outline and hole pattern (the two EasyEDA parts differ by 0.2–0.6 mm), and both boards' UART
   logic level.
4. ~~Surge limiting to ≤ 28 V~~ — **decided 2026-09-23: AiO front end reused, no surge stopper** (§4.3).
5. **J20 pinout and cable** — verify pins; choose the HAT-side connector.
6. **5 V budget** on the IO board's 3 A converter (§4.3).
7. **J1 socket vs power-cycle** for restart and watchdog (§5).
