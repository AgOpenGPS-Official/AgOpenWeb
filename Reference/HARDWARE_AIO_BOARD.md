# AgOpenWeb AiO Board — Design Reference

> Design notes for a single-board AiO (all-in-one) carrier for a Raspberry Pi **Compute Module 4**:
> CM4 host, 3× CAN FD over SPI, 2× RS-232, a GPS module slot, wheel-angle/current/VIN sensing on an
> external ADC, direct steering outputs, and a hardware watchdog.
>
> **Status (2026-09-14): schematic captured in EasyEDA Standard, full-board netlist exported, PCB
> layout partly done — power, Ethernet and NVMe are placed/routed; CAN, serial, ADC, field I/O and
> HMI are not.** A review of the export found schematic issues (§13); the table there notes which
> ones touch the areas already laid out. JLC stock figures date from 2026-07; re-check at order time.
>
> **Companion docs:** `HARDWARE_AIO_NETLIST.md` (as-built wiring, every net),
> `HARDWARE_AIO_BOM.md` + `HARDWARE_AIO_BOM_JLCPCB.csv` (parts), `HARDWARE_AIO_LAYOUT_GUIDE.md`
> (routing). Ref designators are the real EasyEDA ones and match across all of them.

---

## 0. Revision history

| Date | Tool | Architecture | Source |
|---|---|---|---|
| 2026-07-04 → 07-09 | EasyEDA Pro | CM4 + **STM32G473** (hard-RT loop, failsafe, clean shutdown), TCAN1042 ×3, isolated RS-485, OLED | `Full-board_2026-07-08.net`, git history of this file |
| 2026-09-14 | **EasyEDA Standard** | **CM4 only.** STM32 removed; CAN via 3× MCP251863 on SPI; external ADC; STWD100 watchdog; SK6812 status LEDs | `Full-board_2026-09-14.net` |

The Jul→Sep redesign (including the move from EasyEDA Pro to Standard) was never pushed, and its
working notes were lost in a disk crash. **Where this doc explains *why* for September changes, the
reasoning is inferred from the netlist** and marked *(inferred)*. Everything the July design settled
that the new netlist still follows is kept as-is.

### What changed Jul → Sep

| Area | July (Pro) | September (Standard) |
|---|---|---|
| Real-time controller | STM32G473RCT6 + 8 MHz crystal, SWD | **removed**, CM4 runs everything |
| CAN ×3 | STM FDCAN + 3× TCAN1042, split-term + CMC footprints | **3× MCP251863** (controller + transceiver) on CM4 SPI0, shared 40 MHz oscillator, **no termination/CMC** |
| Analog (WAS, current, VIN) | STM32 ADC (3.3 V ref, divider) | **ADC128S102** on SPI0, VA = 5V_MAIN (0–5 V, ratiometric) |
| CM4 power gate | eFuse EN driven by STM `PI_PWR_EN` | eFuse **always on** (EN pulled up) |
| Watchdog / failsafe | STM32 independently de-energizes outputs | **STWD100** resets the CM4 via `RUN_PG` |
| RS-485 (motor bus) | isolated CA-IS3092W on J1.21/22 | **removed**, J1.21/22 unused |
| RS-232 | NMEA-out + ext port, driven by STM | 2 general ports on CM4 UART2/UART3 |
| GPS slot | STM UART4 + UART5, PPS, dual-F9P, XBee | UM982 **or** ArduSimple on CM4 UART5, no PPS |
| USB | CM4 host ↔ STM device via TS3USB221 mux + micro-USB rpiboot | 2×2 header H3 (USB D± + nRPIBOOT) for provisioning only |
| HMI | STM-driven 1.3" OLED + page rocker + piezo | 4× SK6812 status LEDs, power LED, piezo, TH reset button |
| Steering outputs | opto-isolated in first pass, then direct drive (Jul 7) | direct CM4 GPIO via 330 Ω |
| J1 CAN pinout | H on even pins 14/16/18 | **L on even, H on odd** (§13 F7) |

---

## 1. Concept

- **Host = Raspberry Pi Compute Module 4.** The backend runs at ~8–11 % of a Pi Zero 2W (rendering
  is client-side), so compute isn't the sizing driver. Storage reliability, I/O and solder-down
  mounting are. The CM4 form factor is a de-facto standard with clones, which gives supply options.
- **No separate MCU (September).** *(inferred)* The CM4 now owns the guidance loop, all I/O and its
  own supervision. CAN and analog move to SPI peripherals that buffer in hardware (MCP251863 FIFOs,
  ADC conversions), so Linux scheduling jitter affects when data is read, not whether it's captured.
  A hardware watchdog replaces the STM32's independent failsafe (§6).
- **Trade-off accepted by that choice:** Linux is soft real-time. The July doc's argument for an MCU
  ("MCU owns the µs loop, immune to host stalls") no longer applies. Steering safety now depends on
  the watchdog + GPIO reset defaults (§6) rather than on a separate processor.

---

## 2. Block diagram

```
                        ALUMINUM CASE (heatsink, gap pad on CM4 SoC)
 +-------------------------------------------------------------------------------------+
 |  J1 ATS-26                                                                          |
 |  VIN ─► Q1 rev-pol ─► TV1 + C6/C7 hold-up ─► U1 buck ─► 5V_MAIN ─┬─► U2 eFuse ─► 5V_CM ─► CM4
 |            │                                                    ├─► U3 LDO ─► +3V3     │
 |            └─► VIN_SENSE divider ─────────────────┐             ├─► CAN VCC, LEDs, GPS, piezo, WAS 5V
 |                                                   │             └─► U4 buck (EN from 5V_CM) ─► +3V3_NVME ─► M.2
 |                                                   ▼                                        │
 |  WAS ─► ESD+RC ─► ┌────────────┐                                                          │
 |  ISENSE ─► ESD+RC►│ U21 ADC128 │◄─┐                                                        │
 |                   └────────────┘  │ SPI0 (SCLK/MOSI/MISO + 4 CS)                           │
 |  CAN1 ◄► ┌───────────────┐        │                                                        │
 |  CAN2 ◄► │ U7/U8/U9       │◄──────┤                  ┌──────────────────────────────┐     │
 |  CAN3 ◄► │ MCP251863 ×3   │ nINT ─┼────────────────► │  U19  CM4 Lite Wireless       │◄─PCIe─┘
 |          └──────▲────────┘        └────────────────  │  28 GPIO all allocated        │◄─GbE──► L1 RJ45
 |                 └── X1 40 MHz                        │  UART0 console ─► H1          │◄─USB──► H3 (+nRPIBOOT)
 |  RS-232 ×2 ◄► U12 SP3232 ◄── UART2 / UART3 ──────────│                               │
 |  GPS slot (U16 UM982 | P2 ArduSimple) ◄── UART5 ─────│                               │
 |  SW work/engage/remote ─► TVS+RC+ESD ─► GPIO in ─────│                               │
 |  STEER PWM/DIR/EN ◄── 330 Ω ◄── GPIO18/23/24 ────────│                               │
 |                                                      │ RUN_PG ◄── U20 STWD100 WDO    │
 |  SK6812 ×4 ◄── U22 AHCT125 ◄── GPIO2                 │         ◄── SW1 reset          │
 |  Piezo ◄── Q2 ◄── GPIO6                              └──────────────────────────────┘
 +-------------------------------------------------------------------------------------+
```

---

## 3. Compute module (CM4)

| Decision | Choice | Rationale / hedge |
|---|---|---|
| Form factor | **CM4 Lite Wireless** (CM4101000 footprint), DF40 ×2 mezzanine, no eMMC | de-facto standard footprint; Lite offsets NVMe cost; wireless for the WiFi module path |
| Connectivity | **Ethernet (RJ45, on-module GbE PHY) + WiFi (on-module + external antenna)** | AgOpen module network (PGN-over-UDP); metal case needs an external antenna |
| RAM | 1–2 GB, low SKU | load is tiny; low SKUs cheaper and better stocked |
| Root FS | read-only + overlayfs (or A/B) on NVMe | power loss can't corrupt the OS partition |
| Boot / root / data | **NVMe (128 GB M.2), single medium** | more reliable than SD and eMMC |
| SD slot | none | redundant once NVMe is present |
| Provisioning | **rpiboot over H3** (USB D± + nRPIBOOT jumper) | only way to image a bare Lite's NVMe + set `BOOT_ORDER`; also field recovery |
| GPIO bank | 3.3 V (`GPIO_VREF` pin 78 tied to `CM4_3V3` 84/86, C76) | datasheet: pin 78 must not float |
| Carrier layout | accept any CM4 variant | lead-time hedge |

---

## 4. Storage

- **Single medium: NVMe (128 GB M.2) on a CM4 Lite. No SD, no eMMC.** The CM4 boots directly from
  NVMe once `BOOT_ORDER` is set.
- **Why NVMe — measured (NVMe vs SD, same rig):** seq read 515.7 vs 62.7 MB/s (8.2×), 4K rand read
  ~49.8 vs ~7.3 MB/s (6.9×), **seq write 278.1 vs 15.0 MB/s (18.6×)**, **4K rand write ~81.3 vs
  ~2.3 MB/s (35.5×)**. Field logging is 4K random writes, so the 35× case is the one that matters.
  SD also stalls (hundred-ms FTL hiccups) and wears under sustained logging. Keep the write path off
  the control-loop thread regardless.
- **No SD bootstrap paradox.** The CM4 (Lite included) has an on-module SPI EEPROM holding the
  bootloader + `BOOT_ORDER`, separate from any boot medium.
- **Provisioning via H3:** (1) jumper H3.1 (`NRPIBOOT`) to H3.3 (GND), power on → SoC mask ROM
  enters USB device mode; (2) connect a host PC to H3.2/H3.4 (USB D−/D+, plus GND) — **no VBUS on
  the header**, the board powers itself; (3) `rpiboot` flashes the EEPROM with `BOOT_ORDER=…6…`, then
  mass-storage-gadget exposes the NVMe for imaging; (4) remove jumper, power-cycle → boots NVMe.
  `USB_OTG_ID` (pin 101) floats (device mode), which is what rpiboot needs.
- **Production flow:** set `BOOT_ORDER=6` once per CM4 at incoming inspection; bulk-image NVMe drives
  externally on a USB-NVMe adapter; final assembly seats a pre-imaged drive.
- **NVMe choice:** decent 128 GB 2230/2242; industrial/pSLC for heavy continuous logging.
- **PCIe routing:** 3 length-matched diff pairs (TX/RX/REFCLK) + PERST#/CLKREQ#; no AC caps on the
  carrier (CM4 and SSD have them). See `HARDWARE_AIO_LAYOUT_GUIDE.md` §3.

---

## 5. Thermal

- CM4 is ~5–7 W → **fanless, conduct to the enclosure.**
- CM SoC heat-spreader → compressible thermal gap pad (0.5–1 mm) → machined boss on the aluminum lid.
  Not rigid metal-to-metal (tolerance stack-up + vibration).
- U1 buck + D1 catch diode and L2 are the other heat sources; give them copper.

---

## 6. Supervision, failsafe & real-time

**Hardware watchdog — U20 STWD100 (open-drain WDO → `RUN_PG`).**

1. Power-on / any reset: `WDT_EN` (GPIO3) is held high by R63 4.7 k + GPIO3's 1.8 k module pull-up
   → watchdog **disabled** while Linux boots.
2. Linux running: drive GPIO3 low (enable), toggle GPIO22 (`WDT_WDI`) within t<sub>WD</sub>.
3. Linux hangs → WDI stops → WDO pulls `RUN_PG` low for ~210 ms → CM4 resets → back to step 1.

- **Timeout:** the fitted **STWD100NXWY3F is 102 ms** (71–142 ms). Kicking reliably inside 71 ms from
  Linux means the kernel `gpio-wdt` driver (device tree `linux,wdt-gpio`, toggle mode), not a
  userspace daemon. **STWD100NYWY3F (1.6 s)** is the same footprint and much more forgiving (§13 F4).
- `RUN_PG` (pin 92) is the correct reset input. `nEXTRST` (pin 100) is a reset *output* and
  `GLOBAL_EN` (pin 99) powers the module off.
- The CM4's built-in BCM2711 watchdog still exists. The STWD100 is the one that works even if the SoC
  watchdog or its driver is wedged.

**Steering failsafe (inferred).** Without the STM32, "outputs off when the host is down" comes from
reset defaults: during and after a CM4 reset the GPIOs return to inputs with their default pulls.
`STEER_EN` (GPIO24) and `PWM_MOTA` (GPIO18) default to pull-**low**, so the motor driver sees EN/PWM
low. The drivers' own input pulls must agree, and firmware must only assert `STEER_EN` once guidance
is live. The watchdog reset path makes a hung kernel fall into that state within ~0.1–2 s.

**Real-time.** CAN frames are buffered in each MCP251863's FIFOs and raise `nINT`, and ADC samples are
taken on demand, so the latency-critical path is "Linux reads SPI within its loop period." A
PREEMPT_RT kernel and `isolcpus` for the guidance thread are the usual mitigations; see
`Plans/DEPLOYMENT_PATTERNS.md`.

---

## 7. Power & shutdown

Chain: `VIN → Q1 → VIN_PROT (TV1, 2×470 µF) → U1 buck → 5V_MAIN → { U2 eFuse → 5V_CM → CM4 ;
U3 LDO → +3V3 ; U4 buck → +3V3_NVME (EN from 5V_CM) }`

- **Single always-on 5 V buck (Option A, unchanged from July).** TPS54560 60 V / 5 A, 400 kHz,
  6.8 µH, 3×47 µF out, comp 16.9 k / 4.7 nF / 47 pF (locked to that C<sub>OUT</sub>).
- **12 V system, 40 V operating (unchanged).** SMBJ24A clamps load-dump to ~39 V inside the buck's
  60 V rating. A 24 V-native install needs the populate variant: SMBJ33/48 TVS, re-ratio `VIN_SENSE`.
- **CM eFuse U2 TPS259571** — ILIM 487 Ω (~4.17 A), dVdt 10 nF. **EN is now pulled permanently high
  (R43 → +3V3)** and `PI_FLT` is pulled up but not read by anything.
- **Power-loss handling (inferred).** `VIN_SENSE` (100 k / 8.2 k, 40 V → 3.03 V) now goes to ADC IN2.
  Linux watches it and does a clean `poweroff` when VIN falls, while C6/C7 hold the rail up.
  **Limitation:** once halted, the CM4 only restarts on a power cycle or a `GLOBAL_EN` pulse, neither
  of which the board can produce. A brown-out that triggers shutdown but recovers before the caps
  drain leaves the unit halted until the key is cycled (§13 F10).

---

## 8. CAN (3× CAN FD)

- **U7/U8/U9 = MCP251863T-E/SS** — MCP2518FD controller + ATA6563 transceiver in one SSOP-28. SPI to
  the CM4, Linux driver `mcp251xfd` (SocketCAN `can0..2`). VDD/VIO = +3V3, transceiver VCC = 5V_MAIN.
- Controller-to-transceiver links (TXCAN 15 → TXD 23, RXD 28 → RXCAN 16) are external traces. Keep
  them short.
- **Clock:** one X1 40 MHz oscillator star-fed through 33 Ω (R64–R66) to each OSC1. 40 MHz is the
  MCP2518FD's recommended CAN FD clock.
- **⚠ STBY (pin 5) is unconnected** → internal pull-up → transceiver standby → cannot transmit
  (§13 F1).
- Bus TVS NUP2105L per channel (D5/D6/D7). **No termination on board** (§13 F8).
- CAN chip-selects: CAN1 = SPI0 CE0 (GPIO8), CAN2 = CE1 (GPIO7), CAN3 = GPIO25 (GPIO chip-select, R74
  pull-up). Interrupts: GPIO16/17/27.

---

## 9. Serial & GPS

- **U12 SP3232EEN**, non-isolated, 2 drivers + 2 receivers at 3.3 V, SMAJ12CA TVS on each line
  (D9–D12). Port 1 = UART2 (GPIO0/1) → J1.23/24; port 2 = UART3 (GPIO4/5) → J1.25/26.
- **GPIO0/1 are the ID EEPROM pins** — `config.txt` needs `force_eeprom_read=0` and `disable_poe_fan=1`.
- The July "GPS-out" behavior (raw echo that keeps flowing while the host is down, fused mode,
  re-clocked baud) was STM32 firmware. In this revision any NMEA-out is a Linux service, so **it stops
  when the CM4 is down or rebooting.**
- **GPS slot:** UM982EB (U16) *or* ArduSimple RTK2B (P2) on UART5 (GPIO12/13), 5 V supply. Populate
  **one** — they share the UART (§13 F9). No PPS line. IMU comes from the UM982's INS or an external
  unit over RS-232/CAN.
- **Console:** UART0 (GPIO14/15) on H1. On a wireless CM4, UART0 is assigned to Bluetooth by default,
  so use `dtoverlay=miniuart-bt` or `disable-bt` if the console needs the PL011.
- **Motor RS-485 removed** — the motor driver is reached over CAN or the direct PWM/DIR/EN outputs.

---

## 10. Field I/O & protection

- **J1 = Amphenol ATS13-26PA-BM01**, 26-pin right-angle, IP69K, hand-soldered, front panel. Full
  pinout in `HARDWARE_AIO_NETLIST.md` §5.1.
- **WAS** (J1.4): ESD9B5V (low leakage, doesn't distort a ratiometric reading) → 1 k / 100 nF (~1.6 kHz)
  → ADC IN0. The ADC reference is 5V_MAIN, the same rail that powers the sensor (J1.3), so the reading
  is ratiometric with no divider.
- **Current sense** (J1.6): same front end → ADC IN1.
- **Switch inputs** (J1.7–9): SMAJ16A at the line, 1 k series, 10 k pull-up to +3V3, 100 nF, SRV05-4 to
  +3V3 at the pin. **Contact-to-ground inputs** (§13 F6).
- **Steering outputs** (J1.10–12): CM4 GPIO → 330 Ω → connector, 3.3 V logic (MD13S / IBT-2 style).
  GPIO18 is hardware PWM0_0.
- **Input protection:** Q1 reverse-polarity P-FET + SMBJ24A TVS + in-line harness fuse (not on board).
  **Q1 orientation and gate clamp need fixing** (§13 F2, F3).

---

## 11. HMI

- **Status:** 4× SK6812SIDE-A addressable RGB LEDs, data from GPIO2 via SN74AHCT1G125 (3.3 → 5 V) and
  330 Ω. Replaces the July OLED + page rocker. GPIO2 can't produce the timing in hardware (§13 F5).
- **Power LED:** D23 on `PI_LED_nPWR` (pin 95) (§13 F11).
- **Reset:** SW1 PTS645 through-hole tactile on `RUN_PG`. A hard reset with no clean shutdown, since
  there's no longer an MCU to interpret button presses.
- **Piezo:** 2N7002 low-side driver from GPIO6 (software PWM tones), 470 Ω damping.
- **Antennas:** back panel, U.FL pigtails from the CM4 and GPS module to bulkheads. No board RF.

---

## 12. BOM spine

Full detail in `HARDWARE_AIO_BOM.md`.

| Function | Part | JLC # |
|---|---|---|
| Host | CM4101000 (Lite Wireless) on 2× DF40C-100DS-0.4V(51) | hand-fit / C597931 |
| Storage | 128 GB M.2 NVMe in 91302-55-067R2M socket | — / C2922444 |
| 5 V buck / catch diode / inductor | TPS54560DDAR / SS56C / 6.8 µH | C31966 / C123948 / verify |
| CM eFuse / 3V3 LDO / NVMe buck | TPS259571DSGR / RT9080-33GJ5 / TPS563201DDCR | C471038 / C841192 / C116592 |
| Rev-pol / TVS / gate zener | IRFR5305TRPBF / SMBJ24A / BZT52C12 | C2624 / C87268 / C124196 |
| CAN FD ×3 + clock + bus TVS | MCP251863T-E/SS + 40 MHz osc + NUP2105L | TBD / TBD / C284104 |
| ADC | ADC128S102CIMTX/NOPB | TBD |
| Watchdog | STWD100NXWY3F (consider NYWY3F) | TBD |
| RS-232 ×2 + TVS | SP3232EEN-L/TR + SMAJ12CA | C9378 / C134948 |
| Field ESD/TVS | ESD9B5V / SMAJ16A / SRV05-4 | C2905646 / C283886 / C558418 |
| LEDs | SK6812SIDE-A ×4 + SN74AHCT1G125DBVR | C5378721 / TBD |
| Ethernet | HR911130C magjack | C50933 |
| Connector | ATS13-26PA-BM01 | hand-fit |

---

## 13. Schematic review — `Full-board_2026-09-14.net`

Checked against the CM4 datasheet (Release 4), MCP251863 DS20006624B, ADC128S102 SNAS298G and STWD100
DocID14134. Nets and pins are in `HARDWARE_AIO_NETLIST.md`.

### Must fix before layout

| # | Issue | Evidence | Fix |
|---|---|---|---|
| **F1** | **CAN transceivers stuck in standby.** MCP251863 pin 5 (STBY) is unconnected on U7, U8 and U9. | Datasheet §8.2.2: STBY has an internal pull-up to VIO; normal mode needs STBY low. As wired, no CAN channel can transmit. | Tie pin 5 → GND on all three (simplest), or to pin 7 (nINT0/GPIO0/XSTBY) and enable `XSTBYEN` in firmware. |
| **F2** | **Q1 reverse-polarity FET looks reversed.** Q1.3 (source) is on `VIN` and Q1.2 (drain, tab) on `VIN_PROT`. | A P-FET's body diode runs drain → source. With the battery reversed, VIN is negative and the diode conducts from VIN_PROT (held near 0 V by TV1's forward diode) into VIN, so nothing blocks it. Same wiring in the July netlist. | Confirm the EasyEDA symbol pin map (IRFR5305: 1 = G, 2 = D/tab, 3 = S). If it matches, swap: **drain → `VIN`, source → `VIN_PROT`**. |
| **F3** | **Q1 gate isn't Vgs-clamped.** D2 (12 V zener) goes gate → GND; R40 100 k (VIN → gate) + R47 10 k (gate → GND). | Vgs ≈ −0.91 × VIN: −21.8 V at a 24 V jump start, −35 V at the 39 V TVS clamp. IRFR5305 V<sub>GS</sub> max is ±20 V. | Put the zener **source → gate** (cathode on source), across the source-side resistor. Keep the gate pull-down to GND. |

### Impact on the existing layout (power, Ethernet, NVMe already routed)

| # | Touches laid-out area? | Rework |
|---|---|---|
| F1 | no (CAN block) | tie pin 5 to GND before placing U7–U9 |
| **F2** | **yes — power** | Swap the nets on Q1's pads: pad 2 (tab) → `VIN`, pad 3 → `VIN_PROT`. The tab's large copper pour becomes the input-side pour, so either rotate Q1 so the tab faces J1.1, or re-assign the pours around it. TV1, C1/C2, C6/C7, R2, R42 and U1 stay on `VIN_PROT` (now pad 3). |
| **F3** | **yes — power** (small) | Flip D2 and re-connect it: cathode → Q1 source (`VIN_PROT` after F2), anode → `PGATE`. Move R40's top end from `VIN` to `VIN_PROT` as well, so both gate parts reference the source. R47 stays gate → GND. |
| F4 | no | same footprint, BOM swap only |
| F5 | no | net swap at CM4 pins 58 ↔ 25, both unrouted |
| F6–F10 | no | field I/O / GPS / CAN, not laid out |
| F11 | no (HMI) | adds a SOT-23 near D23 |
| F12 | near CM4 only | adds a 0402 between CM4 pin 92 and the SW1/WDO node |

Ethernet and NVMe routing aren't affected by any finding.

### Should fix

| # | Issue | Evidence | Suggested fix |
|---|---|---|---|
| **F4** | Watchdog timeout 102 ms (71–142 ms) is tight for Linux. | STWD100 order code: "X" = t<sub>WD</sub> 102 ms, "Y" = 1.6 s. | Fit **STWD100NYWY3F** (same footprint, open-drain), or commit to kernel-level kicking. |
| **F5** | SK6812 data on GPIO2, which has no PWM, PCM or SPI function. | WS2812-class timing (800 kHz) from Linux normally uses PWM, PCM (GPIO21) or SPI0 MOSI (GPIO10); GPIO-toggling from userspace isn't reliable. GPIO18's PWM0 is already the steering output. | Swap `LED_DATA` ↔ `SW_REMOTE` (GPIO2 ↔ GPIO21) to use the PCM method. GPIO2's 1.8 k pull-up is harmless on a pulled-up switch input. |
| **F6** | Switch inputs protect for contact closure only. | 12 V on J1.7–9 → 1 k → SRV05-4 clamp at ~+3V3 + V<sub>F</sub> ≈ 4 V, above the CM4 GPIO max of V<sub>GPIO_VREF</sub> + 0.5 V = 3.8 V, with ~8–10 mA pushed into +3V3. July's STM32 pins were 5 V tolerant. | If 12 V-level switch signals must be supported, add a divider (e.g. 10 k : 2.2 k → 14.4 V gives 2.6 V) or raise the series R. Otherwise document the inputs as contact-to-ground only. |
| **F7** | J1 CAN pin order changed. | July: 14/16/18 = H, 15/17/19 = L. Now: 14/16/18 = L, 15/17/19 = H. | Confirm intentional; update the harness drawing to match. |
| **F8** | No CAN termination footprints. | July split-termination R/C and CMC footprints were removed. | Add DNP 2×60 Ω + 4.7 nF split-termination per channel, or document that buses are terminated externally. |
| **F9** | GPS modules share one UART. | U16.15/16 and P2.11/12 are both on `GPS_RX`/`GPS_TX`. | Populate one (document it on the silkscreen), or add 0 Ω DNP links to isolate each slot. |
| **F10** | CM4 can't be power-cycled after a halt. | eFuse EN tied high (R43); `PI_FLT` not routed; `GLOBAL_EN` unconnected. | Route `PI_FLT` to a GPIO if one can be freed. For brown-out recovery, a small supervisor that pulses `GLOBAL_EN` low when VIN returns. |
| **F11** | Power LED on an unbuffered pin. | CM4 datasheet: `PI_LED_nPWR` "needs to be buffered". D23 is driven directly (~1.3 mA via R79 1 k). | Add a 2N7002/BSS138 buffer, or drop the LED. |
| **F12** | `RUN_PG` driven hard to GND. | CM4 datasheet: drive low "via a 220 Ω resistor". SW1 and WDO connect straight to pin 92. | Add a 220 Ω between CM4 pin 92 and the SW1/WDO node. |

### Informational (no change needed)

- `GLOBAL_EN` (99), `nEXTRST` (100), `WL_nDISABLE` (89), `BT_nDISABLE` (91) are single-pin nets. All
  are fine floating per the datasheet; expect EasyEDA DRC warnings.
- `USB_OTG_ID` (101) floats → device mode. Correct for rpiboot over H3 (the July grounding fix was for
  the STM32 host link, which is gone).
- Chip-select pull-ups (R74, R75) sit only on GPIO25/26, which default pull-low; CE0/CE1 (GPIO8/7)
  default pull-high. Consistent.
- ADC128S102 needs SCLK 8–16 MHz for rated accuracy; set its SPI device speed separately from the CAN
  devices.
- Piezo on GPIO6 has no hardware PWM, so tones are software PWM (fine for beeps).
- All 28 GPIOs are allocated. Adding anything (PPS, `PI_FLT`, CAN STBY control) means freeing a pin.
- The CM4 symbol U19 is one module footprint; the two DF40 connectors aren't separate netlist parts
  and must be added to the JLC BOM by hand.

---

## 14. Linux bring-up checklist (starting point)

Unverified on hardware. Confirm overlay names and parameters against the Raspberry Pi OS release used.

```
# /boot/firmware/config.txt
force_eeprom_read=0          # GPIO0/1 used as UART2
disable_poe_fan=1
dtparam=i2c_arm=off          # GPIO2/3 used for LEDs / watchdog EN
dtparam=spi=on
dtoverlay=disable-bt         # PL011 UART0 to console on GPIO14/15 (or miniuart-bt)
dtoverlay=uart2              # RS-232 #1 (GPIO0/1)
dtoverlay=uart3              # RS-232 #2 (GPIO4/5)
dtoverlay=uart5              # GPS (GPIO12/13)
dtoverlay=pwm,pin=18,func=2  # steering PWM0_0
dtoverlay=mcp251xfd,spi0-0,oscillator=40000000,interrupt=16   # CAN1 (CE0)
dtoverlay=mcp251xfd,spi0-1,oscillator=40000000,interrupt=17   # CAN2 (CE1)
# CAN3 (CS = GPIO25, INT = GPIO27) and the ADC (CS = GPIO26) need a custom overlay with cs-gpios.
# External watchdog: custom overlay, compatible = "linux,wdt-gpio", gpios = <&gpio 22 0>,
#   hw_algo = "toggle", hw_margin_ms below t_WD minimum; drive GPIO3 low to enable.
```
