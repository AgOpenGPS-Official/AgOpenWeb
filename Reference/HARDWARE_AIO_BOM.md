# AgOpenWeb AiO Board — BOM (as-built, 2026-09-14)

> Bill of materials generated from `Netlists From EasyEDA/Full-board_2026-09-14.net`: **151 netlist
> parts**, plus the hand-fit items that aren't netlist parts. Companion docs: `HARDWARE_AIO_BOARD.md`
> (why), `HARDWARE_AIO_ISSUES.md` (open issues), `HARDWARE_AIO_NETLIST.md` (wiring). Designators are the real EasyEDA ones.
> JLC upload file: `HARDWARE_AIO_BOM_JLCPCB.csv`.
>
> **JLC part numbers** come from the EasyEDA BOM export (`PCB From EasyEDA/BOM_AOW-v2.0_2026-09-14.csv`),
> i.e. the parts actually attached to the footprints. Stock counts are **not** listed — re-check at
> order time.
>
> **Scale: this is a one-off — 1–2 boards for the designer's own use, not a product.** Where this doc
> carries production-flavoured advice (multi-vendor approvals, buy-ahead quantities, per-unit
> provisioning flow), read it as background, not as required process.
>
> **Pending issues that affect this BOM** (`HARDWARE_AIO_ISSUES.md`): S3 may swap U20 to STWD100NYWY3F;
> S6 added 120 Ω + solder jumper per CAN channel; **S9 adds Q3 = BSS84/DMG2301L P-FET (SOT-23) + R80 100 kΩ**
> (and moves R79 to GND); S10 adds R81 330 Ω (C25104, existing value); S12 (U19 vs DF40); P8 (L2 footprint). JLC part for the
> P-FET: look up at capture — BSS84 and DMG2301L are both common SOT-23 parts.

> **⚠ Newer export available.** These tables describe `Full-board_2026-09-14.net`. Since then S1–S4, S6,
> S9, S10, S12, S2 and S13 have been fixed — see `Full-board_2026-09-17.net` and
> `HARDWARE_AIO_ISSUES.md`. Regenerate from the 09-17 export when convenient.

---

## 1. Active & special parts

| Ref | Qty | Part | Footprint | JLC # | Block | Notes |
|---|---|---|---|---|---|---|
| U1 | 1 | TPS54560DDAR | SOIC-8 EP | C31966 | Power | 60 V / 5 A buck, 400 kHz |
| D1 | 1 | SS56C | SMC | C123948 | Power | buck catch diode |
| L2 | 1 | Sunlord MWSA1004S-6R8MT 6.8 µH | footprint named MDA1054HT 11×10 | C408485 | Power | **part is 10×10 on an 11×10 footprint — check the land pattern** (P8) |
| U2 | 1 | TPS259571DSGR | WSON-8 2×2 | C471038 | Power | CM eFuse |
| U3 | 1 | RT9080-33GJ5 | TSOT-23-5 | C841192 | Power | 3.3 V LDO |
| U4 | 1 | TPS563201DDCR | SOT-23-6 | C116592 | Power | NVMe 3.3 V / 3 A buck |
| L3 | 1 | Sunlord SWPA4030S3R3MT 3.3 µH | footprint named SMNR4012 4×4 | C15269 | Power | both 4×4 mm — confirm pads |
| Q1 | 1 | IRFR5305TRPBF | TO-252 | C2624 | Power | reverse-polarity P-FET. **Orientation issue: S1** |
| TV1 | 1 | SMBJ24A | SMB | C87268 | Power | input TVS |
| D2 | 1 | BZT52C12-7-F | SOD-123 | C124196 | Power | Q1 gate zener. **Placement issue: S2** |
| D4 | 1 | B5819W | SOD-123 | C8598 | Power | VIN_SENSE clamp |
| U5 | 1 | 91302-55-067R2M | M.2 M-key 5.5 mm | C2922444 | CM4 | NVMe socket |
| L1 | 1 | HR911130C | RJ45 THT | C50933 | CM4 | GbE magjack |
| U7, U8, U9 | 3 | MCP251863T-E/SS | SSOP-28 | C5226885 | CAN | CAN FD controller + transceiver |
| X1 | 1 | YXC OT2EL4C4JI-111OLP-40M, 40 MHz 3.3 V | 3225 4-pin | C5203551 | CAN | CAN clock |
| D5, D6, D7 | 3 | NUP2105L | SOT-23 | C284104 | CAN | bus TVS |
| U21 | 1 | ADC128S102CIMTX/NOPB | TSSOP-16 | C179666 | Analog | 8-ch 12-bit SPI ADC |
| U12 | 1 | SP3232EEN-L/TR | SOIC-16 | C9378 | Serial | RS-232 ×2 |
| D9–D12 | 4 | SMAJ12CA-13-F | SMA | C134948 | Serial | RS-232 line TVS |
| D13, D16 | 2 | ESD9B5V | SOD-923 | C2905646 | Field I/O | WAS / current ESD (low leakage) |
| D14, D17, D18 | 3 | SMAJ16A | SMA | C283886 | Field I/O | switch-input TVS. Thin stock in July — approve alternates (Littelfuse C74561) |
| D15 | 1 | SRV05-4 | SOT-23-6 | C558418 | Field I/O | logic-side ESD array |
| U20 | 1 | STWD100NXWY3F | SOT-23-5 | C1852782 | Supervision | watchdog, t<sub>WD</sub> 102 ms. **S3: consider STWD100NYWY3F (1.6 s)** |
| U22 | 1 | SN74AHCT1G125DBVR | SOT-23-5 | C7484 | HMI | 3.3 → 5 V LED data buffer |
| D19–D22 | 4 | SK6812SIDE-A | 4020 side-view | C5378721 | HMI | addressable RGB status LEDs |
| D23 | 1 | KENTO KT-0603YG (symbol says XL-0603QYGC) | 0603 | C2289 | HMI | power LED |
| Q2 | 1 | 2N7002 | SOT-23 | C8545 | HMI | piezo driver |
| BUZZER1 | 1 | TDK PS1240P02BT (passive piezo, 4 kHz) | BUZ-TH_BD12.5 | C76871 | HMI | same part as July |
| SW1 | 1 | C&K PTS645VH83-2LFS | 6×6 TH tactile | C221889 | HMI | reset (July used SMD TS-1187A C318884) |
| H1 | 1 | 1×3 2.54 mm female header | HDR-F 1×3 | C146690 | CM4 | Linux console |
| H3 | 1 | 2×2 2.54 mm female header | HDR-F 2×2 | C239342 | CM4 | USB + nRPIBOOT provisioning |

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
| 52.3 kΩ 1 % | 0402 | 1 | R7 (buck FB top) | C26982 | |
| 33 kΩ 1 % | 0402 | 1 | R8 (NVMe buck FB top) | C25779 | |
| 100 kΩ | 0402 | 3 | R40 (Q1 gate), R42 (VIN_SENSE top), R43 (eFuse EN pull-up) | C25741 | |
| 10 kΩ | 0402 | 12 | R15, R47, R48, R49, R50, R51, R59, R61, R62, R69, R74, R75 | C25744 | |
| 4.7 kΩ 1 % | 0402 | 1 | R63 (watchdog EN pull-up) | C2906869 | FOJAN FRC0402F4701TS |
| 1 kΩ | 0402 | 8 | R13, R14 (Eth LEDs), R58, R60, R70 (switch series), R72, R73 (ADC RC), R79 (power LED) | C11702 | |
| 470 Ω | 0402 | 1 | R32 (piezo damping) | C25117 | |
| 330 Ω | 0402 | 4 | R27, R28, R29 (steering), R71 (LED data) | C25104 | |
| 100 Ω | 0402 | 1 | R33 (piezo gate) | C25076 | |
| 33 Ω 1 % | 0402 | 3 | R64, R65, R66 (CAN clock series) | C2906868 | FOJAN FRC0402F33R0TS |

---

## 3. Hand-fit / off-netlist items

| Item | Part | Notes |
|---|---|---|
| CM4 module (U19) | CM4101000 (1 GB Lite Wireless), or any CM4 Lite variant | RPi reseller; hand-seated |
| CM4 mezzanine ×2 | **DF40C-100DS-0.4V(51)**, JLC C597931 | **not in the netlist or EasyEDA BOM** — U19's footprint carries both land patterns (pins 1–100 / 101–200) but its attached part is the CM4 module (C20754863). Exclude U19 from assembly; add `J_CM_A` at 56.41, 86.23 mm and `J_CM_B` at 90.41, 86.23 mm (pick-and-place coordinates), rotation as U19. Needs JLC's 0.4 mm fixture. See S12. |
| NVMe SSD | 128 GB M.2 2230/2242 | pre-imaged |
| M.2 standoff + M2 screw | plated hole + hand-fit standoff | JLC SMT standoffs unreliable |
| GPS module (U16 **or** P2) | UM982EB module **or** ArduSimple simpleRTK2B | populate one (S7) |
| J1 connector | Amphenol ATS13-26PA-BM01 + mating ATS06-26SA plug and size-20 contacts | hand-soldered right-angle, front panel |
| Input fuse | in-line blade holder in the harness | not on the board |
| Antennas | U.FL pigtails → SMA (GPS) / RP-SMA (WiFi) bulkheads | back panel |
| Thermal | 0.5–1 mm gap pad on CM4 SoC | enclosure boss |

---

## 3a. CM4 mezzanine connector — sourcing (checked 2026-09-17)

**C597931 (Hirose DF40C-100DS-0.4V(51)) is out of stock at LCSC** (notify-me; ref price $0.73–1.09, no
discontinued flag). The (58) variant **C3642394 has 1 unit** — effectively unavailable, 1000/reel.
The part is chronically short at Digi-Key/Mouser/Farnell too (Raspberry Pi forum thread t=325030).

| Option | Part | Price | Status |
|---|---|---|---|
| **Leading candidate** | **LBF15-G100S-B0R02** (LXWCONN) — **C52269441** | $0.50–0.95 | **4,425 in stock** (~2,200 boards). **Land pattern verified identical** to the pads already on the board (below). Datasheet: `PCB From EasyEDA/LXWCONN_LBF15-G100S-B0R02_C52269441.pdf` |
| LCSC substitute 2 | **GT-B0403FSV14-100B1101** (G-Switch) | ~$1.43 | ~1387 in JLC stock (693 boards); land pattern not checked |
| Consign | Hirose from Mouser / Digi-Key / Farnell | — | JLC accepts customer-supplied parts for a fee; only 2 per board |
| Hand-solder | Hirose, any source | — | 0.4 mm pitch, hot air + flux + drag; 2 parts per board |
| Last resort | DF40HC(3.0)/(4.0)-100DS variants | — | changes module height → standoffs, gap-pad thickness and enclosure all shift |

### Sourcing strategy (2026-09-17)

**At 1–2 boards, the whole strategy is: buy 4 Hirose DF40C-100DS-0.4V(51) from DigiKey/Mouser** (2 per
board + spares, days not weeks), **delete U19's row from the BOM and CPL**, and **hand-solder the two
connectors** (order the stencil, ~$8 — paste + hot air, the part self-aligns) or consign them to JLC.
No clone, no test-mate risk, no stock timing, and §3b's BOM/CPL additions aren't needed because JLC
isn't placing them. Order ~5 bare boards rather than 2 — nearly the same price, and spares survive
rework mistakes. Everything below is the volume picture, kept for reference.



**Treat this as a multi-source part, not a single line item.** The pads are the standard DF40 land
pattern, so Hirose (51)/(54)/(58), the LXWCONN LBF15 and (pending a pad check) the G-Switch part all
fit the same footprint. **Approve them all as alternates** so the build uses whatever is in stock that
week; the design is not tied to C52269441.

Ordering from China takes long enough that stock can disappear mid-cycle, so:

1. **Don't sequence sample → test → production order.** Buy the build quantity *with* the samples in one
   order. 100 pcs ≈ $58 covers 50 boards plus spares — cheaper than a stock-out.
2. **First prototypes: buy Hirose domestically** (DigiKey / Mouser / Arrow list it; days, not weeks).
   Guaranteed mating with the CM4, so cross-mate risk stays off the bring-up critical path. 2 per board.
   The clone then becomes the volume/cost fallback, testable at leisure.
3. **Getting them fitted:** domestic parts can be consigned to JLC, or hand-soldered for the first boards.
4. **JLC allocates parts when the order is placed**, not at upload — place an assembled order while stock
   shows rather than uploading and deciding later.

*(Distributor stock at DigiKey/Mouser/Arrow could not be verified here — those sites block automated
fetches. Check before relying on step 2.)*

**C52269441 (LXWCONN) vs the board — checked against the datasheet 2026-09-17:**

| Dimension | LBF15-G100S-B0R02 | U19 pads as drawn | |
|---|---|---|---|
| Pad pitch | 0.40 ± 0.02 mm | 0.40 mm | ✅ |
| Pad width | 0.20 ± 0.02 mm | 0.20 mm | ✅ |
| Pad length | 0.70 mm (3.78 − 2.38, halved) | 0.70 mm | ✅ |
| Row centre-to-centre | 3.08 mm (2.38 + 0.70) | 3.08 mm | ✅ |
| Pad array length (DIM B, 100 pos) | 19.60 mm | 19.60 mm | ✅ |
| Mated height | 1.50 ± 0.15 mm | 1.5 mm stack (CM4IO) | ✅ |
| Rating | 30 V, 0.3 A, −35 to +85 °C, 30 cycles | — | same as the Hirose part |

Body length (DIM A 22.60 mm) also matches the Hirose figures LCSC lists — it is built as a DF40 clone.
**No pad changes needed, so the S12b swap plan is unaffected.**

**⚠ Still unverified — the only real risk: the datasheet never claims DF40 compatibility.** It references
only LXW's own header (LBF15-G**P-B0R01). The CM4 side can't be changed — it ships with Hirose
DF40C-100DP plugs — so **buy two and test-mate with a CM4** before committing: full seating, correct
1.5 mm standoff, neither tight nor sloppy. Also confirm JLC will *assemble* it (0.4 mm fine-pitch
process), not just sell it.

> **Settle the connector before doing the S12b swap.** A clone with a different land pattern changes
> U19's pads as well, and both changes are better made together.

---

## 3b. Ordering notes — getting JLC to fit the DF40s, not the CM4 (S12)

JLC matches the BOM to the pick-and-place (CPL) file **by designator**; it never reads designators from
the Gerbers. So the two receptacles can be added as rows even though they aren't components in the PCB.

1. **Delete U19** from both the BOM and the CPL before uploading — a part in neither file is not sourced
   and not placed. (The parts-selection screen also has a per-line "do not place" toggle, but deleting
   the rows keeps the CM4 from ever being priced.)
2. **Add two rows to each file:**

   BOM:
   ```
   DF40C-100DS-0.4V(51),J_CM_A,DF40-100P-0.4mm,C597931
   DF40C-100DS-0.4V(51),J_CM_B,DF40-100P-0.4mm,C597931
   ```
   CPL (same convention as the EasyEDA export — Y from the bottom edge):
   ```
   J_CM_A,56.41mm,86.23mm,T,<rotation>
   J_CM_B,90.41mm,86.23mm,T,<rotation>
   ```
3. **Take the rotation from JLC's preview, not from U19.** It must match JLC's library orientation for
   C597931, which often differs from the design footprint. The parts-selection step renders each
   placement: confirm each connector sits over its pad array **and pin 1 is at the correct end** — a
   180° error lands pin 1 where pin 100 belongs.
4. **Silkscreen:** add `J_CM_A` / `J_CM_B` text and a pin-1 marker beside each pad group, so the BOM has
   something on the board to match. No nets change.
5. **Order remarks:** "U19 is the Raspberry Pi CM4 module outline — do not source or place. Fit two
   DF40C-100DS-0.4V(51) (C597931) onto the U19 pad arrays: J_CM_A on pins 1–100, J_CM_B on pins 101–200."
   Expect a CAM query anyway and answer with the same note.
6. **Confirm C597931 is available for assembly**, not just for sale — 0.4 mm pitch needs their fine-pitch
   process and was flagged as needing a fixture.

*(Written from how the JLC flow generally works; their UI wording changes, so the preview check in step 3
is the real verification.)*

---

## 4. Removed since the July BOM

For traceability. None of these are in the 2026-09-14 netlist.

| July part | JLC # | Why gone |
|---|---|---|
| STM32G473RCT6 + 8 MHz crystal, 15 pF load caps, ferrite, SWD header | C529361, C2682775, C1548, C1002 | MCU removed |
| TCAN1042VDRQ1 ×3 | C485806 | replaced by MCP251863 |
| CAN split-termination (60.4 Ω ×6, 4.7 nF ×3) + ACT45B CMC ×3 | C137954, C1538, C76584 | removed (S6) |
| CA-IS3092W isolated RS-485 + 120 Ω term | C2890051, C25079 | RS-485 removed |
| TS3USB221 USB mux + USBLC6-2SC6 + micro-USB | C130085, C7519 | STM USB link removed; H3 header instead |
| 1.3" SH1106 OLED + I²C pull-ups + page rocker header | — | replaced by SK6812 LEDs |
| TS-1187A SMD reset tactile | C318884 | replaced by PTS645 TH |
| 6N137S / LTV-357T optos | C5123515, C119089 | already dropped Jul 7 (direct drive) |
| SMAJ12CA D8 (5th RS-232 TVS) | C134948 | RS-485/NMEA line gone |
