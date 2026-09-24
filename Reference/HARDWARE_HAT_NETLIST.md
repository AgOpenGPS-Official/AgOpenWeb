# AgOpenWeb HAT — Schematic Checklist

> **What this is:** a step-by-step list of every part and net to draw for the HAT in **EasyEDA
> Standard**, in drawing order. Tick each box as you go. When a netlist is exported, it gets checked
> against this list.
>
> **Sources:** `HARDWARE_HAT_OPTION.md` (the HAT design and its decisions),
> `HARDWARE_AIO_NETLIST.md` (the AiO circuits being reused), and `HARDWARE_AIO_ISSUES.md` (the S#
> fixes, **all already applied below**, so don't copy an AiO sheet without them).
>
> **Status:** written 2026-09-23, before drawing starts. Values are from the docs, not yet exported
> or built.

## 0. Conventions

- **Keep the AiO net names.** In EasyEDA, net labels connect by name, so a sheet copied from the AiO
  project hooks up on its own as long as these names exist:

  | Net | On the HAT it is… |
  |---|---|
  | `VIN` | vehicle constant 12 V, after the harness fuse |
  | `VIN_PROT` | after Q1 (reverse polarity) and TV1 |
  | `12V_SW` | **new:** after the latch's P-FET, out to the IO board's J20 |
  | `5V_MAIN` | **header pins 2 and 4**: the IO board's 5 V. No converter on the HAT. |
  | `+3V3` | U3's output (from `5V_MAIN`) |
  | `3V3_AON` | U23's output (from `VIN_PROT`): the always-on latch supply |

- **Ref designators:** AiO parts keep their AiO refs (U7, R59…), and the S8c.7 latch keeps its refs
  (Q4–Q11, R82–R93…). New HAT-only parts are numbered from **Q12 / R94 / C102 / D26**, with named
  connectors. Renumbering at the end is fine: the checklist only needs to be matched once.
- **Parts:** LCSC numbers for the new parts are in `HARDWARE_HAT_OPTION.md` §7a. AiO parts keep their
  AiO BOM numbers (`HARDWARE_AIO_BOM.md`).
- **`[ ]`** = to draw, **`[x]`** = drawn.

---

## Step 1 — Start the project and the board outline

A Pi 3 HAT footprint from the EasyEDA user library is a good starting point (the HAT mechanical spec
hasn't changed since the Pi B+), but **check it against the IO board numbers** (HAT doc §3.1) before
trusting it:

- [ ] Holes: 4 × M2.5 (2.7 mm) at **(3.5, 3.5), (61.5, 3.5), (3.5, 52.5), (61.5, 52.5)**, measured from
      the HAT's top-left corner.
- [ ] Header pin 1 at **(8.37, 4.82)**, pin 2 at (8.37, 2.28), pin 40 at (56.63, 2.28). Templates put it
      at y 4.77 / 2.23: 0.05 mm off, fine either way.
- [ ] **The header must be a female socket on the BOTTOM layer.** Templates usually have a male header
      on top. Swap the part, put it on the bottom, then re-check pin 1 in top view (EasyEDA mirrors
      bottom footprints).
- [ ] **Delete the camera/display cable slots** if the template has them.
- [ ] **Extend the outline upward by ~16 mm** for the GPS (HAT doc §3.4.0): the top edge moves from
      y 0 to y ≈ −16. Keep 3 mm corner radii.
- [ ] Right-hand extension: **not needed** unless the layout runs short (up to 25 mm, y 0–35 only,
      with the Ethernet/USB notch; HAT doc §3.3).

## Step 2 — Power in and the key/hold latch (sheet: Power)

### 2.1 Connectors

- [ ] **Recommendation, confirm before drawing: bring vehicle power in on its own connector, not the
      ribbon.**
  - The ribbon's pin 1 was the AiO's `VIN`, but one 28 AWG ribbon conductor is ~1 A, and the HAT
    passes **~2 A peak** (HAT doc §4.3).
  - **`J_PWR`**, JST-VH 2-pin (C160315): pin 1 `VIN`, pin 2 `GND`. Discrete wires run from the panel
    Deutsch connector.
  - The ribbon's pin 1 is then left unconnected (step 6).
- [ ] **`J_12V`**, JST-VH 2-pin (C160315): pin 1 `12V_SW`, pin 2 `GND`. This is the lead to the IO board's
      J20 (pin 1 = +12 V, pins 2 + 3 = GND; **not** the floppy colour convention).

### 2.2 Input protection (copy AiO §1.1 **with S1, S2 and S13 fixed**)

- [ ] **Q1** IRFR5305 (C2624): **drain (tab, pad 2) → `VIN`, source (pad 3) → `VIN_PROT`**, gate → `PGATE`.
- [ ] **R40** 100 k: `VIN_PROT` → `PGATE`.
- [ ] **R47** 10 k: `PGATE` → GND.
- [ ] **D2** BZT52C12: cathode → `VIN_PROT` (Q1 source), anode → `PGATE`.
- [ ] **TV1** SMBJ24A: cathode → `VIN_PROT`, **anode → GND** (S13: not `PGATE`).
- [ ] **C1, C2** 10 µF 50 V 1206 on `VIN_PROT`. C6/C7 (470 µF) **are not needed**: they were U1's
      hold-up, and the IO board has its own input bulk.

### 2.3 Always-on supply

- [ ] **U23** TPS7B6933QDBVR: pin 1 IN → `VIN_PROT`, pins 3 + 4 → GND, pin 5 OUT → `3V3_AON`, pin 2 NC.
- [ ] **C92** 1 µF 50 V 0805 on the input. **C93 10 µF** 0603 on the output (needs ≥ 2.2 µF *effective*).

### 2.4 Key front end (S8c.7 block A)

- [ ] **TV2** SMBJ24A: `KEY_IN` → GND.
- [ ] **R82** 100 k `KEY_IN` → `KEY_SENSE`; **R83** 8.2 k `KEY_SENSE` → GND; **C94** 10 nF `KEY_SENSE` → GND.
- [ ] **D24** B5819W: anode `KEY_SENSE`, cathode `+3V3`.
- [ ] **R84** 100 k `KEY_IN` → `KEY_DIV`; **R85** 33 k `KEY_DIV` → GND; **C95** 100 nF `KEY_DIV` → GND.
- [ ] `KEY_SENSE` goes to ADC IN4 (step 4); `KEY_IN` comes from the ribbon, pin 21 (step 6).

### 2.5 Enable OR → P-FET switch (S8c.7 block B, **HAT version**: HAT doc §4.2)

On the HAT the latch switches **12 V to the IO board**. It no longer drives U1's `EN`, so
**Q4, R86 and `OFF_G` are not drawn.**

- [ ] **Q12** DMP6023LE (C154901): **source → `VIN_PROT`, drain → `12V_SW`**, gate → `PFET_G`.
- [ ] **R94** 100 k: `PFET_G` → `VIN_PROT` (holds it off).
- [ ] **R95** 47 k: `PFET_G` → `ON_N`. At 12 V this gives V<sub>GS</sub> ≈ −8 V.
- [ ] **D26** BZT52C12: cathode → `VIN_PROT`, anode → `PFET_G`. Clamps V<sub>GS</sub> at −12 V during jump starts.
- [ ] **C102** 10 nF 50 V: `PFET_G` → `12V_SW`. Soft start, so it doesn't slam the IO board's input caps.
- [ ] **Q5** BSS138P: gate `KEY_DIV`, drain `ON_N`, source GND. The key turns it on.
- [ ] **Q6** BSS138P: gate `HOLD_G`, drain `ON_N`, source GND. A running CM4 holds it on.

### 2.6 CM4-alive hold (block C)

- [ ] **R87** 1 k: `CM4_3V3` → `ALIVE_R`. `CM4_3V3` = **header pins 1 and 17** (step 3).
- [ ] **D25** 1N4148W: anode `ALIVE_R`, cathode `HOLD_G`.
- [ ] **C96** 1 µF X7R: `HOLD_G` → GND. **R88** 2.2 M: `HOLD_G` → GND. Hold ≈ 1.2–2.6 s.

### 2.7 Restart pulse (block E)

- [ ] **U24** SN74LVC1G14DBVR: pin 2 A ← `HOLD_G`, pin 4 Y → `DEAD`, pin 5 VCC → `3V3_AON`, pin 3 GND,
      pin 1 NC.
- [ ] **C97** 100 nF on U24's VCC.
- [ ] **C98** 100 nF: `DEAD` → `GEN_G`. **R89** 1 M: `GEN_G` → GND.
- [ ] **Q7** BSS138P: gate `GEN_G`, drain **`GLOBAL_EN`**, source `GEN_MID`.
- [ ] **Q8** BSS138P: gate `KEY_DIV`, drain `GEN_MID`, source GND.
- [ ] `GLOBAL_EN` goes to J1 socket pin 1 (step 3).

### 2.8 Low-voltage cut-off (block D)

- [ ] **U25** TLV3011AIDBVR: 1 OUT → `LV`, 2 V− → GND, 3 IN+ ← REF (pin 5), 4 IN− ← `VLV`,
      6 V+ → `3V3_AON`. **C99** 100 nF on V+.
- [ ] **R90** 1 M 1% `VIN_PROT` → `VLV`; **R91** 121 k 1% `VLV` → GND; **C100** 10 µF `VLV` → GND.
      Trip point 11.5 V.
- [ ] **R92** 1 M: `LV` → `3V3_AON`.
- [ ] **Q9** BSS138P: gate `KEY_DIV`, drain `NKEY`, source GND.
- [ ] **R93** 10 M: `NKEY` → `3V3_AON`; **C101** 2.2 µF: `NKEY` → GND. Delay of 6–13 s after key-off.
- [ ] **Q10** BSS138P: gate `LV`, drain `HOLD_G`, source `LV_MID`.
- [ ] **Q11** BSS138P: gate `NKEY`, drain `LV_MID`, source GND.

### 2.9 Voltage sense (AiO §1.6)

- [ ] **R42** 100 k `VIN_PROT` → `VIN_SENSE`; **R6** 8.2 k `VIN_SENSE` → GND; **C30** 10 nF.
- [ ] **D4** B5819W: anode `VIN_SENSE`, cathode `+3V3`. `VIN_SENSE` goes to ADC IN2 (step 4).

### 2.10 Logic 3.3 V (AiO §1.4)

- [ ] **U3** RT9080-33GJ5: VIN + EN → `5V_MAIN`, VOUT → `+3V3`. **C54, C63** 1 µF in; **C55, C62**
      1 µF + **C67** 10 µF out.

## Step 3 — Pi header and J1 socket (sheet: Host)

### 3.1 `J_HDR`: 2 × 20 female, 8.5 mm (C22373925), bottom layer

| Pin | Net | | Pin | Net |
|---|---|---|---|---|
| 1 | `CM4_3V3` | | 2 | `5V_MAIN` |
| 3 | `SW_REMOTE` (GPIO2) | | 4 | `5V_MAIN` |
| 5 | `WDT_EN` (GPIO3) | | 6 | GND |
| 7 | `RS232_2_TX` (GPIO4) | | 8 | `CON_TX` (GPIO14) |
| 9 | GND | | 10 | `CON_RX` (GPIO15) |
| 11 | `CAN2_INT` (GPIO17) | | 12 | `PWM_MOTA` (GPIO18) |
| 13 | `CAN3_INT` (GPIO27) | | 14 | GND |
| 15 | `WDT_WDI` (GPIO22) | | 16 | `MOT_DIR` (GPIO23) |
| 17 | `CM4_3V3` | | 18 | `STEER_EN` (GPIO24) |
| 19 | `SPI0_MOSI` (GPIO10) | | 20 | GND |
| 21 | `SPI0_MISO` (GPIO9) | | 22 | `NCS_CAN3` (GPIO25) |
| 23 | `SPI0_SCLK` (GPIO11) | | 24 | `NCS_CAN1` (GPIO8) |
| 25 | GND | | 26 | `NCS_CAN2` (GPIO7) |
| 27 | `RS232_1_TX` (GPIO0) | | 28 | `RS232_1_RX` (GPIO1) |
| 29 | `RS232_2_RX` (GPIO5) | | 30 | GND |
| 31 | `PIEZO_PWM` (GPIO6) | | 32 | `GPS_TX` (GPIO12) |
| 33 | `GPS_RX` (GPIO13) | | 34 | GND |
| 35 | `SW_WORK` (GPIO19) | | 36 | `CAN1_INT` (GPIO16) |
| 37 | `NCS_ADC` (GPIO26) | | 38 | `SW_ENGAGE` (GPIO20) |
| 39 | GND | | 40 | `LED_DATA` (GPIO21) |

- [ ] All 40 pins drawn as above. This includes the **S4 swap**: `LED_DATA` on GPIO21 (pin 40) and
      `SW_REMOTE` on GPIO2 (pin 3).
- [ ] **Pins 1/17 are `CM4_3V3`, not `+3V3`.** They feed only the hold circuit (R87). Don't tie them to
      U3's rail.
- [ ] **Pins 2/4 are `5V_MAIN`.** Everything that ran from the AiO's `5V_MAIN` now runs from here.

### 3.2 `J_J1`: 1 × 3 female, 8.5 mm (C22373889), bottom layer, at HAT (18.04, 32.00) — **off-grid**

- [ ] Pin 1 → `GLOBAL_EN` (Q7 drain), pin 2 → GND, pin 3 → `RUN_PG` (R81, step 7).

### 3.3 Console

- [ ] **H1** 1 × 3 (optional): 1 GND, 2 `CON_TX`, 3 `CON_RX`. The IO board has USB and HDMI for
      bring-up, so this can be left out.

## Step 4 — SPI peripherals (sheet: SPI) — copy AiO §3 **with S6**

- [ ] Shared bus: `SPI0_SCLK` / `SPI0_MOSI` / `SPI0_MISO` → U7/U8/U9 pins 9/10/11 and U21 pins 16/14/15.
- [ ] **R74** 10 k `NCS_CAN3` → `+3V3`; **R75** 10 k `NCS_ADC` → `+3V3` (GPIO25/26 default to pull-low).
- [ ] **U7/U8/U9** MCP251863T-E/SS, n = 1/2/3, each wired as:
  - VIO (1) + VDD (14) → `+3V3`; VCC (25) → `5V_MAIN`
  - **STBY (5) → GND**
  - CANL (3) → `CANn_L`; CANH (4) → `CANn_H`
  - nCS (13) → `NCS_CANn`; nINT (19) → `CANn_INT`
  - **link TXCAN (15) → TXD (23) and RXD (28) → RXCAN (16) with wires on the schematic** (nets `Un_15` / `Un_16`, as on the AiO). The package doesn't connect them internally.
  - OSC1 (21) ← `CAN_CLKn`; VSS (22) / GND (24) → GND
  - decoupling: 2 × 100 nF on `+3V3`, 1 × 100 nF on `5V_MAIN`
- [ ] **X1** 40 MHz oscillator: EN (1) + VDD (4) → `+3V3`, OUT (3) → `CAN_CLK`. **R64/R65/R66** 33 Ω
      from `CAN_CLK` to `CAN_CLK1/2/3`.
- [ ] **D5/D6/D7** NUP2105L: pins 1/2 on `CANn_H` / `CANn_L`, pin 3 → GND.
- [ ] **S6 termination:** **R9/R10/R11** 120 Ω in series with **SJ_CAN1/2/3_TERM** (solder jumper)
      across each `CANn_H` / `CANn_L`.
- [ ] **U21** ADC128S102:
  - CS (1) ← `NCS_ADC`; VA (2) → `5V_MAIN`; VD (13) → `+3V3`; AGND/DGND (3/12) → GND
  - IN0 (4) ← `WAS_IN_AA` (R72 1 k + C90 100 nF from `WAS_IN`)
  - IN1 (5) ← `ISENSE_IN_AA` (R73 1 k + C91 100 nF from `ISENSE_IN`)
  - IN2 (6) ← `VIN_SENSE`
  - **IN4 (8) ← `KEY_SENSE`**
  - **IN3 (7), IN5–IN7 (9–11) → GND.** IN3 was earmarked for the AiO eFuse's `PI_FLT`; the HAT has no eFuse.

## Step 5 — Serial and GPS (sheet: Serial) — copy AiO §4.1; GPS is new

- [ ] **U12** SP3232EEN, exactly as AiO §4.1:
  - T1IN ← `RS232_1_TX`, R1OUT → `RS232_1_RX`
  - T2IN ← `RS232_2_TX`, R2OUT → `RS232_2_RX`
  - line side `RS232_n_TXD` / `RS232_n_RXD` with **D9–D12** SMAJ12CA
  - C44–C47 100 nF; VCC → `+3V3`, C64/C66 100 nF
- [ ] **`H981`** 1 × 8 female 2.54 (C27438), the EMAX UM981:
  - 1 GND, 2 `5V_MAIN`, **7 → `GPS_RX`** (module TX1), **8 ← `GPS_TX`** (module RX1)
  - 3 (EV), 4/5 (TX2/RX2), 6 (PPS): no connection
- [ ] **`H14`** 2 × 14 female 2.0 (C22436146), the UM982EB:
  - **6 → `5V_MAIN`**; 14, 17, 20, 22 → GND
  - **15 → `GPS_RX`** (module TX1), **16 ← `GPS_TX`** (module RX1)
  - all others: no connection
- [ ] Only one GPS board is fitted at a time. Both drive `GPS_RX`, so **never fit both**: say so on
      the silkscreen.
- [ ] Standoff holes: M3 at the HAT doc §3.4.0 positions (holes A–F; A is shared).

## Step 6 — Field I/O (sheet: Field) — copy AiO §5 **with S11**

### 6.1 `J_FIELD`: 2 × 13 latched IDC header (C7431126)

Same pin numbers as the AiO's J1, so one harness serves both boards. Only pins 1 and 21 change.

| Pin | Net | | Pin | Net |
|---|---|---|---|---|
| 1 | **NC** (was `VIN`; power is on `J_PWR`) | | 14 | `CAN1_L` |
| 2 | GND | | 15 | `CAN1_H` |
| 3 | `5V_MAIN` (WAS supply) | | 16 | `CAN2_L` |
| 4 | `WAS_IN` | | 17 | `CAN2_H` |
| 5 | GND | | 18 | `CAN3_L` |
| 6 | `ISENSE_IN` | | 19 | `CAN3_H` |
| 7 | `SW_WORK_IN` | | 20 | GND |
| 8 | `SW_ENGAGE_IN` | | **21** | **`KEY_IN`** (S8c.7) |
| 9 | `SW_REMOTE_IN` | | 22 | NC |
| 10 | `STEER_PWM` | | 23 | `RS232_1_TXD` |
| 11 | `STEER_DIR` | | 24 | `RS232_1_RXD` |
| 12 | `STEER_EN_OUT` | | 25 | `RS232_2_TXD` |
| 13 | GND | | 26 | `RS232_2_RXD` |

- [ ] Drawn as above. IDC pin n = ribbon conductor n, so each CAN H/L pair is on neighbouring
      conductors.
- [ ] **WAS / current inputs:** **D13** ESD9B5V on `WAS_IN`, **D16** ESD9B5V on `ISENSE_IN`, then
      R72/R73 as in step 4.
- [ ] **Switch inputs** (contact-to-ground only, S11), each wired as:
  - line side: SMAJ16A to GND, then 1 k in series
  - logic side: 10 k to `+3V3`, 100 nF to GND, and one SRV05-4 channel
  
  | Input | Line net | TVS | Series R | Logic net | Pull-up | Cap | SRV05-4 |
  |---|---|---|---|---|---|---|---|
  | Work | `SW_WORK_IN` | D14 | R58 | `SW_WORK` | R59 | C72 | D15.1 |
  | Engage | `SW_ENGAGE_IN` | D17 | R60 | `SW_ENGAGE` | R61 | C73 | D15.3 |
  | Remote | `SW_REMOTE_IN` | D18 | R70 | `SW_REMOTE` | R69 | C83 | D15.4 |
  
  D15: pin 5 → `+3V3`, pin 2 → GND.
- [ ] **Steering outputs:** R27 330 Ω `PWM_MOTA` → `STEER_PWM`; R28 330 Ω `MOT_DIR` → `STEER_DIR`;
      R29 330 Ω `STEER_EN` → `STEER_EN_OUT`.

## Step 7 — Supervision and HMI (sheet: HMI) — copy AiO §6 **with S3, S9, S10**

- [ ] **U20** STWD100**NY**WY3F (1.6 s, S3):
  - 1 WDO → `RUN_PG_G`; 2 GND
  - 3 EN ← `WDT_EN`, with **R63** 4.7 k → `+3V3`
  - 4 WDI ← `WDT_WDI`; 5 VCC → `+3V3`, with C84 1 µF + C85 100 nF
- [ ] **R81** 330 Ω: `RUN_PG_G` → **`RUN_PG`** (S10). `RUN_PG` = J1 socket pin 3.
- [ ] **SW1** reset button: `RUN_PG_G` → GND. It sits on the far side of R81, as S10 requires. Optional,
      since the HAT covers the IO board anyway.
- [ ] **LEDs:**
  - **U22** SN74AHCT1G125: A ← `LED_DATA`, OE̅ → GND, VCC → `5V_MAIN`, Y → `LED_DATA_OUT`
  - **R71** 330 Ω → `LED_DATA_IN` → D19 DIN; chain D19 → D20 → D21 → D22 (SK6812SIDE-A), each on `5V_MAIN`
  - C86–C89 100 nF, C80 1 µF, C74 100 nF
- [ ] **Piezo:**
  - **R33** 100 Ω `PIEZO_PWM` → `PZ_GATE`; **R62** 10 k `PZ_GATE` → GND
  - **Q2** 2N7002: G `PZ_GATE`, D `PZ_DRAIN`
  - **BUZZER1** between `5V_MAIN` and `PZ_DRAIN`, with **R32** 470 Ω across it
- [ ] **Power LED (S9): not drawn.** `PI_LED_nPWR` isn't on the 40-pin header, and the IO board has
      its own power LED. Q3, R79, R80 and D23 are dropped.

## Step 8 — Not carried over from the AiO board (don't copy these)

U1 buck, L2, D1, C6/C7, C32, R1–R5, R7, R48 · U2 eFuse, R4, R43, R51, C29 · U4 NVMe buck, L3 and
its caps · U5 M.2 · L1 magjack · CN1–CN3 CM4 connectors · H3 USB/rpiboot · J1 ATS13 (replaced by
`J_FIELD`) · P2 ArduSimple RTK2B · Q3/R79/R80/D23 power LED.

## Step 9 — Before exporting

- [ ] ERC clean, apart from the deliberate no-connects on H981 / H14 / J_FIELD 1 and 22.
- [ ] Each of these nets has the expected number of pins. Export the netlist and send it for checking.

  | Net | Pins |
  |---|---|
  | `GPS_RX` | 3: header 33, H981.7, H14.15 |
  | `GPS_TX` | 3: header 32, H981.8, H14.16 |
  | `GLOBAL_EN` | 2: J_J1.1, Q7.D |
  | `RUN_PG` | 2: J_J1.3, R81 |
  | `CM4_3V3` | 3: header 1, header 17, R87 |
  | `KEY_DIV` | 6: R84, R85, C95, Q5.G, Q8.G, Q9.G |
  | `HOLD_G` | 6: D25.K, C96, R88, Q6.G, U24.A, Q10.D |
- [ ] **Bench test before trusting the latch in a tractor:** the IO board's rated minimum input is
      **7.5 V**, higher than the AiO's U1 (~5–6 V). Try a crank-dip profile on a bench supply, so
      you know whether the IO board rides it out.
