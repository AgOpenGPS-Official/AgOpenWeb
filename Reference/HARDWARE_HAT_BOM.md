# AgOpenWeb HAT — Bill of Materials

> Every part on the HAT, with its JLCPCB/LCSC number.
>
> **Part of the HAT document set:**
> - `HARDWARE_HAT_DESIGN.md`: why
> - `HARDWARE_HAT_NETLIST.md`: wiring
> - this file: parts
> - `HARDWARE_HAT_ISSUES.md`: open items
>
> Ref designators match across all four.
>
> **LCSC numbers:**
> - Checked against their manufacturer part numbers on 2026-09-23 (JLCPCB parts search). **Stock is a
>   snapshot: re-check at order time.**
> - Most parts are **Extended**, because JLC's Basic library no longer covers them. "Pref-Ext" parts
>   may avoid the extended loading fee on Economic assembly: confirm on the order page.
> - Scale: 1–2 prototype boards.

## 1. ICs and active parts

| Ref | Qty | Part | Package | LCSC | Type | Notes |
|---|---|---|---|---|---|---|
| U1 | 1 | TPS7B6933QDBVRQ1 | SOT-23-5 | C781801 | Ext | always-on 3.3 V, 40 V in, 15 µA. Pins: 1 IN, 2 NC, 3/4 GND, 5 OUT. Needs ≥ 2.2 µF *effective* on its output (C4 = 10 µF) |
| U2 | 1 | SN74LVC1G14DBVR | SOT-23-5 | C7835 | Ext | Schmitt inverter. Pins: 1 NC, 2 A, 3 GND, 4 Y, 5 VCC |
| U3 | 1 | TLV3011AIDBVR | SOT-23-6 | C2870632 | Ext | comparator, open-drain, 1.242 V ref. Pins: 1 OUT, 2 V−, 3 IN+, 4 IN−, 5 REF, 6 V+ |
| U4 | 1 | RT9080-33GJ5 | TSOT-23-5 | C841192 | Ext | logic 3.3 V LDO |
| U5, U6, U7 | 3 | MCP251863T-E/SS | SSOP-28 | C5226885 | Ext | CAN FD controller + transceiver |
| U8 | 1 | ADC128S102CIMTX/NOPB | TSSOP-16 | C179666 | Ext | 8-channel, 12-bit ADC |
| U9 | 1 | SP3232EEN-L/TR | SOIC-16 | C9378 | Ext | RS-232 × 2 |
| U10 | 1 | STWD100NYWY3F | SOT-23-5 | C46043 | Ext | watchdog, 1.6 s |
| U11 | 1 | SN74AHCT1G125DBVR | SOT-23-5 | C7484 | Ext | 3.3 → 5 V LED data buffer |
| X1 | 1 | YXC OT2EL4C4JI-111OLP-40M | 3225 4-pin | C5203551 | Ext | 40 MHz, 3.3 V, CAN clock |
| Q1 | 1 | IRFR5305TRPBF | DPAK | C2624 | Ext | reverse-polarity P-FET, −55 V |
| Q2 | 1 | DMP6023LE-13 | SOT-223 | C154901 | Ext | 12 V switch P-FET, −60 V, 35 mΩ at 4.5 V. Alternative: another IRFR5305 (C2624) |
| Q3–Q9 | 7 | Nexperia BSS138P,215 | SOT-23 | C75547 | Ext | latch logic. V<sub>th</sub> ≤ 1.5 V checked. **Don't substitute C7420339** (V<sub>th</sub> up to 1.6 V). Dual option: BSS138DW-7-F, C154900 |
| Q10 | 1 | 2N7002 | SOT-23 | C8545 | Basic | piezo driver |

## 2. Diodes and protection

| Ref | Qty | Part | Package | LCSC | Type | Notes |
|---|---|---|---|---|---|---|
| TV1, TV2 | 2 | SMBJ24A | SMB | C908801 | Ext | constant and keyed 12 V inputs |
| D1, D3 | 2 | BZT52C12 | SOD-123 | C19077410 | Pref-Ext | Q1 and Q2 gate clamps |
| D2, D5 | 2 | B5819W | SOD-123 | C8598 | Basic | `KEY_SENSE` and `VIN_SENSE` clamps |
| D4 | 1 | 1N4148W | SOD-123 | C81598 | Basic | hold diode |
| D6, D7, D8 | 3 | NUP2105L | SOT-23 | C284104 | Ext | CAN bus TVS |
| D9–D12 | 4 | SMAJ12CA-13-F | SMA | C134948 | Ext | RS-232 line TVS |
| D13, D14 | 2 | ESD9B5V | SOD-923 | C2905646 | Ext | WAS and current inputs |
| D15, D16, D17 | 3 | SMAJ16A | SMA | C283886 | Ext | switch inputs (alternative: Littelfuse, C74561) |
| D18 | 1 | SRV05-4 | SOT-23-6 | C558418 | Ext | logic-side ESD array |
| D19–D22 | 4 | SK6812SIDE-A | 4020 side-view | C5378721 | Ext | status LEDs |

## 3. Resistors (0402)

| Value | Qty | Refs | LCSC | Type |
|---|---|---|---|---|
| 1 MΩ | 4 | R1, R11, R12, R14 | C26083 | Basic |
| 100 kΩ | 5 | R2, R3, R5, R7, R16 | C25741 | Basic |
| 8.2 kΩ | 2 | R4, R17 | C25924 | Pref-Ext |
| 33 kΩ | 1 | R6 | C25779 | Basic |
| 47 kΩ | 1 | R8 | C25792 | Basic |
| 1 kΩ | 6 | R9, R26, R27, R28, R29, R30 | C11702 | Basic |
| 2.2 MΩ | 1 | R10 | C2998080 | Ext |
| 121 kΩ 1% | 1 | R13 | C11693 | Ext |
| 10 MΩ | 1 | R15 | C2933065 | Ext |
| 10 kΩ | 6 | R18, R19, R31, R32, R33, R41 | C25744 | Basic |
| 33 Ω 1% | 3 | R20, R21, R22 | C2906868 | Ext |
| 120 Ω 1% | 3 | R23, R24, R25 | C2909315 | Ext |
| 330 Ω | 5 | R34, R35, R36, R38, R39 | C25104 | Basic |
| 4.7 kΩ 1% | 1 | R37 | C2906869 | Ext |
| 100 Ω | 1 | R40 | C25076 | Basic |
| 470 Ω | 1 | R42 | C25117 | Basic |

42 resistors, R1–R42.

## 4. Capacitors

| Value | Package | Qty | Refs | LCSC | Type |
|---|---|---|---|---|---|
| 10 µF 50 V X5R | 1206 | 2 | C1, C2 | C13585 | Basic |
| 1 µF 50 V X7R | 0805 | 1 | C3 | C28323 | Basic |
| 10 µF 25 V X5R | 0603 | 2 | C4, C12 | C96446 | Basic |
| 10 nF 50 V X7R | 0402 | 3 | C5, C7, C14 | C15195 | Basic |
| 100 nF 16 V X7R | 0402 | 33 | C6, C9, C10, C11, C20–C42, C44–C49 | C1525 | Basic |
| 1 µF 16 V X7R | 0603 | 1 | C8 | C59782 | Ext |
| 2.2 µF 16 V X5R | 0603 | 1 | C13 | C23630 | Basic |
| 1 µF X5R | 0603 | 6 | C15, C16, C17, C18, C43, C50 | C15849 | Basic |
| 10 µF 25 V X5R | 0805 | 1 | C19 | C15850 | Basic |

50 capacitors, C1–C50.

## 5. Connectors, switch and piezo

| Ref | Qty | Part | LCSC | Type | Notes |
|---|---|---|---|---|---|
| J1 | 1 | 2 × 20 female 2.54 mm, 8.5 mm (Hong Cheng HC-PM254-8.5H-2x20PZ) | C22373925 | Ext | footprint on top, body fitted underneath; same series as J2 |
| J2 | 1 | 1 × 3 female 2.54 mm, 8.5 mm (Hong Cheng HC-PM254-8.5H-1x3PZ-02A) | C22373889 | Ext | footprint on top, body fitted underneath; off-grid |
| J3 | 1 | 2 × 13 IDC box header, straight, shrouded + keyed, **no latch** (CONNFLY DS1013-26SSiB1-B-0) | C75755 | Ext | 40.64 × 9.12 mm; EasyEDA footprint IDC-TH_DS1013-26SSIB1-B-0. Latched versions (52.5 mm long) don't fit the left strip. |
| J4, J5 | 2 | JST B2P-VH(LF)(SN), 2-pin | C160315 | Ext | power in and 12 V out; 10 A rated |
| J6 | 1 | 1 × 8 female 2.54 mm (BOOMELE) | C27438 | Ext | EMAX UM981, 8.5 mm body |
| J7 | 1 | 2 × 14 female **2.0 mm** (Hong Cheng HC-PM200-4.3H-2x14PZ) | C22436146 | Ext | UM982EB, **4.3 mm body**, low stock (~1.1k) |
| J8 | 1 | 1 × 3 female 2.54 mm | C146690 | Ext | console, **optional** |
| SW1 | 1 | C&K PTS645VH83-2LFS | C221889 | Ext | reset, **optional** |
| BZ1 | 1 | TDK PS1240P02BT, passive piezo 4 kHz | C76871 | Ext | |
| SJ1–SJ3 | 3 | solder jumper (footprint only) | — | — | CAN termination, open by default |
| H1–H4 | 4 | M2.5 plated hole | — | — | HAT mounting |
| H5–H10 | 6 | M3 plated hole | — | — | GNSS standoffs |

## 6. Hand-fit and off-board items

| Item | Notes |
|---|---|
| Raspberry Pi CM4 IO Board + CM4 module | the host |
| 1 × 3 male 2.54 mm header | **soldered into the IO board's J1 pads**, to mate with J2 |
| 4 × M2.5 standoffs, 11 mm | HAT ↔ IO board |
| 3–4 × M3 standoffs | GNSS board ↔ HAT. **Length depends on the board:** J6 is 8.5 mm tall, J7 is 4.3 mm |
| GNSS board: EMAX UM981 **or** Unicore UM982EB | fit one only |
| Antenna pigtails → panel bulkheads | one for the UM981, two for the UM982EB |
| NVMe + M.2-to-PCIe ×1 adapter | in the IO board's PCIe slot |
| J5 → IO board J20 lead | JST VHR-2N housing + SVH crimps at the HAT end. **TE 171822-4 Berg housing** at the IO board end, on J20 pin 1 (+12 V) and pins 2 + 3 (GND). **J20's pin order is the reverse of floppy-cable colours**: meter it first |
| J4 lead | JST VHR-2N + crimps → panel connector, constant 12 V + GND |
| Ribbon | 26-way 1.27 mm flat cable + 2 × 2 × 13 IDC sockets, with strain relief |
| Panel connector | Deutsch/ATS-style, 26+ ways, carrying the J3 signals + J4 power |
| Fuses (in the harness) | **5 A** on constant 12 V at the battery; **1 A** on the keyed feed |
