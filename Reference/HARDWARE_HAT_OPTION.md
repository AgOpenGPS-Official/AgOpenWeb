# AgOpenWeb HAT Option — CM4 IO Board + AOW HAT

> **Status: idea, 2026-09-23. Nothing decided; the AiO board (`HARDWARE_AIO_BOARD.md`) is still the
> plan of record.** This records an alternative that came up during the S8c bench tests: keep the
> official Raspberry Pi **CM4 IO Board** as the host and move the AOW-specific circuits onto a
> **40-pin HAT**.
>
> Sources: CM4 IO Board datasheet (`cm4io-datasheet.pdf`, §2.2, §2.12, §2.14 and the PSU/GPIO
> schematic sheets), a photo of the author's IO board, and the AiO netlist/issue docs. Anything taken
> from the photo rather than a measurement is marked **(photo — measure)**.

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

**What the HAT doesn't win:** a sealed tractor enclosure. IO board + HAT is a 160 × 90 mm stack with
connectors on every edge, against the AiO board's single front-panel ATS13. If AOW is to be a sealed
unit, the AiO board stays the better end product. The HAT could also serve as a **stepping stone**:
its peripheral circuits are the AiO board's, so a HAT build proves CAN, the ADC, RS-232 and the key
latch before committing to the DF40 board.

## 2. Hosts

| Host | GPIO allocation | Power into the host | Notes |
|---|---|---|---|
| **CM4 IO Board** (primary) | identical to the AiO board (same CM4) | switched 12 V into **J20** (§4) | tested: S8c.8 checks 1–3 were run on this board |
| Pi 4B | identical (same BCM2711) | 5 V into header pins 2/4 | would need a 5 V converter on the HAT (DNP on the IO board build) |
| Pi 5 | **recheck** — RP1 maps UARTs, PCM and PWM differently | 5 V / 5 A into the header | U1-class 5 A converter at its limit; GPS on UART5 and the LED drive need rework |

**Recommendation if pursued: target the IO board only**, keeping the standard HAT footprint so a
Pi 4 variant stays possible later.

## 3. Mechanical

### 3.1 Outline and holes

- **Standard HAT: 65 × 56.5 mm, 4 × M2.5 holes on a 58 × 49 mm pattern, 3.5 mm from the edges**, the
  40-pin header along one long edge. The IO board provides these holes "so that standard HATs may
  be used" (datasheet §2.12).
- The four HAT holes on the IO board **(photo — measure)**: one at each end of the 40-pin header, plus
  the top-left corner hole and the left-hand hole level with the bottom of the header (next to the
  Ethernet jack). The hole just inside the top-left corner, and the one near CAM1, look like the IO
  board's own mounting holes. **Check:** the header-end pair 58 mm apart; each 49 mm from its
  partner across the board.
- Standard stacking: 11 mm M2.5 standoffs with a 2 × 20 female header on the HAT.

### 3.2 What sits under the HAT **(photo — measure heights)**

| Item | Where | Constraint |
|---|---|---|
| CR2032 holder | directly under | ~5 mm; clears 11 mm standoffs — keep tall HAT bottom-side parts off it |
| HDMI0 / HDMI1 | under the left edge | receptacles clear; **plug overmoulds may hit the HAT** — acceptable, AOW runs headless |
| PoE 2 × 2 header | under the corner at the bottom of the 40-pin header | as tall as the 40-pin header: bottom-side keepout or a notch |
| **J1 pads — `GLOBAL_EN` / GND / `RUN_PG`** | under the HAT, beside the battery holder | an opportunity, not a problem: §5 |

### 3.3 Growing past 65 × 56.5 mm

- **Left, over the HDMI connectors to the board edge:** fine with 11 mm standoffs. The likely
  direction if the GPS module doesn't fit.
- **Down, over the CM4:** only if it clears the heatsink — and it would trap the CM4's heat. Avoid.
- **Right, past the header:** blocked by the J6 jumpers and J2's sideways pins.

A wider board gives up Pi 4 compatibility (overhang meets the Pi's USB/Ethernet end).

## 4. Power path

The IO board takes **+12 V on J19 (barrel) or J20** — a 4-pin "Berg" floppy-style connector
(mating part TE 171822-4) carrying +5 V, +12 V and GND. Its on-board converter makes 5 V at 3 A for
the CM4; the 12 V bus goes straight to the PCIe slot and fan connector.

### 4.1 Chosen direction: the HAT switches protected 12 V into J20

```
vehicle constant 12 V ─ reverse-polarity + surge limit ─ P-FET high-side switch ─ cable ─ IO board J20 +12 V
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

- **Input voltage: the IO board is rated 7.5–28 V** with PCIe and fan unused (datasheet §2.2). The
  AiO front end's SMBJ24A clamps at ~39 V — **too high**. The HAT needs a tighter limit in front of
  J20: a surge stopper (LTC4380-class) or a lower TVS plus a series FET. **This is the main new
  design item.**
- **5 V budget: 3 A** from the IO board's converter for the CM4 (Raspberry Pi budget 9 W) **plus**
  the HAT: 3 × CAN (MCP251863), the GPS module, 4 × SK6812, the WAS sensor supply, U3's 3.3 V loads,
  and anything on the IO board's USB ports. Probably fine; add it up before committing.
- **Standby draw** (S8c.7 budget ≈ 180 µA) no longer includes U1's `EN` divider, but the surge
  limiter's quiescent current joins it.

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
| switch inputs, steering outputs, SK6812 chain, piezo | J1 field connector: likely a board-mount header + harness | H3 USB/rpiboot header (IO board has micro-USB) |
| U3 3.3 V LDO, U20 watchdog, SW1 reset | | |

## 8. Open questions

1. **Product or platform?** A sealed tractor unit favours the AiO board; a community build on
   off-the-shelf hardware, or a low-risk first build, favours the HAT.
2. **Measure** the HAT hole positions and the heights of the parts under it (§3).
3. **Does the GPS module fit** on 65 × 56.5 mm alongside everything else, or does the board grow
   left (§3.3)?
4. **Surge limiting to ≤ 28 V** — pick the part (§4.3).
5. **J20 pinout and cable** — verify pins; choose the HAT-side connector.
6. **5 V budget** on the IO board's 3 A converter (§4.3).
7. **J1 socket vs power-cycle** for restart and watchdog (§5).
