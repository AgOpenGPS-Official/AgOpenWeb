# AgOpenWeb AiO Board — Netlist (pre-capture)

> Human-readable net list, organized **one section per schematic page** (major-subsystem grain,
> ~6–8 pages for a board this size). This is *not* an EDA-exported netlist — it's the connection
> intent we lock before capture. Pages are tied by **global net labels**, not wires. Nodes are
> `RefDes.Pin`. Companion docs: **`HARDWARE_AIO_BOARD.md`** (design *why*) and
> **`HARDWARE_AIO_BOM.md`** (parts + JLC stock). Ref designators shared across all three.
>
> Status: **Page 1 (POWER) first pass, 2026-07-04.** JLC stock verified live where noted;
> `TBD@layout` = value settles at layout/thermal.

---

## Schematic page map (= BOM sheet = netlist section)

| Pg | Page | Contents | Owns (produces) | Consumes |
|---|---|---|---|---|
| **1** | **POWER** | J1 in, protection (Q1/D2/F1/C_HU), U1 buck, U2 CM eFuse, U3 3V3 LDO, VIN_SENSE | VIN, VIN_PROT, 5V_MAIN, 5V_CM, +3V3, VIN_SENSE | — |
| **2** | **CPU (CM4)** | DF40 ×2 mezzanine, CM power/GPIO, NVMe M.2 + PCIe, nRPIBOOT + USB-OTG | +3V3_CM, PCIe pairs | 5V_CM |
| **3** | **MCU (STM32G473)** | MCU + decoupling, HSE clock, BOOT0/SWD/reset, USB link to CM, FDCAN mux | PI_PWR_EN, CM-USB, UART nets | +3V3, VIN_SENSE, PI_FLT |
| **4** | **CAN** | 3× FDCAN transceiver + TVS + termination | CAN{1,2,3}\_{H,L} | +3V3, STM FDCAN pins |
| **5** | **SERIAL / OFF-BOARD COMMS** | ISO RS-485 (motor), RS-232 ×2 (NMEA + ext), GPS multi-module slot, **XBee radio (opt)** | field serial | +3V3, STM UART nets |
| **6** | **OFF-BOARD I/O & PROTECTION** | WAS/current entry, switch inputs, opto steering outputs, TVS (no section FETs) | sensor/steer | +3V3, STM GPIO/ADC |
| **7** | **HMI / PANEL** | OLED (STM I²C), recessed reset, in-case piezo, front/back panel, antenna bulkheads | HMI | +3V3, STM I²C/GPIO/PWM |

### Global net labels (the cross-page tie)

`GND`, `VIN`, `VIN_PROT`, `5V_MAIN`, `5V_CM`, `+3V3`, `+3V3_CM`, `VIN_SENSE`, `PI_PWR_EN`,
`PI_FLT`, `USB_CM_D+`, `USB_CM_D-`, `CAN1_H/L`, `CAN2_H/L`, `CAN3_H/L`, + per-link STM
UART nets (named on Page 5). Every page is a producer/consumer of these; no page-to-page wires.

---

## Voltage domains

| Net | Domain | Source | Always-on? | Nominal | Abs-max design point |
|---|---|---|---|---|---|
| `VIN` | vehicle 12 V system | J1.1 | yes (vehicle-switched) | 12–14.4 V | jump-start 24 V |
| `VIN_PROT` | after rev-pol + TVS | Q1.D | yes | 12–14.4 V | TVS clamp ~39 V |
| `SW1` | buck switch node | U1.SW | — | — | — |
| `5V_MAIN` | main 5 V rail | U1 out | **yes** | 5.0 V | — |
| `5V_CM` | switched 5 V to CM4 | U2 (eFuse) out | no (STM-gated) | 5.0 V | — |
| `+3V3` | STM + logic-side rail | U3 (LDO) out | yes | 3.3 V | — |
| `+3V3_NVMe` | M.2 NVMe SSD rail | U5 (buck) out | no (enabled with CM) | 3.3 V | — |
| `+3V3_CM` | CM4-supplied ref | CM4 module | follows CM | 3.3 V | — |
| iso field rails | per-link isolated | inside iso parts | per-link | 3.3/5 V | integrated iso-DCDC |

> **24 V-native populate variant (not default):** raise D2 to SMBJ33/48-class, keep the 60 V buck,
> re-check the `VIN_SENSE` divider ratio. Footprints stay variant-ready; default BOM is 12 V.

---

## PAGE 1 — POWER

### 1.1 Input protection  `VIN → VIN_PROT`

| Net | Nodes | Notes |
|---|---|---|
| `VIN` | J1.1, Q1.S | Vehicle +12 V in (J1 = Deutsch DT13-2P). **Fuse = external in-line holder in the harness (user-supplied), like the AiO — NOT on board.** |
| `GND` | J1.2, (board GND plane) | Vehicle return / board ground |
| `VIN_PROT` | Q1.D, D2.1(cathode), C_HU.+, U1.VIN, R_S1.1 | Reverse-protected, TVS-clamped bus → buck + hold-up + sense |
| `PGATE` | Q1.G, R1.(a), R2.(a), D3.(cathode) | Q1 gate: R1 = S→G pull-up, R2/D3 zener clamp G→GND |

- **Q1** = P-channel MOSFET, ideal-diode reverse-polarity (S=`VIN_F`, D=`VIN_PROT`, G=`PGATE`).
  **IRFR5305PBF (C2624), 55 V DPAK.** 55 V is ample: the FET is *on* during a forward load-dump
  (source≈drain≈39 V clamp, Vds≈0), so it never has the clamp across it — it only *blocks* reverse
  battery (~14 V nom, ~24 V reverse-jump) when off. A 100 V spec (earlier note) was wrong and
  forced TO-220/out-of-stock parts; 55 V SMD is the right call.
- **D2** = unidirectional TVS, cathode `VIN_PROT`, anode `GND`. **SMBJ24A (C87268).** Standoff 24 V
  (survives 24 V jump-start), clamps 38.9 V < buck 60 V rating. Placed *after* Q1 so a reverse
  connection doesn't forward-bias it. SMBJ33/48 for the 24 V variant.
- **D3** = gate-clamp zener ~12 V. **BZT52C12 (C124196), SOD-123.**
- **Fuse** = **external in-line blade holder in the harness (user-supplied)**, sized to buck peak +
  inrush — NOT on the board (JLC is thin on holders; same approach as the AiO). No F1 footprint.
- **C_HU** = bulk hold-up on `VIN_PROT` (energy = ½C·(Vin²−Vdo²) ≫ energy at 5 V). Value
  `TBD@layout` — read-only root ⇒ near-instant halt, likely modest. Rating ≥ 50 V.

### 1.2 Main buck  `VIN_PROT → 5V_MAIN`  (U1 = TPS54560, 60 V, 5 A, **asynchronous**)

| Net | Nodes | Notes |
|---|---|---|
| `VIN_PROT` | U1.VIN, C_IN2.1(×2, 50 V), C_HU.+(×2 470 µF) | Buck input: 50 V ceramic + bulk/hold-up |
| `SW1` | U1.SW, L1.1, C_BOOT.1, **D_SW.cathode** | Switch node → inductor + bootstrap + **catch diode** |
| `5V_MAIN` | L1.2, C_OUT.1(×3 47 µF), R_FB1.1, U2.IN, U3.IN, U5.VIN | Main 5 V rail |
| `FB1` | R_FB1.2, R_FB2.1, U1.FB | Feedback 52.3 k/10 k → 4.98 V |
| `BOOT1` | C_BOOT.2, U1.BOOT | Bootstrap cap high side |
| `EN1` | U1.EN, R_EN1(→VIN_PROT, 442 k), R_EN2(→GND, 90.9 k) | UVLO start 6.5 V / stop 5.0 V |
| `RT1` | U1.RT/CLK, R_RT.1 (→GND, 243 k) | **freq-set → 400 kHz** |
| `COMP1` | U1.COMP, Rc.1 (16.9 k), Cc2.1 (47 pF) | **compensation** (was missing) |
| `CCMID` | Rc.2, Cc1.1 (4.7 nF) | Rc–Cc1 series mid-node |
| `GND` | U1.GND/PAD, D_SW.anode, C_IN2.−, C_OUT.−, C_HU.−, R_FB2.2, R_EN2.2, R_RT.2, Cc1.2, Cc2.2 | Return |

- **U1 = TPS54560DDAR (C31966), fsw 400 kHz** (TI reference design point). **Catch diode
  D_SW = SS56C (C123948).** Not synchronous. **No SS pin** — soft-start is internal (2.56 ms).
- **COMP network locked to C_OUT = 3×47 µF/10 V (C96123):** Rc 16.9 k / Cc1 4.7 nF / Cc2 47 pF.
  Recompute if C_OUT changes. **C_IN ceramics must be 50 V (C13585)** — 25 V dies on the clamp.
- L1 = **6.8 µH, Isat 9.6 A (MWSA1004, C408485)**; R_RT 243 k; UVLO 442 k/90.9 k — all datasheet-locked.

### 1.3 CM power gate (eFuse)  `5V_MAIN → 5V_CM`

| Net | Nodes | Notes |
|---|---|---|
| `5V_MAIN` | U2.IN, C_E1.1 (1 µF) | eFuse input |
| `5V_CM` | U2.OUT, C_E2.1 (10 µF), → CM4 +5V (Page 2) | Switched CM 5 V |
| `PI_PWR_EN` | U2.EN, R_ENPD.1 (→GND, 100 k), ← STM32 (Page 3) | **STM gates CM.** Pulldown = off until STM asserts (no float) |
| `PI_FLT` | U2.FLT(o.d.), R_FLT.1 → `+3V3` (10 k), → STM32 (Page 3) | Fault flag (OCP/thermal) |
| `ILIM` | U2.ILIM, R_ILIM.1 (→GND, 487 Ω) | Current-limit **→ 4.17 A** (487 Ω = datasheet floor) |
| `DVDT` | U2.dVdt, C_dVdt.1 (→GND, 10 nF) | **inrush soft-start** (was missing) |
| `GND` | U2.GND, C_E1.2, C_E2.2, R_ILIM.2, C_dVdt.2 | Return |

- **U2 = TPS259571 (C471038):** EN driven by STM `PI_PWR_EN` (not a UVLO divider) + 100 k pulldown.
  R_ILIM 487 Ω → 4.17 A (4.5 A not achievable). C_dVdt 10 nF paces CM4 inrush. Pick a **FLT** variant
  (not the x5/QOD variant) for the fault flag.

### 1.4 Logic LDO  `5V_MAIN → +3V3`

| Net | Nodes | Notes |
|---|---|---|
| `5V_MAIN` | U3.IN, C_L1.1 | LDO input |
| `+3V3` | U3.OUT, C_L2.1, → STM32 VDD (Page 3), CAN VIO (Page 4), pull-ups | STM + logic-side 3V3 |
| `EN3` | U3.EN → `5V_MAIN` | Always-on |
| `GND` | U3.GND, C_L1.2, C_L2.2 | Return |

- **U3** = 3.3 V LDO ~300–600 mA. Isolated field sides self-powered by iso parts (Page 5).

### 1.5 Power-loss sense  `VIN_PROT → VIN_SENSE`

| Net | Nodes | Notes |
|---|---|---|
| `VIN_PROT` | R_S1.1 | Divider top |
| `VIN_SENSE` | R_S1.2, R_S2.1, C_S1.1, D_S1.(cathode), → STM32 ADC (Page 3) | Scaled VIN |
| `+3V3` | D_S1.(clamp ref) | Clamp `VIN_SENSE` to 3V3 |
| `GND` | R_S2.2, C_S1.2 | Return |

- Divider scales ~40 V max → ≤ ~3.0 V (e.g. 100 k / 8.2 k → 3.03 V @ 40 V). STM watches slope for
  the SHUTDOWN-PGN trigger (design doc §7).

### 1.6 M.2 NVMe rail  `5V_MAIN → +3V3_NVMe`  (U5, 3.3 V / 3 A sync buck)

| Net | Nodes | Notes |
|---|---|---|
| `5V_MAIN` | U5.VIN, C_N1.1(×2) | Buck input |
| `SW5` | U5.SW, L5.1, C_BOOT5.1 | Switch node + bootstrap |
| `BOOT5` | U5.VBST, C_BOOT5.2 | Bootstrap (if DDC exposes VBST) |
| `+3V3_NVMe` | L5.2, C_N2.1(×2), R_N1.1, → M.2 3.3 V (Page 2) | SSD rail |
| `FB5` | R_N1.2, R_N2.1, U5.FB | 0.768 V ref → 33 k/10 k = 3.30 V |
| `EN5` | U5.EN, R_EN5.1 (10 k) → `5V_CM` | pull-up (C25744) — enables with CM (datasheet-verified) |
| `GND` | U5.GND, C_N1.2, C_N2.2, R_N2.2 | Return |

- **U5 = TPS563201DDCR (C116592), 4.5–17 Vin, 3.3 V/3 A sync, EN.** Added because **M.2 NVMe is
  3.3 V-only** and pulls ~1–1.5 A (peak more) — past the U3 logic LDO. Vout = 0.768·(1+33k/10k) =
  **3.30 V**. L5 = **3.3 µH** (Isat ≥ 5 A), Cin = 2×10 µF/25 V, Cout = 2×22 µF/16 V.

### Page-1 control signals crossing to STM (Page 3)

| Signal | Driver | Receiver | Purpose |
|---|---|---|---|
| `PI_PWR_EN` | STM32 GPIO out | U2.EN | Gate/sequence CM 5 V |
| `PI_FLT` | U2.FLT (o.d.) | STM32 GPIO in | CM rail fault |
| `VIN_SENSE` | divider | STM32 ADC | Power-loss detect → clean shutdown |

---

## PAGE 2 — CPU (CM4 Lite)

**Two DF40 ×100 mezzanine receptacles (J_CM_A / J_CM_B)** carry every CM4 signal. Exact DF40 pin
numbers **from the CM4 datasheet / copy the open-source CM4IO KiCad** (reference design for PCIe +
mezzanine) — not transcribed here; this page captures the *net groups* and the non-obvious choices.

### 2.1 CM4 power  (consumes `5V_CM`)

| Net | Nodes | Notes |
|---|---|---|
| `5V_CM` | J_CM.(all +5V pins), C_CM_bulk, C_CM_dec×n | CM4 input, from Page-1 eFuse U2. Multiple +5V + GND pins — bulk + per-rail decoupling |
| `GND` | J_CM.(all GND pins) | Many GND pins on DF40 |
| `+3V3_CM` | J_CM.(3V3 out) | CM-supplied 3.3 V ref only (GPIO bank ref) — not a power source |

### 2.2 PCIe ×1 → M.2  (copy CM4IO)

| Net | Nodes | Notes |
|---|---|---|
| `PCIE_RX_P/N` | J_CM.(PCIE0_RX±) → M.2.(RX±) | CM4 RX ← SSD TX |
| `PCIE_TX_P/N` | J_CM.(PCIE0_TX±) → C_ACx2 → M.2.(TX±) | **AC-coupling caps on CM4 TX pair** (0.1 µF) |
| `PCIE_CLK_P/N` | J_CM.(PCIE0_CLK±) → M.2.(REFCLK±) | 100 Ω diff, length-matched |
| `PCIE_RST_N` | J_CM.(PCIE0_PERST#) → M.2.(PERST#) | reset, CM-driven |
| `PCIE_CLKREQ_N` | J_CM.(PCIE0_CLKREQ#) → M.2.(CLKREQ#) | |
| `GND` | pair shields | 3 length-matched 100 Ω diff pairs |

- Gen2 ×1, the forgiving end of PCIe. AC caps on the TX pair (SSD side usually self-caps its TX).

### 2.3 M.2 socket  (J_M2, M-key 2230/2242; consumes `+3V3_NVMe`)

| Net | Nodes | Notes |
|---|---|---|
| `+3V3_NVMe` | J_M2.(3.3V pins), C_M2_bulk, C_M2_dec | SSD power ← Page-1 U5. Bulk near socket (NVMe current spikes) |
| PCIe pairs | J_M2.(PETp/n, PERp/n, REFCLK±) | to §2.2 |
| `PCIE_RST_N`/`CLKREQ_N` | J_M2 | from §2.2 |
| `M2_CFG` | J_M2.(CFG0-3 / PEDET) | key/config straps per M.2 spec (PEDET = ground for PCIe) |
| `GND` | J_M2.(GND) | + mounting standoff for 2230/2242 |

### 2.4 USB2 + provisioning  ⚠ **one CM4 USB2 port, shared → needs a mux**

| Net | Nodes | Notes |
|---|---|---|
| `CM_USB2_P/N` | J_CM.(USB2±) → U6 (USB2 mux) common | CM4's single native USB 2.0 |
| `USB_CM_D±` | U6.(port A) → STM32 (Page 3, host side) | **normal op:** CM = host ↔ STM = device |
| `PROV_USB_D±` | U6.(port B) → J_PROV (micro-USB) | **provisioning:** rpiboot from a host PC |
| `USB_SEL` | U6.SEL ← nRPIBOOT jumper / GPIO | jumper flips the mux |
| `GND` | — | |

- **The trap:** the CM4 has **one** SoC USB 2.0. It's needed both as **host for the STM link**
  (normal) and as **device for rpiboot** (provisioning). **Decision (confirmed): USB2 mux (U6,
  TS3USB221-class)** routes it to the STM (port A) or the external micro-USB (port B), selected by
  the nRPIBOOT jumper — automatic, robust for field recovery on a downed unit. NVMe is on PCIe, so
  USB2 isn't otherwise contended.
- **U6 = TS3USB221RSER (C130085)**, USB2-HS DPDT 2:1, SEL + OE (tie OE low = always enabled, or a
  2nd GPIO for a hard "disconnect both" during boot). FSUSB42UMX (C11145) = alt-vendor 2nd source.
- **J_PROV (external port):** **no series R on D±** (HS pair, series R degrades the eye); add
  **D_PROV = USBLC6-2SC6 (C7519)**, ~1 pF ESD array between connector and mux. **VBUS = NC** — the
  board self-powers the CM during provisioning, so don't back-drive the CM 5 V from the PC's VBUS
  (a VBUS-sense GPIO + TVS is optional). Internal STM link (port A) needs no ESD (same ground).

### 2.5 CM4 boot / control straps

| Net | Nodes | Notes |
|---|---|---|
| `nRPIBOOT` | J_CM.(nRPIBOOT), JP_RPIBOOT → GND, → `USB_SEL` | assert low = SoC mask ROM USB-device boot (rpiboot) |
| `RUN_PG` / `RUN` | J_CM.(RUN) , R pull-up | global enable / external reset of CM |
| `EEPROM_nWP` | J_CM.(EEPROM_nWP) | bootloader-EEPROM write-protect strap (leave writable for provisioning) |
| `LED_nACT`/`LED_nPWR` | J_CM.(LED pins) → LEDs | activity / power LEDs (optional) |
| `WL_nDISABLE`/`BT_nDISABLE` | J_CM, pull-ups | **wireless variant** — leave wireless/BT enabled (don't assert disable) |

- **Provisioning flow (design-doc §4):** assert `nRPIBOOT` jumper (also flips USB mux to J_PROV) →
  power on → rpiboot on PC flashes SPI EEPROM `BOOT_ORDER=…6…` + images NVMe (mass-storage-gadget)
  → remove jumper → boots NVMe over PCIe.

### 2.6 Ethernet — CM4 on-module GbE PHY → RJ45  (wired module network)

| Net | Nodes | Notes |
|---|---|---|
| `ETH_0..3_P/N` | J_CM.(ETH pairs) → J_RJ45 magnetics | 4 diff pairs, 1000BASE-T; **CM4 has the PHY on-module** |
| Bob Smith term | magjack center taps → 75 Ω ×4 → 1 nF/2 kV → chassis | common-mode termination |
| `ETH_LED_*` | J_CM.(ETH LED) → J_RJ45 LEDs | link/activity |

- Carrier only needs the **magjack + Bob Smith + RJ45** — no PHY chip. **J_RJ45 = HanRun HR911130C
  (C50933)**, gigabit integrated magnetics + LEDs (CM4IO's EDAC ref part isn't at JLC). Bob Smith =
  4×75 Ω → common node → 1 nF/2 kV → chassis. AgOpen module network (PGN-over-UDP, 192.168.5.x);
  ESP32 sections + modules connect via a switch.

### 2.7 WiFi — CM4 wireless variant + external antenna

| Net | Nodes | Notes |
|---|---|---|
| `ANT` | CM4 on-module U.FL → pigtail → **RP-SMA bulkhead** (case) | external antenna |

- **CM4 = CM4102000 (2 GB Lite Wireless)**; wireless variants have the U.FL on-module. (Same DF40
  footprint/pinout as the 1 GB CM4101000 — RAM is internal only.) **Metal
  enclosure blocks the on-module antenna → external antenna mandatory. NO board RF connector** —
  U.FL (MHF1) pigtail straight from the module to a case-mounted RP-SMA bulkhead (lowest loss,
  fewer parts). Secondary module-network path + BT.

- **J_CM_A/B = DF40C-100DS-0.4V(51) (C597931)** ✓ 1.5 mm CM4IO height, 14 k stock (needs JLC 0.4 mm
  assembly fixture). **J_M2 = 91302-55-067R2M (C2922444)** ✓ 5.5 mm deep-stock default (3.2 mm
  C1509730 low-profile alt if Z-tight). SSD standoff = hand-fit M2 (JLC SMT standoffs unreliable).

### Open items (Page 2)

- **USB2 mux U6 = TS3USB221RSER (C130085) ✓**; D_PROV = USBLC6-2SC6 (C7519) ✓. U5 (M.2 buck) =
  TPS563201DDCR (C116592) ✓.
- **Ethernet J_RJ45 = HR911130C (C50933) ✓; CM4 = CM4102000 (2 GB Lite Wireless) ✓; antenna = U.FL→RP-SMA
  pigtail (no board connector) ✓. Page 2 fully sourced.**
- Copy CM4IO KiCad for exact DF40 pin numbers + PCIe + Ethernet routing/length-match rules.
- M.2 socket Z-height (5.5 vs 3.2 mm) at layout.

## PAGE 3 — MCU (STM32G473RCT6, LQFP64)

**U4 = STM32G473RCT6 (JLC C529361, verified §6).** Consumes `+3V3`, `VIN_SENSE`, `PI_FLT`;
drives `PI_PWR_EN`, `CAN{1,2,3}`, USB to CM, and the Page-5 UART nets. Pin numbers in §3.7 pending
the LQFP64 pinmap; support networks (§3.1–3.6) are pin-name-referenced and firm.

### 3.1 Core power & decoupling  (consumes `+3V3`)

| Net | Nodes | Notes |
|---|---|---|
| `+3V3` | U4.VDD(×all), C_VDD1..n.1, C_BULK.1, U4.VBAT (via C_VBAT / tie), FB1.1 | Digital supply |
| `+3V3A` | FB1.2, U4.VDDA, C_VDDA1.1, C_VDDA2.1 | Analog supply, ferrite-isolated from `+3V3` |
| `VREF+` | U4.VREF+, C_VREF1.1, C_VREF2.1, → `+3V3A` (ratiometric tie) | ADC reference (see WAS note §3.6) |
| `GND` | U4.VSS(×all), U4.VSSA, all C.−, FB return | Return |

- **One 100 nF X7R per VDD pin** — VDD = pins **16, 32, 48, 64** (4×), placed at the pin + one bulk
  4.7–10 µF on `+3V3`. VSS = 15/31/47/63, VSSA = 27.
- **VDDA**: ferrite bead **FB1** from `+3V3` → `+3V3A`, decoupled 1 µF ∥ 100 nF (∥ 10 nF optional).
- **VREF+**: tied to `+3V3A` (ratiometric with a 3.3 V-referenced ADC), decoupled 1 µF ∥ 100 nF.
  *Not* using the internal VREFBUF (want ratiometric — §3.6).
- **VBAT**: no coin cell / RTC-backup requirement → tie `VBAT` to `+3V3` (100 nF local).

### 3.2 Clock — HSE crystal  **(LOCKED: 8 MHz HSE)**

| Net | Nodes | Notes |
|---|---|---|
| `OSC_IN` | U4.OSC_IN (PF0), Y1.1, CL1.1 | HSE crystal in |
| `OSC_OUT` | U4.OSC_OUT (PF1), Y1.(other), CL2.1, (R_OSC opt.) | HSE crystal out |
| `GND` | CL1.2, CL2.2, Y1.(case pads) | Return / shield |

- **Decision: 8 MHz HSE crystal — Y1 = YXC X32258MOB4SI (C2682775), 12 pF CL, ±10/±20 ppm.**
  Clock tree: **HSE → PLL → 170 MHz sysclk + FDCAN kernel clock**. **USB FS runs off HSI48 + CRS**
  (SOF-trimmed), *independent of the crystal* — no single PLL VCO yields both 170 MHz and 48 MHz,
  and that's fine. So the crystal is justified purely by **FDCAN bit-timing + sysclk stability**;
  8 MHz is the canonical G4 HSE (PLLM=2→4 MHz, PLLN=85→340 MHz VCO, PLLR=2→170 MHz).
- **CL1/CL2 = 15 pF C0G** (from `2·(12 − ~4 pF stray) ≈ 16` → nearest standard). Optional series
  **R_OSC** on `OSC_OUT` (0 Ω/DNP default) for drive limiting.
- No LSE (32.768 kHz) — no RTC requirement; LSI is enough for watchdog/wake.

### 3.3 Reset & BOOT0  ⚠ **BOOT0 pin = FDCAN1_RX — handled in option bits, no hardware**

| Net | Nodes | Notes |
|---|---|---|
| `NRST` | U4.PG10-NRST (pin 7), C_NRST.1, J_SWD.(rst opt) | Reset; 100 nF to GND (internal pull-up) |
| `GND` | C_NRST.2 | Return |

- **BOOT0 is the shared PB8-BOOT0 pin (pin 61) — which we use as FDCAN1_RX.** A CAN RXD idles
  **high** → sampling BOOT0 at reset would boot the **system bootloader, not firmware (brick-on-
  boot).** FDCAN1_RX has only PA11(=USB) or PB8 on this package, so the pin can't move.
- **Fix = option bits `nSWBOOT0 = 0`, `nBOOT0 = 1`** → BOOT0 taken from the option bit (always boot
  main flash), **PB8 pin level ignored** → PB8 free for CAN. **No BOOT0 resistor/jumper** (the
  earlier 10 k-pulldown network was wrong — PB8 carries CAN traffic).
- **STM firmware update:** SWD (primary/dev) + **software-triggered USB DFU** (app commands STM to
  jump to system-memory bootloader → DFU over the CM USB link). No hardware boot jumper needed.

### 3.4 USB link to CM  (produces `USB_CM_D±`)

| Net | Nodes | Notes |
|---|---|---|
| `USB_CM_D+` | U4.PA12 (USB_DP), R_USBP.1 → CM4 (Page 2) | FS D+, internal board trace |
| `USB_CM_D-` | U4.PA11 (USB_DM), R_USBM.1 → CM4 (Page 2) | FS D− |
| `GND` | — | Common (shared ground, internal link) |

- G473 FS USB has an **embedded DP pull-up** → no external 1.5 k. STM USB is **Full-Speed** (G4 has
  no HS peripheral) → **R_USBP/R_USBM = DNP by default** (footprint kept; series R is a legacy FS
  habit that hurts the eye — omit). Internal trace to the CM (via U6 mux, Page 2) → **no TVS/ESD, no
  isolation** (same ground). STM = USB **device**, CM = host.

### 3.5 SWD debug

| Net | Nodes | Notes |
|---|---|---|
| `SWDIO` | U4.PA13, J_SWD.2 | |
| `SWCLK` | U4.PA14, J_SWD.4 | |
| `NRST` | J_SWD.10 (opt.) | reset from probe |
| `+3V3` / `GND` | J_SWD.1 / J_SWD.3,5 | ref + return |

- 4–10 pin SWD header/pads (Cortex-M `+3V3`/SWDIO/GND/SWCLK/…/NRST). PA13/PA14 reserved, never
  reassigned.

### 3.6 ADC front-ends  (consumes `VIN_SENSE`; conditions WAS + current)

| Net | Nodes | Notes |
|---|---|---|
| `VIN_SENSE` | U4.ADC(pin §3.7) | already scaled/clamped on Page 1 (divider) — just the ADC pin here |
| `WAS_ADC` | U4.ADC(pin), R_WAS1/R_WAS2 divider, C_WAS filter, D_WAS clamp | wheel-angle sensor, conditioned |
| `WAS_IN` | R_WAS1.1 ← Page 6 (protected connector entry) | raw WAS from off-board sensor |
| `ISENSE_ADC` | U4.ADC(pin), R_CS/C_CS filter | motor current feedback (if amp provides analog) |

- **WAS = 5 V ratiometric** (per the AiO/Teensy design). Raw sensor enters via a **protected
  connector on Page 6** (TVS there); **divider ~5 V→3.0 V (headroom under VREF) + RC anti-alias +
  clamp on this page** at the ADC pin. **Sensor powered from a stable 5 V** (`5V_MAIN`) so the
  fixed-divider + 3.3 V-VREF path stays accurate — ratiometric error is bounded by 5V-rail
  stability, which the buck holds tight. **Resolution:** STM32G4 12-bit ADC + **hardware
  oversampling** (accumulate→16-bit) covers the AiO's 16-bit ADS1115 role without an external ADC;
  ±45° over 4096+ counts is ample for steering. Minor open: confirm oversampling depth at firmware.
- **VIN_SENSE**: conditioned on Page 1; Page 3 only routes it to an ADC channel.
- **AIN_IMOT (PA5): reserved, left as-is for now** (motor current-sense source TBD — revisit).

### 3.7 Peripheral pin allocation  **(LQFP64, conflict-free)**

> Pin **numbers** from ST DS12589 (G431 LQFP64) applied via documented G4 pin-compatibility; AF
> numbers + package bonding confirmed from the STM32G473RCTx CubeMX/Zephyr pinctrl. **Final eyeball
> vs DS12712 (G473) before tape-out.** FDCAN3 **confirmed bonded** on RCTx. FDCAN AF: 1&2 = AF9,
> 3 = AF11. USART links AF7, UART4/5 AF5, RS-485 DE = hardware USART2_RTS_DE (AF7).

| Pin | Name | Function | AF | Net |
|---|---|---|---|---|
| 1 | VBAT | supply | — | +3V3 (no coin cell) |
| 2 | PC13 | **GPIO: RESET button** | — | BTN_RST (Page 7, recessed) |
| 3 | PC14 | spare/LSE | — | — |
| 4 | PC15 | spare/LSE | — | — |
| 5 | PF0-OSC_IN | HSE in | — | OSC_IN |
| 6 | PF1-OSC_OUT | HSE out | — | OSC_OUT |
| 7 | PG10-NRST | reset | — | NRST |
| 8 | PC0 | spare (ADC/LPUART1) | — | GPIO_SPARE2 |
| 9 | PC1 | spare (ADC/LPUART1) | — | GPIO_SPARE3 |
| 10 | PC2 | spare (ADC) | — | GPIO_SPARE4 |
| 11 | PC3 | spare (ADC) | — | GPIO_SPARE5 |
| 12 | PA0 | **ADC WAS** | analog | AIN_WAS (ADC1_IN1) |
| 13 | PA1 | **RS-485 DE** | 7 | RS485_DE (USART2_RTS_DE) |
| 14 | PA2 | **USART2_TX** | 7 | UART2_TX → RS-485 (motor) |
| 15 | VSS_2 | gnd | — | GND |
| 16 | VDD_2 | +3V3 | — | +3V3 (100 nF) |
| 17 | PA3 | **USART2_RX** | 7 | UART2_RX → RS-485 (motor) |
| 18 | PA4 | **ADC VIN sense** | analog | AIN_VIN (ADC2_IN17) ← Page-1 divider |
| 19 | PA5 | **ADC motor current** | analog | AIN_IMOT (ADC2_IN13) |
| 20 | PA6 | **TIM3_CH1 PWM** | 2 | PWM_VALVE1 |
| 21 | PA7 | **TIM17_CH1 PWM: piezo** | 1 | PIEZO_PWM (Page 7) |
| 22 | PC4 | **I2C: OLED SCL** | (I2C/soft) | OLED_SCL (Page 7) |
| 23 | PC5 | **I2C: OLED SDA** | (I2C/soft) | OLED_SDA (Page 7) |
| 24 | PB0 | **GPIO STEER ENABLE** | — | STEER_EN (out) |
| 25 | PB1 | **GPIO DIR** | — | MOT_DIR (out) |
| 26 | PB2 | **GPIO work-switch** | — | SW_WORK (in) |
| 27 | VSSA | analog gnd | — | AGND (star) |
| 28 | VREF+ | ADC ref | — | VREF+ → +3V3A (ratiometric) |
| 29 | VDDA | analog supply | — | +3V3A (ferrite from +3V3) |
| 30 | PB10 | **GPIO engage-switch** | — | SW_ENGAGE (in) |
| 31 | VSS | gnd | — | GND |
| 32 | VDD | +3V3 | — | +3V3 (100 nF) |
| 33 | PB11 | spare (USART3/LPUART1/ADC) | — | GPIO_SPARE9 |
| 34 | PB12 | spare (FDCAN2 alt/LPUART1 DE) | — | GPIO_SPARE10 |
| 35 | PB13 | **GPIO status LED** | — | LED_STAT (out) |
| 36 | PB14 | spare (USART3 DE/ADC/TIM) | — | GPIO_SPARE11 |
| 37 | PB15 | spare (ADC/TIM) | — | GPIO_SPARE12 |
| 38 | PC6 | **TIM8_CH1 PWM** | 4 | PWM_MOTB |
| 39 | PC7 | spare GPIO | — | GPIO_SPARE (was PG1 — TPS54560 has no PWRGD) |
| 40 | PC8 | **GPIO PI_PWR_EN out** | — | PI_PWR_EN → Page-1 U2.EN |
| 41 | PC9 | **GPIO PI_FLT in** | — | PI_FLT ← Page-1 U2.FLT |
| 42 | PA8 | **TIM1_CH1 PWM** | 6 | PWM_MOTA |
| 43 | PA9 | **USART1_TX** | 7 | UART1_TX → NMEA-out (ADM3251E) |
| 44 | PA10 | **USART1_RX** | 7 | UART1_RX |
| 45 | PA11 | **USB_DM** | — | USB_CM_D- |
| 46 | PA12 | **USB_DP** | — | USB_CM_D+ |
| 47 | VSS | gnd | — | GND |
| 48 | VDD | +3V3 | — | +3V3 (100 nF) |
| 49 | PA13 | **SWDIO** | 0 | SWDIO |
| 50 | PA14 | **SWCLK** | 0 | SWCLK |
| 51 | PA15 | spare (FDCAN3 alt/USART2) | — | GPIO_SPARE13 |
| 52 | PC10 | **UART4_TX** | 5 | UART4_TX → GPS #1 (slot) |
| 53 | PC11 | **UART4_RX** | 5 | UART4_RX → GPS #1 (slot) |
| 54 | PC12 | **UART5_TX** | 5 | UART5_TX → GPS #2 (dual-F9P) |
| 55 | PD2 | **UART5_RX** | 5 | UART5_RX → GPS #2 (dual-F9P) |
| 56 | PB3 | **FDCAN3_RX** | 11 | CAN3_RX |
| 57 | PB4 | **FDCAN3_TX** | 11 | CAN3_TX |
| 58 | PB5 | **FDCAN2_RX** | 9 | CAN2_RX |
| 59 | PB6 | **FDCAN2_TX** | 9 | CAN2_TX |
| 60 | PB7 | **TIM4_CH2 PWM** | 2 | PWM_VALVE2 |
| 61 | PB8-BOOT0 | **FDCAN1_RX** (BOOT0 via opt-bit) | 9 | CAN1_RX ⚠ see §3.3 |
| 62 | PB9 | **FDCAN1_TX** | 9 | CAN1_TX |
| 63 | VSS | gnd | — | GND |
| 64 | VDD | +3V3 | — | +3V3 (100 nF) |

**UART link map (→ Page 5):** USART2+DE = RS-485 motor · USART1 = NMEA-out (SP3232 ch1) ·
UART4 = GPS #1 · UART5 = GPS #2 (dual-F9P) · **LPUART1 = external RS-232** (SP3232 ch2, ext IMU/GPS).
IMU arrives via GPS/RS-232/RS-485/CAN — no dedicated line. **HMI (Page 7):** OLED I²C = PC4/PC5,
reset = PC13, piezo = PA7 (TIM17). **~9 spare pins** remain for growth.

**Open items (Page 3):** WAS = 5 V ratiometric (locked) — only confirm ADC oversampling depth at
firmware; `AIN_IMOT` (PA5) reserved, current-sense source revisited later; final pin-number
cross-check vs DS12712; FDCAN1/2/3 share one message-RAM block (firmware budgeting, not pinout).

## PAGE 4 — CAN (×3 identical channels)

Three identical FDCAN channels off the Page-3 pins: **CAN1 = PB8/PB9, CAN2 = PB5/PB6, CAN3 =
PB3/PB4**. **Transceiver = TCAN1042VDRQ1 (C485806)** — 5 V VCC + VIO=3.3 V, FD 5 Mbps, ±58 V fault.
Refs: **U7/U8/U9** (xcvr), **D_CAN1/2/3** (bus TVS = NUP2105L), split-term + CM-choke
(populate-optional), **J_CAN1/2/3** (connectors). **VCC = `5V_MAIN` (always-on)** → CAN alive with
the STM even when the CM is down.

### 4.x  Per-channel template  (n = 1, 2, 3 → U7/U8/U9)

| Net | Nodes | Notes |
|---|---|---|
| `CANn_TX` | STM32 FDCANn_TX (Page 3) → Un.TXD | 3.3 V logic from MCU |
| `CANn_RX` | Un.RXD → STM32 FDCANn_RX (Page 3) | |
| `CANn_H` | Un.CANH → (L_CMn) → D_CANn.H → J_CANn.H; R_TERMn_a | bus high |
| `CANn_L` | Un.CANL → (L_CMn) → D_CANn.L → J_CANn.L; R_TERMn_b | bus low |
| `CANn_SPLIT` | R_TERMn_a.2, R_TERMn_b.2, C_SPLITn.1 | split-term midpoint (DNP unless bus-end) |
| `Un.VCC` | ← `5V_MAIN` (always-on), C_CANn 100 nF | 5 V transceiver supply |
| `Un.VIO` | ← `+3V3` | 3.3 V logic ref (TCAN1042**V**) → RXD swings 0–3.3 V |
| `Un.STB` | → GND (normal mode); GPIO option (DNP) | standby control |
| `GND` | Un.GND, C_SPLITn.2, D_CANn.gnd, J_CANn.GND | return / bus ground |

- **U7/8/9 = TCAN1042VDRQ1 (C485806)**: VCC=`5V_MAIN`, VIO=`+3V3`, TXD/RXD→FDCAN (Page 3), STB→GND.
  Footprint is **pin-identical to the ±16 kV HV variant** — lay out for V, drop in HV if stock allows.
- **D_CANn = NUP2105L (C284104)** on CANH/CANL at the connector (81 k stock, cheap). Low-cap FD
  option **PESD2CANFD24V-K (C5278878)** for the high-rate motor/implement buses if loading matters.
- **Split termination** (R_TERMn_a + R_TERMn_b = 2×60 Ω, midpoint → C_SPLITn ~4.7 nF → GND):
  **designed-in but populate at bus-ends only.** Fail-safe bias optional.
- **Common-mode choke L_CMn (~51 µH CAN CMC, DNP footprint + 0 Ω jumper alt):** populate on the
  noisy **motor/implement buses**, leave DNP on the short **ISOBUS** stub. Würth 744232090 / TDK
  ACT45B-510 class — verify JLC stock at order (CMCs come and go). **Bus order: xcvr → CMC →
  TVS+split → connector.**
- **Connector: all 3 buses on ONE combined DTM-12** (front panel) — 3×(CANH, CANL) + shared GND(s)
  ≈ 7–9 pins. Saves panel face vs 3 separate CAN connectors (Page 7 panel budget).

### Open items (Page 4)

- Connector family per bus (ISOBUS vs motor/implement) — pick at layout.
- CMC exact SKU — verify JLC stock at order (footprint is DNP-ready either way).
- STB handling: GND (always-on) default; per-channel GPIO available (~13 spare) if standby wanted.

## PAGE 5 — SERIAL / OFF-BOARD COMMS

Four links off the Page-3 UARTs. Driver stock re-verify pending (agent); ADM3251E fallback TBD.
Isolate links crossing to a foreign ground (motor RS-485, NMEA-out); GPS/IMU shared-ground.

### 5.1 Motor RS-485 (isolated) — U10 = CA-IS3092W (C2890051) ✓ 12 k stock

| Net | Nodes | Notes |
|---|---|---|
| `UART2_TX` | STM PA2 (Page 3) → U10.DI | |
| `UART2_RX` | U10.RO → STM PA3 | |
| `RS485_DE` | STM PA1 → U10.DE (+RE) | hardware USART2 DE — half-duplex direction |
| `+3V3` | U10.VCC1 (logic side), C dec | iso field side self-powered (integrated iso DC-DC) |
| `RS485_A/B` | U10.A/B → D_485 TVS → J_485; R_T485 (DNP) | isolated bus; 120 Ω term populate at bus-end |
| iso GND | U10 field-side GND2 → J_485.GND | separate ground domain |

- Bus can also carry an **external RS-485 IMU** (shared bus w/ motor driver) — user's IMU may arrive
  here, over CAN, or over the RS-232 port (§5.2).

### 5.2 NMEA-out + external RS-232 — U11 = SP3232EEN (C9378) ✓ 96 k stock  **(non-isolated default)**

One SP3232 (2 drv / 2 rcv) serves **both** ports on shared ground:

| Net | Nodes | Notes |
|---|---|---|
| `UART1_TX` | STM PA9 (Page 3) → U11.T1in | **NMEA-out** driver |
| `UART1_RX` | U11.R1out → STM PA10 | NMEA-out config-in (optional) |
| NMEA RS-232 | U11.T1out/R1in → D_NMEA (SMAJ12CA) → J_NMEA | to 3rd-party tool |
| `LPUART1_TX/RX` | STM PC0/PC1 (or PB11/10) ↔ U11 ch2 | **external RS-232 port** (external IMU/GPS) |
| ext RS-232 | U11.T2out/R2in → D_EXT (SMAJ12CA) → J_EXT232 | shared-ground external device |
| `+3V3`/5 V | U11.VCC + charge-pump caps | non-isolated |

- **Isolation = populate-option, not default.** For an install that truly needs a galvanically
  isolated NMEA-out, an **ADM3251E (C579198) footprint is kept DNP** in place of the SP3232 NMEA
  channel (or the SP3232+ISO7721 C366164 discrete build). Default is non-isolated — deep stock,
  shared chassis ground covers the common case.
- **D_NMEA / D_EXT = SMAJ12CA (C134948)** per RS-232 line (12 V standoff clears ±5.5 V swing).

### 5.3 GPS slot — **AiO multi-module footprint** (TTL, on-board)

| Net | Nodes | Notes |
|---|---|---|
| `GPS_UART1` | GPS module → STM **UART4** (PC10/PC11) | primary GPS (ArduSimple / F9P #1 / UM98x) |
| `GPS_UART2` | GPS module → STM **UART5** (PC12/PD2) | 2nd F9P (dual = moving-baseline heading) |
| `GPS_PPS` | GPS.PPS → STM (timer capture) | timing |
| `GPS_PWR` | ← 3V3 + 5 V | module supply (both rails available at slot) |
| GND | slot GND | |

- **Slot = one board space accepting 1× ArduSimple (Arduino-Uno shield footprint) OR 2× F9P OR
  1× UM98x** — all 3.3 V TTL UART, **no RS-232 driver for GPS.** Antenna (SMA/u.FL) lives on the
  module. Two UARTs wired so a dual-F9P heading pair works; single-module configs use UART4.
- **IMU is not a dedicated on-board part** — it comes from the **GPS (UM981/2 built-in INS)**, or an
  **external unit over RS-232 (§5.2) / RS-485 (§5.1) / CAN (Page 4)**. No IMU transceiver needed.

### 5.3.1 GPS slot — standardized module symbols  (post-swap, `GPS_2026-07-05`)

The AiO header/jumper/LED pile was replaced with clean module symbols that carry **pin labels**, and
the copied nets were stripped — so wiring is now just "label each module pin with our net." The slot
is **populate-one-option**: all module choices share the same UART nets, so only one set is stuffed
per build (DNP the rest, or their TX pins would fight on `GPS1_TX`).

| Symbol | Module | Role |
|---|---|---|
| `P2` | ArduSimple simpleRTK2B (full shield) | single-F9P option |
| `U16` | UM982EB | one-module dual-antenna heading + INS |
| `U17` / `U18` | AS-RTK2B Micro-F9P ×2 | dual-F9P moving-baseline (U17 = primary, U18 = heading) |
| `JP1` | XBee castellated (1-long-pad) | RTK-over-radio, populate-optional |

**Wire each module's labeled pins to the shared nets → U7:**

| Our net | → U7 pin | ArduSimple P2 | UM982 U16 | Micro-F9P | XBee JP1 |
|---|---|---|---|---|---|
| `GPS1_TX` (mod→MCU) | PC11 UART4_RX | TX1 | UART1_TX | U17 TX | — |
| `GPS1_RX` (MCU→mod) | PC10 UART4_TX | RX1 | UART1_RX | U17 RX | — |
| `GPS2_TX` | PD2 UART5_RX | (2nd board) | UART2_TX | U18 TX | DOUT |
| `GPS2_RX` | PC12 UART5_TX | (2nd board) | UART2_RX | U18 RX | DIN |
| `GPS_PPS` | timer pin (PA15 spare) | PPS | PPS | U17 PPS | — |
| `+3V3` | — | 3V3 | 3V3 | 3V3 | 3V3 |
| `5V_MAIN` | — | 5V | 5V | 5V | — |
| `GND` | — | GND | GND | GND | GND |

- **UART5 is shared** between the 2nd-GPS (dual-F9P heading) and the XBee — only one is used per
  config (dual-F9P heading **OR** radio-RTK), so no runtime conflict. UM982 does heading in one
  module, freeing UART5 for the XBee.
- UM982 UART3 = spare (leave as a labeled pin / DNP to the STM).
- **Confirm each symbol's exact label text as you wire** — it varies (`TX1` / `ZED_TX1` / `COM1_TX`).

**Still open on this sheet:** OLED is 0.96″ (`OLED1`) vs the 1.3″ SH1106 spec — swap the footprint or
accept the smaller window. Deutsch field connectors, input fuse `F1`, and the SWD header are still
not placed anywhere.

### 5.4 XBee radio (RTK-over-radio) — POPULATE-OPTIONAL castellated pads

| Net | Nodes | Notes |
|---|---|---|
| XBee DOUT/DIN | ↔ STM UART (repurpose UART5 when single-GPS) **or** wire to GPS RTCM-in | RTCM corrections |
| `+3V3` / GND | XBee VCC / GND | 3.3 V |
| XBee RESET/RSSI | STM GPIO (optional) | |
| XBee U.FL | → back-panel antenna bulkhead (Page 7) | |

- **Castellated XBee-SMT footprint (pads only, no socket)**, populate-optional. Gives an
  **RTK-corrections-over-radio** link (base→rover RTCM where there's no cell/NTRIP). UART routing is
  a GPS-slot detail (uses UART5 in single-GPS configs, or feeds the F9P's RTCM-in directly).

### Open items (Page 5)

- NMEA-out connector type (DB9 vs screw); confirm ADM3251E iso footprint kept as DNP alt.
- XBee UART routing (UART5-repurpose vs direct-to-GPS) — finalize with GPS-slot layout.
- GPS slot: finalize the Arduino-Uno + dual-F9P footprint pin/power mapping at layout (mirror AiO).
- GPS routing default (STM→PGN→CM vs straight-to-CM jumper) — design-doc §12.2.

## PAGE 6 — OFF-BOARD I/O & PROTECTION

**No on-board section drivers** — sections run on an **external ESP32 module over the Page-2 module
network** (Ethernet/WiFi), which also makes tool-swaps harness-free. This page = analog sensor
entry, digital switch inputs, isolated steering-control outputs to the off-board amp, and general
off-board protection.

### 6.1 Analog sensor entry (WAS, current) → Page-3 ADC

| Net | Nodes | Notes |
|---|---|---|
| `WAS_IN` | J_SENS.(WAS) → D_WAS_p TVS → Page-3 R_WAS1 | 5 V ratiometric sensor |
| `WAS_5V` | `5V_MAIN` → J_SENS.(WAS pwr) | sensor supply (stable rail for ratiometric) |
| `ISENSE_IN` | J_SENS.(I) → D_IS TVS → Page-3 R_CS | reserved (amp analog Iout, if any) |
| `GND` | J_SENS.GND | |

### 6.2 Digital switch inputs (work / engage / remote)

| Net | Nodes | Notes |
|---|---|---|
| `SW_WORK` | J_SW.(work) → R_div + RC + D_SWi TVS → STM PB2 (Page 3) | contact / 12 V-level input |
| `SW_ENGAGE` | J_SW.(engage) → R_div + RC + D_SWi → STM PB10 | autosteer engage |
| (remote) | J_SW.(remote) → spare GPIO | optional |
| pull-ups + clamp to `+3V3` | 12 V inputs → divider + clamp before the pin | |

### 6.3 Steering control outputs → off-board amp (isolated, controller-agnostic)

| Net | Nodes | Notes |
|---|---|---|
| `PWM_MOTA` | STM PA8 TIM1 (Page 3) → OPTO_PWM → J_STEER.(PWM) | **fast opto** (PWM rate) |
| `MOT_DIR` | STM PB1 → OPTO_DIR → J_STEER.(DIR) | direction |
| `STEER_EN` | STM PB0 → OPTO_EN → J_STEER.(EN) | enable / inhibit |
| (Danfoss / dual-PWM) | TIM8/TIM3/TIM4 (Page 3) → optos → J_STEER | controller-agnostic output set |
| amp-side pull-up | J_STEER opto collectors ← amp supply | **no iso-DCDC on our board** |

- **Opto-isolate** the direct control lines (design principle); amp side self-powered (collector
  pulled up by the amp's rail → no isolated supply needed here). Amps are *also* reachable via
  isolated **RS-485 (Page 5)** / **CAN (Page 4)** — this is the direct-PWM subset.
- **WAS closes the steering loop ON-BOARD** (Page-3 ADC) → even a dumb remote amp gives full autosteer.

### 6.4 General protection
- TVS/ESD on every off-board conductor (per-block above). Input load-dump/reverse = Page 1.
- **Section outputs: NONE on-board** — external ESP32 module via the module network (Page 2).

### Parts (verified 2026-07-04)
- **Isolation = optos** (amp self-powers far side): **OPTO_PWM = 6N137S (C5123515)** 10 Mbit;
  **OPTO_DIR/EN = LTV-357T (C119089)**. If amp is sign-magnitude (DIR toggles per PWM cycle), drive
  DIR from a 2nd 6N137 channel. ISO7741 rejected (needs far-side iso-DCDC).
- **WAS = ESD9B5V (C2905646) low-leakage ESD, NOT an SMAJ** — SMAJ's ~800 µA leak distorts the
  ratiometric reading. (Same part reserved for the current-in line.) One clamp at connector entry;
  don't double-clamp the analog on Page 3.
- **Digital inputs = two-stage:** SMAJ16A (C283886 DOWO / C74561 Littelfuse) at the 12 V line
  (16 V standoff clears 14.4 V charging) + **SRV05-4 (C558418)** array on the 5 V logic bank.

### Open items (Page 6)
- **SMAJ16A is thin-stock** — approve 2–3 vendors in the BOM so PCBA can't stall.
- Connector families (J_SENS / J_SW / J_STEER); which controller-output modes to physically break out.

---

## PAGE 7 — HMI / PANEL

**STM32 owns the HMI** (always-on → status shows on power-up + during CM boot/fault). Front panel =
display + reset + connectors; back panel = antenna bulkheads; piezo lives in-case.

### 7.1 OLED status display — 1.3" SH1106 (STM I²C)

| Net | Nodes | Notes |
|---|---|---|
| `OLED_SCL` | STM PC4 → OLED.SCL | 128×64, SH1106 |
| `OLED_SDA` | STM PC5 ↔ OLED.SDA | HW I²C2 or soft-I²C |
| `+3V3`/GND | OLED.VCC/GND | via **J_OLED 4-pin header**; module hand-fit |

- **OLED is hand-fit** (all 1.3" I²C OLEDs are COG-glass/module — not JLC-placeable). Board carries
  **J_OLED 4-pin header + 2× 4.7 kΩ I²C pull-ups (R_OLED)** since bare modules lack them.
- Shows power / fix + sats / RTK / IP + network / CM-state, instantly on power-up and through CM
  boot/wedge/fail. CM pushes richer strings to the STM over the USB-PGN link.

### 7.2 Reset button — recessed, probe-hole (STM GPIO)

| Net | Nodes | Notes |
|---|---|---|
| `BTN_RST` | probe-hole → SW_RST (**TS-1187A, C318884**) → STM PC13 | + 10 k pull-up + RC debounce |

- Firmware-defined: **short-press = clean CM restart; hold-at-power-on = enter recovery/rpiboot.**
  Not a dumb reset line — the always-on STM interprets it.

### 7.3 Piezo alert — in-case (STM PWM)

| Net | Nodes | Notes |
|---|---|---|
| `PIEZO_PWM` | STM PA7 (TIM17_CH1) → R_PZG 100 Ω → Q_PIEZO.G (**2N7002, C8545**) | tone PWM; 100 k gate pulldn |
| `PIEZO_DRV` | 5 V → LS_PIEZO (**PS1240P02BT, C76871**) → Q_PIEZO.D; R_PZD ~470 Ω ∥ piezo | low-side + damping R |

- **Passive transducer** (not fixed-tone buzzer) → STM makes distinct sounds (fix / RTK-fixed /
  autosteer on-off / fault). In-case, no panel face. ~70 dB single-ended off 5 V; **antiphase 2nd
  FET = +6 dB** if louder needed.

### 7.4 Front panel (124 × 50 mm, −case wall ≈ 118 × 44)

**Every rigid board-mounted connector is on the FRONT (one-end registration)** so case length stays
non-critical (§7.6). Antennas (back) are case-mounted on flexible U.FL pigtails → not a second
registration end.

```
 +------------------------------------------------------------+
 |                                                            |
 | [ ATS-26  44.5×35.5 ] [ OLED 1.3" 35×33 ] [RJ45]  (rst o)  |
 |                                                            |
 +------------------------------------------------------------+
       44.5 mm               35 mm           16 mm
```
**Single horizontal row** (right-angle headers can't stack — upper would need riser legs no part
makes — and the 44 mm height can't fit a connector above/below the 33 mm OLED). ATS-26 BM01
(44.5 W × 35.5 H) + OLED (35) + RJ45 (16) ≈ 95.5 mm of 118 → **~22 mm margin**; 35.5 mm tall clears
the 44 mm panel. No RF on the front (antennas back, §7.5).

### 7.5 Back panel — antenna bulkheads (all U.FL→bulkhead pigtails, hand-fit, nothing soldered)

| Antenna | Source U.FL | Bulkhead | Notes |
|---|---|---|---|
| GPS ×2 | GPS-slot module(s) | 2× SMA | dual-F9P / UM98x-dual / ArduSimple+heading |
| WiFi | CM4 wireless module | 1× RP-SMA | |
| XBee (opt) | XBee module (Page 5) | 1× RP-SMA/SMA | populate-optional |

### 7.6 Connector strategy (all off-board wire I/O)

**One Amphenol ATS-26, right-angle, hand-soldered, on the front panel** (superseded: 5 Deutsch →
2× DTM-12 → AMPSEAL-23 → this). **Why ATS-26 wins:** the front is a **forced single horizontal row**
(right-angle headers can't stack; the 44 mm panel height can't fit a connector above/below the 33 mm
OLED), so it's a pure **width** game — and the ATS-26 BM01 flange is **44.5 W × 35.5 H mm**, vs
AMPSEAL-23's 61 mm wide, giving **~22 mm panel margin** (35.5 tall clears the 44 mm panel). It also
has **26 pins** (vs 23) so the 2nd RS-232 and motor current-sense come back, and it seals to **IP69K**
(a notch above AMPSEAL's IP67). Size-20 contacts, 7.5 A (fine for VIN ~4 A; gang if wanted). **All
connectors on ONE end (front)** so only the front registers the board → case length non-critical
(antennas OK on the back — case-mounted pigtail).

**Board header = `ATS13-26PA-BM01`** (Amphenol ATS, 26-pos, 90° right-angle, keyed A, size-20 pin,
**sealed self-threading flange, 44.5 × 35.5 mm**, BoardLock). Alts: `-BM21` (3-hole flange), or the
custom `-BM287-8` (39.5 mm, narrower — confirm stock). Straight variant = `ATS15-26PA-…`. Plug =
`ATS06-26SA` + size-20 stamped/formed contacts (`SP20W2G…`) or machined (`MP20W23…`). Hand-fit (not in
JLC library); footprint via SamacSys/SnapEDA → EasyEDA. RJ45 + U.FL sockets remain normal SMT.

**ATS-26 pin-out** (26 pins → full I/O, nothing dropped)

| Pin | Signal | Net | Notes |
|---|---|---|---|
| 1 | VIN | `VIN` | +12 V in (in-line fuse upstream in harness) |
| 2 | GND | `GND` | power return / board ground |
| 3 | WAS 5 V | `5V_MAIN` | ratiometric sensor supply |
| 4 | WAS sig | `WAS_IN` | → Page-6 TVS → Page-3 ADC (PA0) |
| 5 | AGND | `GND` | analog/sensor ground |
| 6 | Motor I-sense | `ISENSE_IN` | amp analog Iout → Page-6 TVS → STM PA5 |
| 7 | Work sw | `SW_WORK` | 12 V-level in (PB2) |
| 8 | Engage sw | `SW_ENGAGE` | autosteer engage (PB10) |
| 9 | Remote sw | `SW_REMOTE` | spare GPIO in |
| 10 | Steer PWM | `PWM_MOTA` | opto out (PA8) |
| 11 | Steer DIR | `MOT_DIR` | opto out (PB1) |
| 12 | Steer EN | `STEER_EN` | opto out (PB0) |
| 13 | Steer RTN | `STEER_RTN` | amp-side opto common (isolated) |
| 14 | CAN1 H | `CAN1_H` | U8 |
| 15 | CAN1 L | `CAN1_L` | U8 |
| 16 | CAN2 H | `CAN2_H` | U9 |
| 17 | CAN2 L | `CAN2_L` | U9 |
| 18 | CAN3 H | `CAN3_H` | U10 |
| 19 | CAN3 L | `CAN3_L` | U10 |
| 20 | CAN/sig GND | `GND` | CAN shield + RS-232 signal ground |
| 21 | RS-485 A | `RS485_A` | isolated (CA-IS3092W) |
| 22 | RS-485 B | `RS485_B` | isolated |
| 23 | NMEA TX | `NMEA_TX` | RS-232 out (USART1) |
| 24 | NMEA RX | `NMEA_RX` | RS-232 in |
| 25 | Ext-232 TX | `EXT232_TX` | 2nd RS-232 out (LPUART1, ext IMU/GPS) |
| 26 | Ext-232 RX | `EXT232_RX` | 2nd RS-232 in |

- **Full I/O, nothing dropped** — power, WAS, current-sense, 3 switches, steering, all 3 CAN, RS-485,
  and **both** RS-232 ports all fit the 26 pins.
- Ethernet = **J_RJ45 (C50933)** on the front (own connector — can't share the ATS). Antennas = back
  bulkheads (§7.5).

### Open items (Page 7)
- **Connector = Amphenol ATS-26 right-angle `ATS13-26PA-BM01` (44.5 × 35.5 mm sealed flange), front,
  hand-solder — CONFIRMED.** (AMPSEAL-23 superseded: ATS is narrower + 26 pins + IP69K. DTM/Micro-Fit
  rejected earlier.) `-BM287-8` (39.5 mm) is a narrower custom alt if it sources.
- Footprint/symbol/3D requested via Mouser → SamacSys (~1 business day); SnapEDA is a faster fallback.
  Confirm the flange mounting-hole pattern from the model for the panel + board outline.
- Panel mechanical (probe-hole ⌀, OLED window, ATS cutout + flange bolts, bulkhead holes) at enclosure design.

---

## Open power-tree items (Page 1)

- C_HU (hold-up) value + U2 ILIM resistor — `TBD@layout` (inrush/halt sizing). Fuse is external
  in-line in the harness (user-supplied), not a board part.
- R_FB1/R_FB2 fixed by U1 Vref (0.8 V) → set for 5.0 V.
- D_SW Schottky selection (≥60 V, ~5 A) — pick in-stock JLC part.
- **12 V system — CONFIRMED** (24 V-native = populate variant only: SMAJ33/48 + divider re-ratio).
- JLC stock verify still pending for U2 (eFuse), U3 (LDO), Q1, D2, D3, J1 (agents running).
