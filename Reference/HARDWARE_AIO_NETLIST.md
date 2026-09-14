# AgOpenWeb AiO Board — Netlist (as-built, 2026-09-14)

> Human-readable wiring reference generated from the **full-board EasyEDA Standard export**
> `Netlists From EasyEDA/Full-board_2026-09-14.net` (151 parts, 128 nets, every part connected).
> Nodes are `RefDes.pin` with the pin *function* in brackets. Pin functions for the CM4, MCP251863,
> ADC128S102 and STWD100 were checked against their datasheets on 2026-09-14. The rest come from
> standard pinouts and the net names, so confirm them against the symbols.
> Companion docs: **`HARDWARE_AIO_BOARD.md`** (design *why*), **`HARDWARE_AIO_ISSUES.md`** (open issues),
> **`HARDWARE_AIO_BOM.md`** (parts), **`HARDWARE_AIO_LAYOUT_GUIDE.md`** (routing rules).
>
> **This revision is the CM4-only design.** The STM32G473, the TCAN1042 transceivers, the isolated
> RS-485, the OLED and the USB mux from the July (EasyEDA Pro) design are gone. The July pre-capture
> intent doc this file replaces is in git history (`HARDWARE_AIO_NETLIST.md` before 2026-09-14).
>
> **Export note (EasyEDA Standard):** the netlist export names the file after the sheet that is
> open (this one came out as `Power_2026-09-14.net`) but contains **every sheet**. Rename to
> `Full-board_<date>.net` when committing. Format = Protel/Altium (`[part]` / `(net)` blocks).

⚠ **Open schematic issues** are listed in `HARDWARE_AIO_ISSUES.md`. The must-fix ones are both in the
input protection (§1.1 below): **Q1 orientation (S1)** and **Q1 gate clamp (S2)**.

---

## Block map

| § | Block | Main parts |
|---|---|---|
| 1 | Power | J1.1 in, Q1, TV1, C6/C7, U1 5 V buck, U2 CM eFuse, U3 3V3 LDO, U4 NVMe 3V3 buck, VIN_SENSE |
| 2 | CM4 core | U19 CM4, GPIO_VREF strap, RUN_PG/reset, nRPIBOOT + USB header H3, console H1, PCIe → U5 M.2, Ethernet → L1 RJ45 |
| 3 | SPI peripherals | 3× MCP251863 CAN FD (U7/U8/U9) + 40 MHz clock X1, ADC128S102 (U21) |
| 4 | Serial & GPS | SP3232 (U12) 2× RS-232, GPS slot (U16 UM982 / P2 ArduSimple) |
| 5 | Field I/O | J1 ATS-26, switch inputs, WAS/current analog, steering outputs |
| 6 | Supervision & HMI | STWD100 watchdog (U20), reset SW1, SK6812 LEDs + level shifter (U22), power LED, piezo |
| 7 | CM4 GPIO allocation | full GPIO table |

---

## Voltage domains

| Net | Source | On when | Nominal | Loads |
|---|---|---|---|---|
| `VIN` | J1.1 | vehicle power | 12–14.4 V | Q1 |
| `VIN_PROT` | Q1 | vehicle power | 12–14.4 V (TVS clamp ~39 V) | U1, hold-up C6/C7, VIN_SENSE divider |
| `5V_MAIN` | U1 buck | always (with VIN) | 4.98 V | U2, U3, U4, CAN VCC, ADC VA, GPS modules, LEDs, piezo, WAS sensor (J1.3) |
| `5V_CM` | U2 eFuse | always (EN pulled high) | 5.0 V | CM4 +5V (6 pins), U4 EN |
| `+3V3` | U3 LDO | always | 3.3 V | CAN VDD/VIO, ADC VD, SP3232, osc X1, watchdog, pull-ups, RJ45 LEDs |
| `+3V3_NVME` | U4 buck | with `5V_CM` | 3.30 V | M.2 socket U5 |
| `CM4_3V3` | CM4 pins 84/86 | with CM4 | 3.3 V | GPIO_VREF strap only |
| `CM4_1V8` | CM4 pins 88/90 | with CM4 | 1.8 V | not used (pins tied together only) |

---

## 1. POWER

### 1.1 Input protection `VIN → VIN_PROT`

| Net | Nodes | Notes |
|---|---|---|
| `VIN` | J1.1, Q1.3 [S], R40.2 | vehicle +12 V; fuse is in-line in the harness, not on the board |
| `PGATE` | Q1.1 [G], R40.1, R47.1, D2.1 [K] | gate: R40 100 k to VIN, R47 10 k to GND, D2 BZT52C12 to GND |
| `VIN_PROT` | Q1.2 [D, tab], TV1.1 [K], C6.1, C7.1, C1.1, C2.1, U1.2 [VIN], R2.1, R42.1 | protected input bus |

⚠ **Q1 orientation — ISSUES S1.** With source on `VIN` and drain on `VIN_PROT`, the
P-FET body diode (drain→source) is forward-biased by a reversed battery, so reverse polarity is not
blocked. The usual high-side P-FET arrangement is drain = battery, source = load. D2 also clamps
gate-to-GND rather than gate-to-source, so Vgs isn't limited (ISSUES S2). Both carried over
unchanged from the July netlist.

### 1.2 Main buck `VIN_PROT → 5V_MAIN` (U1 TPS54560DDAR, 400 kHz, async)

| Net | Nodes | Notes |
|---|---|---|
| `SW_BUCK1` | U1.8 [SW], L2.1, C32.1, D1.1 [K] | switch node; D1 SS56C catch diode |
| `BOOT1` | U1.1 [BOOT], C32.2 | 100 nF bootstrap |
| `5V_MAIN` | L2.2, C3/C4/C5 (47 µF), R7.1, … | 6.8 µH output |
| `FB1` | U1.5 [FB], R7.2 (52.3 k), R48.1 (10 k → GND) | 0.8 V × 6.23 = 4.98 V |
| `EN1` | U1.3 [EN], R2.2 (442 k from VIN_PROT), R3.1 (90.9 k → GND) | UVLO start ~6.5 V / stop ~5.0 V |
| `RT1` | U1.4 [RT/CLK], R1.1 (243 k → GND) | 400 kHz |
| `COMP1` | U1.6 [COMP], R5.1 (16.9 k), C9.1 (47 pF → GND) | compensation |
| `CCMID` | R5.2, C8.1 (4.7 nF → GND) | Rc–Cc1 mid-node |
| input caps | C1, C2 (10 µF 1206) + C6, C7 (470 µF) on `VIN_PROT` | |

### 1.3 CM eFuse `5V_MAIN → 5V_CM` (U2 TPS259571DSGR)

| Net | Nodes | Notes |
|---|---|---|
| `5V_MAIN` | U2.3, U2.4 [IN] | input; C56 1 µF |
| `5V_CM` | U2.5 [OUT], C12, C19, C20, C21 (10 µF), R50.2, CM4 +5V pins | CM4 supply |
| `EFUSE_EN` | U2.2 [EN], R43.1 (100 k → +3V3) | **always enabled** — no CM4 power control |
| `PI_FLT` | U2.6 [FLT], R51.1 (10 k → +3V3) | fault flag **not routed** to the CM4 |
| `ILIM` | U2.7, R4.1 (487 Ω → GND) | ~4.17 A limit |
| `DVDT` | U2.1, C29.1 (10 nF → GND) | inrush soft-start |

### 1.4 Logic LDO `5V_MAIN → +3V3` (U3 RT9080-33GJ5)

| Net | Nodes | Notes |
|---|---|---|
| `5V_MAIN` | U3.1 [VIN], U3.3 [EN] | always on; C54, C63 1 µF |
| `+3V3` | U3.5 [VOUT] | C55, C62 1 µF + C67 10 µF bulk |

### 1.5 NVMe rail `5V_MAIN → +3V3_NVME` (U4 TPS563201DDCR)

| Net | Nodes | Notes |
|---|---|---|
| `5V_MAIN` | U4.3 [VIN], C10, C11 (10 µF) | |
| `SW5` | U4.2 [SW], L3.1 (3.3 µH), C33.1 | |
| `BOOT5` | U4.6 [VBST], C33.2 | |
| `+3V3_NVME` | L3.2, C13–C18 (6× 22 µF), C34 (100 nF), R8.1, U5 3.3 V pins | |
| `FB5` | U4.4 [VFB], R8.2 (33 k), R49.1 (10 k → GND) | 0.768 V × 4.3 = 3.30 V |
| `EN5` | U4.5 [EN], R50.1 (10 k from `5V_CM`) | SSD powers with the CM rail |

### 1.6 Input voltage sense `VIN_PROT → ADC`

| Net | Nodes | Notes |
|---|---|---|
| `VIN_SENSE` | R42.2 (100 k from VIN_PROT), R6.1 (8.2 k → GND), C30.1 (10 nF), D4.2 [A], U21.6 [IN2] | 40 V → 3.03 V; D4 B5819W clamps to `+3V3` |

---

## 2. CM4 CORE (U19, CM4101000 footprint)

### 2.1 Power & straps

| Net | CM4 pins | Other nodes | Notes |
|---|---|---|---|
| `5V_CM` | 77, 79, 81, 83, 85, 87 | U2 out, C12, C19–C21 | +5V input |
| `CM4_3V3` | 78 [GPIO_VREF], 84, 86 | C76 100 nF | 3.3 V GPIO bank (July fix C1 kept) |
| `CM4_1V8` | 88, 90 | — | tied together, unused |
| `GND` | 1, 2, 7, 8, 13, 14, 22, 23, 32, 33, 42, 43, 52, 53, 59, 60, 65, 66, 71, 74, 98, 107, 108, 113, 114, 119, 120, 125, 126, 131, 132, 137, 138, 144, 150, 155, 156, 161, 162, 167, 168, 173, 174, 179, 180, 185, 186, 191, 192, 197, 198 | | |

### 2.2 Reset, boot, supervision

| Net | Nodes | Notes |
|---|---|---|
| `RUN_PG` | U19.92, SW1.1, SW1.2, U20.1 [WDO] | CM4 reset input (10 k internal pull-up). SW1 pins 3/4 → GND. See §6.1 |
| `NRPIBOOT` | U19.93, H3.1 | jumper H3.1↔H3.3 (GND) = rpiboot/USB boot |
| `GLOBAL_EN` | U19.99 only | floating OK (100 k internal pull-up to +5V) |
| `NEXTRST` | U19.100 only | CM4 reset *output*, unused |
| `WL_NDISABLE` / `BT_NDISABLE` | U19.89 / U19.91 only | floating = wireless + BT enabled |
| `PI_LED_NPWR` | U19.95, D23.1 | power LED; D23.2 → R79 1 k → `+3V3` |
| `USB_OTG_ID` (pin 101) | not connected | internal pull-up → USB device mode (correct for rpiboot) |

### 2.3 USB + console headers

| Header | Pin | Net | CM4 pin |
|---|---|---|---|
| **H3** (2×2, USB/rpiboot) | 1 | `NRPIBOOT` | 93 |
| | 2 | `USB_N` | 103 |
| | 3 | `GND` | — |
| | 4 | `USB_P` | 105 |
| **H1** (1×3, Linux console, 3.3 V TTL) | 1 | `GND` | — |
| | 2 | `CON_TX` | 55 (GPIO14, TXD0) |
| | 3 | `CON_RX` | 51 (GPIO15, RXD0) |

H3 has no VBUS pin — the board powers the CM4 during provisioning. No ESD part on USB (internal
header).

### 2.4 PCIe ×1 → M.2 M-key (U5 91302-55-067R2M)

| Net | CM4 pin | M.2 pin | Notes |
|---|---|---|---|
| `PCIE_TX_P` / `PCIE_TX_N` | 122 / 124 | 49 / 47 | CM4 transmit → SSD; AC caps on CM4 |
| `PCIE_RX_P` / `PCIE_RX_N` | 116 / 118 | 43 / 41 | SSD transmit → CM4; AC caps on SSD |
| `PCIE_CK_P` / `PCIE_CK_N` | 110 / 112 | 55 / 53 | REFCLK; AC caps on CM4 |
| `PCIE_NRST` | 109 | 50 | PERST# |
| `PCIE_CLKREQ` | 102 | 52 | CLKREQ#; R15 10 k → `+3V3` |
| `+3V3_NVME` | — | 2, 4, 12, 14, 16, 18, 70, 72, 74 | |
| `GND` | — | 1, 3, 9, 15, 21, 27, 33, 39, 45, 51, 57, 71, 73, 75 | |

### 2.5 Ethernet → L1 HR911130C magjack

| Net | CM4 pin | L1 pin |
|---|---|---|
| `ETH0_P` / `ETH0_N` | 12 / 10 | P2 / P3 |
| `ETH1_P` / `ETH1_N` | 4 / 6 | P4 / P7 |
| `ETH2_P` / `ETH2_N` | 11 / 9 | P5 / P6 |
| `ETH3_P` / `ETH3_N` | 3 / 5 | P8 / P9 |
| `NLED1` → R14 1 k → `ETH_LED_YEL` | 19 [Ethernet_nLED1] | 13 |
| `NLED2` → R13 1 k → `ETH_LED_GRN` | 17 [Ethernet_nLED2] | 12 |
| `+3V3` (LED anodes) | — | 11, 14 |
| `L1_P1` → C22 1 nF / 2 kV → GND | — | P1 |
| `GND` | — | P10, SHIELD0, SHIELD1 |

---

## 3. SPI PERIPHERALS (CM4 SPI0)

### 3.0 Shared bus

| Net | CM4 pin (GPIO) | Nodes |
|---|---|---|
| `SPI0_SCLK` | 38 (GPIO11) | U7.9, U8.9, U9.9 [SCK], U21.16 [SCLK] |
| `SPI0_MOSI` | 44 (GPIO10) | U7.10, U8.10, U9.10 [SDI], U21.14 [DIN] |
| `SPI0_MISO` | 40 (GPIO9) | U7.11, U8.11, U9.11 [SDO], U21.15 [DOUT] |
| `NCS_CAN1` | 39 (GPIO8, SPI0_CE0) | U7.13 — default pull-high GPIO, no resistor |
| `NCS_CAN2` | 37 (GPIO7, SPI0_CE1) | U8.13 — default pull-high GPIO, no resistor |
| `NCS_CAN3` | 41 (GPIO25) | U9.13, R74 10 k → `+3V3` (GPIO25 defaults pull-low) |
| `NCS_ADC` | 24 (GPIO26) | U21.1 [CS], R75 10 k → `+3V3` (GPIO26 defaults pull-low) |

CAN3 and ADC chip-selects are GPIO chip-selects (`cs-gpios` in the device tree).

### 3.1 CAN FD ×3 — MCP251863T-E/SS (controller + transceiver), n = 1/2/3 → U7/U8/U9

| Pin | Function | Net |
|---|---|---|
| 1 | VIO | `+3V3` |
| 2 | NC | — |
| 3 | CANL | `CANn_L` → Dn+4 NUP2105L.2 → J1 |
| 4 | CANH | `CANn_H` → Dn+4 NUP2105L.1 → J1 |
| 5 | STBY | `GND` (transceiver normal mode) |
| 6 | nINT1/GPIO1 | — |
| 7 | nINT0/GPIO0/XSTBY | — |
| 9 / 10 / 11 / 13 | SCK / SDI / SDO / nCS | SPI0, `NCS_CANn` |
| 14 | VDD | `+3V3` |
| 15 ↔ 23 | TXCAN ↔ TXD | `U7_15` / `U8_15` / `U9_15` (controller → transceiver) |
| 16 ↔ 28 | RXCAN ↔ RXD | `U7_16` / `U8_16` / `U9_16` (transceiver → controller) |
| 18 | CLKO/SOF | — |
| 19 | nINT | `CANn_INT` → CM4 |
| 20 | OSC2 | — (open with external clock) |
| 21 | OSC1 | `CAN_CLKn` ← R64/R65/R66 33 Ω ← `CAN_CLK` |
| 22 / 24 | VSS / GND | `GND` |
| 25 | VCC (transceiver) | `5V_MAIN` |

STBY has an internal pull-up to VIO (DS20006624 §8.2.2) and normal mode needs it low, so grounding
pin 5 is what makes the transceivers usable. Pin 7 (XSTBY) is left free.

| Interrupt | CM4 pin (GPIO) |
|---|---|
| `CAN1_INT` | 29 (GPIO16) |
| `CAN2_INT` | 50 (GPIO17) |
| `CAN3_INT` | 48 (GPIO27) |

Decoupling on the CAN block: `+3V3` C69/C70/C71 + C77/C78/C79 (100 nF), `5V_MAIN` C48/C49/C50 (100 nF).
Bus TVS: D5 (CAN1), D6 (CAN2), D7 (CAN3) NUP2105L, pin 3 → GND. **No termination and no
common-mode chokes on the board** (July split-termination and CMC footprints were removed).

### 3.2 CAN clock — X1 40 MHz oscillator

| Net | Nodes | Notes |
|---|---|---|
| `CAN_CLK` | X1.3 [OUT], R64.1, R65.1, R66.1 | one oscillator, star-fed through 33 Ω series R to each OSC1 |
| `+3V3` | X1.1 [EN], X1.4 [VDD] | |

### 3.3 ADC — U21 ADC128S102 (8-ch, 12-bit, VA = reference)

| Pin | Function | Net | Notes |
|---|---|---|---|
| 1 | CS | `NCS_ADC` | |
| 2 | VA (and reference) | `5V_MAIN` | input range 0–5 V, ratiometric with the WAS supply (J1.3) |
| 3 / 12 | AGND / DGND | `GND` | |
| 4 | IN0 | `WAS_IN_AA` | R72 1 k + C90 100 nF from `WAS_IN` |
| 5 | IN1 | `ISENSE_IN_AA` | R73 1 k + C91 100 nF from `ISENSE_IN` |
| 6 | IN2 | `VIN_SENSE` | §1.6 |
| 7–11 | IN3–IN7 | `GND` | unused inputs grounded |
| 13 | VD | `+3V3` | 3.3 V logic to the CM4 |
| 14 / 15 / 16 | DIN / DOUT / SCLK | SPI0 | rated performance at SCLK 8–16 MHz |

---

## 4. SERIAL & GPS

### 4.1 RS-232 ×2 — U12 SP3232EEN (non-isolated, 3.3 V)

| Pin | Function | Net | Goes to |
|---|---|---|---|
| 11 | T1IN | `RS232_1_TX` | CM4 36 (GPIO0, TXD2) |
| 14 | T1OUT | `RS232_1_TXD` | J1.23, D9 SMAJ12CA |
| 13 | R1IN | `RS232_1_RXD` | J1.24, D10 SMAJ12CA |
| 12 | R1OUT | `RS232_1_RX` | CM4 35 (GPIO1, RXD2) |
| 10 | T2IN | `RS232_2_TX` | CM4 54 (GPIO4, TXD3) |
| 7 | T2OUT | `RS232_2_TXD` | J1.25, D11 SMAJ12CA |
| 8 | R2IN | `RS232_2_RXD` | J1.26, D12 SMAJ12CA |
| 9 | R2OUT | `RS232_2_RX` | CM4 34 (GPIO5, RXD3) |
| 1 / 3 | C1+ / C1− | `SP_C1P` / `SP_C1N` | C44 100 nF |
| 4 / 5 | C2+ / C2− | `SP_C2P` / `SP_C2N` | C45 100 nF |
| 2 / 6 | V+ / V− | `SP_VP` / `SP_VN` | C46 / C47 100 nF → GND |
| 16 / 15 | VCC / GND | `+3V3` / `GND` | C64, C66 100 nF |

### 4.2 GPS slot — populate ONE of U16 or P2

| Net | CM4 pin (GPIO) | U16 UM982EB | P2 ArduSimple RTK2B |
|---|---|---|---|
| `GPS_RX` (module TX → CM4) | 28 (GPIO13, RXD5) | 15 | 11 |
| `GPS_TX` (CM4 → module RX) | 31 (GPIO12, TXD5) | 16 | 12 |
| `5V_MAIN` | — | 6 | 29 |
| `GND` | — | 14, 17, 20, 22 | 7, 30, 31 |

Both modules share UART5, so fitting both would short their TX outputs together. No PPS line and no
3.3 V supply to the slot in this revision. The July dual-F9P (U17/U18) and XBee (JP1) options are gone.

---

## 5. FIELD I/O

### 5.1 J1 — Amphenol ATS13-26PA-BM01 (front panel, hand-fit)

| Pin | Net | To | Change vs July |
|---|---|---|---|
| 1 | `VIN` | Q1 (§1.1) | — |
| 2 | `GND` | power return | — |
| 3 | `5V_MAIN` | WAS sensor supply | — |
| 4 | `WAS_IN` | D13 ESD9B5V, R72 → ADC IN0 | — |
| 5 | `GND` | analog/sensor ground | — |
| 6 | `ISENSE_IN` | D16 ESD9B5V, R73 → ADC IN1 | — |
| 7 | `SW_WORK_IN` | §5.2 | — |
| 8 | `SW_ENGAGE_IN` | §5.2 | — |
| 9 | `SW_REMOTE_IN` | §5.2 | — |
| 10 | `STEER_PWM` | R27 330 Ω ← CM4 GPIO18 | — |
| 11 | `STEER_DIR` | R28 330 Ω ← CM4 GPIO23 | — |
| 12 | `STEER_EN_OUT` | R29 330 Ω ← CM4 GPIO24 | — |
| 13 | `GND` | steering return | — |
| 14 | `CAN1_L` | U7.3 | **was CAN1_H** |
| 15 | `CAN1_H` | U7.4 | **was CAN1_L** |
| 16 | `CAN2_L` | U8.3 | **was CAN2_H** |
| 17 | `CAN2_H` | U8.4 | **was CAN2_L** |
| 18 | `CAN3_L` | U9.3 | **was CAN3_H** |
| 19 | `CAN3_H` | U9.4 | **was CAN3_L** |
| 20 | `GND` | CAN / RS-232 signal ground | — |
| 21 | — | not connected | **was RS485_A** |
| 22 | — | not connected | **was RS485_B** |
| 23 | `RS232_1_TXD` | U12 T1OUT | was NMEA_TX |
| 24 | `RS232_1_RXD` | U12 R1IN | was NMEA_RX |
| 25 | `RS232_2_TXD` | U12 T2OUT | was EXT232_TX |
| 26 | `RS232_2_RXD` | U12 R2IN | was EXT232_RX |

### 5.2 Switch inputs (contact-to-ground, 3.3 V GPIO)

| Input | Line side | Logic side | CM4 pin (GPIO) |
|---|---|---|---|
| Work | J1.7 `SW_WORK_IN`, D14 SMAJ16A, R58 1 k | `SW_WORK`: R59 10 k → +3V3, C72 100 nF, D15.1 | 26 (GPIO19) |
| Engage | J1.8 `SW_ENGAGE_IN`, D17 SMAJ16A, R60 1 k | `SW_ENGAGE`: R61 10 k → +3V3, C73 100 nF, D15.3 | 27 (GPIO20) |
| Remote | J1.9 `SW_REMOTE_IN`, D18 SMAJ16A, R70 1 k | `SW_REMOTE`: R69 10 k → +3V3, C83 100 nF, D15.4 | 25 (GPIO21) |

D15 SRV05-4: pin 5 → `+3V3`, pin 2 → GND. The CM4 GPIOs are 3.3 V only, so these inputs suit
switches to ground, not 12 V-level signals (ISSUES S11).

### 5.3 Steering outputs (direct GPIO, 3.3 V logic)

| Net (CM4 side) | CM4 pin (GPIO) | Series R | Net (J1 side) | J1 |
|---|---|---|---|---|
| `PWM_MOTA` | 49 (GPIO18, PWM0_0) | R27 330 Ω | `STEER_PWM` | 10 |
| `MOT_DIR` | 47 (GPIO23) | R28 330 Ω | `STEER_DIR` | 11 |
| `STEER_EN` | 45 (GPIO24) | R29 330 Ω | `STEER_EN_OUT` | 12 |

No optos: this matches the July "Option A" direct-drive decision for MD13S / IBT-2 style drivers.

---

## 6. SUPERVISION & HMI

### 6.1 Hardware watchdog — U20 STWD100NXWY3F

| Pin | Function | Net | Notes |
|---|---|---|---|
| 1 | WDO (open-drain, active low) | `RUN_PG` | pulls CM4 pin 92 low for t<sub>PW</sub> ≈ 210 ms on timeout |
| 2 | GND | `GND` | |
| 3 | EN (low = watchdog on) | `WDT_EN` | CM4 56 (GPIO3) + R63 4.7 k → `+3V3` |
| 4 | WDI | `WDT_WDI` | CM4 46 (GPIO22) |
| 5 | VCC | `+3V3` | C84 1 µF + C85 100 nF |

- EN high = off. R63 plus GPIO3's own 1.8 k pull-up keep the watchdog **off through boot and after
  every reset**, until Linux drives GPIO3 low.
- **"NX" = t<sub>WD</sub> 102 ms (71–142 ms).** Linux has to toggle WDI at least every ~70 ms.
  STWD100**NY**WY3F is the 1.6 s version (ISSUES S3).
- GPIO3 is also I²C1 SCL, so `dtparam=i2c_arm` must stay off.

### 6.2 Reset button — SW1 PTS645VH83-2LFS (through-hole)

SW1.1/SW1.2 → `RUN_PG`, SW1.3/SW1.4 → GND. Pressing it resets the CM4 directly (hard reset, no
clean shutdown).

### 6.3 Status LEDs — 4× SK6812SIDE-A chain

| Net | Nodes | Notes |
|---|---|---|
| `LED_DATA` | CM4 58 (GPIO2), U22.2 [A] | GPIO2 has a 1.8 k pull-up on the module |
| `LED_DATA_OUT` | U22.4 [Y], R71.1 | SN74AHCT1G125 shifts 3.3 V → 5 V; U22.1 [OE̅] → GND, U22.5 → `5V_MAIN` |
| `LED_DATA_IN` | R71.2 (330 Ω), D19.1 [DIN] | |
| `LED1_DOUT` / `LED2_DOUT` / `LED3_DOUT` | D19.3→D20.1 / D20.3→D21.1 / D21.3→D22.1 | D22.3 DOUT open |
| `5V_MAIN` / `GND` | D19–D22 pin 2 / pin 4 | C86–C89 100 nF, C80 1 µF, C74 100 nF |

GPIO2 has no PWM, PCM or SPI function, so the LED timing would have to be bit-banged from Linux
(ISSUES S4).

### 6.4 Power LED

`PI_LED_NPWR` (CM4 95) → D23 XL-0603QYGC → `LED_PWR` → R79 1 k → `+3V3`. The CM4 datasheet says
this pin must be buffered (ISSUES S9).

### 6.5 Piezo

| Net | Nodes | Notes |
|---|---|---|
| `PIEZO_PWM` | CM4 30 (GPIO6), R33.1 (100 Ω) | GPIO6 has no hardware PWM, so tones come from software PWM |
| `PZ_GATE` | R33.2, Q2.1 [G] 2N7002, R62.1 (10 k → GND) | |
| `PZ_DRAIN` | Q2.3 [D], BUZZER1.2, R32.2 | R32 470 Ω across the piezo (damping) |
| `5V_MAIN` | BUZZER1.1, R32.1 | |

---

## 7. CM4 GPIO ALLOCATION

GPIO numbers are from the CM4 datasheet pinout. "Default pull" is the BCM2711 reset state.

| GPIO | CM4 pin | Net | Function | Default pull | Notes |
|---|---|---|---|---|---|
| 0 | 36 | `RS232_1_TX` | UART2 TXD (ALT4) | high | ID_SD pin: set `force_eeprom_read=0`, `disable_poe_fan=1` |
| 1 | 35 | `RS232_1_RX` | UART2 RXD (ALT4) | high | ID_SC pin, as above |
| 2 | 58 | `LED_DATA` | SK6812 data | high (1.8 k on module) | no hardware timing peripheral |
| 3 | 56 | `WDT_EN` | watchdog enable (low = on) | high (1.8 k on module) | keep I²C1 disabled |
| 4 | 54 | `RS232_2_TX` | UART3 TXD (ALT4) | high | |
| 5 | 34 | `RS232_2_RX` | UART3 RXD (ALT4) | high | |
| 6 | 30 | `PIEZO_PWM` | piezo tone (software PWM) | high | |
| 7 | 37 | `NCS_CAN2` | SPI0 CE1 | high | |
| 8 | 39 | `NCS_CAN1` | SPI0 CE0 | high | |
| 9 | 40 | `SPI0_MISO` | SPI0 | low | |
| 10 | 44 | `SPI0_MOSI` | SPI0 | low | |
| 11 | 38 | `SPI0_SCLK` | SPI0 | low | |
| 12 | 31 | `GPS_TX` | UART5 TXD (ALT4) | low | |
| 13 | 28 | `GPS_RX` | UART5 RXD (ALT4) | low | |
| 14 | 55 | `CON_TX` | console TX | low | UART0 goes to Bluetooth by default on wireless CM4 |
| 15 | 51 | `CON_RX` | console RX | low | |
| 16 | 29 | `CAN1_INT` | MCP251863 #1 nINT | low | |
| 17 | 50 | `CAN2_INT` | MCP251863 #2 nINT | low | |
| 18 | 49 | `PWM_MOTA` | steering PWM (PWM0_0, ALT5) | low | |
| 19 | 26 | `SW_WORK` | work switch in | low | |
| 20 | 27 | `SW_ENGAGE` | engage switch in | low | |
| 21 | 25 | `SW_REMOTE` | remote switch in | low | PCM_DOUT: a candidate pin for the LEDs |
| 22 | 46 | `WDT_WDI` | watchdog kick | low | |
| 23 | 47 | `MOT_DIR` | steering direction | low | |
| 24 | 45 | `STEER_EN` | steering enable | low | low at boot = steering off |
| 25 | 41 | `NCS_CAN3` | MCP251863 #3 CS (GPIO CS) | low | R74 pull-up |
| 26 | 24 | `NCS_ADC` | ADC CS (GPIO CS) | low | R75 pull-up |
| 27 | 48 | `CAN3_INT` | MCP251863 #3 nINT | low | |

**All 28 GPIOs are used.** Four PL011 UARTs are in use (0 console, 2 RS-232 #1, 3 RS-232 #2,
5 GPS). UART4 isn't available because its pins (GPIO8/9) belong to SPI0.
