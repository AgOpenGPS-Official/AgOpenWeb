# AgOpenWeb AiO Board — BOM

> Bill of materials, organized **one section per schematic page** (major-subsystem grain, ~6–8
> pages), matching `HARDWARE_AIO_NETLIST.md`. Companion: `HARDWARE_AIO_BOARD.md` (design *why*).
> Ref designators shared across all three.
>
> Status: **Page 1 (POWER) — U1/U2/U3 stock-verified live 2026-07-04.** Discretes (Q1/D2/D3/J1)
> + catch diode D_SW pending. `TBD@layout` = value settles at layout. **JLC class matters:**
> Extended parts add a per-part handling fee — noted per row. Stock is volatile in the 2026
> shortage; re-check at order.

**Columns:** Ref · Qty · Function · MPN · JLC # · Class · Key rating · Stock (2026-07-04) · Status

---

## Page map

| Pg | Page | Status |
|---|---|---|
| **1** | POWER | **complete ✓** (incl. `+3V3_NVMe` rail U5, verified) |
| **2** | CPU (CM4) | **complete ✓** — core + Ethernet (HR911130C) + WiFi (CM4101000) verified |
| **3** | MCU (STM32G473) | **complete ✓** — pinmap allocated, crystal verified |
| **4** | CAN ×3 | **complete ✓** — TCAN1042V + NUP2105L verified |
| **5** | SERIAL / OFF-BOARD COMMS | **complete ✓** — non-iso NMEA-out; GPS multi-module slot; IMU via interfaces |
| **6** | OFF-BOARD I/O & PROTECTION | **complete ✓** — optos + ESD/TVS verified (no section FETs) |
| **7** | HMI / PANEL | **complete ✓** — reset/piezo/FET verified; OLED+connectors hand-fit (documented) |

---

## Consolidated flat BOM (all 7 pages) — verified 2026-07-04

**JLC-placed parts (SMT/assembly) — the machine-populated set.** Passive values (L/C/R) settle at
layout; qty shown per board.

| Ref | Qty | Part | JLC # | Pg |
|---|---|---|---|---|
| U1 | 1 | TPS54560DDAR (60V buck) | C31966 | 1 |
| D_SW | 1 | SS56C (catch diode) | C123948 | 1 |
| U2 | 1 | TPS259571DSGR (CM eFuse) | C471038 | 1 |
| U3 | 1 | RT9080-33GJ5 (3V3 LDO) | C841192 | 1 |
| U5 | 1 | TPS563201DDCR (M.2 3V3 buck) | C116592 | 1 |
| Q1 | 1 | IRFR5305PBF (rev-pol FET) | C2624 | 1 |
| D2 | 1 | SMBJ24A (input TVS) | C87268 | 1 |
| D3 | 1 | BZT52C12 (gate zener) | C124196 | 1 |
| J_CM_A/B | 2 | DF40C-100DS-0.4V(51) (mezzanine) | C597931 | 2 |
| J_M2 | 1 | 91302-55-067R2M (M.2 socket) | C2922444 | 2 |
| U6 | 1 | TS3USB221RSER (USB2 mux) | C130085 | 2 |
| D_PROV | 1 | USBLC6-2SC6 (USB ESD) | C7519 | 2 |
| J_RJ45 | 1 | HR911130C (GbE magjack) | C50933 | 2 |
| J_UFL | 3–4 | IPEX MHF1 U.FL socket | TBD | 2/7 |
| U4 | 1 | STM32G473RCT6 (MCU) | C529361 | 3 |
| Y1 | 1 | X32258MOB4SI (8 MHz XTAL) | C2682775 | 3 |
| U7/U8/U9 | 3 | TCAN1042VDRQ1 (CAN-FD) | C485806 | 4 |
| D_CAN1/2/3 | 3 | NUP2105L (CAN bus TVS) | C284104 | 4 |
| U10 | 1 | CA-IS3092W (iso RS-485) | C2890051 | 5 |
| U11 | 1 | SP3232EEN-L/TR (RS-232 ×2) | C9378 | 5 |
| D_NMEA/D_EXT | 2 | SMAJ12CA (RS-232 TVS) | C134948 | 5 |
| OPTO_PWM | 1 | 6N137S (fast opto) | C5123515 | 6 |
| OPTO_DIR/EN | 2 | LTV-357T (opto) | C119089 | 6 |
| D_WAS_p/D_IS | 2 | ESD9B5V (low-leak ESD) | C2905646 | 6 |
| D_SW12 | 2–3 | SMAJ16A (12V input TVS) | C283886 | 6 |
| D_SWlogic | 1 | SRV05-4 (logic ESD array) | C558418 | 6 |
| SW_RST | 1 | TS-1187A-B-A-B (reset tactile) | C318884 | 7 |
| LS_PIEZO | 1 | PS1240P02BT (piezo) | C76871 | 7 |
| Q_PIEZO | 1 | 2N7002 (piezo FET) | C8545 | 7 |
| J_OLED, headers, R/C/L passives | — | commodity (values @ layout) | Basic | all |

**Hand-fit parts (NOT JLC-placed — sourced + assembled separately):**

| Item | Part | Notes |
|---|---|---|
| CM4 module | **CM4101000** (1 GB Lite Wireless) | seats on DF40; RPi reseller |
| NVMe | 128 GB M.2 2230/2242 | boot/root/data |
| GPS module | ArduSimple / 2× F9P / UM98x | multi-module slot |
| OLED | 1.3" SH1106 I²C module | 4-pin header on board |
| XBee (opt) | Digi XBee-SMT (castellated pads) | RTK-over-radio |
| Connectors | Deutsch **DT13-2P / DTM13-06/08/12PA** | IP67, hand-soldered TH |
| Antennas | 2× SMA + 2× RP-SMA bulkheads + U.FL pigtails | back panel |

> **Extended-class note:** most active parts are Extended (automotive/60V/isolation aren't Basic) —
> Basic parts: RT9080-alt AMS1117, 2N7002, SRV05-4, most passives. Re-verify all stock at order.
> Thin-stock watch: SMAJ16A (approve 2–3 vendors), ADM3251E (DNP option only).

### Passives — consolidated by value (all JLC **Basic**; C# = Basic-library, verifying)

Grouped for capture: place one Basic part per value. `DNP` = footprint only (populate at bus-end /
option). Per-IC bypass caps fold into the 100 nF line.

| Value | Footprint | Qty | Designators | JLC # (Basic) |
|---|---|---|---|---|
| **100 nF X7R** (decoupling/bypass + SP3232 charge-pump) | 0402 | ~28 | C_BOOT, C_BOOT5, C_VDD1-4, C_VDDA2, C_VREF2, C_VBAT, C_NRST, C_CAN1-3, C_AC1-2, C_RST, C_SP1-4, +per-IC bypass | **C1525** |
| **1 µF X5R 50 V** | 0603 | 5 | C_VDDA1, C_VREF1, C_L1, C_L2, C_E1 | **C15849** |
| **10 µF 25 V** (X5R) | 0805 | 4 | C_IN2×2, C_N1×2 | **C15850** |
| **22 µF 25 V** (X5R) | 0805 | 5 | C_OUT2×2, C_N2×2, C_M2_bulk | **C45783** |
| **4.7 µF** (X5R) | 0603 | 1 | C_BULK | **C19666** |
| **47 µF 50 V electrolytic** | SMD D6.3 | 2 | C_IN1 (50 V), C_OUT1 (16 V) | **C3349** (verify) |
| **15 pF C0G** | 0402 | 2 | CL1, CL2 | **C1548** |
| **10 nF X7R** | 0402 | 3 | C_S1, C_WAS, C_CS | **C15195** |
| **4.7 nF X7R** `DNP` | 0402 | 3 | C_SPLIT1-3 | **C1538** |
| **1 nF 2 kV** ⚠ **Extended** | 1206 | 1 | C_BS (Bob Smith) | **C9196** |
| **100 kΩ** | 0402 | ~8 | R1, R_PG, R_FLT, R_S1, R_PZP, +CM straps | **C25741** |
| **10 kΩ** | 0402 | 5 | R2, R_FB2, R_N2, R_RST, R_CS | **C25744** |
| **8.2 kΩ** | 0402 | 1 | R_S2 | **C25924** |
| **6.8 kΩ 1%** | 0402 | 1 | R_WAS1 (÷ w/ R_WAS2=10k) | **C25917** |
| **4.7 kΩ** | 0402 | 2 | R_OLED1-2 (I²C pull-up) | **C25900** |
| **52.3 kΩ 1%** | 0402 | 1 | R_FB1 (buck FB → 5.0 V) | **C26982** (verify) |
| **33 kΩ 1%** | 0402 | 1 | R_N1 (M.2 buck FB) | **C25779** |
| **1 kΩ** | 0402 | 3 | R_LED, LED_ACT/PWR R | **C11702** |
| **470 Ω** | 0402 | 1 | R_PZD (piezo damp) | **C25117** |
| **330 Ω** | 0402 | 3 | R_OPTO1-3 (opto LED series) | **C25104** |
| **100 Ω** | 0402 | 1 | R_PZG (piezo gate) | **C25076** |
| **120 Ω** `DNP` | 0402 | 1 | R_T485 (RS-485 term) | **C25079** |
| **75 Ω 1%** | 0402 | 4 | R_BS1-4 (Bob Smith) | **C25133** |
| **60.4 Ω 1%** `DNP` | 0402 | 6 | R_TERM1-6 (CAN split-term) | **C137954** (Extended; C25844 was wrong) |
| **0 Ω** `DNP` | 0402 | 3 | R_OSC, R_USBP, R_USBM | **C17168** |
| **3.3 µH** | SWPA4030 | 1 | L5 (M.2 buck) | **C15269** (3.6 A Isat — OK if NVMe <3 A; ≥5 A = Extended) |
| **Ferrite 600 Ω@100 MHz** | 0603 | 1 | FB1 (VDDA) | **C1002** (0805 = C1017) |
| **B5819W Schottky** (BAT54 = Extended) | SOD-123 | 1 | D_S1 (VIN_SENSE clamp) | **C8598** |
| **LED green / red** | 0603 | 3 | LED_ST, LED_ACT, LED_PWR | **C72043 / C2286** |
| **CAN CMC ~51 µH** `DNP` | — | 3 | L_CM1-3 | at order (Extended) |

> **Passive notes:** all **Basic** except **1 nF 2 kV (C9196, Extended)** — the one HV Bob-Smith cap
> (unavoidable). Bulk caps 10/22/4.7 µF are **X5R** not X7R (fine for decoupling/bulk). **BAT54 single
> is Extended → use B5819W (C8598)** Basic Schottky for the ADC clamp. **L5 3.3 µH**: C15269 Basic is
> 3.6 A Isat — fine at real NVMe draw (~1–1.5 A); a true ≥5 A part is Extended. Verify C3349 / C26982 /
> C25844 class at upload.

**Former "TBD@layout" 8 — now RESOLVED (datasheet-locked 2026-07-04):**

| Ref | Locked value | JLC # |
|---|---|---|
| R_RT | 243 kΩ (→400 kHz) | C43249 |
| L1 | 6.8 µH, Isat 9.6 A | C408485 |
| C_SS | **deleted** — internal SS | — |
| R_EN1/R_EN2 | 442 k / 90.9 k | C273339 / C26989 |
| R_ILIM | 487 Ω (→4.17 A) | C3015778 |
| F1 | 3 A slow-blow (blade holder) | hand-fit |
| C_HU | 2×470 µF/50 V | C90272 |
| R_EN5 | 10 kΩ | C25744 |

*Datasheet pass also added:* Rc 16.9 k (C25858), Cc1 4.7 nF (C1538), Cc2 47 pF (C1567), C_dVdt 10 nF
(C15195), R_ENPD 100 k (C25741); and corrected C_OUT→3×47 µF/10 V (C96123), C_IN→10 µF/**50 V** (C13585).
Compensation (Rc/Cc1/Cc2) is locked to Cout=3×47 µF — recompute if Cout changes.

---

## PAGE 1 — POWER

### 1.1 Input protection (12 V system, 40 V operating)

| Ref | Qty | Function | MPN | JLC # | Class | Rating | Stock | Status |
|---|---|---|---|---|---|---|---|---|
| **J1** | 1 | Power-in connector | **DT13-2P** (Deutsch, hand-fit) | — | — | IP67, DT-series | — | per connector decision (Page 7); JLC-placed alt = screw-term WJ500V **C8465** (field-wireable) |
| F1 | 1 | Input fuse **3 A slow-blow** | blade holder + 3 A blade (hand-fit) | — | — | ~1.2 A steady + inrush | — | deep-stock SMD 3 A are fast (nuisance-trip); SMD-slow SF-1206S300-2 = verify C# |
| **Q1** | 1 | Reverse-pol P-FET (ideal-diode) | IRFR5305PBF | **C2624** | Extended | **55 V** DPAK, 31 A, 65 mΩ | 37,048 | ✓ verified — 55 V is correct (see note), not 100 V |
| **D2** | 1 | Input TVS (load-dump) | SMBJ24A (Brightking) | **C87268** | Extended | standoff 24 V, clamp 38.9 V | 27,840 | ✓ verified; SMBJ33 C353366 / SMBJ48 C353356 = 24 V variant |
| **D3** | 1 | Q1 gate zener clamp | BZT52C12 | **C124196** | Extended | 12 V, SOD-123 | 28,192 | ✓ verified |
| R1 | 1 | Q1 gate pull-up (S→G) | ~100 kΩ | — | — | — | — | value TBD |
| R2 | 1 | Q1 gate series | ~10 kΩ | — | — | — | — | value TBD |
| C_HU | 2 | Bulk hold-up (VIN_PROT) | 470 µF/50 V SMD ROQANG ×2 | **C90272** | Extended | **≥50 V** | 41,105 | ≈940 µF; 1000 µF/50 V SMD unstocked → 2×470; ride-through not shutdown-reservoir |

> **Q1 = 55 V, not 100 V (corrected).** The reverse-polarity FET is *on* during a forward
> load-dump (source≈drain≈39 V clamp, Vds≈0) — it never has the clamp across it. It only *blocks*
> reverse battery (~14 V nom, ~24 V reverse-jump) when off, so 55 V is ample margin. Insisting on
> 100 V forced either a TO-220 through-hole part (breaks all-SMT) or an out-of-stock SMD part —
> for zero benefit. IRFR5305 (55 V DPAK, SMD, 37 k stock) is the right call.

### 1.2 Main buck — U1 verified ✓

| Ref | Qty | Function | MPN | JLC # | Class | Rating | Stock | Status |
|---|---|---|---|---|---|---|---|---|
| **U1** | 1 | Buck 12 V→5 V (**async**, ext. diode) | **TPS54560DDAR** | **C31966** | Extended | 60 V in, 5 A | **50,490** | ✓ **verified** |
| U1-alt | — | Lower-cost 3.5 A alt | TPS54360BDDAR | C524806 | Extended | 60 V, 3.5 A | 30,749 | alternate if 3.5 A covers budget |
| U1-2src | — | Pin-compat 2nd source | TPS54560DDAR (Tokmas) | C7420431 | Extended | 60 V, 5 A | 1,149 ⚠ | drop-in, thin stock |
| **D_SW** | 1 | Buck catch diode (SW→GND) | SS56C | **C123948** | Extended | 60 V, 5 A, SMC | 5,081 | ✓ verified — SMC for thermal margin (~2.9 A avg); SMB SS56 C402303 alt |
| L1 | 1 | Buck inductor | 6.8 µH Sunlord MWSA1004S-6R8MT | **C408485** | Extended | Isat 9.6 A / Irms 7 A | 1,556 | ✓ datasheet-locked (fsw 400 kHz) |
| C_IN1 | 1 | Buck input bulk | 470 µF/50 V SMD (ROQANG) | **C90272** | Extended | ≥50 V | 41,105 | shared w/ C_HU part |
| C_IN2 | 2 | Buck input ceramic | 10 µF/50 V X5R 1206 | **C13585** | Basic | **≥50 V** | 1.7 M | ✓ 50 V (was 25 V — corrected) |
| C_OUT | 3 | Buck output ceramic | 47 µF/10 V X5R 1206 | **C96123** | Basic | ≥10 V | 555 k | ✓ 3×47 µF (2×22 µF was inadequate) |
| C_BOOT | 1 | Bootstrap | 0.1 µF/16 V | — | Basic | — | — | (=100 nF C1525) |
| ~~C_SS~~ | 0 | ~~Soft-start~~ | — | — | — | — | — | **removed** — TPS54560 SS is internal (2.56 ms fixed) |
| R_FB1/R_FB2 | 2 | Feedback divider (0.8 V → 5.0 V) | 52.3 k / 10 k 1 % | C26982 / C25744 | Ext/Basic | — | — | → 4.98 V (datasheet-verified) |
| R_EN1/R_EN2 | 2 | EN/UVLO divider (start 6.5 V/stop 5.0 V) | 442 k / 90.9 k 1 % | **C273339 / C26989** | Extended | — | 11 k / 32 k | ✓ datasheet-locked |
| Rc | 1 | COMP resistor | 16.9 kΩ 1 % | **C25858** | Extended | — | 56 k | ✓ comp network (was missing) |
| Cc1 | 1 | COMP series cap | 4.7 nF | **C1538** | Basic | — | — | (reuse split-term value) |
| Cc2 | 1 | COMP parallel cap | 47 pF C0G | **C1567** | Basic | — | 805 k | ✓ required |
| R_PG | 1 | PWRGD pull-up → +3V3 | ~100 kΩ | C25741 | Extended | — | — | optional STM sense |

### 1.3 CM power gate (eFuse) — U2 verified ✓

| Ref | Qty | Function | MPN | JLC # | Class | Rating | Stock | Status |
|---|---|---|---|---|---|---|---|---|
| **U2** | 1 | eFuse: EN + soft-start + OCP + FLT | **TPS259571DSGR** | **C471038** | Extended | 4 A, 2.7–18 V, adj ILIM | **1,937** | ✓ **verified** — primary |
| U2-alt | — | Footprint-compat family alternates | TPS259541 / 259530 / (259530-fam) | C2155673 / C2155775 / C1849463 | Extended | 4 A | 1,041 / 984 / — | list as BOM alternates (de-risk) |
| U2-alt2 | — | No-OCP load-switch fallback | TPS22965NDSGR | C1848403 | Extended | 6 A, soft-start only | 788 ⚠ | only w/ external protection |
| R_ILIM | 1 | Current-limit set (→4.17 A) | 487 Ω 1 % | **C3015778** | Extended | — | 16 k | ✓ datasheet floor (4.5 A not achievable) |
| C_dVdt | 1 | Inrush soft-start (dVdt→GND) | 10 nF | **C15195** | Basic | ≥10 V | — | ✓ added — CM4 inrush control |
| R_ENPD | 1 | EN pulldown (default-off) | 100 kΩ | C25741 | Extended | — | — | EN driven by **PI_PWR_EN** (STM); pulldown keeps CM off until asserted |
| R_FLT | 1 | FLT pull-up → +3V3 | 10 kΩ | C25744 | Basic | — | — | fault to STM |
| C_E1 | 1 | eFuse input cap | 1 µF 0603 | C15849 | Basic | ≥10 V | — | — |
| C_E2 | 1 | eFuse output cap | 10 µF | — | Basic | ≥10 V | — | — |

### 1.4 Logic LDO (3.3 V) — U3 verified ✓

| Ref | Qty | Function | MPN | JLC # | Class | Rating | Stock | Status |
|---|---|---|---|---|---|---|---|---|
| **U3** | 1 | 3.3 V LDO | **RT9080-33GJ5** | **C841192** | Extended | 600 mA, 2 µA Iq, EN | **84,182** | ✓ **verified** — primary (best stock) |
| U3-alt | — | Equal-spec alternate | AP2112K-3.3TRG1 | C51118 | Extended | 600 mA, EN | 40,681 | your original candidate |
| U3-basic | — | Basic-tier no-fee fallback | AMS1117-3.3 | C6186 | **Basic** | 1 A, no EN | 1,758,509 | if avoiding Extended fee; higher Iq/heat, no EN (we tie EN hi anyway) |
| C_L1 | 1 | LDO input cap | 1 µF | — | — | ≥10 V | — | — |
| C_L2 | 1 | LDO output cap | 1 µF | — | — | ≥10 V | — | — |

### 1.5 Power-loss sense

| Ref | Qty | Function | MPN | JLC # | Class | Rating | Stock | Status |
|---|---|---|---|---|---|---|---|---|
| R_S1 | 1 | VIN_SENSE divider top | ~100 kΩ | — | — | 1 % | — | ~40 V → ≤3.0 V |
| R_S2 | 1 | VIN_SENSE divider bottom | ~8.2 kΩ | — | — | 1 % | — | pair to R_S1 |
| C_S1 | 1 | Sense RC filter | ~10 nF | — | — | — | — | — |
| D_S1 | 1 | Clamp to +3V3 | BAT-type Schottky | TBD | — | — | — | ADC protect |

> **Extended-class note:** U1, U2, U3 are all Extended (only the AMS1117 LDO fallback is Basic).
> No Basic option exists for a 60 V buck or a 4 A eFuse in JLC's library — the Extended per-part
> fee on this page is inherent, not avoidable.

### 1.6 M.2 NVMe rail (added — surfaced during Page 2)

| Ref | Qty | Function | MPN | JLC # | Class | Rating | Stock | Status |
|---|---|---|---|---|---|---|---|---|
| **U5** | 1 | 5 V→3.3 V/3 A sync buck | TPS563201DDCR | **C116592** | Extended | 4.5–17 Vin, 3.3 V/3 A, EN | 108,182 | ✓ verified |
| L5 | 1 | Buck inductor | 3.3 µH | TBD | Basic | Isat ≥ 5 A | — | 580 kHz sweet spot |
| C_N1 | 2 | Input cap | 10 µF X7R 0805 | TBD | Basic | ≥25 V | — | ×2 |
| C_N2 | 2 | Output cap | 22 µF X7R 0805 | TBD | Basic | ≥16 V | — | ×2 (D-CAP2 needs bulk) |
| R_N1/R_N2 | 2 | FB divider (→3.30 V) | 33 k / 10 k 1 % | TBD | Basic | — | — | Vref 0.768 V |
| C_BOOT5 | 1 | Bootstrap | 0.1 µF | TBD | Basic | — | — | if VBST exposed |
| R_EN5 | 1 | EN pull-up to 5V_CM | 10 kΩ | **C25744** | Basic | — | — | ✓ enables w/ CM (datasheet-verified) |

> **M.2 is 3.3 V-only** and pulls ~1–1.5 A → dedicated buck (not the U3 logic LDO). `EN5` tied to
> `5V_CM` so the SSD powers only with the CM (PCIe host). This rail was missed in the first Page-1
> pass and caught while drafting Page 2 — exactly what stock-as-we-go is for.

---

## PAGE 3 — MCU (STM32G473RCT6, LQFP64)

### 3.0 MCU + clock

| Ref | Qty | Function | MPN | JLC # | Class | Rating | Stock | Status |
|---|---|---|---|---|---|---|---|---|
| **U4** | 1 | MCU | STM32G473RCT6 | **C529361** | Extended | LQFP64, 3× FDCAN | ~2,243 | ✓ verified §6 |
| **Y1** | 1 | HSE crystal 8 MHz | YXC X32258MOB4SI | **C2682775** | Extended | 12 pF CL, ±10/±20 ppm, 3225 | 94,258 | ✓ verified |
| CL1/CL2 | 2 | Crystal load caps | 15 pF C0G 0402 | TBD | Basic | — | — | ✓ value set (12 pF CL, ~4 pF stray) |
| R_OSC | 1 | OSC_OUT drive-limit | 0 Ω / DNP | — | — | — | — | optional |

### 3.1 Core power & decoupling

| Ref | Qty | Function | MPN | JLC # | Class | Rating | Stock | Status |
|---|---|---|---|---|---|---|---|---|
| C_VDD1..4 | 4 | Per-VDD-pin decoupling | 100 nF X7R 0402 | TBD | Basic | ≥16 V | — | VDD pins 16/32/48/64 |
| C_BULK | 1 | Bulk on +3V3 | 4.7–10 µF X7R | TBD | Basic | ≥10 V | — | — |
| FB1 | 1 | VDDA ferrite isolation | ferrite bead ~600 Ω@100 MHz | TBD | Basic | — | — | +3V3 → +3V3A |
| C_VDDA1/2 | 2 | VDDA decoupling | 1 µF + 100 nF | TBD | Basic | ≥10 V | — | — |
| C_VREF1/2 | 2 | VREF+ decoupling | 1 µF + 100 nF | TBD | Basic | ≥10 V | — | tied to +3V3A |
| C_VBAT | 1 | VBAT local cap | 100 nF | TBD | Basic | ≥10 V | — | VBAT tied to +3V3 |

### 3.2 Reset / BOOT0 / USB link / SWD / status

| Ref | Qty | Function | MPN | JLC # | Class | Rating | Stock | Status |
|---|---|---|---|---|---|---|---|---|
| C_NRST | 1 | Reset cap | 100 nF | TBD | Basic | ≥10 V | — | internal pull-up |
| ~~R_BOOT0 / JP_BOOT~~ | 0 | ~~BOOT0 network~~ | — | — | — | — | — | **removed** — PB8=FDCAN1_RX; BOOT0 via option bits `nSWBOOT0=0/nBOOT0=1`; DFU is software-triggered |
| R_USBP/R_USBM | 2 | USB series (to CM) | **DNP** (footprint only) | — | — | — | — | FS link; series R hurts eye — omit |
| J_SWD | 1 | SWD header/pads | 2.54 mm / tag-connect | TBD | — | — | — | PA13/PA14 |
| LED_ST | 1 | Status LED | 0603 | TBD | Basic | — | — | on a GPIO |
| R_LED | 1 | LED resistor | ~1 kΩ | TBD | Basic | — | — | — |

### 3.3 ADC front-end conditioning

| Ref | Qty | Function | MPN | JLC # | Class | Rating | Stock | Status |
|---|---|---|---|---|---|---|---|---|
| R_WAS1/R_WAS2 | 2 | WAS 5 V→~3.0 V divider | 1 % | TBD | Basic | — | — | WAS = 5 V ratiometric (AiO); sensor on stable 5V_MAIN |
| C_WAS | 1 | WAS anti-alias | ~10 nF | TBD | Basic | — | — | RC with R_WAS2 |
| D_WAS | 0 | WAS ADC clamp | — | — | — | — | — | **DNP** — covered by Page-6 ESD9B5V (don't double-clamp analog) |
| R_CS/C_CS | 2 | Current-sense filter (PA5) | 1 % / ~10 nF | TBD | Basic | — | — | **reserved, left for now** — source TBD |

> **Page 3 active parts: fully sourced ✓** — MCU (C529361) + crystal (C2682775) verified; the rest
> are Basic-tier commodity passives. Pin allocation is conflict-free (netlist §3.7). Off-board
> WAS/current TVS + connectors live on Page 6. Firmware note: BOOT0 via option bits (PB8=FDCAN1_RX).

---

## PAGE 2 — CPU (CM4 Lite)

### 2.1 Mezzanine + module

| Ref | Qty | Function | MPN | JLC # | Class | Rating | Stock | Status |
|---|---|---|---|---|---|---|---|---|
| **J_CM_A/B** | 2 | DF40 100-pin mezzanine receptacle | DF40C-100DS-0.4V(51) | **C597931** | Extended | 0.4 mm, **1.5 mm mated (CM4IO ref)** | 13,980 | ✓ verified — needs JLC assembly fixture; avoid (58) C3642394 |
| M1 (CM4) | 1 | Compute Module 4 Lite **Wireless** | **CM4101000** (1 GB) | — | — | 1 GB, no eMMC, WiFi/BT, on-module U.FL | — | ✓ SKU confirmed; hand-seated, not JLC (higher RAM = CM4102000/104000/108000) |

### 2.2 Storage (M.2 + PCIe)

| Ref | Qty | Function | MPN | JLC # | Class | Rating | Stock | Status |
|---|---|---|---|---|---|---|---|---|
| **J_M2** | 1 | M.2 M-key socket (2230/2242) | 91302-55-067R2M | **C2922444** | Extended | 5.5 mm, M-key, PCIe ×1 | 3,052 | ✓ verified (deep stock). Low-profile alt: 91302-32 **C1509730** 3.2 mm / **331 ⚠** |
| STDOFF | 1 | M.2 standoff + screw (M2) | hand-fit (NOT JLC SMT) | — | — | 2230=30 mm / 2242=42 mm | — | JLC SMT standoffs unreliable — plated M2 hole, fit post-assembly |
| C_AC1/2 | 2 | PCIe TX AC-coupling | 0.1 µF 0402 | TBD | Basic | ≥6.3 V | — | on CM4 TX pair |
| C_M2_bulk | 1 | SSD rail bulk | 22–47 µF | TBD | Basic | ≥10 V | — | near socket (current spikes) |

### 2.3 USB2 + provisioning

| Ref | Qty | Function | MPN | JLC # | Class | Rating | Stock | Status |
|---|---|---|---|---|---|---|---|---|
| **U6** | 1 | USB2 mux (STM link ↔ rpiboot) | TS3USB221RSER | **C130085** | Extended | HS USB2, DPDT 2:1, SEL+OE | 41,228 | ✓ verified; FSUSB42 C11145 2nd source |
| J_PROV | 1 | micro-USB (rpiboot) | TBD | TBD | — | USB 2.0; **VBUS = NC** | — | provisioning + recovery |
| D_PROV | 1 | USB ESD array (J_PROV D±) | USBLC6-2SC6 | **C7519** | Extended | ~1 pF, USB2 | — | ✓ external-port ESD (no series R on D±) |
| JP_RPIBOOT | 1 | nRPIBOOT jumper (+ USB_SEL) | 1×2 header | TBD | Basic | — | — | assert = rpiboot mode |
| LED_ACT/PWR | 2 | CM activity/power LEDs | 0603 + R | TBD | Basic | — | — | optional |

### 2.4 Connectivity (module network) — Ethernet + WiFi

| Ref | Qty | Function | MPN | JLC # | Class | Rating | Stock | Status |
|---|---|---|---|---|---|---|---|---|
| **J_RJ45** | 1 | RJ45 + integrated GbE magnetics | HR911130C | **C50933** | Economic/Std (THT) | 1000BASE-T, 4-pair, LEDs | ~7,400 | ✓ verified; SMT alt HR961160C C55683 |
| R_BS×4, C_BS | 5 | Bob Smith termination | 75 Ω ×4 + 1 nF/2 kV | TBD | Basic | 2 kV cap | — | ✓ values confirmed; magjack center taps |
| (antenna) | 1 | WiFi antenna: U.FL→RP-SMA pigtail + rubber-duck | MHF1 / RG-178 | — | — | 2.4/5 GHz | — | **no board RF connector** — CM4 U.FL → case RP-SMA; mechanical |

> **Page 2 = connectors + the CM4 module.** Verified: DF40 mezzanine, M.2, U5 buck, U6 mux, ESD.
> New (network decision): **RJ45 magjack + RP-SMA antenna** (agent running) and **CM4 = Lite
> Wireless variant**. Exact DF40/Ethernet routing: copy CM4IO KiCad.

---

## PAGE 4 — CAN (×3 identical channels)

Three identical channels off Page-3 FDCAN pins (CAN1=PB8/9, CAN2=PB5/6, CAN3=PB3/4). Qty below = per-board (×3).

| Ref | Qty | Function | MPN | JLC # | Class | Rating | Stock | Status |
|---|---|---|---|---|---|---|---|---|
| **U7/U8/U9** | 3 | CAN-FD transceiver | TCAN1042VDRQ1 | **C485806** | Extended | 5 V+VIO, FD 5 Mbps, ±58 V | 8,418 | ✓ verified; VCC=5V_MAIN, VIO=+3V3; HV variant pin-compat |
| D_CAN1/2/3 | 3 | CAN-bus TVS (CANH/L) | NUP2105L | **C284104** | Extended | 24 V, dual bidir | 81,800 | ✓ verified; low-cap alt PESD2CANFD24V-K **C5278878** |
| C_CAN1/2/3 | 3 | Xcvr VCC decoupling | 100 nF | TBD | Basic | — | — | — |
| R_TERM×6 | 6 | Split termination (2×60 Ω/ch) | 60 Ω 1 % | TBD | Basic | — | — | **populate at bus-end only** |
| C_SPLIT1/2/3 | 3 | Split-term cap | 4.7 nF | TBD | Basic | — | — | with R_TERM |
| L_CM1/2/3 | 3 | Common-mode choke ~51 µH | Würth 744232090 / TDK ACT45B-510 cls | TBD | — | CAN CMC | — | **DNP footprint** — fit on motor/impl buses; verify stock at order |
| J_CAN1/2/3 | 3 | Bus connector | family TBD | TBD | — | CANH/L/GND | — | screw-term / M12 / DT |

> Page 4 = one transceiver channel ×3. Active parts (U7-9 xcvr + D_CAN TVS) **verified**. Termination
> + CM-choke are populate-optional footprints. One Extended feeder covers all 3 transceivers.

---

## PAGE 5 — SERIAL / OFF-BOARD COMMS

| Ref | Qty | Function | MPN | JLC # | Class | Rating | Stock | Status |
|---|---|---|---|---|---|---|---|---|
| **U10** | 1 | Isolated RS-485 (motor) + iso pwr | CA-IS3092W | **C2890051** | Extended | iso RS-485, half-duplex | 12,211 | ✓ verified |
| **U11** | 1 | RS-232 ×2 (NMEA-out + ext) | SP3232EEN-L/TR | **C9378** | Extended | 2 drv/2 rcv, non-iso | 96,455 | ✓ verified — serves both ports |
| D_NMEA/D_EXT | 2 | RS-232 line TVS | SMAJ12CA | **C134948** | Extended | 12 V standoff, bidir | 4,658 | ✓ verified |
| D_485 | 1 | RS-485 bus TVS | SM712 / PESD-class | TBD | — | RS-485 | — | pick at layout |
| R_T485 | 1 | RS-485 term 120 Ω | 120 Ω | TBD | Basic | — | — | DNP unless bus-end |
| J_485 | 1 | RS-485 connector | screw-term | TBD | — | A/B/GND | — | — |
| J_NMEA | 1 | NMEA-out connector | DB9 / screw | TBD | — | RS-232 | — | connector TBD |
| J_EXT232 | 1 | External RS-232 (IMU/GPS) | screw / DB9 | TBD | — | RS-232 | — | — |
| **GPS slot** | 1 | Multi-module footprint | Arduino-Uno hdr + dual-F9B | TBD | — | 1×ArduSimple / 2×F9P / 1×UM98x | — | TTL UART; mirror AiO layout |
| **U11-iso** | 0 | Iso NMEA-out (POPULATE-OPT) | ADM3251E **C579198** / SP3232+ISO7721 **C366164** | — | — | iso RS-232 | 444 ⚠ | DNP footprint — for installs needing isolation |
| **XBee** | 0 | RTK-over-radio (POPULATE-OPT) | Digi XBee-SMT (castellated **pads only**) | — | — | 3.3 V, UART | — | pads not socket; UART = UART5 (single-GPS) or to GPS; antenna on back |

> **NMEA-out = non-isolated SP3232 default** (deep stock; shared chassis ground covers common case),
> with an **isolated ADM3251E/discrete footprint kept DNP** as the escape hatch. One SP3232 does both
> NMEA-out + external RS-232. IMU rides GPS/RS-232/RS-485/CAN — no dedicated part. GPS = on-board TTL
> multi-module slot, no RS-232 driver.

---

## PAGE 6 — OFF-BOARD I/O & PROTECTION

**No on-board section drivers** (external ESP32 module over the module network). Light page.

| Ref | Qty | Function | MPN | JLC # | Class | Rating | Stock | Status |
|---|---|---|---|---|---|---|---|---|
| **OPTO_PWM** | 1 | Fast opto (PWM to amp) | 6N137S | **C5123515** | Extended | 10 Mbit, SOP-8 | 10,838 | ✓ verified; 2nd ch for DIR if sign-magnitude |
| **OPTO_DIR/EN** | 2 | General opto (DIR/ENABLE) | LTV-357T | **C119089** | Extended | phototransistor, SOP-4 | 8,315 | ✓ verified |
| **D_WAS_p** | 1 | WAS analog ESD (low-leak) | ESD9B5V | **C2905646** | Extended | 5 V, ~100 nA leak, SOD-923 | 68,848 | ✓ **low-leak, NOT SMAJ** (ratiometric) |
| **D_SW12** | 2–3 | Digital-input TVS (12 V line) | SMAJ16A | **C283886** (DOWO) | Extended | 16 V standoff / 26 V clamp | 4,960 | ✓ +alt C74561 Littelfuse ⚠ thin — approve 2-3 vendors |
| **D_SWlogic** | 1 | Logic-bank ESD array | SRV05-4 | **C558418** | Extended | 5-line, SOT-23-6 | 430,337 | ✓ deepest-stock part on board |
| D_IS | 1 | Current-in ESD (low-leak) | ESD9B5V | C2905646 | Extended | 5 V | 68,848 | reserved (shares WAS part) |
| R/C nets | — | Input dividers + RC + pull-ups | commodity | TBD | Basic | — | — | at layout |
| J_SENS | 1 | Sensor connector (WAS/current/5V/GND) | family TBD | TBD | — | — | — | — |
| J_SW | 1 | Switch-input connector | family TBD | TBD | — | — | — | — |
| J_STEER | 1 | Steering-output connector (to amp) | family TBD | TBD | — | PWM/DIR/EN | — | controller-agnostic breakout |

> Page 6 active parts (opto isolation + I/O ESD/TVS) **verified**. No section FETs (external ESP32).
> Steering direct-outputs opto-isolated (amp self-powers far side → no iso-DCDC here). WAS uses a
> **low-leakage ESD** (not SMAJ) to protect the ratiometric reading. **SMAJ16A is the one thin-stock
> line — approve DOWO C283886 + Littelfuse C74561 (+ a 3rd) so PCBA can't stall.**

---

## PAGE 7 — HMI / PANEL

### 7.1 OLED status (hand-fit module)

| Ref | Qty | Function | MPN | JLC # | Class | Rating | Stock | Status |
|---|---|---|---|---|---|---|---|---|
| J_OLED | 1 | 4-pin I²C header (VCC/GND/SCL/SDA) | 2.54 mm | TBD | Basic | — | — | JLC-placed; module hand-fit |
| (OLED) | 1 | 1.3" SH1106 I²C module | hand-sourced (~35×33 mm, 0x3C) | — | — | 128×64 | — | **NOT JLC-placeable** (COG/module) |
| R_OLED1/2 | 2 | I²C pull-ups | 4.7 kΩ | TBD | Basic | — | — | on-board (module lacks them) |

### 7.2 Reset (recessed)

| Ref | Qty | Function | MPN | JLC # | Class | Rating | Stock | Status |
|---|---|---|---|---|---|---|---|---|
| **SW_RST** | 1 | Tactile (probe-actuated reset) | TS-1187A-B-A-B | **C318884** | Preferred | 5.1 mm, top-act | 1,716,049 | ✓ verified |
| R_RST/C_RST | 2 | pull-up + debounce | 10 kΩ + 100 nF | TBD | Basic | — | — | — |

### 7.3 Piezo (in-case)

| Ref | Qty | Function | MPN | JLC # | Class | Rating | Stock | Status |
|---|---|---|---|---|---|---|---|---|
| **LS_PIEZO** | 1 | Passive piezo (tones) | PS1240P02BT | **C76871** | Extended | Ø12.2 mm TH, ~70 dB | 13,239 | ✓ verified; SMD alt PKMCS0909 C910763 |
| **Q_PIEZO** | 1 | Piezo driver FET | 2N7002 | **C8545** | Basic | SOT-23 N-FET | huge | ✓ verified |
| R_PZG/R_PZP/R_PZD | 3 | gate 100 Ω + pulldn 100 k + damp ~470 Ω | 1 % | TBD | Basic | — | — | antiphase 2nd FET = +6 dB option |

### 7.4 Connectors + antenna (mostly hand-fit — see netlist §7.6)

| Ref | Qty | Function | MPN | JLC # | Class | Rating | Stock | Status |
|---|---|---|---|---|---|---|---|---|
| J_PWR/J_SENS/J_STEER/J_CAN/J_232 | 5 | Deutsch DTM13/DT13 PCB headers | DT13-2P / DTM13-08/12PA | — | — | IP67, TH | — | **hand-solder** (not JLC); or Micro-Fit C122400 JLC-placed |
| J_UFL ×3–4 | 3–4 | Board-side U.FL socket | IPEX MHF1 | TBD | — | RF | — | **JLC-placeable**; pigtail to bulkhead |
| antenna bulkheads + pigtails | 4 | 2× SMA + 2× RP-SMA + U.FL pigtails | — | — | — | 2.4/5 GHz, GPS | — | hand-fit mechanical |

> **Page 7 JLC-places:** tactile (C318884), piezo (C76871) + FET (C8545), headers, U.FL sockets,
> pull-ups. **Hand-fit:** OLED module, Deutsch connectors, antenna bulkheads/pigtails. Consistent
> with the board's hand-fit set (CM4, GPS module, XBee, drives).

---

## Verified vs pending (Page 1)

| Item | Part | JLC # | State |
|---|---|---|---|
| U1 buck | TPS54560DDAR | C31966 | ✓ verified 50 k stock |
| U2 eFuse | TPS259571DSGR | C471038 | ✓ verified 1.9 k stock (+alternates) |
| U3 LDO | RT9080-33GJ5 | C841192 | ✓ verified 84 k stock |
| Q1 rev-pol FET | IRFR5305PBF (55 V DPAK) | C2624 | ✓ verified 37 k stock |
| D2 TVS | SMBJ24A (Brightking) | C87268 | ✓ verified 28 k stock |
| D3 gate zener | BZT52C12 | C124196 | ✓ verified 28 k stock |
| J1 connector | WJ500V-5.08-2P screw term | C8465 | ✓ verified 293 k stock |
| D_SW catch diode | SS56C (SMC, 60 V/5 A) | C123948 | ✓ verified 5 k stock |
| F1 fuse, C_HU, L1, passives | assorted | — | `TBD@layout` |

**Page 1 active parts: fully sourced ✓** — all ICs + discretes stock-verified 2026-07-04.
Remaining items are `TBD@layout` commodity passives (fuse, hold-up cap, inductor, feedback/EN
resistors, decoupling), not stock risks.
