# AgOpenWeb HAT — Netlist

Each section lists its parts (designator + LCSC part number), then every net and the pins it
connects. Placement and the reasons behind the design are in `HARDWARE_HAT_DESIGN.md`.

**Pin notation:**
- `R3.1` = R3 pin 1 (resistors and capacitors: either end)
- diodes: `.K` / `.A` (cathode / anode)
- MOSFETs: `.G` `.D` `.S`
- ICs and connectors: pin numbers

---

## 1. Host connectors

| Ref | Part # |
|---|---|
| J1 | C22373925 |
| J2 | C22373889 |
| J8 | C146690 (optional) |

J1 and J2: footprints on the **top** layer (don't flip; that mirrors the pins). The socket bodies are fitted on the underside.

**J1**

| Pin | Net | | Pin | Net |
|---|---|---|---|---|
| 1 | `CM4_3V3` | | 2 | `5V_MAIN` |
| 3 | `SW_REMOTE` | | 4 | `5V_MAIN` |
| 5 | `WDT_EN` | | 6 | GND |
| 7 | `RS232_2_TX` | | 8 | `CON_TX` |
| 9 | GND | | 10 | `CON_RX` |
| 11 | `CAN2_INT` | | 12 | `PWM_MOTA` |
| 13 | `CAN3_INT` | | 14 | GND |
| 15 | `WDT_WDI` | | 16 | `MOT_DIR` |
| 17 | `CM4_3V3` | | 18 | `STEER_EN` |
| 19 | `SPI0_MOSI` | | 20 | GND |
| 21 | `SPI0_MISO` | | 22 | `NCS_CAN3` |
| 23 | `SPI0_SCLK` | | 24 | `NCS_CAN1` |
| 25 | GND | | 26 | `NCS_CAN2` |
| 27 | `RS232_1_TX` | | 28 | `RS232_1_RX` |
| 29 | `RS232_2_RX` | | 30 | GND |
| 31 | `PIEZO_PWM` | | 32 | `GPS_TX` |
| 33 | `GPS_RX` | | 34 | GND |
| 35 | `SW_WORK` | | 36 | `CAN1_INT` |
| 37 | `NCS_ADC` | | 38 | `SW_ENGAGE` |
| 39 | GND | | 40 | `LED_DATA` |

**J2:** 1 `GLOBAL_EN`, 2 GND, 3 `RUN_PG`

**J8:** 1 GND, 2 `CON_TX`, 3 `CON_RX`

---

## 2. Power and latch

| Ref | Part # |
|---|---|
| J4, J5 | C160315 |
| Q1 | C2624 |
| Q2 | C154901 |
| Q3–Q9 | C75547 |
| U1 | C781801 |
| U2 | C7835 |
| U3 | C2870632 |
| U4 | C841192 |
| TV1, TV2 | C908801 |
| D1, D3 | C19077410 |
| D2, D5 | C8598 |
| D4 | C81598 |
| R1, R11, R12, R14 | C26083 |
| R2, R3, R5, R7, R16 | C25741 |
| R4, R17 | C25924 |
| R6 | C25779 |
| R8 | C25792 |
| R9 | C11702 |
| R10 | C2998080 |
| R13 | C11693 |
| R15 | C2933065 |
| C1, C2 | C13585 |
| C3 | C28323 |
| C4, C12 | C96446 |
| C5, C7, C14 | C15195 |
| C6, C9, C10, C11 | C1525 |
| C8 | C59782 |
| C13 | C23630 |
| C15, C16, C17, C18 | C15849 |
| C19 | C15850 |

| Net | Pins |
|---|---|
| `VIN` | J4.1, Q1.D |
| `VIN_PROT` | Q1.S, R1.1, D1.K, TV1.K, C1.1, C2.1, C3.1, U1.1, Q2.S, R7.1, D3.K, R12.1, R16.1 |
| `PGATE` | Q1.G, R1.2, R2.1, D1.A |
| `3V3_AON` | U1.5, C4.1, U2.5, C9.1, U3.6, C11.1, R14.1, R15.1 |
| `KEY_IN` | J3.21, TV2.K, R3.1, R5.1 |
| `KEY_SENSE` | R3.2, R4.1, C5.1, D2.A, U8.8 |
| `KEY_DIV` | R5.2, R6.1, C6.1, Q3.G, Q6.G, Q7.G |
| `PFET_G` | Q2.G, R7.2, R8.1, D3.A, C7.1 |
| `ON_N` | R8.2, Q3.D, Q4.D |
| `12V_SW` | Q2.D, C7.2, J5.1 |
| `CM4_3V3` | J1.1, J1.17, R9.1 |
| `ALIVE_R` | R9.2, D4.A |
| `HOLD_G` | D4.K, C8.1, R10.1, Q4.G, U2.2, Q8.D |
| `DEAD` | U2.4, C10.1 |
| `GEN_G` | C10.2, R11.1, Q5.G |
| `GLOBAL_EN` | Q5.D, J2.1 |
| `GEN_MID` | Q5.S, Q6.D |
| `VLV` | R12.2, R13.1, C12.1, U3.4 |
| `U3_REF` | U3.5, U3.3 |
| `LV` | U3.1, R14.2, Q8.G |
| `NKEY` | Q7.D, R15.2, C13.1, Q9.G |
| `LV_MID` | Q8.S, Q9.D |
| `VIN_SENSE` | R16.2, R17.1, C14.1, D5.A, U8.6 |
| `5V_MAIN` | U4.1, U4.3, C15.1, C16.1 |
| `+3V3` | U4.5, C17.1, C18.1, C19.1, D2.K, D5.K |
| GND | J4.2, J5.2, R2.2, TV1.A, TV2.A, C1.2, C2.2, C3.2, U1.3, U1.4, C4.2, R4.2, C5.2, R6.2, C6.2, Q3.S, Q4.S, C8.2, R10.2, U2.3, C9.2, R11.2, Q6.S, U3.2, C11.2, R13.2, C12.2, Q7.S, C13.2, Q9.S, R17.2, C14.2, U4.2, C15.2, C16.2, C17.2, C18.2, C19.2 |
| no connect | U1.2, U2.1, U4.4 |

---

## 3. CAN and ADC

| Ref | Part # |
|---|---|
| U5, U6, U7 | C5226885 |
| U8 | C179666 |
| X1 | C5203551 |
| D6, D7, D8 | C284104 |
| R18, R19 | C25744 |
| R20, R21, R22 | C2906868 |
| R23, R24, R25 | C2909315 |
| R26, R27 | C11702 |
| C20–C33 | C1525 |
| SJ1, SJ2, SJ3 | solder jumper (open) |

| Net | Pins |
|---|---|
| `SPI0_SCLK` | J1.23, U5.9, U6.9, U7.9, U8.16 |
| `SPI0_MOSI` | J1.19, U5.10, U6.10, U7.10, U8.14 |
| `SPI0_MISO` | J1.21, U5.11, U6.11, U7.11, U8.15 |
| `NCS_CAN1` | J1.24, U5.13 |
| `NCS_CAN2` | J1.26, U6.13 |
| `NCS_CAN3` | J1.22, U7.13, R18.1 |
| `NCS_ADC` | J1.37, U8.1, R19.1 |
| `CAN1_INT` | J1.36, U5.19 |
| `CAN2_INT` | J1.11, U6.19 |
| `CAN3_INT` | J1.13, U7.19 |
| `U5_TX` | U5.15, U5.23 |
| `U5_RX` | U5.16, U5.28 |
| `U6_TX` | U6.15, U6.23 |
| `U6_RX` | U6.16, U6.28 |
| `U7_TX` | U7.15, U7.23 |
| `U7_RX` | U7.16, U7.28 |
| `CAN_CLK` | X1.3, R20.1, R21.1, R22.1 |
| `CAN_CLK1` | R20.2, U5.21 |
| `CAN_CLK2` | R21.2, U6.21 |
| `CAN_CLK3` | R22.2, U7.21 |
| `CAN1_H` | U5.4, D6.1, R23.1, J3.15 |
| `CAN1_L` | U5.3, D6.2, SJ1.2, J3.14 |
| `CAN1_TERM` | R23.2, SJ1.1 |
| `CAN2_H` | U6.4, D7.1, R24.1, J3.17 |
| `CAN2_L` | U6.3, D7.2, SJ2.2, J3.16 |
| `CAN2_TERM` | R24.2, SJ2.1 |
| `CAN3_H` | U7.4, D8.1, R25.1, J3.19 |
| `CAN3_L` | U7.3, D8.2, SJ3.2, J3.18 |
| `CAN3_TERM` | R25.2, SJ3.1 |
| `WAS_IN_AA` | R26.2, C32.1, U8.4 |
| `ISENSE_IN_AA` | R27.2, C33.1, U8.5 |
| `+3V3` | U5.1, U5.14, U6.1, U6.14, U7.1, U7.14, X1.1, X1.4, U8.13, R18.2, R19.2, C20.1, C21.1, C23.1, C24.1, C26.1, C27.1, C29.1, C31.1 |
| `5V_MAIN` | U5.25, U6.25, U7.25, U8.2, C22.1, C25.1, C28.1, C30.1 |
| GND | U5.5, U5.22, U5.24, U6.5, U6.22, U6.24, U7.5, U7.22, U7.24, X1.2, D6.3, D7.3, D8.3, U8.3, U8.7, U8.9, U8.10, U8.11, U8.12, C20.2–C33.2 |
| no connect | U5, U6, U7 pins 2, 6, 7, 8, 12, 17, 18, 20, 26, 27 |

`R26.1` and `R27.1` are in section 5 (`WAS_IN`, `ISENSE_IN`). `U8.6` (`VIN_SENSE`) and `U8.8`
(`KEY_SENSE`) are in section 2.

---

## 4. Serial and GNSS

| Ref | Part # |
|---|---|
| U9 | C9378 |
| C34–C39 | C1525 |
| D9–D12 | C134948 |
| J6 | C27438 |
| J7 | C22436146 |

| Net | Pins |
|---|---|
| `RS232_1_TX` | J1.27, U9.11 |
| `RS232_1_RX` | J1.28, U9.12 |
| `RS232_2_TX` | J1.7, U9.10 |
| `RS232_2_RX` | J1.29, U9.9 |
| `RS232_1_TXD` | U9.14, D9.1, J3.23 |
| `RS232_1_RXD` | U9.13, D10.1, J3.24 |
| `RS232_2_TXD` | U9.7, D11.1, J3.25 |
| `RS232_2_RXD` | U9.8, D12.1, J3.26 |
| `SP_C1P` | U9.1, C34.1 |
| `SP_C1N` | U9.3, C34.2 |
| `SP_C2P` | U9.4, C35.1 |
| `SP_C2N` | U9.5, C35.2 |
| `SP_VP` | U9.2, C36.1 |
| `SP_VN` | U9.6, C37.1 |
| `GPS_TX` | J1.32, J6.8, J7.16 |
| `GPS_RX` | J1.33, J6.7, J7.15 |
| `+3V3` | U9.16, C38.1, C39.1 |
| `5V_MAIN` | J6.2, J7.6 |
| GND | U9.15, C36.2, C37.2, C38.2, C39.2, D9.2, D10.2, D11.2, D12.2, J6.1, J7.14, J7.17, J7.20, J7.22 |
| no connect | J6.3, J6.4, J6.5, J6.6; J7.1–5, J7.7–13, J7.18, J7.19, J7.21, J7.23–28 |

---

## 5. Field I/O

| Ref | Part # |
|---|---|
| J3 | C7431126 |
| D13, D14 | C2905646 |
| D15, D16, D17 | C283886 |
| D18 | C558418 |
| R28, R29, R30 | C11702 |
| R31, R32, R33 | C25744 |
| R34, R35, R36 | C25104 |
| C40, C41, C42 | C1525 |

**J3**

| Pin | Net | | Pin | Net |
|---|---|---|---|---|
| 1 | no connect | | 14 | `CAN1_L` |
| 2 | GND | | 15 | `CAN1_H` |
| 3 | `5V_MAIN` | | 16 | `CAN2_L` |
| 4 | `WAS_IN` | | 17 | `CAN2_H` |
| 5 | GND | | 18 | `CAN3_L` |
| 6 | `ISENSE_IN` | | 19 | `CAN3_H` |
| 7 | `SW_WORK_IN` | | 20 | GND |
| 8 | `SW_ENGAGE_IN` | | 21 | `KEY_IN` |
| 9 | `SW_REMOTE_IN` | | 22 | no connect |
| 10 | `STEER_PWM` | | 23 | `RS232_1_TXD` |
| 11 | `STEER_DIR` | | 24 | `RS232_1_RXD` |
| 12 | `STEER_EN_OUT` | | 25 | `RS232_2_TXD` |
| 13 | GND | | 26 | `RS232_2_RXD` |

| Net | Pins |
|---|---|
| `WAS_IN` | J3.4, D13.1, R26.1 |
| `ISENSE_IN` | J3.6, D14.1, R27.1 |
| `SW_WORK_IN` | J3.7, D15.K, R28.1 |
| `SW_WORK` | R28.2, R31.1, C40.1, D18.1, J1.35 |
| `SW_ENGAGE_IN` | J3.8, D16.K, R29.1 |
| `SW_ENGAGE` | R29.2, R32.1, C41.1, D18.3, J1.38 |
| `SW_REMOTE_IN` | J3.9, D17.K, R30.1 |
| `SW_REMOTE` | R30.2, R33.1, C42.1, D18.4, J1.3 |
| `PWM_MOTA` | J1.12, R34.1 |
| `STEER_PWM` | R34.2, J3.10 |
| `MOT_DIR` | J1.16, R35.1 |
| `STEER_DIR` | R35.2, J3.11 |
| `STEER_EN` | J1.18, R36.1 |
| `STEER_EN_OUT` | R36.2, J3.12 |
| `+3V3` | R31.2, R32.2, R33.2, D18.5 |
| GND | J3.2, J3.5, J3.13, J3.20, D13.2, D14.2, D15.A, D16.A, D17.A, C40.2, C41.2, C42.2, D18.2 |
| no connect | J3.1, J3.22, D18.6 |

---

## 6. Watchdog, LEDs, piezo

| Ref | Part # |
|---|---|
| U10 | C46043 |
| U11 | C7484 |
| D19–D22 | C5378721 |
| Q10 | C8545 |
| SW1 | C221889 (optional) |
| BZ1 | C76871 |
| R37 | C2906869 |
| R38, R39 | C25104 |
| R40 | C25076 |
| R41 | C25744 |
| R42 | C25117 |
| C43, C50 | C15849 |
| C44–C49 | C1525 |

| Net | Pins |
|---|---|
| `WDT_EN` | J1.5, U10.3, R37.1 |
| `WDT_WDI` | J1.15, U10.4 |
| `RUN_PG_G` | U10.1, R38.1, SW1.1, SW1.2 |
| `RUN_PG` | R38.2, J2.3 |
| `LED_DATA` | J1.40, U11.2 |
| `LED_DATA_OUT` | U11.4, R39.1 |
| `LED_DATA_IN` | R39.2, D19.1 |
| `LED1_DOUT` | D19.3, D20.1 |
| `LED2_DOUT` | D20.3, D21.1 |
| `LED3_DOUT` | D21.3, D22.1 |
| `PIEZO_PWM` | J1.31, R40.1 |
| `PZ_GATE` | R40.2, R41.1, Q10.G |
| `PZ_DRAIN` | Q10.D, BZ1.2, R42.2 |
| `+3V3` | U10.5, R37.2, C43.1, C44.1 |
| `5V_MAIN` | U11.5, C45.1, D19.2, D20.2, D21.2, D22.2, C46.1, C47.1, C48.1, C49.1, C50.1, BZ1.1, R42.1 |
| GND | U10.2, C43.2, C44.2, U11.1, U11.3, C45.2, D19.4, D20.4, D21.4, D22.4, C46.2, C47.2, C48.2, C49.2, C50.2, R41.2, Q10.S, SW1.3, SW1.4 |
| no connect | D22.3 |
