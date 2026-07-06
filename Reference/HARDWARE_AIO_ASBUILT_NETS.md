# AgOpenWeb AiO — AS-BUILT netlist (real EasyEDA designators)

> Wiring guide keyed to the **actual designators EasyEDA assigned** during capture (C1, R40, U1…),
> built one sheet at a time from each sheet's exported component list. This is the doc you wire
> **from** — every net lists `RefDes.pin`. Planning names (C_IN, R_RT…) are kept in the map tables
> for traceability back to `HARDWARE_AIO_NETLIST.md`.
>
> Convention: multi-valued parts (e.g. 4× 100k) are assigned to roles **in numeric designator
> order** — the designators are interchangeable within a value group, so wire whichever you like as
> long as the *count per role* matches. `[place: …]` = part the plan needs that wasn't on the export
> yet; give it a designator when you drop it.

---

## SHEET 1 — POWER  (from `Power_2026-07-05.net`, 50 parts)

### 1.0 Designator map (real → function)

| RefDes | Value | Function | Plan name |
|---|---|---|---|
| U1 | TPS54560DDAR | main buck 12→5 V | U1 |
| U2 | TPS259571DSGR | CM eFuse 5V_MAIN→5V_CM | U2 |
| U3 | RT9080-33GJ5 | 3.3 V logic LDO | U3 |
| U4 | TPS563201DDCR | M.2 3.3 V buck | U5 |
| Q1 | IRFR5305 | reverse-polarity P-FET | Q1 |
| L2 | 6.8 µH | buck inductor | L1 |
| L3 | 3.3 µH | M.2 buck inductor | L5 |
| D1 | SS56C | buck catch diode | D_SW |
| D2 | BZT52C12 | Q1 gate zener clamp | D3 |
| D4 | B5819W | VIN_SENSE clamp → +3V3 | D_S1 |
| TV1 | SMBJ24A | input TVS | D2 |
| C1, C2 | 10 µF 1206 | buck input (VIN_PROT) | C_IN2 ×2 |
| C3, C4, C5 | 47 µF 1206 | buck output (5V_MAIN) | C_OUT ×3 |
| C6, C7 | 470 µF | hold-up (VIN_PROT) | C_HU ×2 |
| C8 | 4.7 nF | comp Cc1 | Cc1 |
| C9 | 47 pF | comp Cc2 | Cc2 |
| C10, C11 | 10 µF 0805 | M.2 buck input | C_N1 ×2 |
| C12 | 10 µF 0805 | eFuse output (5V_CM) | C_E2 |
| C13, C14 | 22 µF 0805 | M.2 buck output (+3V3_NVMe) | C_N2 ×2 |
| C15 | 22 µF 0805 | **unassigned — confirm** (3rd out cap? or move to CPU as C_M2_bulk) | — |
| C32 | 100 nF | U1 bootstrap | C_BOOT |
| C33 | 100 nF | U4 bootstrap | C_BOOT5 |
| C54 | 1 µF | LDO input | C_L1 |
| C55 | 1 µF | LDO output | C_L2 |
| C56 | 1 µF | eFuse input | C_E1 |
| C62, C63 | 1 µF | **unassigned — confirm** (5V/3V3 rail bulk?) | — |
| R1 | 243 k | buck freq set (RT) | R_RT |
| R2 | 442 k | UVLO top | R_EN1 |
| R3 | 90.9 k | UVLO bottom | R_EN2 |
| R4 | 487 Ω | eFuse ILIM | R_ILIM |
| R5 | 16.9 k | comp Rc | Rc |
| R6 | 8.2 k | VIN_SENSE divider bottom | R_S2 |
| R7 | 52.3 k | buck FB top | R_FB1 |
| R8 | 33 k | M.2 buck FB top | R_N1 |
| R40 | 100 k | Q1 gate pull-up (→VIN_F) | R1 |
| R41 | 100 k | PWRGD pull-up (→+3V3) | R_PG |
| R42 | 100 k | VIN_SENSE divider top | R_S1 |
| R43 | 100 k | eFuse EN pulldown (→GND) | R_ENPD |
| R47 | 10 k | Q1 gate→GND bleed (with D2) | R2 |
| R48 | 10 k | buck FB bottom | R_FB2 |
| R49 | 10 k | M.2 buck FB bottom | R_N2 |
| R50 | 10 k | M.2 buck EN pull-up (→5V_CM) | R_EN5 |
| R51 | 10 k | eFuse FLT pull-up (→+3V3) | R_FLT |

### 1.1 Nets — input protection & main buck

| Net | Nodes |
|---|---|
| `VIN` | J1.1 (ATS-26, §Connectors — fuse is in-line in the harness, off-board), Q1.S, R40.2 |
| `PGATE` | Q1.G, R40.1, R47.1, D2.cathode |
| `VIN_PROT` | Q1.D, TV1.cathode, C6.+, C7.+, C1.1, C2.1, U1.VIN, R42.1 |
| `SW1` | U1.SW, L2.1, C32.1, D1.cathode |
| `5V_MAIN` | L2.2, C3.1, C4.1, C5.1, R7.1, U2.IN, U3.IN, U4.VIN, C10.1, C11.1, C56.1, C54.1 |
| `FB1` | R7.2, R48.1, U1.FB |
| `BOOT1` | C32.2, U1.BOOT |
| `EN1` | U1.EN, R2.1(→VIN_PROT), R3.1(→GND) |
| `RT1` | U1.RT/CLK, R1.1(→GND) |
| `COMP1` | U1.COMP, R5.1, C9.1 |
| `CCMID` | R5.2, C8.1 |
| `PG1` | U1.PWRGD, R41.1(→+3V3), → STM (MCU sheet) |
| `GND` (buck) | U1.GND/PAD, D1.anode, D2.anode, R47.2, C1.2, C2.2, C3.2, C4.2, C5.2, C6.−, C7.−, C8.2, C9.2, R1.2, R3.2, R48.2, TV1.anode |

### 1.2 Nets — eFuse (U2), LDO (U3), M.2 buck (U4), VIN-sense

| Net | Nodes |
|---|---|
| `5V_CM` | U2.OUT, C12.1, R50.2, → CM4 +5V (CPU sheet) |
| `PI_PWR_EN` | U2.EN, R43.1(→GND), ← STM PC8 (MCU sheet) |
| `PI_FLT` | U2.FLT, R51.1(→+3V3), → STM PC9 (MCU sheet) |
| `ILIM` | U2.ILIM, R4.1(→GND) |
| `DVDT` | U2.dVdt, `[place: C_dVdt 10 nF].1(→GND)` |
| `+3V3` | U3.OUT, C55.1, → MCU/CAN/logic (all sheets) |
| `EN3` | U3.EN → `5V_MAIN` |
| `SW5` | U4.SW, L3.1, C33.1 |
| `BOOT5` | U4.VBST, C33.2 |
| `+3V3_NVMe` | L3.2, C13.1, C14.1, R8.1, → M.2 3.3 V (CPU sheet) |
| `FB5` | R8.2, R49.1, U4.FB |
| `EN5` | U4.EN, R50.1(→5V_CM) |
| `VIN_SENSE` | R42.2, R6.1, D4.cathode, `[place: C_S1 10 nF].1`, → STM ADC PA4 (MCU sheet) |
| `GND` (rest) | U2.GND, U3.GND, U4.GND, C10.2, C11.2, C12.2, C13.2, C14.2, C54.2, C55.2, C56.2, R4.2, R43.2, R6.2, R49.2 |
| `+3V3` (clamp ref) | D4.anode-side clamp ref |

### 1.3 Gaps to close on the Power sheet

- **Fuse = external in-line holder (user-supplied), like the AiO** — NOT on the board (JLC is thin on
  holders). VIN enters at J1 and goes straight to Q1.S; the ATO/ATC blade holder lives in the harness
  upstream of the connector. No F1 footprint.
- **`[place: J1]` AMPSEAL-23 connector** — VIN/GND land on J1.1/J1.2 (see §Connectors). Not placed yet.
- **`[place: C_dVdt]` 10 nF** — eFuse inrush-pacing cap (U2.dVdt→GND) not on the export. Without it,
  CM4 inrush is uncontrolled. Add a 10 nF 0402 (C15195).
- **`[place: C_S1]` 10 nF** — VIN_SENSE RC anti-alias cap not on the export. Add a 10 nF 0402.
- **C15 (3rd 22 µF)** and **C62/C63 (2× 1 µF)** — present but unassigned. Confirm role (extra rail
  bulk?) or delete so the BOM reconciles.

---

## CONNECTORS — off-board wire I/O  (ATS-26, front panel, hand-fit)

**J1 = Amphenol ATS-26 right-angle header `ATS13-26PA-BM01`** (plug `ATS06-26SA` + size-20 contacts),
one hand-fit connector on the front, single row (RA headers can't stack; §7.4/§7.6), 44.5 × 35.5 mm
sealed flange → ~22 mm panel margin. No JLC C#; footprint via SamacSys/SnapEDA → EasyEDA. Place the
symbol on whatever sheet you capture it on, assign the real designator then; wire each pin to the net
below. 26 pins → full I/O, nothing dropped.

**J1 (ATS-26) — full off-board I/O**

| Pin | Net | To |
|---|---|---|
| 1 | `VIN` | Q1.S (Power); in-line fuse upstream in harness |
| 2 | `GND` | board ground / power return |
| 3 | `5V_MAIN` | WAS supply (Power) |
| 4 | `WAS_IN` | Page-6 TVS → STM PA0 ADC |
| 5 | `GND` | analog ground |
| 6 | `ISENSE_IN` | motor current: Page-6 TVS → STM PA5 ADC |
| 7 | `SW_WORK` | STM PB2 |
| 8 | `SW_ENGAGE` | STM PB10 |
| 9 | `SW_REMOTE` | STM spare GPIO |
| 10 | `PWM_MOTA` | OPTO_PWM out |
| 11 | `MOT_DIR` | OPTO_DIR out |
| 12 | `STEER_EN` | OPTO_EN out |
| 13 | `STEER_RTN` | amp-side opto common |
| 14 | `CAN1_H` | U8 (Page 4) |
| 15 | `CAN1_L` | U8 |
| 16 | `CAN2_H` | U9 |
| 17 | `CAN2_L` | U9 |
| 18 | `CAN3_H` | U10 |
| 19 | `CAN3_L` | U10 |
| 20 | `GND` | CAN shield + RS-232 signal ground |
| 21 | `RS485_A` | U11 (isolated) |
| 22 | `RS485_B` | U11 (isolated) |
| 23 | `NMEA_TX` | U12 (SP3232 ch1) |
| 24 | `NMEA_RX` | U12 |
| 25 | `EXT232_TX` | U12 (SP3232 ch2, LPUART1) |
| 26 | `EXT232_RX` | U12 |

*Full I/O — nothing dropped* (26 pins covers both RS-232 ports + current-sense).

---

## SHEET 2 — CPU · SHEET 3 — MCU · SHEET 4 — CAN · SHEET 5 — SERIAL · SHEET 6 — I/O · SHEET 7 — HMI

*Pending each sheet's export.*
