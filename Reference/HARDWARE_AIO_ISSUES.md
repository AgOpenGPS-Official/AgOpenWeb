# AgOpenWeb AiO Board — Issue lists

> **The single place for open design issues**, one list for the schematic and one for the PCB layout.
> Other docs refer to these IDs (`S#`, `P#`) instead of repeating the details.
>
> Sources reviewed (2026-09-14): `Netlists From EasyEDA/Full-board_2026-09-14.net` and
> `PCB From EasyEDA/PCB_PCB_AOW-v2.0_2026-09-14.json`, checked against the CM4 datasheet (Release 4),
> MCP251863 DS20006624B, ADC128S102 SNAS298G and STWD100 DocID14134. Every schematic item below was
> re-verified pin-by-pin against the netlist.
>
> **Old IDs:** earlier revisions of `HARDWARE_AIO_BOARD.md` used F1–F12 (schematic) and L1–L8
> (layout); the "Was" column maps them. PCB coordinates are mm from the board's top-left corner.
>
> Status: **open**, **decide** (a choice for the designer rather than a defect), **fixed**, **retracted**.

---

## Schematic issues

### Must fix

| ID | Was | Issue | Evidence | Fix | Status |
|---|---|---|---|---|---|
| **S1** | F2 | **Q1 reverse-polarity P-FET is reversed.** | Q1.3 (source) on `VIN`, Q1.2 (drain, tab) on `VIN_PROT`. A P-FET's body diode runs drain → source, so with the battery reversed it conducts from `VIN_PROT` (held near 0 V by TV1's forward diode) into `VIN`. Nothing blocks reverse polarity. Present since the July design; confirmed on the PCB (tab pad on `VIN_PROT`). | Drain (pad 2, tab) → `VIN`, source (pad 3) → `VIN_PROT`. Layout rework: P7. | **fixed 2026-09-15** — confirm in the next export |
| **S2** | F3 | **Q1 gate-source voltage not clamped.** | D2 (12 V zener) is gate → GND. R40 100 k (VIN → gate) + R47 10 k (gate → GND) give Vgs ≈ −0.91 × VIN: −21.8 V at a 24 V jump start, −35 V at the 39 V TVS clamp. IRFR5305 V<sub>GS</sub> max ±20 V. | D2 cathode → Q1 source (`VIN_PROT` after S1), anode → `PGATE`. R40 top → `VIN_PROT`. R47 stays gate → GND. Layout rework: P7. | **fixed 2026-09-15** — confirm in the next export |

### Should fix

| ID | Was | Issue | Evidence | Fix | Status |
|---|---|---|---|---|---|
| **S3** | F4 | Watchdog timeout 102 ms is tight for Linux. | U20 = STWD100**NX**WY3F: t<sub>WD</sub> 102 ms (71–142 ms). "NY" = 1.6 s (1.12–2.24 s). | Fit STWD100NYWY3F (same footprint, open-drain), or commit to kernel-level kicking. | **fixed 2026-09-15** — confirm the fitted part in the next BOM export |
| **S4** | F5 | SK6812 LED data on GPIO2, which has no PWM, PCM or SPI function. | WS2812-class timing from Linux uses PWM, PCM (GPIO21) or SPI0 MOSI (GPIO10). GPIO18's PWM0 is already the steering output. | Swap `LED_DATA` ↔ `SW_REMOTE` (CM4 pins 58 ↔ 25, GPIO2 ↔ GPIO21) to use PCM. | **fixed 2026-09-15** — confirm the pin in the next export |
| **S5** | F7 | J1 CAN pin order changed from July. | July: 14/16/18 = H, 15/17/19 = L. Now: 14/16/18 = L, 15/17/19 = H. | **Closed 2026-09-15: no harness built yet**, so there's nothing to reconcile. The J1 pinout in `HARDWARE_AIO_NETLIST.md` §5.1 (L on even 14/16/18, H on odd 15/17/19) is the reference for the harness when it is made. | closed |
| **S6** | F8 | No CAN termination footprints. | Each CAN net only touches its MCP251863, the NUP2105L and J1. July's split-termination and CMC footprints are gone. | **Fixed 2026-09-15:** a single 120 Ω across CANH/CANL per channel with a solder jumper, matching the other AiO boards. (Split termination + common-mode choke were the July design; the simpler jumper is the established AgOpenGPS approach.) | **fixed** — confirm in the next export |
| **S7** | F9 | GPS modules share one UART. | U16.15/16 and P2.11/12 both on `GPS_RX`/`GPS_TX` (CM4 UART5). | **Closed 2026-09-15: only one GPS module physically fits**, so the shared UART can never be contended. No change needed. | closed |
| **S8** | F10 | **Brown-out halt: the board can't restart itself, and the hold-up caps can't cover a shutdown anyway.** | (a) C6+C7 = 940 µF on `VIN_PROT` gives only **13–19 ms** from 12–13.8 V to the buck's ~6 V cutoff at 3–5 W — a `poweroff` takes seconds, so VIN loss is an abrupt cut, not a graceful halt. (The July note claiming the hold-up covers the halt was wrong.) (b) After any software halt, the CM4 datasheet needs `GLOBAL_EN` (pin 99, unconnected) pulled low >1 ms or 5 V removed; the eFuse is tied permanently on (R43), so neither happens. | **DECIDED 2026-09-15: don't halt on VIN dips** (they're inevitable in a vehicle). Use `VIN_SENSE` for telemetry/warnings only, never `poweroff`; keep root read-only + overlay. The buck regulates down to ~5.5–6 V in, so cranking dips ride through, and an outright power loss simply reboots when VIN returns — no extra parts, and (b) becomes moot. **Only if a graceful shutdown is really wanted:** add supercap hold-up on `VIN_PROT` (≈ 0.07 F for 1 s, ≈ 0.22 F for 3 s at 4 W, 12 → 6 V — 100–250× the present 940 µF, plus a blocking diode and inrush limiting) **and** an auto power-cycle one-shot: watch `RUN_PG` (high while running) and pulse `GLOBAL_EN` low via an open-drain FET after it has been low ~2 s with VIN present (555 + Schmitt, or an ATtiny10 which can also hold off while the rpiboot jumper is fitted). The watchdog covers a *hung* kernel; this covers a *halted* one. Fault flag (`PI_FLT`) → **ADC IN3** (U21 pin 7, one of 5 spare grounded inputs) — no GPIO needed, and all 28 are allocated. | **decided** — no board change |
| **S9** | F11 | Power LED on an unbuffered CM4 pin. | CM4 datasheet: `PI_LED_nPWR` (pin 95) "needs to be buffered". D23 is driven directly (~1.3 mA via R79). | Add a 2N7002/BSS138 buffer, or drop the LED. Adds a SOT-23. | open |
| **S10** | F12 | `RUN_PG` driven hard to GND. | CM4 datasheet: drive low "via a 220 Ω resistor". SW1 and U20 WDO connect straight to pin 92. | 220 Ω between CM4 pin 92 and the SW1/WDO node. Adds an 0402. | open |
| **S11** | F6 | Switch inputs only handle switches to ground. | 12 V on J1.7–9 → 1 k → SRV05-4 clamp at ~+3V3 + V<sub>F</sub> ≈ 4 V, above the CM4 GPIO max of 3.8 V, pushing ~8–10 mA into +3V3. | If 12 V-level inputs are needed, add a divider (e.g. 10 k : 2.2 k). Otherwise document contact-to-ground only. | decide |

### Parts / BOM data

| ID | Was | Issue | Evidence | Fix | Status |
|---|---|---|---|---|---|
| **S12** | L8 | U19's attached part is the CM4 module, not the receptacles. | EasyEDA BOM: U19 = CM4101000 (C20754863). The DF40 receptacles aren't in the netlist or BOM. | Exclude U19 from JLC assembly; add 2× DF40C-100DS-0.4V(51) (C597931). Pad check: P9. | open |

### Retracted

| ID | Was | Claim | Why retracted |
|---|---|---|---|
| — | F1 | "MCP251863 STBY (pin 5) floating → transceivers stuck in standby." | **Wrong.** Pin 5 on U7, U8 and U9 is on `GND` in the netlist, next to VSS (22) and GND (24), so the transceivers are in normal mode. The original review misread the pin dump. |

### Verified OK (earlier fixes still present)

- **MCP251863 grounds:** VSS (22), GND (24) and STBY (5) on `GND` for all three; VIO/VDD on +3V3, VCC on 5V_MAIN.
- **`GPIO_VREF`** (CM4 pin 78) tied to `CM4_3V3` (84/86) with C76 — July fix C1.
- **CM4 GND pin 107** on `GND` — July commit cf6be0fb.
- **All 62 capacitors have both pins connected** — the July "5 floating cap grounds" fix holds.
- **`USB_OTG_ID`** (pin 101) intentionally floating (device mode, needed for rpiboot). The July "ground it" fix only applied to the removed STM32 host link.

### Notes (no change needed)

- `GLOBAL_EN` (99), `nEXTRST` (100), `WL_nDISABLE` (89), `BT_nDISABLE` (91) are single-pin nets. All fine floating per the datasheet; expect DRC warnings.
- CS pull-ups (R74, R75) only on GPIO25/26, which default pull-low; CE0/CE1 (GPIO8/7) default high. Consistent.
- ADC128S102 needs SCLK 8–16 MHz for rated accuracy; give it its own SPI speed.
- Piezo on GPIO6 has no hardware PWM, so tones are software PWM.
- **All 28 CM4 GPIOs are allocated.** Spare capacity elsewhere: **ADC IN3–IN7** (U21 pins 7–11, tied
  to GND today) and **J1 pins 21–22** (free since RS-485 was dropped). To free an actual GPIO:
  console RX (GPIO15, keeps TX for boot messages) or the piezo (GPIO6) each free one; wire-ORing the
  three CAN `nINT` lines onto one pull-up frees two (MCP251863 INTOD gives open-drain outputs), but
  check first that the `mcp251xfd` driver handles a shared interrupt and sets INTOD.

---

## PCB layout issues

State of the board at review: 184 × 119 mm, 4-layer; 35/151 parts placed; `VIN`, `VIN_PROT`, 5 V buck,
Ethernet ×8 and PCIe ×6 routed on L1; Inner1 solid GND plane; Inner2 and bottom empty; **3 vias**.
PCB pad nets match the netlist (0 differences).

### Must fix

| ID | Was | Issue | Measured | Fix | Status |
|---|---|---|---|---|---|
| **P1** | L1 | **Input TVS on a thin trace.** | TV1 pad 1 reaches `VIN_PROT` only through a 0.254 mm × 4.8 mm trace to C6. SMBJ24A clamps ~15 A peak. | TV1 pad 1 straight onto the wide `VIN_PROT` copper right after Q1; pad 2 to GND with several vias. | open |
| **P2** | L2 | **No thermal vias under U1, almost no GND stitching.** | U1 pad 9 (2.0 × 2.0 mm, GND) has no vias. The whole board has 3 vias (near D2/R47), so every SMD GND pad (CM4 DF40, M.2, buck caps, D1) reaches the Inner1 plane only through the top pour. | Thermal-via array under U1 (TI land pattern). GND stitching vias at CM4 and M.2 GND pins beside the PCIe/Ethernet pairs, at each bypass cap, and along pour edges. | open |
| **P3** | L4 | **Ethernet pairs likely ≈ 66 Ω differential instead of 100 Ω.** | `ETH` rule width 0.377 mm with 0.10 mm gap (necking to 0.2/0.15 mm at pins). IPC-2141 estimate on JLC 1.6 mm 4-layer 7628 (L1–L2 ≈ 0.21 mm, εr ≈ 4.4): ≈ 48 Ω single-ended / ≈ 66 Ω differential. 0.377 mm looks like a 50 Ω single-ended width. PCIe (0.226/0.103 mm) estimates ≈ 89 Ω, which is OK. | Confirm with JLC's impedance calculator for the ordered stackup; set the `ETH` rule to the 100 Ω differential width/gap; re-route (all on L1, no vias). | open |
| **P7** | F2/F3 rework | **Q1 area rework for S1 + S2.** | Q1 tab (pad 2) faces J1 with a 2.5 mm trace toward C6/U1; pad 3 has a 2.0 mm trace to J1. D2/R40/R47 sit just below Q1 pad 1; a 30 mm × 0.25 mm `VIN` trace runs from J1.1 around to R40. | No rotation needed: swap nets, route J1.1 → tab, pad 3 → existing `VIN_PROT` trace. Flip D2 onto source/gate; R40 top → `VIN_PROT`, which removes the long thin `VIN` trace. | open |

### Should fix

| ID | Was | Issue | Measured | Fix | Status |
|---|---|---|---|---|---|
| **P4** | L3 | Buck switching loop is large. | Loop C1/C2 → U1 VIN → SW → D1 → back. C1/C2 GND pads at (4.9, 48–51), D1 anode at (9.4, 34.5): ≈ 15 mm apart through the pour. U1 VIN ≈ 5.4 mm from C1/C2. | Place D1's anode and C1/C2's GND pads together next to U1 pin 7 / pad 9; keep SW node copper small. | open |
| **P5** | L5 | Ethernet intra-pair skew. | P−N: ETH0 0.27 mm, **ETH1 8.45 mm**, ETH2 1.34 mm, ETH3 2.11 mm (target ≤ 0.13 mm). ETH1's magjack pins (P4, P7) are 4.6 mm apart. PCIe OK: TX 0.05, RX 0.00, REFCLK 0.08 mm. | Length-tune the short leg near the magjack (ETH1_P, ETH2_N, ETH3_N, ETH0_N) — after P3. | open |
| **P8** | L7 | L2 inductor footprint doesn't match the part. | Footprint `MDA1054HT` (11 × 10 mm); part Sunlord MWSA1004S-6R8MT (10 × 10 mm, C408485). | Check pads against the MWSA1004S land pattern or swap footprints. | open |
| **P9** | — | DF40 receptacle pads on the U19 footprint unverified. | U19 footprint `COMM-SMD_L55.0-W40.0_CM4101000` (see S12). | Check pad positions against the CM4 datasheet mechanical drawing and DF40C-100DS land pattern. | open |

### Minor

| ID | Was | Issue | Measured | Fix | Status |
|---|---|---|---|---|---|
| **P6** | L6 | ETH1 pair gap below the design rule at the magjack escape. | 0.093 mm at (20.8, 8.7) vs 0.10 mm DRC clearance. | Nudge apart; will change with P3. | open |

### Layout impact of schematic issues

| Schematic ID | Touches routed copper? | What changes on the PCB |
|---|---|---|
| S1, S2 | **yes** | P7 |
| S3 | no | BOM swap, same footprint |
| S4 | no | net swap at CM4 pins 58 ↔ 25 (both unrouted) |
| S6 | no | adds DNP footprints in the CAN block (not placed yet) |
| S9 | no | adds a SOT-23 near D23 |
| S10 | near CM4 | adds an 0402 at CM4 pin 92 |
| S5, S7, S8, S11 | no | CAN/GPS/field I/O not placed yet |

### Not yet routed (progress, not issues)

- **Partial:** `5V_MAIN` — buck output done; J1.3 and LEDs not.
- **Parts placed, nets unrouted:** `5V_CM` (U2 unplaced), `+3V3_NVME` (U4 unplaced), `GPIO_VREF`/`CM4_3V3`
  (78–84–86), `CM4_1V8`, `PCIE_NRST`, `PCIE_CLKREQ`, `RUN_PG`, `PI_LED_NPWR`, LED data chain, RJ45 LED `+3V3`.
- **Not placed:** CAN block, SP3232, ADC, watchdog, field I/O protection, eFuse/LDO/NVMe buck, GPS slot, piezo.
