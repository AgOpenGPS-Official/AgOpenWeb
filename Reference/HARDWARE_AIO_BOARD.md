# AgOpenWeb AiO Board — Design Reference

> Working design notes for a single-board AiO (all-in-one) that carries a Raspberry Pi
> Compute Module (host brain), an STM32 (hard-RT + watchdog + protected I/O front-end),
> a GPS slot, 3× CAN, and isolated RS-232/RS-485 for off-board peripherals.
>
> Status: **concept / part-selection**. Nothing laid out yet. JLC stock figures verified
> **2026-07** — re-check at order time (NAND/DRAM and MCU stock are volatile this year).
>
> **Companion docs (built bottom-up, power first):** `HARDWARE_AIO_NETLIST.md` (connection
> intent, pre-capture) and `HARDWARE_AIO_BOM.md` (part lines + JLC stock). This doc is the *why*;
> those two are the *what*. Ref designators are shared across all three.

---

## 1. Concept

- **Host = Raspberry Pi Compute Module (CM4 footprint).** The backend runs at ~8–11 % of a
  Pi Zero 2W (rendering is client-side), so compute is *not* the sizing driver — storage
  reliability, I/O, and solder-down mounting are. CM4 form factor is now a de-facto standard
  targeted by Pi and clones, giving supply optionality on one carrier.
- **MCU = STM32 (STM32G473RCT6).** Owns the hard real-time loop, the independent failsafe
  watchdog, power sequencing/clean-shutdown, and all protected off-board I/O.
- **Split rationale.** The x86 "use the spare CPU" idea was dropped: the host load is trivial,
  and x86 SMM/SMIs (hundreds of µs, unpreemptable) make x86 a poor hard-RT host anyway. The
  MCU owns the µs loop (immune to host stalls); the CM does soft-RT guidance/planning. This is
  the "MCU owns the hard loop, Linux is soft-RT" split.

---

## 2. Block diagram

```
                              ALUMINUM CASE (heatsink)
  +==============================================================================+
  |   machined boss --[gap pad 0.5-1mm]-- CM4 SoC heat-spreader                  |
  |                                        |                                     |
  |  +-------------------------------------+-----------------------------------+ |
  |  |  AiO CARRIER BOARD                   v                                   | |
  |  |   +----------------------------------------------+                       | |
  |  |   |  CM4 (Pi or clone - DF40 x2 mezzanine)        |                       | |
  |  |   |   1-2 GB RAM, 8 GB eMMC (low SKU)             |                       | |
  |  |   |   carrier accepts ANY CM4 family variant      |                       | |
  |  |   |   read-only overlay root                      |                       | |
  |  |   +--+--------+-----------+-----------+-----------+                       | |
  |  |      |USB2     |PCIe x1    | GPIO      | 5V PWR-IN                        | |
  |  |      |(host)   |(optional) |(alt GPS)  | (switched)                      | |
  |  |      |      [M.2 NVMe]     |       +---+----------+                       | |
  |  |      |      populate-if-   |       | e-fuse /      |<-- PI_PWR_EN         | |
  |  |      |      heavy-logging  |       | P-MOSFET      |                      | |
  |  |      |                     |       +---+----------+                       | |
  |  |  +---+ USB2 trace          |           | switched 5V                     | |
  |  |  |   | (RawHID PGN)        |       +---+----------+                       | |
  |  |  |   v (device)            |       | Buck 12/24->5V|<-- VBAT (load-dump   | |
  |  |  | +------------------------------+ | + hold-up cap |    protected)       | |
  |  |  | |  STM32G473RCT6 (LQFP64)       | +---+----------+                      | |
  |  |  | |  RT loop . watchdog .         |<----- VIN_SENSE (12V divider)        | |
  |  |  | |  clean-shutdown . I/O         |------ PI_PWR_EN -->                   | |
  |  |  | |  FDCAN1 on PB8/PB9 (USB frees |                                      | |
  |  |  | |  PA11/PA12)                   |                                      | |
  |  |  | +-+----+----+------+-----+------+                                      | |
  |  |  |   |TIM |ADC/| CANx3 | UARTx |                                          | |
  |  |  |   |PWM |op  |       |       |                                          | |
  |  |  |   v    v    v       v                                                  | |
  |  |  | +-----------------------------------------------+                      | |
  |  |  | |  PROTECTED I/O FRONT-END (TVS on every line)  |                      | |
  |  |  | |                                               |                      | |
  |  |  | |  motor driver . current sense                 |                      | |
  |  |  | |  3x CAN-FD transceiver (TCAN/1051-class)       |--> CAN1/2/3         | |
  |  |  | |  ISO RS-485 (CA-IS3092W, +iso pwr) ---------- |--> MOTOR-DRIVER BUS  | |
  |  |  | |  ISO RS-232 (ADM3251E) ---------------------- |--> NMEA OUT (3rd pty)| |
  |  |  | |  RS-232/TTL (SP3232EEN + TVS) --------------- |--> GPS IN / IMU      | |
  |  |  | |  section/switch I/O                           |--> SECTIONS          | |
  |  |  | +-----------------------------------------------+                      | |
  |  |  |                                                                        | |
  |  |  |  [GPS SLOT] --> STM32 UART (default; STM owns NMEA-OUT echo)           | |
  |  |  |             \-> CM GPIO/USB (alt jumper: raw NMEA on host)             | |
  |  |  |                                                                        | |
  |  |  |  CM4 LITE + NVMe (128GB M.2) = boot/root/data. NO SD, NO eMMC.         | |
  |  |  |  rpiboot provisioning: USB-OTG + nRPIBOOT jumper (also field recovery) | |
  |  |  +------------------------------------------------------------------------+ |
  |  +----------------------------------------------------------------------------+ |
  +==============================================================================+
```

---

## 3. Compute module (CM4)

| Decision | Choice | Rationale / hedge |
|---|---|---|
| Form factor | **CM4 Lite Wireless** (Pi or clone), DF40 ×2 mezzanine, no eMMC | De-facto standard footprint; Lite (no eMMC) offsets NVMe; **Wireless variant** for the WiFi module path |
| Connectivity | **Ethernet (RJ45, on-module GbE PHY) + WiFi (on-module + external RP-SMA antenna)** | AgOpen module network (PGN-over-UDP) — ESP32 sections + peripherals join here; metal case ⇒ external antenna |
| RAM | **1–2 GB**, low SKU | 8–11 %-of-Zero-2W load; lowest SKUs cheaper + more available in 2026 shortage |
| Root FS | Read-only + overlayfs (or A/B) on NVMe | Power loss can't corrupt OS partition; tiny write footprint |
| Boot / root / data | **NVMe (128 GB M.2), single medium** | More reliable than SD *and* eMMC; cheap; huge logging headroom |
| SD slot | **None** | Redundant + unreliable once NVMe is present; drop it (real-estate + reliability win) |
| Provisioning | **rpiboot/usbboot required** (USB-OTG + nRPIBOOT jumper) | Only path to image a bare Lite's NVMe + set BOOT_ORDER=6; also field re-image/recovery |
| Carrier layout | Accept any CM4 variant | Lead-time hedge; eMMC+NVMe is the no-fuss fallback if Lite provisioning is painful |

---

## 4. Storage

- **Single medium: NVMe (128 GB M.2) on a CM4 Lite. No SD, no eMMC.** A **Lite CM4 boots directly
  from NVMe** with no SD and no eMMC once the bootloader is configured. NVMe is more reliable than
  SD *and* eMMC, 128 GB is cheap with huge logging headroom, and the Lite's lower price offsets
  part of the NVMe. With read-only root + the STM32 clean-shutdown (§7), unclean-power-loss
  corruption is largely designed out.
- **Why NVMe — measured (NVMe vs SD, same rig):** the decisive gap is *writes*, which is the
  logging workload. Seq read 515.7 vs 62.7 MB/s (8.2×), 4K rand read ~49.8 vs ~7.3 MB/s (6.9×),
  **seq write 278.1 vs 15.0 MB/s (18.6×)**, **4K rand write ~81.3 vs ~2.3 MB/s (35.5×)**. Field
  logging *is* 4K random writes (coverage cells, position/section records) → the 35× case is the
  real one. SD also *stalls* (hundred-ms FTL hiccups) and wears out under sustained logging; NVMe
  has real wear-leveling + spare area (mostly-free 128 GB helps). Keep the write path **off the
  control-loop thread** regardless — NVMe makes that drain trivial but the decoupling is the rule.
- **No SD bootstrap paradox — why.** The CM4 (**Lite included**) has a **dedicated on-module SPI
  EEPROM** holding the bootloader + `BOOT_ORDER`, separate from eMMC/SD/NVMe. That's where "boot
  NVMe" persists — no boot medium required to store it. (NOT the eMMC — earlier note corrected.)
- **Provisioning (the one design consequence):** `rpiboot` writes that EEPROM at the **boot-ROM
  level over USB — nothing boots first**: (1) assert **nRPIBOOT** jumper, power on → SoC mask ROM
  enters USB-device mode (silicon, no medium needed); (2) host `rpiboot` flashes SPI EEPROM with
  `BOOT_ORDER=…6…`; (3) same session, mass-storage-gadget exposes the NVMe → write the OS image;
  (4) remove jumper, power-cycle → bootloader reads mode 6 → PCIe → boots NVMe. The SD's old
  "boot once to run rpi-eeprom-config" job is fully replaced by rpiboot. So the carrier **must**
  wire the CM4 **USB-OTG/slave + nRPIBOOT** (micro-USB + jumper — smaller than an SD slot); it
  also *is* the **field re-image / recovery** path. M.2 socket = "pull SSD → image on laptop →
  replug" fallback if rpiboot is fussy (setup is "a little involved" per Geerling).
- **Production flow (the two steps are independent):**
  1. **Set `BOOT_ORDER=6` once per CM4** at incoming inspection (rpiboot over USB, nRPIBOOT). The
     SPI EEPROM then remembers NVMe forever — **no per-unit rpiboot at assembly.**
  2. **Bulk-image NVMe drives externally** on a host PC via a USB-to-NVMe adapter (drives out of
     the board).
  3. **Final assembly = seat a pre-imaged drive.** No CM touch, no bootloader step.
  rpiboot only returns for a bootloader update or field recovery. (For sealed units / no-pull
  recovery, use rpiboot's mass-storage-gadget to image the NVMe in place instead of pulling it.)
- **NVMe choice:** decent 128 GB 2230/2242; cheap drives vary on power-loss behavior (clean-
  shutdown covers most); industrial/pSLC is the belt-and-suspenders option for continuous logging.
- **Fallback (no-fuss):** if Lite provisioning proves painful at volume, use the **smallest 8 GB
  eMMC module** (holds bootloader, boots instantly, root still on NVMe) — costs the eMMC saved.
- **PCIe routing:** 3 length-matched 100 Ω diff pairs (TX/RX/REFCLK) + AC caps + PERST#/CLKREQ# +
  a 3.3 V rail. Gen2 ×1 is the forgiving end of PCIe; copy the **open-source Raspberry Pi CM4IO
  KiCad** layout. Verify any CM4 *clone* actually exposes PCIe.

---

## 5. Thermal

- CM4 is ~5–7 W (CM5 ~10 W) vs 25 W+ for x86 → **fanless, conduct to the enclosure.**
- CM SoC heat-spreader → **compressible thermal gap pad / putty (0.5–1 mm)** → machined boss on
  the aluminum case lid. *Not* rigid metal-to-metal (tolerance stack-up + vibration).
- STM32 needs no cooling.

---

## 6. MCU — STM32G473RCT6

**Driver of the choice: 3× CAN.** G431 has only 1× FDCAN. The 3×FDCAN lines are **G473/G474**
(G491 has only 2). G473 chosen over G474 because the only G474 delta is HRTIM (unused for
steering) and on JLC's assembly side the G473RCT6 is both cheaper and far better stocked.

| Part | Pkg | Flash | FDCAN | JLC stock (2026-07) | JLC price | Verdict |
|---|---|---|---|---|---|---|
| **STM32G473RCT6** (C529361) | LQFP64 | 256 KB | **3** | 2,243 | $3.67 (→$2.31 @1k) | **Pick** |
| STM32G474RET6 (C521608) | LQFP64 | 512 KB | 3 + HRTIM | 410 | $8.50 | HRTIM unused, ⅕ stock |
| STM32G431CBT6 (C529355) | LQFP48 | 128 KB | 1 | 3,556 | $3.37 | Only 1 CAN — rejected |

**Longevity:** G4 is on ST's 10-year Longevity Commitment (read as runway from ~2019 intro),
Active status. Confirm not NRND at order time.

**Family footprint hedge:** lay out the **G473/G474 R-line LQFP64** footprint — it accepts a
pin-compatible ladder: G473RCT6 (256 KB) → G473RET6 (512 KB) → G474RCT6/RET6 (adds unused
HRTIM). Four+ populate options on one footprint.

**Escape hatch:** STM32 pinout also roughly fits GD32/AT32 clones (re-validate firmware; treat
as supply insurance, not a true second source).

**Firmware portability = risk reduction:** Cube LL/CMSIS, standard peripherals only (TIM PWM,
ADC/op-amp current sense, FDCAN, one USB device to CM, GPIO watchdog). No part-unique
peripheral → density/clone swaps stay cheap.

**Package:** LQFP (not QFN/BGA) for hand-rework and broad stock.

---

## 7. Power & clean-shutdown

Chain: `12V --> Buck 5V (always-on) --> [-> STM32 + LDO] + [e-fuse(PI_PWR_EN) -> CM4]`

**Topology decided (Option A — single always-on buck).** One 5 V buck free-runs whenever VIN is
present; the STM + 3V3 LDO hang directly off `5V_MAIN`; the CM leg is `5V_MAIN → eFuse → 5V_CM`
with the eFuse as the gate. The two-stage "tiny always-on buck so the main buck can fully sleep"
alternative was dropped: VIN is vehicle-switched, so "sit powered with the CM off" isn't a real
scenario, and fewer parts wins.

**Input-stage voltage decided (12 V system, 40 V operating).** 40 V is the 12 V-automotive point:
survives a 24 V jump-start, and a **SMBJ24A** TVS clamps load-dump to ~39 V. The **buck is 60 V-
rated** anyway (**TPS54560**-class) so that TVS clamp transient sits inside its rating — "40 V rail,
60 V silicon" is deliberate, not a mismatch. A true **24 V-native** install load-dumps to ~58 V and
needs the *populate variant*: raise the TVS (SMBJ33/48), keep the 60 V buck, re-ratio `VIN_SENSE`.
Footprints stay variant-ready; default BOM is 12 V.

1. **Boot** — 5V rail up → STM32 boots (always-on) → asserts `PI_PWR_EN` → e-fuse feeds CM4.
2. **Run** — CM4 ↔ STM32 exchange PGN over the USB trace; STM32 watches `VIN_SENSE`.
3. **Power loss** — `VIN_SENSE` drops → STM32 sends `SHUTDOWN` PGN → CM4 halts (read-only root =
   almost nothing to flush) → **input-side hold-up cap** keeps the rail up through unmount →
   STM32 de-asserts `PI_PWR_EN`.
4. **Failsafe** — watchdog timeout (CM hung / kernel panic / rebooting) → STM32 **independently**
   de-energizes steering + sections. The failsafe cannot live on the host — it guards *against*
   the host.

**Size at layout:** hold-up cap (spans CM halt time); e-fuse rating (CM4 inrush + steady).

---

## 8. CAN (3× FDCAN)

- 3× FDCAN native on the G473. **FDCAN1 default pins PA11/PA12 collide with USB_DM/DP** — move
  **FDCAN1 to PB8/PB9** and keep USB on PA11/PA12. LQFP64 has the pins to break out all three
  + USB cleanly (a reason the package is LQFP64, not 48).
- **3× CAN-FD transceivers** — **TCAN1042VDRQ1 (JLC C485806)**, 5 V VCC + VIO=3.3 V, FD 5 Mbps,
  ±58 V bus-fault (VCC on the always-on `5V_MAIN` so CAN survives CM-down). Bus TVS = NUP2105L
  (C284104) + optional split-term + DNP common-mode-choke footprint. *(Note: TJA1051/1042 are
  classic CAN, only 1 Mbps-guaranteed — not FD-qualified; TCAN1042V is.)* Three CAN connectors +
  transceivers eat board edge — place early. Full detail: `HARDWARE_AIO_NETLIST.md` §Page 4.
- Motor drivers may live on CAN *or* the RS-485 bus (§9) depending on the driver — the board
  supports both.

---

## 9. Serial front-end (RS-232 / RS-485) — **isolated where available**

The G473 has 5 USART/UART + LPUART1 (~6 channels) — ample. Isolate every link that crosses into
a foreign ground domain.

### GPS-out design (the important bit)

Third-party tools vary — **some want raw GPS, some want fused; raw is the more popular format.**
So the out-feed is a **configurable mode**, not a fixed behavior. The **always-on STM32 owns the
NMEA-OUT UART**:
- **Default = raw echo.** STM32 re-emits GPS NMEA out the RS-232 port — keeps flowing
  **even if the CM is booting/wedged/off**; matches the common case; STM32 boots to this before
  the CM is up.
- **Fused mode** (config switch, pushed from the app over PGN): STM32 emits the CM's fused
  sentences (position + heading + roll / PANDA), and **auto-falls back to raw if the CM drops**,
  so the third party never goes dark.
- **Firmware echo, not a hardware Y-split** — even in raw mode this lets the STM32
  **re-clock to a different output baud** (tools often want 4800/9600 regardless of the GPS's
  baud) and **filter to a sentence subset** (e.g. just GGA/VTG/RMC). A hardware passthrough
  can't do either. "Raw" = raw content, independently clocked/filtered.
- **Second RS-232 port comes free** — the NMEA-out SP3232 has a 2nd channel (§Page 5), usable as a
  2nd NMEA-out consumer (raw + fused) *or* an external RS-232 IMU/GPS input, config-dependent. A
  3rd concurrent port = a 2nd SP3232 (populate-optional). Isolation on any of these = the DNP
  ADM3251E/discrete footprint (see isolation policy below).

### Transceiver selection (JLC stock verified 2026-07)

| Link | Part | JLC # | Stock | ~Price | Notes |
|---|---|---|---|---|---|
| **Motor-driver RS-485** | **CA-IS3092W** | C2890051 | 12,426 | $3.22 | **Isolated + integrated iso power**, half-duplex; use STM32 USART DE mode |
| RS-485 (alt) | ADM2587EBRWZ | C12081 | 1,915 | $4.39 | Isolated + iso power backup |
| RS-485 (avoid) | CA-IS3082W | C528766 | **0** | $1.06 | Cheaper but out of stock + no iso power |
| **NMEA-OUT RS-232** | **ADM3251EARWZ** | C579198 | 444 | $8.53 | Isolated single-channel + iso power; SOIC-20. The one line that truly needs isolation (foreign ground). Modest stock — see hedge |
| GPS-in / IMU RS-232 | SP3232EEN-L/TR | C9378 | 89,365 | $0.34 | Non-isolated, ±15 kV ESD, 2drv/2rcv. Board-powered modules share ground → TVS is enough |

**Isolation policy (updated — see `HARDWARE_AIO_NETLIST.md` §Page 5):**
- **Isolate the motor-driver RS-485** (CA-IS3092W) — always. It crosses to the drive's ground.
- **NMEA-out = non-isolated by default (SP3232EEN + SMAJ12CA TVS).** Decision reversed from "always
  isolate": every integrated iso-RS-232 part is thin/dead at JLC (ADM3251E 444, ISOW7841 0–34,
  ADM3252E 11), and most third-party tools share the tractor chassis ground. **Isolated ADM3251E
  (C579198) / discrete (SP3232+ISO7721 C366164 + iso-DCDC) kept as a DNP populate-option** for
  installs that genuinely need galvanic isolation.
- **One SP3232 (2 ch) serves NMEA-out + the external RS-232 port** (external IMU/GPS, shared ground).
- **GPS = on-board TTL multi-module slot** (1× ArduSimple Arduino-Uno footprint / 2× F9P / 1× UM98x)
  → STM UART direct, no RS-232 driver. **IMU** arrives via the GPS (UM981/2 INS) or external
  RS-232/RS-485/CAN — no dedicated IMU part.

### RS-485 bus housekeeping
- Half-duplex 2-wire default; STM32 USART **Driver-Enable (DE) auto-direction**.
- 120 Ω termination as **populate-optional** (only if board is at a bus end) + fail-safe bias.

---

## 10. Protection (all off-board lines)

- **TVS / ESD** on every off-board conductor — folded into the front-end block.
- Load-dump / reverse-polarity protection on the **12 V** input ahead of the buck (24 V = variant).
- Isolation (§9) handles ground-loop/transient coupling on the long motor + third-party runs.

---

## 11. BOM — full detail in companion docs

The complete per-page BOM + netlist (all parts, JLC #s, stock, hand-fit flags) live in
**`HARDWARE_AIO_BOM.md`** and **`HARDWARE_AIO_NETLIST.md`** — 7 schematic pages, every active part
stock-verified live 2026-07-04. Headline verified spine:

| Function | Part | JLC # |
|---|---|---|
| Host | **CM4101000** (1 GB Lite Wireless) | hand-fit |
| Storage | 128 GB M.2 NVMe (2230/2242) | — |
| MCU + HSE | STM32G473RCT6 + 8 MHz XTAL | C529361 + C2682775 |
| 5 V buck / catch diode | TPS54560DDAR + SS56C | C31966 + C123948 |
| M.2 3V3 buck / CM eFuse / 3V3 LDO | TPS563201 / TPS259571 / RT9080-33 | C116592 / C471038 / C841192 |
| Rev-pol / TVS / power conn | IRFR5305 / SMBJ24A / screw | C2624 / C87268 / C8465 |
| DF40 ×2 / M.2 / USB mux / RJ45 | DF40C-100DS / 91302-55 / TS3USB221 / HR911130C | C597931 / C2922444 / C130085 / C50933 |
| CAN-FD ×3 + bus TVS | TCAN1042VDRQ1 + NUP2105L | C485806 + C284104 |
| Iso RS-485 / RS-232 ×2 | CA-IS3092W / SP3232EEN | C2890051 / C9378 |
| Steering opto / WAS ESD / input array | 6N137S / ESD9B5V / SRV05-4 | C5123515 / C2905646 / C558418 |
| HMI: OLED / reset / piezo+FET | SH1106 (hand-fit) / TS-1187A / PS1240+2N7002 | C318884 / C76871+C8545 |

---

## 12. Decisions — RESOLVED (layout-time TBDs remain)

**All pre-layout architecture decisions are closed** (details in the netlist/BOM per-page notes):
- **GPS-out** — configurable mode, raw default + fused fallback; **non-isolated SP3232 default**,
  iso ADM3251E/discrete = DNP option. (§9, Page 5)
- **GPS routing** — default GPS→STM32→PGN→CM. **GPS slot = on-board TTL multi-module** (ArduSimple /
  2× F9P / UM98x); **IMU via GPS/RS-232/RS-485/CAN**, no dedicated part. (Page 5)
- **Storage** — CM4 Lite + 128 GB NVMe, no SD/eMMC; rpiboot provisioning. (§4)
- **Sections** — **none on-board**; external ESP32 over the module network. (Page 6)
- **Module network** — Ethernet (RJ45, on-module PHY) + WiFi (CM4 wireless + ext antenna). (Page 2)
- **Motor amp** — off-board; reachable via opto PWM/DIR/EN + isolated RS-485 + CAN. (Page 6)
- **Power** — single always-on 60 V buck + eFuse gate; **12 V system / 40 V** (24 V = variant). (§7)
- **CAN transceiver** — TCAN1042VDRQ1 (C485806). (Page 4)
- **HMI** — STM-owned 1.3" OLED + recessed reset + in-case piezo. (Page 7)
- **Connectors** — Deutsch DTM13 hand-soldered (IP67); antennas on back panel. (Page 7)

**Layout-time TBDs (not decisions — values/mechanical):** hold-up cap C_HU + F1 fuse + eFuse ILIM;
all inductor/cap/feedback passive values; DS12712 pin-number final eyeball; connector pinouts +
which controller-output modes to break out; panel mechanical (OLED window, probe-hole, bulkheads);
copy CM4IO KiCad for DF40/PCIe/Ethernet routing; re-verify all JLC stock at order.
