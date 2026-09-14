# AgOpenWeb AiO Board — BOM (as-built, 2026-09-14)

> Bill of materials generated from `Netlists From EasyEDA/Full-board_2026-09-14.net`: **151 netlist
> parts**, plus the hand-fit items that aren't netlist parts. Companion docs: `HARDWARE_AIO_BOARD.md`
> (why + review findings §13), `HARDWARE_AIO_NETLIST.md` (wiring). Designators are the real EasyEDA ones.
> JLC upload file: `HARDWARE_AIO_BOM_JLCPCB.csv`.
>
> **JLC part numbers:** carried over from the July stock-verified BOM where the part is unchanged.
> **`TBD`** = new in the September redesign, not yet looked up. **`verify`** = MPN or footprint
> differs from the July line, so the old C# may be the wrong part. July stock counts are
> **not** repeated here — re-check everything at order time.
>
> **Pending schematic changes that affect this BOM** (`HARDWARE_AIO_BOARD.md` §13): F4 may swap U20 to
> STWD100NYWY3F; F8 may add DNP CAN termination; F11/F12 may add a buffer FET and a 220 Ω.

---

## 1. Active & special parts

| Ref | Qty | Part | Footprint | JLC # | Block | Notes |
|---|---|---|---|---|---|---|
| U1 | 1 | TPS54560DDAR | SOIC-8 EP | C31966 | Power | 60 V / 5 A buck, 400 kHz |
| D1 | 1 | SS56C | SMC | C123948 | Power | buck catch diode |
| L2 | 1 | 6.8 µH (Isat ≥ 9 A) | MDA1054HT 11×10 | TBD / verify | Power | July C408485 was MWSA1004S (10×10), a different footprint |
| U2 | 1 | TPS259571DSGR | WSON-8 2×2 | C471038 | Power | CM eFuse |
| U3 | 1 | RT9080-33GJ5 | TSOT-23-5 | C841192 | Power | 3.3 V LDO |
| U4 | 1 | TPS563201DDCR | SOT-23-6 | C116592 | Power | NVMe 3.3 V / 3 A buck |
| L3 | 1 | 3.3 µH | SMNR4012 4×4 | TBD / verify | Power | July C15269 was SWPA4030 (different footprint) |
| Q1 | 1 | IRFR5305TRPBF | TO-252 | C2624 (verify) | Power | reverse-polarity P-FET. **Orientation issue: §13 F2** |
| TV1 | 1 | SMBJ24A | SMB | C87268 | Power | input TVS |
| D2 | 1 | BZT52C12-7-F | SOD-123 | C124196 (verify) | Power | Q1 gate zener. **Placement issue: §13 F3** |
| D4 | 1 | B5819W | SOD-123 | C8598 | Power | VIN_SENSE clamp |
| U5 | 1 | 91302-55-067R2M | M.2 M-key 5.5 mm | C2922444 | CM4 | NVMe socket |
| L1 | 1 | HR911130C | RJ45 THT | C50933 | CM4 | GbE magjack |
| U7, U8, U9 | 3 | MCP251863T-E/SS | SSOP-28 | TBD | CAN | CAN FD controller + transceiver. **STBY issue: §13 F1** |
| X1 | 1 | 40 MHz oscillator, 3.3 V | 3225 4-pin | TBD | CAN | CAN clock |
| D5, D6, D7 | 3 | NUP2105L | SOT-23 | C284104 | CAN | bus TVS |
| U21 | 1 | ADC128S102CIMTX/NOPB | TSSOP-16 | TBD | Analog | 8-ch 12-bit SPI ADC |
| U12 | 1 | SP3232EEN-L/TR | SOIC-16 | C9378 | Serial | RS-232 ×2 |
| D9–D12 | 4 | SMAJ12CA-13-F | SMA | C134948 (verify) | Serial | RS-232 line TVS |
| D13, D16 | 2 | ESD9B5V | SOD-923 | C2905646 | Field I/O | WAS / current ESD (low leakage) |
| D14, D17, D18 | 3 | SMAJ16A | SMA | C283886 | Field I/O | switch-input TVS. Thin stock in July — approve alternates (Littelfuse C74561) |
| D15 | 1 | SRV05-4 | SOT-23-6 | C558418 | Field I/O | logic-side ESD array |
| U20 | 1 | STWD100NXWY3F | SOT-23-5 | TBD | Supervision | watchdog, t<sub>WD</sub> 102 ms. **§13 F4: consider STWD100NYWY3F (1.6 s)** |
| U22 | 1 | SN74AHCT1G125DBVR | SOT-23-5 | TBD | HMI | 3.3 → 5 V LED data buffer |
| D19–D22 | 4 | SK6812SIDE-A | 4020 side-view | C5378721 | HMI | addressable RGB status LEDs |
| D23 | 1 | XL-0603QYGC | 0603 | TBD | HMI | power LED |
| Q2 | 1 | 2N7002 | SOT-23 | C8545 | HMI | piezo driver |
| BUZZER1 | 1 | 4000 Hz piezo, Ø12.5 mm TH, 5 mm pitch | BUZ-TH_BD12.5 | TBD / verify | HMI | driven by PWM, so a passive transducer is expected. July part was PS1240P02BT (C76871) |
| SW1 | 1 | PTS645VH83-2LFS | 6×6 TH tactile | TBD | HMI | reset (July used SMD TS-1187A C318884) |
| H1 | 1 | 1×3 2.54 mm header | HDR 1×3 | confirm | CM4 | Linux console |
| H3 | 1 | 2×2 2.54 mm header | HDR 2×2 | confirm | CM4 | USB + nRPIBOOT provisioning |

---

## 2. Passives — by value

| Value | Footprint | Qty | Designators | JLC # | Notes |
|---|---|---|---|---|---|
| 100 nF X7R | 0402 | 30 | C32, C33, C34, C44–C50, C64, C66, C69–C74, C76–C79, C83, C85–C91 | C1525 | decoupling, bootstrap, charge pump, RC filters |
| 1 µF X5R | 0603 | 7 | C54, C55, C56, C62, C63, C80, C84 | C15849 | |
| 10 nF X7R | 0402 | 2 | C29 (eFuse dVdt), C30 (VIN_SENSE) | C15195 | |
| 4.7 nF X7R | 0402 | 1 | C8 (buck comp) | C1538 | |
| 47 pF C0G | 0402 | 1 | C9 (buck comp) | C1567 | |
| 1 nF 2 kV | 1206 | 1 | C22 (RJ45 centre-tap) | C9196 | Extended |
| 10 µF 25 V X5R | 0805 | 7 | C10, C11 (NVMe buck in), C12, C19–C21 (5V_CM), C67 (+3V3) | C15850 | |
| 22 µF X5R | 0805 | 6 | C13–C18 (+3V3_NVME) | C45783 | was 2 + 1 bulk in July |
| 10 µF 50 V X5R | 1206 | 2 | C1, C2 (buck input) | C13585 | must be ≥ 50 V (TVS clamp) |
| 47 µF 10 V X5R | 1206 | 3 | C3, C4, C5 (buck output) | C96123 | comp network is locked to 3×47 µF |
| 470 µF 50 V electrolytic | SMD D12.5 | 2 | C6, C7 (hold-up) | C90272 | |
| 243 kΩ 1 % | 0402 | 1 | R1 (buck RT) | C43249 | |
| 442 kΩ 1 % | 0402 | 1 | R2 (UVLO top) | C273339 | |
| 90.9 kΩ 1 % | 0402 | 1 | R3 (UVLO bottom) | C26989 | |
| 487 Ω 1 % | 0402 | 1 | R4 (eFuse ILIM) | C3015778 | |
| 16.9 kΩ 1 % | 0402 | 1 | R5 (buck comp) | C25858 | |
| 8.2 kΩ | 0402 | 1 | R6 (VIN_SENSE bottom) | C25924 | |
| 52.3 kΩ 1 % | 0402 | 1 | R7 (buck FB top) | C26982 (verify) | |
| 33 kΩ 1 % | 0402 | 1 | R8 (NVMe buck FB top) | C25779 | |
| 100 kΩ | 0402 | 3 | R40 (Q1 gate), R42 (VIN_SENSE top), R43 (eFuse EN pull-up) | C25741 | |
| 10 kΩ | 0402 | 12 | R15, R47, R48, R49, R50, R51, R59, R61, R62, R69, R74, R75 | C25744 | |
| 4.7 kΩ | 0402 | 1 | R63 (watchdog EN pull-up) | C25900 | |
| 1 kΩ | 0402 | 8 | R13, R14 (Eth LEDs), R58, R60, R70 (switch series), R72, R73 (ADC RC), R79 (power LED) | C11702 | |
| 470 Ω | 0402 | 1 | R32 (piezo damping) | C25117 | |
| 330 Ω | 0402 | 4 | R27, R28, R29 (steering), R71 (LED data) | C25104 | |
| 100 Ω | 0402 | 1 | R33 (piezo gate) | C25076 | |
| 33 Ω | 0402 | 3 | R64, R65, R66 (CAN clock series) | TBD | new |

---

## 3. Hand-fit / off-netlist items

| Item | Part | Notes |
|---|---|---|
| CM4 module (U19) | CM4101000 (1 GB Lite Wireless), or any CM4 Lite variant | RPi reseller; hand-seated |
| CM4 mezzanine ×2 | **DF40C-100DS-0.4V(51)**, JLC C597931 | **not in the netlist** (U19 is one module footprint). Add to the JLC upload by hand; needs JLC's 0.4 mm fixture |
| NVMe SSD | 128 GB M.2 2230/2242 | pre-imaged |
| M.2 standoff + M2 screw | plated hole + hand-fit standoff | JLC SMT standoffs unreliable |
| GPS module (U16 **or** P2) | UM982EB module **or** ArduSimple simpleRTK2B | populate one (§13 F9) |
| J1 connector | Amphenol ATS13-26PA-BM01 + mating ATS06-26SA plug and size-20 contacts | hand-soldered right-angle, front panel |
| Input fuse | in-line blade holder in the harness | not on the board |
| Antennas | U.FL pigtails → SMA (GPS) / RP-SMA (WiFi) bulkheads | back panel |
| Thermal | 0.5–1 mm gap pad on CM4 SoC | enclosure boss |

---

## 4. Removed since the July BOM

For traceability. None of these are in the 2026-09-14 netlist.

| July part | JLC # | Why gone |
|---|---|---|
| STM32G473RCT6 + 8 MHz crystal, 15 pF load caps, ferrite, SWD header | C529361, C2682775, C1548, C1002 | MCU removed |
| TCAN1042VDRQ1 ×3 | C485806 | replaced by MCP251863 |
| CAN split-termination (60.4 Ω ×6, 4.7 nF ×3) + ACT45B CMC ×3 | C137954, C1538, C76584 | removed (§13 F8) |
| CA-IS3092W isolated RS-485 + 120 Ω term | C2890051, C25079 | RS-485 removed |
| TS3USB221 USB mux + USBLC6-2SC6 + micro-USB | C130085, C7519 | STM USB link removed; H3 header instead |
| 1.3" SH1106 OLED + I²C pull-ups + page rocker header | — | replaced by SK6812 LEDs |
| TS-1187A SMD reset tactile | C318884 | replaced by PTS645 TH |
| PS1240P02BT piezo | C76871 | 4000 Hz TH part in netlist (verify) |
| 6N137S / LTV-357T optos | C5123515, C119089 | already dropped Jul 7 (direct drive) |
| SMAJ12CA D8 (5th RS-232 TVS) | C134948 | RS-485/NMEA line gone |
