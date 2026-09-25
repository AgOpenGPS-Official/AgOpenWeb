# AgOpenWeb HAT — Design Reference

> **The AOW HAT:** a 40-pin HAT for the official Raspberry Pi **CM4 IO Board**. It carries everything
> AgOpenWeb needs beyond the CM4 itself:
> - 3 × CAN FD
> - 2 × RS-232
> - a GNSS board (EMAX UM981 or Unicore UM982EB)
> - wheel-angle and current sensing
> - steering outputs and switch inputs
> - a hardware watchdog and status LEDs
> - an **always-on + keyed 12 V power latch** that lets Linux shut down cleanly at key-off
>
> **Status (2026-09-25): schematic drawn and verified against `HARDWARE_HAT_NETLIST.md`** (150 parts, every pin; J8 console optional and not fitted). PCB outline, holes and J1/J2/J6/J7 placed and verified. **2026-09-25: power section reworked for direct power** (design §4); Power sheet updated and verified (142 parts, pin for pin); layout next. This is the prototype
> plan of record. Designed in **EasyEDA Standard**.
>
> **The HAT document set.** These four files are complete on their own; you don't need any other doc:
>
> | File | Contents |
> |---|---|
> | `HARDWARE_HAT_DESIGN.md` (this file) | what the board does and why: mechanical, power, circuits, pinouts |
> | `HARDWARE_HAT_NETLIST.md` | step-by-step schematic checklist: every part and net |
> | `HARDWARE_HAT_BOM.md` | every part with its LCSC number |
> | `HARDWARE_HAT_ISSUES.md` | open decisions, bench checks still to do, results so far |
>
> Ref designators (U1, R12, J3…) are the HAT's own and match across all four files.
>
> **Sources:**
> - CM4 IO Board datasheet (`cm4io-datasheet.pdf`) and Raspberry Pi's IO board KiCad design
>   (`CM4IO-KiCAD.zip` → `CM4IOv5.kicad_pcb`), both from datasheets.raspberrypi.com. All IO board
>   coordinates and pinouts come from these.
> - EasyEDA user parts for the GNSS boards, and AgOpenGPS AiO v5.0i for their combined footprint.
> - Bench tests on a CM4 IO Board, 2026-09-23.

---

## 1. Overview

```
vehicle constant 12 V ─ 5 A fuse ─────────────────────────────► IO board J20 (+12 V)   (not via the HAT)
                                           ┌──────────── AOW HAT ─────────────────────────────┐
12 V sense wire ─────► J3.1 ─ VIN sense / low-voltage cut-off ─┐
vehicle keyed 12 V ──► J3.21 ─ key sense ──────────────────────┼─ key/hold latch ─┬─► Q2: 5 V_IO → 5V_MAIN
IO board header pin 1 (CM4 3.3 V) ─ hold ──────────────────────┘                  └─► Q1: holds GLOBAL_EN low (CM4 off)
IO board header pins 2/4 (5V_IO, always on) ─► U1 (latch supply); via Q2 → CAN, ADC, GNSS, LEDs, WAS supply, U4 → +3V3
IO board header GPIO ─► SPI (3 × CAN + ADC), 4 UARTs, switches, steering, watchdog, LEDs, piezo
IO board J1 pads ◄── J2 socket: GLOBAL_EN (restart), RUN_PG (watchdog reset)
field harness ◄──► J3 (26-pin shrouded IDC) ─ ribbon ─ panel Deutsch connector
```

**Why a HAT on the IO board.** Everything high-speed and risky is already done and tested on
Raspberry Pi's IO board:
- the CM4 mezzanine connectors
- Gigabit Ethernet
- PCIe (NVMe through an M.2 adapter card in the ×1 slot)
- the CM4's power rails

What's left is low-speed (SPI, UARTs, GPIO, analog), so the HAT can be a cheap, forgiving board
that's easy to rework.

**Enclosure.** The IO board + HAT stack lives inside the enclosure. A ribbon cable runs from J3 to a
panel-mount Deutsch connector, and the enclosure is sealed there, so the IO board's own edge
connectors don't matter.

**Host.** The CM4 IO Board only. The HAT uses the IO board's J1 pads (§5) to hold the CM4 off and
restart it, so it doesn't fit a Pi 4/5 as it stands. It keeps the standard HAT hole pattern, so a Pi 4
variant stays possible (same BCM2711, same GPIO allocation).

---

## 2. Mechanical

**Coordinates: HAT frame.** Origin at the HAT's top-left corner = IO board KiCad (79.0, 104.0); x to
the right, y **down**, seen from above. The HAT's left edge sits on the IO board's left edge, and
its bottom edge is 0.5 mm past the IO board's bottom edge, so it covers a corner of the IO board.
**EasyEDA editor frame** (as set up 2026-09-24): origin at the board's **top-left** corner, Y **up**,
so the board lies at negative Y. **X_editor = x, Y_editor = −(y + 16).** The board outline runs
(0, 0) to (65, −72.5).

| Feature | X_editor | Y_editor | Ø |
|---|---|---|---|
| H1 / H2 (HAT mount) | 3.50 / 61.50 | −19.50 | 2.7 |
| H3 / H4 (HAT mount) | 3.50 / 61.50 | −68.50 | 2.7 |
| H5 / H6 (GNSS A / B) | 22.00 / 56.30 | −4.00 | 3.3 |
| H7 / H8 (GNSS C / D) | 22.00 / 56.30 | −68.60 | 3.2 |
| H9 / H10 (GNSS E / F) | 22.00 / 58.00 | −40.00 / −39.50 | 3.6 |
| J1 pin 1 / pin 2 | 8.37 | −20.82 / −18.28 | |
| J2 pin 1 | 18.04 | −48.00 | |
| **J1 origin** (header centre), 0° | 32.50 | −19.55 | |
| **J2 origin** (pin 2), **180°** | 15.50 | −48.00 | |
| **J6** (EMAX): pin 1 / pin 8, pins run +X | 31.12 / 48.90 | −41.20 | |
| **J7** (UM982EB): pins 1, 2 / 27, 28, pins run −X | 52.00 / 26.00 | −69.80 odd, −67.80 even | |
| **J3** (field header) centre, pins run along Y | 5.90 | −43.32 | pin 1 (4.63, −28.08), pin 2 (7.17, −28.08) |

### 2.1 Outline, holes and host connectors

| Feature | HAT x, y (mm) | IO board KiCad x, y | Notes |
|---|---|---|---|
| Standard outline | 0–65 × 0–56.5 | 79.0–144.0 × 104.0–160.5 | plus the upward extension, §2.3 |
| H1 | 3.5, 3.5 | 82.5, 107.5 | M2.5 (2.7 mm), header end |
| H2 | 61.5, 3.5 | 140.5, 107.5 | header end, beside the IO board's PoE pins |
| H3 | 3.5, 52.5 | 82.5, 156.5 | |
| H4 | 61.5, 52.5 | 140.5, 156.5 | beside the RJ45 |
| **J1 pin 1** (CM4 3.3 V) | **8.37, 4.82** | 87.37, 108.82 | mates with the IO board's 40-pin header (its J8); odd pins on the inner row |
| J1 pin 2 (5 V) | 8.37, 2.28 | 87.37, 106.28 | even pins on the edge row |
| J1 pins 39 / 40 | 56.63, 4.82 / 56.63, 2.28 | | 2.54 mm pitch along x |
| **J2 pin 1 `GLOBAL_EN`** | **18.04, 32.00** | 97.04, 136.00 | mates with the IO board's J1 pads; runs in **−x** from pin 1 |
| J2 pin 2 GND | 15.50, 32.00 | 94.50, 136.00 | |
| J2 pin 3 `RUN_PG` | 12.96, 32.00 | 91.96, 136.00 | |

- The hole pattern is confirmed as 58.0 × 49.0 mm.
- **J2 is not on J1's 2.54 mm grid** (9.67 mm, 27.18 mm from J1 pin 1). Place it by coordinates.
- **J1 and J2: keep the footprints on the TOP layer; fit the socket bodies on the underside.** The
  pads are through-hole, so a top-layer footprint puts every pin at the right XY. Flipping the
  footprint to the bottom in EasyEDA would **mirror** the pin order, and no rotation fixes that.
  Hand-solder the sockets from the top. Both are **8.5 mm tall** so they seat together.
- **Stacking:** 11 mm M2.5 standoffs. The IO board needs one change: **a 1 × 3 male header soldered
  into its J1 pads**.

### 2.2 IO board parts under the HAT

The only parts on the HAT's underside are J1 and J2. Keep the pins of through-hole parts on top
trimmed, and away from the PoE pins.

| IO board part | HAT x, y extent (mm) | Clearance with 11 mm standoffs |
|---|---|---|
| CR2032 holder | 3.4–18.6 × 6.6–30.4 | ≈ 5 mm tall: clears |
| J1 pads + fitted header | 11.7–19.3 × 30.7–33.3 | mates with J2 |
| HDMI0 / HDMI1 | centred at x 23.0 and 48.0, along the bottom edge (y ≈ 42–56) | receptacles clear; plugs may not, and AOW runs headless |
| PoE 2 × 2 pins | 60.2–62.8 × 8.4–10.9 | tips ≈ 2.5 mm below the HAT: no pin ends above them |
| CSI/DSI I²C jumpers | 41.4–43.9 × −3.3…−0.8 | under the upward extension; low enough |
| RJ45 magjack | from x 64.8, y 35.7 down | **right against the HAT's right edge** and taller than 11 mm: never extend right below y ≈ 35 |

### 2.3 Board size: standard HAT + ~16 mm upward

| Direction | Room | Used? |
|---|---|---|
| Left, down | none: IO board edges | — |
| **Up (−y)** | ≈ 20 mm, x 0–65 only (the CM4 is to the right of that) | **yes, ~16 mm for the GNSS board** (§3). The IO board's hole H7 lands at HAT (11.0, −21.0), a possible fifth standoff. |
| Right (+x) | up to ≈ 25 mm (measured), **y 0–35 only**, notched round the Ethernet/USB | **not needed.** Held in reserve; if used, keep it ≥ 10 mm short of the CM4 heatsink, and put light, low parts at its unsupported tip |

**Outline: 65 mm wide, y −16 … 56.5 (≈ 72.5 mm tall), 3 mm corner radii.** A Pi 3 HAT template from
the EasyEDA user library is a fine starting point, if you check it against §2.1.

---

## 3. GNSS boards: EMAX UM981 or Unicore UM982EB

**The HAT takes one of two boards, never both:** an **EMAX UM981** (42 × 42 mm, single antenna,
built-in IMU) or a **Unicore UM982EB** (46 × 71 mm, dual antenna for heading). These are the boards
the author owns. Other receivers are left for anyone who remixes the board.

Both:
- stack above the HAT on M3 standoffs
- run from 5 V
- talk on **UART5**: GPIO12 = `GPS_TX` → module RX1, GPIO13 = `GPS_RX` ← module TX1

Only power, GND and TX1/RX1 are connected. PPS, event, and the second and third UARTs go to
unconnected pads, because all 28 GPIOs are allocated (§7).

### 3.1 Placement (checked against the PCB export, 2026-09-24)

**Both boards sit in their native orientation:** as drawn by their library parts, which is the true
top view, with antennas toward the HAT's top edge. They share hole H5.

| Feature | Editor X, Y | HAT x, y | Used by |
|---|---|---|---|
| H5 | 22.00, −4.00 | 22.0, −12.0 | **both** |
| H6 | 56.30, −4.00 | 56.3, −12.0 | UM982EB |
| H7 | 22.00, −68.60 | 22.0, 52.6 | UM982EB |
| H8 | 56.30, −68.60 | 56.3, 52.6 | UM982EB |
| H9 | 22.00, −40.00 | 22.0, 24.0 | EMAX |
| H10 | 58.00, −40.00 | 58.0, 24.0 | EMAX |
| **J6** pin 1 → pin 8 (pins run +X) | 31.12 → 48.90, −41.20 | 31.12 → 48.90, 25.2 | EMAX |
| **J7** pin 1 / 2 (pins run −X) | 52.00, −69.80 / −67.80 | 52.0, 53.8 / 51.8 | UM982EB |
| **J7** pin 27 / 28 | 26.00, −69.80 / −67.80 | 26.0, 53.8 / 51.8 | UM982EB |
| UM982EB board | X 16.0–62.0, Y −0.8 … −71.8 | | |
| EMAX board | X 19.0–61.0, Y −1.0 … −43.0; antenna out past the top edge to Y ≈ +9 | | |

- All GNSS holes are **Ø 3.3** (M3), so one standoff length and size serves both boards. The
  EMAX's own holes are 3.1.
- **The EMAX sits on 3 standoffs** (H5, H9, H10), all on its square corners. Its odd, off-square
  hole (§3.2) isn't used.
- The UM982EB's J7 pad spacing matches both the library part and AgOpenGPS AiO v5.0i (a built
  board), which uses the same layout rotated by 90°.
- **Checks:**
  - J7's end pads clear H7 and H8 by ≥ 1.6 mm.
  - Nothing lands on J1's pin band (Y −17.4 … −21.7) or on J2.
- During layout the library outline parts ("UM982-NO-PADS", "UM981 EMAX") are only placement guides.
  **Delete them before fabrication:** they carry their own holes and pads (duplicate drills), and
  their refs clash with the netlist.

### 3.2 EMAX UM981: 42 × 42 mm

From the EasyEDA user part "UM981 EMAX" (contributor `mtz8302`, AgOpenGPS community). Own frame:
origin top-left, y down.

| Feature | x, y (mm) | Notes |
|---|---|---|
| Outline | 0–42 × 0–42 | |
| Mounting holes | (3.0, 3.0), (3.0, 39.0), (39.0, 39.0), **(39.0, 3.5)** | Ø 3.1. **One hole is 0.5 mm off the square: measure a real board.** |
| Header | 1 × 8, 2.54 mm, pin 1 at (12.12, 40.20), pin 8 at (29.90, 40.20) | 1.8 mm in from the bottom edge |
| Antenna | x 17.75–24.25, sticks out 10 mm above the top edge | single |
| Second connector (probably USB) | x −3.0…3.5, y 26.5–35.5, sticks out 3 mm left | identify it on the board |

| J6 pin | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 |
|---|---|---|---|---|---|---|---|---|
| Module | GND | **5V** | EV | TX2 | RX2 | PPS | **TX1** | **RX1** |
| HAT net | GND | `5V_MAIN` | — | — | — | — | `GPS_RX` | `GPS_TX` |

### 3.3 Unicore UM982EB: 46 × 71 mm

From the EasyEDA user part "UM982-NO-PADS" (contributor `wildbuckwheat`). Its pin numbers were
cross-checked against an independent UM982EB footprint (contributor `viluchinsk`), and all the
power, ground and UART pins agree. Own frame: origin top-left, y down.

| Feature | x, y (mm) | Notes |
|---|---|---|
| Outline | 0–46.0 × 0–71.0 | the other footprint says 45.4 × 71.0: **measure** |
| Mounting holes | (6.0, 3.2), (40.3, 3.2), (6.0, 67.8), (40.3, 67.8) | Ø 3.2, pattern 34.3 × 64.6 (other footprint: 34.0 × 64.75). **Measure.** |
| Header | 2 × 14, **2.0 mm**, x 10.0–36.0; odd pins on the outer row (y 69.0), even on the inner (y 67.0) | pin 1 at (36.0, 69.0) |
| Antennas | two, about (15.5, 4.4) and (32.5, 4.4) | ~15 mm of cable clearance past the top edge |

| J7 pin | Module | HAT net | | J7 pin | Module | HAT net |
|---|---|---|---|---|---|---|
| 1 | MOSI | — | | 2 | CSN | — |
| 3 | CLK | — | | 4 | SDRY | — |
| 5 | LNA_PWR | — | | **6** | **VIN** | `5V_MAIN` |
| 7 | MISO | — | | 8 | RX3 | — |
| 9 | RSTN | — | | 10 | RSV | — |
| 11 | EVENT | — | | 12 | RSV | — |
| 13 | TX3 | — | | **14** | **GND** | GND |
| **15** | **TX1** | `GPS_RX` | | **16** | **RX1** | `GPS_TX` |
| **17** | **GND** | GND | | 18 | TX2 | — |
| 19 | RX2 | — | | **20** | **GND** | GND |
| 21 | PVT_STAT | — | | **22** | **GND** | GND |
| 23 | PPS | — | | 24 | RSV | — |
| 25 | RTK_STAT | — | | 26 | ERR | — |
| 27 | SDA | — | | 28 | SCL | — |

**Socket heights differ.** The only 2 × 14 2.0 mm female socket JLC stocks is 4.3 mm tall; the
EMAX's 1 × 8 is 8.5 mm. So pick the standoff length for each board.

**Current (measured by the author):** the UM982EB draws **275 mA at start-up, 200 mA steady** at 5 V.
The UM981 draws less.

---

## 4. Power

**Direct power (decided 2026-09-25).** Vehicle constant 12 V goes **straight to the IO board's J20**,
not through the HAT. The HAT draws only a few hundred mA of 5 V, so running the IO board's 2 A
through it added parts for no benefit. Instead of cutting the IO board's power, the latch:
- **holds the CM4 off** through `GLOBAL_EN`, and
- **switches off the HAT's own 5 V loads.**

This works because the IO board draws only **~150 µA at 12 V with the CM4 off** (measured, §4.5).

### 4.1 Supplies

| Net | Source | When it's on | Feeds |
|---|---|---|---|
| — (12 V to the IO board) | vehicle **constant** 12 V → 5 A fuse → IO board J20 pin 1 | always | the IO board: its 5 V converter, and the PCIe 3.3 V converter |
| `5V_IO` | IO board 5 V, **header pins 2 and 4** | always, while the battery is connected | U1 and Q2 only |
| `3V3_AON` | U1 TPS7B6933 from `5V_IO` | always | the latch logic (U2, U3, pull-ups) |
| `5V_MAIN` | **Q2**, the HAT's 5 V load switch | key on, or CM4 running | CAN transceivers, ADC reference, GNSS, LEDs, piezo, WAS supply, U4 |
| `+3V3` | U4 RT9080-33 from `5V_MAIN` | with `5V_MAIN` | CAN controllers, X1, SP3232, watchdog, pull-ups |
| `VIN` | the **12 V sense wire**, J3 pin 1 (constant 12 V, ~0.1 mA) | always | `VIN_SENSE` and the low-voltage cut-off dividers only |
| `CM4_3V3` | the CM4's own 3.3 V output, header pins 1 and 17 | **CM4 running only**: drops to 0 V at halt | the hold circuit only (§4.4) |

### 4.2 Input protection

- **The IO board's 12 V input** has its own reverse-polarity protection (an ideal-diode P-FET) but
  **no surge clamp**, and it's rated 7.5–28 V. AgOpenGPS boards run with little input protection
  and no field problems, and tractor alternators have load-dump suppression. *Optional:* an inline TVS
  (SMBJ24A-class) across +12 V/GND at the J20 end of the lead.
- **On the HAT:**
  - TV1 (SMBJ24A) on the 12 V sense wire (J3.1).
  - TV2 (SMBJ24A) on the keyed input (J3.21).
  - Both feed only 100 kΩ dividers, so a reversed or over-voltage wire can't push real current anywhere.
- *Set aside:* an LT4363-2 surge stopper (C118131, ~$4–6).

### 4.3 Why a key/hold latch

The IO board runs from **constant 12 V**, and the HAT reads the **keyed 12 V** purely as a signal,
the same way an automotive head unit does. Key-off then becomes an explicit "shut down now"
message, and Linux has as long as it needs, because everything is still powered.

**The key is the only on/off control.** A shutdown started from the UI with the key on restarts the
unit about 2 s after the halt. So the UI should offer **Restart**, not "Shut down".

### 4.4 The latch circuit

Five blocks, all discrete, no firmware. **"On" = the key is on OR the CM4 is running.** On means Q2
supplies `5V_MAIN` and Q1 releases `GLOBAL_EN`. Off means `5V_MAIN` is dead and `GLOBAL_EN` is held
low (the CM4 stays off).

| Block | Parts | What it does |
|---|---|---|
| **A: key sense** | TV2; R3/R4/C5/D2 → `KEY_SENSE`; R5/R6/C6 → `KEY_DIV` | `KEY_SENSE` (100 k / 8.2 k, clamped to `+3V3` by D2) goes to ADC IN4, so Linux can read the key. `KEY_DIV` (100 k / 33 k: 12 V → 2.98 V) drives the FET gates: Q3, Q6 and Q7. The two dividers are separate because the ADC is unpowered while the HAT is off and would drag a shared node down. |
| **B: switch and hold-off** | Q2 AO3401A, R7, R8, C7; Q3, Q4; Q1 | Q3 (key) **or** Q4 (hold) pulls `ON_N` low. That turns Q2 on through R8 (10 k): R7 (100 k, gate→`5V_IO`) and R8 give V<sub>GS</sub> ≈ −4.5 V, and C7 (10 nF gate→drain) slows the turn-on so the GNSS and CAN capacitors don't pull the IO board's 5 V down. **Q1** (gate on `ON_N`) holds `GLOBAL_EN` low whenever `ON_N` is high (off), and releases it when `ON_N` goes low: the CM4 boots. |
| **C: hold** | R9 1 k, D4 1N4148W, C8 1 µF, R10 2.2 M → `HOLD_G` | `CM4_3V3` charges `HOLD_G` to ~2.6 V, which turns Q4 on. At halt `CM4_3V3` drops, and `HOLD_G` decays for **1.2–2.6 s** before Q4 lets go. That bridges reboot and watchdog dips. |
| **E: restart** | U2 74LVC1G14; C10 100 nF, R11 1 M; Q5, Q6 | Once `HOLD_G` has decayed, U2 drives `DEAD` high. The edge through C10 turns Q5 on for ~80 ms. **If the key is on** (Q6), that pulls `GLOBAL_EN` low, and **a halted CM4 restarts**. It covers a UI shutdown and a key-off → key-on during shutdown. |
| **D: low-voltage cut-off** | U3 TLV3011; R12/R13/C12 → `VLV`; R14; Q7, R15, C13 → `NKEY`; Q8, Q9 | If the key has been off for 6–13 s (`NKEY`, R15 10 M × C13 2.2 µF) **and** VIN is below **11.5 V** (R12 1 M / R13 121 k from the sense wire against the 1.242 V reference, filtered ~1.1 s by C12), Q8 + Q9 pull `HOLD_G` low. The latch goes off: the CM4 is forced off and the HAT's loads cut. This is what protects the battery if Linux ignores the key. It latches, because `CM4_3V3` has gone. |

**Sequence:**

| State | What happens |
|---|---|
| Battery connected, key off | the IO board's 5 V comes up; `ON_N` is high, so Q1 holds `GLOBAL_EN` low: **the CM4 stays off**, and `5V_MAIN` stays off |
| Key on | Q3 → `ON_N` low → Q2 on (`5V_MAIN`) and Q1 releases `GLOBAL_EN` → the CM4 boots → `CM4_3V3` → Q4 also on |
| Crank | the IO board is on the battery directly and **needs ≥ 7.5 V**: see the issues list (B4). If the keyed feed drops, Q4 holds the latch on. |
| Key off | Q4 keeps it on. Linux sees `KEY_SENSE` low for 3 s and runs `poweroff` |
| Halt | `CM4_3V3` → 0 V. `HOLD_G` decays for 1.2–2.6 s, then Q4 turns off → `ON_N` high → Q2 off and **Q1 holds `GLOBAL_EN` low**. The IO board sits at ~150 µA |
| Key on during shutdown, or UI shutdown with the key on | the latch stays on through Q3. After the halt, block E pulses `GLOBAL_EN` → the CM4 restarts |
| Key off, Linux doesn't shut down | U10 (watchdog) catches a hung kernel: it reboots, sees the key off and shuts down again. A running but key-ignoring Linux is caught by block D once the battery drops below 11.5 V |

### 4.5 Standby draw (key off, CM4 held off)

| Path | Current |
|---|---|
| IO board, CM4 off (**measured 2026-09-25**) | ~150 µA at 12 V |
| `VIN_SENSE` divider R16/R17 (sense wire) | 116 µA at 12 V |
| LV divider R12/R13 (sense wire) | 11 µA at 12 V |
| HAT on `5V_IO`: U1 ~15 µA, U3 + R14 ~6 µA, Q1 holding `GLOBAL_EN` against the CM4's 100 k pull-up ~50 µA, leakage ~5 µA | ~76 µA at 5 V ≈ 35 µA at 12 V |
| **Total** | **≈ 0.31 mA ≈ 7.5 mAh/day ≈ 2.7 Ah/year** |

Negligible against a tractor battery. **Check (issues H6):** whether an NVMe in the PCIe slot adds to
the IO board's 150 µA. The slot's 3.3 V converter runs from 12 V.

### 4.6 The 12 V lead (panel connector → IO board J20)

- **IO board J20 pinout** (from the IO board KiCad design): **pin 1 = +12 V, pins 2 and 3 = GND,
  pin 4 = +5 V.** ⚠ **That's the reverse of the usual floppy-cable colours** (red +5 V on pin 1,
  yellow +12 V on pin 4). **Don't use a stock floppy/Molex adapter by colour**: it would put 12 V on
  the 5 V pin.
- **Build the lead to the IO board's labels, and meter J20 first:** power the IO board from its
  barrel jack, and the pin reading 12 V is pin 1.
- A Berg housing (TE 171822-4) on J20 pin 1 and **both** GND pins 2 and 3; J20's +5 V pin stays
  unconnected. The other end goes to the panel connector's power pair. **18 AWG, ≥ 2 A.** Fuse:
  **5 A** at the battery.
- **Don't connect the barrel jack and J20 at the same time.** They share the IO board's 12 V bus.

### 4.7 Current budget

**5 V (IO board converter, rated 3 A):**

| Load | Worst case | Typical | Basis |
|---|---|---|---|
| CM4 | 1,800 mA | ~1,100 mA | Raspberry Pi's 9 W budget for the CM4 |
| GNSS: UM982EB | 275 mA | 200 mA | measured |
| 3 × CAN transceivers (U5–U7) | ~210 mA | ~75 mA | ~70 mA each when dominant: **check the MCP251863 datasheet** |
| U4 → +3V3 loads | ~90 mA | ~70 mA | linear, so the 5 V current equals the 3.3 V current |
| 4 × SK6812 (D19–D22) | 240 mA | ~20 mA | ~60 mA each at full white |
| WAS sensor, piezo, ADC | ~50 mA | ~25 mA | |
| IO board's own (USB hub, LEDs) | ~100 mA | ~100 mA | estimate |
| **Total** | **≈ 2.77 A (92%)** | **≈ 1.6 A (53%)** | |

- **Cap the LED brightness in software (≤ 25%).** That saves ~180 mA: they're status lights.
- **No power-hungry USB devices** on the IO board. A keyboard or flash drive for service is fine.
- **Q2 carries the HAT's share**, ≤ ~0.9 A: about 45 mV across the AO3401A.

**12 V:** the 5 V side draws ≈ 1.3 A worst case (≈ 88% converter efficiency), plus NVMe up to
≈ 0.75 A, giving **≈ 2 A peak, ≈ 0.8 A typical** in the 12 V lead to J20. Harness fuse: **5 A**.

---

## 5. Restart and watchdog: the J2 socket

The 40-pin header doesn't carry `GLOBAL_EN` or `RUN_PG`. The IO board brings both out on its **J1**,
three unpopulated pads under the HAT: 1 = `GLOBAL_EN`, 2 = GND, 3 = `RUN_PG`.

- **IO board:** solder a 1 × 3 male header into J1.
- **HAT:** J2, a 1 × 3 female socket at the §2.1 coordinates (footprint on the top layer, body fitted underneath).
- **J2 pin 1 `GLOBAL_EN`** ← Q1 (held low while the latch is off, keeping the CM4 off) and Q5 (the
  block E restart pulse). Releasing it, or pulling it low for > 1 ms and then releasing it, starts a
  halted or held-off CM4.
- **J2 pin 3 `RUN_PG`** ← R38 330 Ω ← `RUN_PG_G`, the node shared by U10's WDO and SW1. Resetting
  through 330 Ω follows the CM4 datasheet's advice not to pull `RUN_PG` hard to ground.
- **Watchdog U10** STWD100NYWY3F: 1.6 s timeout (range 1.12–2.24 s), open-drain.
  - WDI ← `WDT_WDI` (GPIO22).
  - EN ← `WDT_EN` (GPIO3; low = watchdog on). R37 4.7 k plus GPIO3's own pull-up keep it **off
    through boot**, until Linux enables it.
  - On timeout it pulls `RUN_PG` low for ~210 ms.
  - GPIO3 is also I²C1 SCL, so `dtparam=i2c_arm` must stay off.
- **SW1** reset button: `RUN_PG_G` to GND, through the same R38. It's optional, since the HAT covers
  the IO board anyway.

---

## 6. Peripherals

- **CAN FD × 3:** U5/U6/U7 MCP251863 (controller + transceiver) on SPI0. Linux driver `mcp251xfd`,
  giving SocketCAN `can0..2`.
  - CS: `NCS_CAN1` = GPIO8 (CE0), `NCS_CAN2` = GPIO7 (CE1), `NCS_CAN3` = GPIO25 (a GPIO CS, with
    R18 pull-up).
  - Interrupts: GPIO16/17/27.
  - **STBY (pin 5) goes to GND.** The pin is pulled up internally, and normal mode needs it low.
  - One 40 MHz oscillator (X1) is star-fed through 33 Ω (R20–R22) to each OSC1.
  - Each bus gets a NUP2105L TVS (D6–D8) and a **120 Ω termination in series with a solder jumper**
    (R23–R25 + SJ1–SJ3): close it at a bus end.
  - VIO/VDD on `+3V3`, transceiver VCC on `5V_MAIN`.
- **ADC:** U8 ADC128S102, 8 channels, 12-bit, on SPI0, with CS on GPIO26 (R19 pull-up).
  - **VA = `5V_MAIN` is also the reference**, and the same rail powers the WAS sensor (J3.3), so the
    reading is ratiometric.
  - Inputs:
    - IN0: WAS, through R26/C32
    - IN1: current sense, through R27/C33
    - IN2: `VIN_SENSE` (telemetry and low-battery warnings only; never shut down on it)
    - IN4: `KEY_SENSE`
    - IN3 and IN5–IN7: grounded
- **RS-232 × 2:** U9 SP3232EEN at 3.3 V. UART2 (GPIO0/1) and UART3 (GPIO4/5). SMAJ12CA on each
  line (D9–D12). Non-isolated.
- **Switch inputs** (work, engage, remote): **contact-to-ground only.**
  - Each input: SMAJ16A at the line, 1 k in series, 10 k pull-up to `+3V3`, 100 nF, and an SRV05-4
    (D18).
  - **Don't apply 12 V-level signals.** They'd clamp at ~4 V, above the CM4's GPIO maximum. Say so
    in the harness notes.
- **Steering outputs:** PWM (GPIO18, hardware PWM0), DIR (GPIO23) and EN (GPIO24; low at boot means
  steering off). Direct 3.3 V logic through 330 Ω (R34–R36), for MD13S / IBT-2 style drivers.
- **Status LEDs:** 4 × SK6812SIDE-A (D19–D22) on `5V_MAIN`.
  - Data comes from **GPIO21**, whose PCM function gives the WS2812-class timing.
  - U11 SN74AHCT1G125 shifts it from 3.3 V to 5 V, through R39 330 Ω.
- **Piezo:** BZ1 driven by Q10 2N7002 from GPIO6 (software PWM). R40 100 Ω gate, R41 10 k
  pull-down, R42 470 Ω across the piezo.
- **No power LED:** the CM4's `PI_LED_nPWR` isn't on the header, and the IO board has its own.

---

## 7. 40-pin header allocation (J1)

**All 28 GPIOs are used.** Four PL011 UARTs are in use: 0 console, 2 RS-232 #1, 3 RS-232 #2 and
5 GNSS. UART4 isn't available, because its pins (GPIO8/9) belong to SPI0.

| Pin | GPIO | Net | | Pin | GPIO | Net |
|---|---|---|---|---|---|---|
| 1 | — | `CM4_3V3` | | 2 | — | `5V_MAIN` |
| 3 | 2 | `SW_REMOTE` | | 4 | — | `5V_MAIN` |
| 5 | 3 | `WDT_EN` | | 6 | — | GND |
| 7 | 4 | `RS232_2_TX` | | 8 | 14 | `CON_TX` |
| 9 | — | GND | | 10 | 15 | `CON_RX` |
| 11 | 17 | `CAN2_INT` | | 12 | 18 | `PWM_MOTA` |
| 13 | 27 | `CAN3_INT` | | 14 | — | GND |
| 15 | 22 | `WDT_WDI` | | 16 | 23 | `MOT_DIR` |
| 17 | — | `CM4_3V3` | | 18 | 24 | `STEER_EN` |
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

**Linux / boot configuration:**
- **Bootloader EEPROM: `POWER_OFF_ON_HALT=1` and `WAKE_ON_GPIO=0`.** The first only works with the
  second. Without them `CM4_3V3` stays up at halt, and the latch never releases.
  - Write them with `rpiboot` (IO board J2 boot jumper + micro-USB).
  - `rpi-eeprom-config` from Linux is off by default on the CM4. Enabling it needs a `[cm4]` block in
    `config.txt`, and on the bench that lost the saved Wi-Fi profile.
- `force_eeprom_read=0`: GPIO0/1 are the HAT ID-EEPROM pins, used here for RS-232 #1, so this isn't
  a spec-compliant HAT and has no ID EEPROM.
- Keep `dtparam=i2c_arm` off (GPIO2/3).
- **Key monitor:** a small systemd service, separate from the app so an app crash can't block it.
  - It reads ADC IN4. `KEY_SENSE` = 0.0758 × V<sub>key</sub>: on above ~8 V (0.61 V), off below
    ~4 V (0.30 V).
  - Key off for 3 s → `systemctl poweroff`.
  - Booting with the key already off (e.g. after a watchdog reset) just shuts down again.
- **Single-feed detection:** if `VIN_SENSE` collapses together with `KEY_SENSE`, both wires are on
  switched 12 V. Log it and warn at the next boot.

---

## 8. Field wiring

### 8.1 J3: 26-pin shrouded IDC header → ribbon → panel Deutsch connector

| Pin | Net | | Pin | Net |
|---|---|---|---|---|
| 1 | `VIN`: 12 V sense wire (constant, fused 1 A) | | 2 | GND |
| 3 | `5V_MAIN` (WAS supply) | | 4 | `WAS_IN` |
| 5 | GND | | 6 | `ISENSE_IN` |
| 7 | `SW_WORK_IN` | | 8 | `SW_ENGAGE_IN` |
| 9 | `SW_REMOTE_IN` | | 10 | `STEER_PWM` |
| 11 | `STEER_DIR` | | 12 | `STEER_EN_OUT` |
| 13 | GND | | 14 | `CAN1_L` |
| 15 | `CAN1_H` | | 16 | `CAN2_L` |
| 17 | `CAN2_H` | | 18 | `CAN3_L` |
| 19 | `CAN3_H` | | 20 | GND |
| 21 | `KEY_IN` (keyed 12 V) | | 22 | NC |
| 23 | `RS232_1_TXD` | | 24 | `RS232_1_RXD` |
| 25 | `RS232_2_TXD` | | 26 | `RS232_2_RXD` |

- Odd pins on one row, even on the other, like any 2 × 13 header. IDC pin n = ribbon conductor n, so each CAN H/L pair (14/15, 16/17, 18/19) sits on neighbouring conductors. Keep the
  ribbon short.
- All protection sits on the HAT where the ribbon lands.
- **J3 is a shrouded, keyed header without latches** (C75755, 40.64 × 9.12 mm). The latched versions
  are 52.5 mm long and don't fit anywhere outside the GNSS boards. Against vibration, clamp the ribbon
  with a strain-relief clip or tie it to the enclosure close to J3, and use strain relief at the
  panel end too.
- **Placement: the left strip** (X 0–16, outside both GNSS boards).
  - Centre at **X 5.90, Y −43.32**, pins running along Y.
  - Pin 1 at (4.63, −28.08), pin 2 at (7.17, −28.08); pins 25/26 at Y −58.56.
  - The body spans X 1.3–10.4 and Y −23.0 to −63.6. That clears J1's pins, the H1/H3 standoff nuts
    (~2 mm), J2's pads (1.6 mm) and the IO board's coin-cell holder underneath (~3 mm to the pin
    tails).
  - The ribbon can leave past the IO board's left edge.

### 8.2 Power wiring

- **12 V power pair:** panel connector → **IO board J20** (§4.6), 18 AWG, 5 A fuse at the battery.
  It doesn't go through the HAT or the ribbon.
- **12 V sense wire:** constant 12 V → **J3 pin 1**, fused 1 A. It can branch off the power feed after
  the 5 A fuse. It carries ~0.1 mA, so a ribbon conductor is fine.
- **Keyed 12 V:** IGN/RUN → **J3 pin 21**, fused 1 A.

### 8.3 Install

- **Two-wire (normal):**
  - Constant 12 V → IO board J20 (5 A fuse), and the same feed → J3.1 (sense, 1 A fuse).
  - J3.21 → **IGN/RUN**, not ACC (ACC drops out for the whole crank), fused (1 A is plenty; the load
    is ~0.2 mA).
- **Single-wire fallback:** feed J20, J3.1 and J3.21 all from the one switched feed. The board then powers with the
  key and cuts abruptly at key-off: the read-only root survives, in-flight data doesn't.
- **With J3.21 left open, the board never turns on.** Put that on the silkscreen by J3 and in the
  harness doc.

---

## 9. Decisions (2026-09-23)

| Decision | Choice | Why |
|---|---|---|
| Prototype form | HAT on the CM4 IO Board | simpler to lay out and build; sealing is at the panel connector anyway |
| Key-off shutdown | always-on + keyed 12 V latch, all discrete | no firmware; the key is the only on/off control |
| Watchdog | discrete STWD100 (1.6 s) | independent of everything else |
| Power path | **12 V straight to the IO board's J20** (2026-09-25); the HAT switches only its own 5 V and holds the CM4 off via `GLOBAL_EN` | the IO board draws ~150 µA with the CM4 off, so switching its 12 V gained nothing |
| Input protection | the IO board's own reverse-polarity FET; SMBJ24A on the HAT's sense inputs; optional inline TVS at J20 | field-proven on AgOpenGPS boards |
| Restart / watchdog path | J2 socket onto the IO board's J1 pads | keeps both circuits simple; both tested through these pads |
| GNSS | EMAX UM981 or UM982EB only | the boards on hand; remix for others |
| Board size | standard HAT + ~16 mm upward | fits the GNSS footprint; the right-hand extension is held in reserve |
