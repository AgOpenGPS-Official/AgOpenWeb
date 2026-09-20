# AgOpenWeb AiO Board — Issue lists

> **The single place for open design issues**, one list for the schematic and one for the PCB layout.
> Other docs refer to these IDs (`S#`, `P#`) instead of repeating the details.
>
> Sources reviewed (2026-09-14): `Netlists From EasyEDA/Full-board_2026-09-14.net` and
> `PCB From EasyEDA/PCB_PCB_AOW-v2.0_2026-09-14.json`, checked against the CM4 datasheet (Release 4),
> MCP251863 DS20006624B, ADC128S102 SNAS298G and STWD100 DocID14134. Every schematic item below was
> re-verified pin-by-pin against the netlist.
>
> **Old IDs:** earlier revisions of `HARDWARE_AIO_BOARD.md` used F1–F12 (schematic) and L1–L8
> (layout); the "Was" column maps them. PCB coordinates are mm from the board's top-left corner.
>
> Status: **open**, **decide** (a choice for the designer rather than a defect), **fixed**, **retracted**.

---

## Schematic issues

### Must fix

| ID | Was | Issue | Evidence | Fix | Status |
|---|---|---|---|---|---|
| **S1** | F2 | **Q1 reverse-polarity P-FET is reversed.** | Q1.3 (source) on `VIN`, Q1.2 (drain, tab) on `VIN_PROT`. A P-FET's body diode runs drain → source, so with the battery reversed it conducts from `VIN_PROT` (held near 0 V by TV1's forward diode) into `VIN`. Nothing blocks reverse polarity. Present since the July design; confirmed on the PCB (tab pad on `VIN_PROT`). | Drain (pad 2, tab) → `VIN`, source (pad 3) → `VIN_PROT`. Layout rework: P7. | **fixed 2026-09-15** — confirm in the next export |
| **S2** | F3 | **Q1 gate-source voltage not clamped.** | D2 (12 V zener) is gate → GND. R40 100 k (VIN → gate) + R47 10 k (gate → GND) give Vgs ≈ −0.91 × VIN: −21.8 V at a 24 V jump start, −35 V at the 39 V TVS clamp. IRFR5305 V<sub>GS</sub> max ±20 V. | D2 cathode → Q1 source (`VIN_PROT` after S1), anode → `PGATE`. R40 top → `VIN_PROT`. R47 stays gate → GND. Layout rework: P7. | **fixed 2026-09-15** — confirm in the next export |

| **S13** | — | **Input TVS lost its ground (regression, 2026-09-17).** TV1 pin 2 is on `PGATE` instead of `GND`. | Correct in the 09-14 export; introduced during the Q1 gate rework. As wired there is **no load-dump clamp to ground** — surge current runs `VIN_PROT` → TV1 → `PGATE` → R47 (10 kΩ) → GND, which clamps nothing, and injects surge current into the gate node. | **TV1 pin 2 → `GND`.** | **fixed 2026-09-17** — verified: TV1 pin 2 on `GND` |

### Should fix

| ID | Was | Issue | Evidence | Fix | Status |
|---|---|---|---|---|---|
| **S3** | F4 | Watchdog timeout 102 ms is tight for Linux. | U20 = STWD100**NX**WY3F: t<sub>WD</sub> 102 ms (71–142 ms). "NY" = 1.6 s (1.12–2.24 s). | Fit STWD100NYWY3F (same footprint, open-drain), or commit to kernel-level kicking. | **fixed 2026-09-17** — verified: U20 is STWD100NYWY3F (1.6 s) |
| **S4** | F5 | SK6812 LED data on GPIO2, which has no PWM, PCM or SPI function. | WS2812-class timing from Linux uses PWM, PCM (GPIO21) or SPI0 MOSI (GPIO10). GPIO18's PWM0 is already the steering output. | Swap `LED_DATA` ↔ `SW_REMOTE` (CM4 pins 58 ↔ 25, GPIO2 ↔ GPIO21) to use PCM. | **fixed 2026-09-17** — verified: `LED_DATA` on CM4 pin 25 (GPIO21), `SW_REMOTE` on pin 58 |
| **S5** | F7 | J1 CAN pin order changed from July. | July: 14/16/18 = H, 15/17/19 = L. Now: 14/16/18 = L, 15/17/19 = H. | **Closed 2026-09-15: no harness built yet**, so there's nothing to reconcile. The J1 pinout in `HARDWARE_AIO_NETLIST.md` §5.1 (L on even 14/16/18, H on odd 15/17/19) is the reference for the harness when it is made. | closed |
| **S6** | F8 | No CAN termination footprints. | Each CAN net only touches its MCP251863, the NUP2105L and J1. July's split-termination and CMC footprints are gone. | **Fixed 2026-09-15:** a single 120 Ω across CANH/CANL per channel with a solder jumper, matching the other AiO boards. (Split termination + common-mode choke were the July design; the simpler jumper is the established AgOpenGPS approach.) | **fixed 2026-09-17** — verified: 120 Ω (R9/R10/R11) in series with a solder jumper across each CANH/CANL |
| **S7** | F9 | GPS modules share one UART. | U16.15/16 and P2.11/12 both on `GPS_RX`/`GPS_TX` (CM4 UART5). | **Closed 2026-09-15: only one GPS module physically fits**, so the shared UART can never be contended. No change needed. | closed |
| **S8** | F10 | **Brown-out halt: the board can't restart itself, and the hold-up caps can't cover a shutdown anyway.** | (a) C6+C7 = 940 µF on `VIN_PROT` gives only **13–19 ms** from 12–13.8 V to the buck's ~6 V cutoff at 3–5 W — a `poweroff` takes seconds, so VIN loss is an abrupt cut, not a graceful halt. (The July note claiming the hold-up covers the halt was wrong.) (b) After any software halt, the CM4 datasheet needs `GLOBAL_EN` (pin 99, unconnected) pulled low >1 ms or 5 V removed; the eFuse is tied permanently on (R43), so neither happens. | **DECIDED 2026-09-15: don't halt on VIN dips** (they're inevitable in a vehicle). Use `VIN_SENSE` for telemetry/warnings only, never `poweroff`; keep root read-only + overlay. The buck regulates down to ~5.5–6 V in, so cranking dips ride through, and an outright power loss simply reboots when VIN returns — no extra parts, and (b) becomes moot. **Only if a graceful shutdown is really wanted:** add supercap hold-up on `VIN_PROT` (≈ 0.07 F for 1 s, ≈ 0.22 F for 3 s at 4 W, 12 → 6 V — 100–250× the present 940 µF, plus a blocking diode and inrush limiting) **and** an auto power-cycle one-shot: watch `RUN_PG` (high while running) and pulse `GLOBAL_EN` low via an open-drain FET after it has been low ~2 s with VIN present (555 + Schmitt, or an ATtiny10 which can also hold off while the rpiboot jumper is fitted). The watchdog covers a *hung* kernel; this covers a *halted* one. Fault flag (`PI_FLT`) → **ADC IN3** (U21 pin 7, one of 5 spare grounded inputs) — no GPIO needed, and all 28 are allocated. | **decided** — no board change; **revisited 2026-09-20: §S8c** (always-on + keyed 12 V, preferred) and **§S8b** (battery hold-up, fallback) |
| **S9** | F11 | Power LED on an unbuffered CM4 pin. | CM4 datasheet: `PI_LED_nPWR` (pin 95) "needs to be buffered". D23 is driven directly (~1.3 mA via R79). | **DECIDED 2026-09-16: single P-channel high-side buffer.** Q3 = BSS84 / DMG2301L (SOT-23, pin 1 = G, 2 = S, 3 = D — same numbering as the 2N7002; a 2N7002 can't be used alone because the pin is active-low and an N-FET needs a high gate). **Q3.1 (G) → `PI_LED_NPWR`** (CM4 pin 95); **Q3.2 (S) → `+3V3`**; **R80 100 kΩ gate → source** (holds it off while pin 95 is high-Z); **Q3.3 (D) → D23.2 (anode)**, new net `LED_PWR_A`; **D23.1 (cathode) → R79**, and **R79's other end moves from `+3V3` to `GND`**. So D23 reverses orientation and the CM4 pin only drives a gate. Current at 3.3 V with a ~2 V Vf: 1 kΩ → 1.3 mA; use 470 Ω (C25117, already on the board for R32) for ~2.7 mA. | **fixed 2026-09-17** — verified: Q3 BSS84 (C114481) G/S/D correct, R80 100 k gate–source, D23 reversed, R79 470 Ω → GND |
| **S10** | F12 | `RUN_PG` driven hard to GND. | CM4 datasheet: drive low "via a 220 Ω resistor". SW1 and U20 WDO connect straight to pin 92. | **DECIDED 2026-09-16: 330 Ω (C25104, already on the board for R27–R29/R71) — no 220 Ω line added.** New R81 between **CM4 pin 92** and the **SW1 / U20.1 (WDO) node**: pin 92 keeps its own net, the switch and watchdog share the far side. With the CM4's 10 kΩ internal pull-up, pulling through 330 Ω gives 3.3 × 330/10330 ≈ **0.11 V** (well under V<sub>IL</sub>) and caps the current at ~10 mA. **Not in SW1's ground leg** — that would leave U20's WDO still pulling pin 92 hard to GND on every watchdog reset; one resistor in the pin-92 net covers both pull-downs (two resistors, one per leg, is equivalent if SW1 and U20 end up far apart). | **fixed 2026-09-17** — verified: R81 330 Ω between CM4 pin 92 and the SW1 / U20 WDO node (`RUN_PG_G`) |
| **S11** | F6 | Switch inputs only handle switches to ground. | 12 V on J1.7–9 → 1 k → SRV05-4 clamp at ~+3V3 + V<sub>F</sub> ≈ 4 V, above the CM4 GPIO max of 3.8 V, pushing ~8–10 mA into +3V3. | **DECIDED 2026-09-16: contact-to-ground only — no change to the circuit.** The 10 k pull-ups to +3V3 (R59/R61/R69) and the 1 k series resistors already suit dry contacts; SMAJ16A + SRV05-4 stay as field protection. **Do not apply 12 V-level signals to J1.7/8/9** — say so in the harness documentation and, if there's room, on the silkscreen. | **decided** — no board change |

### S8b — key-off shutdown: LiFePO4 hold-up + self-cut — **ALTERNATIVE 2026-09-20**

> **Superseded as first choice by §S8c** (always-on + keyed 12 V) on the same day. Kept as the
> fallback for an install where a second wire to constant 12 V can't be run, and because it is the
> only option that also survives the battery isolator being thrown right after key-off.

> Revisits S8's "no graceful shutdown" decision. S8 costed hold-up in **supercaps** and found it
> expensive (≈ 0.07 F for 1 s, ≈ 0.22 F for 3 s). A **single LiFePO4 cell** changes the arithmetic
> enough to be worth a second look: minutes of hold-up for one cell plus a charger, which buys a
> behaviour supercaps can't — *ride-through*, where a brief key-off is not a shutdown at all.
> **Nothing here is decided and no board change is authorised yet.** Open questions in §S8b.5.

#### S8b.1 — The problem it solves

Operators key off without warning. As built, that is an abrupt power cut: the root FS survives
(read-only + overlay, S8) but the current job's in-flight writes to the data partition don't, and
every key-off is an unclean shutdown.

#### S8b.2 — Energy budget

| Quantity | Value | Source |
|---|---|---|
| Load (CM4 + board) | 3–5 W; **5 W** assumed | `HARDWARE_AIO_BOARD.md` §7 (CM4 ~5–7 W) |
| Shutdown time | **TBD — measure** (§S8b.5); assume 15 s | `systemctl poweroff` on the real image |
| Ride-through before committing | 10–20 s | proposed here |
| Energy for 20 s + 15 s = 35 s | ~175 J ≈ 0.05 Wh | 5 W × 35 s |
| Cell: 18650 LiFePO4, 1500 mAh, 3.2 V | **4.8 Wh** | ~100× the requirement |
| Cell current at 5 W, 81% end-to-end | **~1.9 A = 1.3C** | see §S8b.3 for the conversion path |
| Charge returned per event | ~8 mAh (**0.5% of capacity**) | cell ages by calendar, not cycles |

**Capacity is not the constraint — discharge rate is.** The cell is sized so 1.9 A is a relaxed ~1.3C
with cold margin, not for runtime. That rules out the small sizes the energy sums would allow: a
14430 (~400 mAh) needs 4.3C and typical cells of that size are rated ~1C; a 100 mAh cell would need
17C. A 14500 (~600 mAh, 2.8C) is possible *only* with a cell explicitly rated for it.

#### S8b.3 — Where the cell injects

Two options; **(a) is the recommendation**.

**(a) Boost into `VIN_PROT` (upstream of U1).** A boost converter, cell → ~11 V, diode-ORed onto
`VIN_PROT`. It idles while the vehicle holds 12–13.8 V and takes over on its own as VIN falls past
11 V — no switchover logic, no contention with U1, and every board rail keeps its existing
regulation. Costs a second conversion (boost ~90% × buck ~90% ≈ 81%), which the energy budget
absorbs. Boost must supply ~0.6 A at 11 V from ~1.9 A in.

**(b) Boost into `5V_MAIN` (downstream of U1), ORed by an ideal-diode FET.** One conversion (~90%,
cell ~1.7 A), but the boost output must sit *below* U1's 5.0 V (≈ 4.85–4.9 V) so it only sources
once the buck collapses, and `5V_MAIN` then runs at ~4.9 V during hold-up — inside the CM4's
4.75–5.25 V, but with less margin across the eFuse drop. A plain Schottky instead of an ideal diode
costs ~0.6 W at 1.7 A.

#### S8b.4 — Sequence, and how the board restarts itself

`VIN_SENSE` → ADC IN2 already lets Linux see VIN fall (S8), so **no new signal to the CM4 is needed**
and no GPIO is required — all 28 are allocated.

1. **VIN present.** Charger floats the cell; boost idle.
2. **VIN lost.** Boost takes over within the existing 940 µF / 13–19 ms hold-up. The CM4 keeps
   running: nothing has happened yet.
3. **VIN returns before T_ride (10–20 s).** Nothing happened, by design — a restart to move a wagon
   never becomes a shutdown. Cranking dips are covered twice over (U1 regulates to ~5.5–6 V in).
4. **VIN still absent at T_ride.** Linux, watching ADC IN2, runs `poweroff`. This is the one place
   S8's 2026-09-15 rule ("never `poweroff` from `VIN_SENSE`") is deliberately relaxed — a *sustained*
   loss, not a dip, and only with the cell fitted. **Without the cell the rule stands unchanged.**
5. **Halt detected.** The one-shot watches `RUN_PG` (high while running); ~2 s low → **disable the
   boost**. Power is fully removed, which is exactly what the CM4 datasheet asks for. **This is why
   the battery path must cut itself: held up but halted, the unit would never restart** — the eFuse
   is permanently enabled (R43) and `GLOBAL_EN` (pin 99) is unconnected. Cutting the boost is
   simpler than the `GLOBAL_EN` pulse S8 sketched and needs no new CM4 connection.
6. **Backstop.** If `RUN_PG` never goes low (shutdown didn't happen), cut anyway at T_max (~3 min),
   plus the boost's own ~2.5 V UVLO, so a software failure can't flatten the cell.
7. **Next key-on.** U1 powers the board normally; the one-shot re-arms. As in S8, the one-shot should
   hold off while the rpiboot jumper is fitted.

The one-shot is the same ATtiny10 S8 already proposed (it must run from the cell at µA standby), or
discrete timer logic.

#### S8b.5 — Open questions before this can be decided

1. **Measure the real shutdown time** on the production image, NVMe and services running — the whole
   case for hold-up rests on it. A hung service stop could make it 30 s.
2. **Where does the cell physically live?** An 18650 is 18 × 65 mm. Off-board on a fused 2-pin lead
   is likely better than board area next to U1's heat — cell life is dominated by sitting
   temperature, not cycles, and it is the only wear-out part on the board (~5–8 years in cab heat).
3. **Confirm U1 tolerates back-fed `VIN_PROT`** at ~11 V in option (a), and pick the ORing device.
4. **Vibration:** tabbed cell, soldered or spot-welded — **not** a spring clip holder, which frets
   and produces random power cuts that read as software faults.
5. **Charging:** ~100–200 mA is ample (8 mAh per event). CN3058E (stocked at JLC) or TP5000 in
   LiFePO4 mode. **Inhibit charge above ~50 °C** via an NTC at the cell — a sealed cab-roof box gets
   there, and hot charging is what kills the cell.
6. **Fuse the cell** (PTC or fuse): a 1500 mAh LiFePO4 sources tens of amps into a fault.
7. **Does the value justify the parts?** Boost + ORing + charger + NTC + one-shot + cell + holder,
   against a read-only root that already protects the OS. The gain is the *data* partition and a
   defined shutdown; decide before layout, since this is a one-off build.

---

### S8c — key-off shutdown: always-on + keyed 12 V — **PROPOSAL 2026-09-20, preferred over §S8b**

> The automotive head-unit pattern: the board is powered from **constant 12 V** and reads a
> **keyed/ignition 12 V** line purely as a signal. Key-off is then an explicit "shut down now"
> message rather than something inferred from a collapsing rail, and the board has as long as it
> needs, because it is still powered. Proposed after §S8b; **nothing decided, no board change
> authorised.**

#### S8c.1 — Why it is preferred over the battery (§S8b)

| | §S8b cell | **§S8c keyed sense** |
|---|---|---|
| Wear-out parts | 18650 (~5–8 yr in cab heat) + charger + NTC + fuse | **none** |
| Shutdown time budget | ~35 s of cell, so the measurement gates the design | **unlimited** — still on vehicle power |
| Key-off detection | threshold + dwell on `VIN_SENSE`; dip vs loss is a judgement | **boolean**, debounce the crank drop-out only |
| Extra conversion | boost + ORing into `VIN_PROT` or `5V_MAIN` | **none** |
| Install | one wire | **two wires** (constant + keyed), both fused |
| Battery isolator thrown after key-off | rides it out | **truncated shutdown** — see S8c.5 |

The restart mechanism is identical in both, so it is not a differentiator: after the halt the board
must remove its own 5 V (S8b.4 step 5).

#### S8c.2 — Signal path

- **Keyed 12 V → a `VIN_SENSE`-style divider (100 k / 8.2 k, 40 V → 3.03 V) → a spare ADC input.**
  `ADC IN4` onward are free (`IN3` is earmarked for `PI_FLT` in S8); **no GPIO is needed**, which
  matters because all 28 are allocated.
- **Do not use J1.7–9.** Per **S11** those inputs are contact-to-ground only: 12 V on them clamps at
  ~4 V through the SRV05-4 and pushes 8–10 mA into +3V3.
- Protection: same front end as the other field inputs (series 1 k, TVS), since a keyed feed carries
  the usual automotive transients.
- Constant 12 V feeds the existing `VIN_PROT` chain unchanged (Q1 reverse-polarity, SMBJ24A, U1).

#### S8c.3 — Sequence

1. **Key on** → keyed line high. U1 is already up (constant 12 V), the CM4 boots as now.
2. **Crank** → the keyed line may drop momentarily on some machines. **Debounce ~2–3 s** before
   believing a key-off; this is the cheap equivalent of S8b's ride-through.
3. **Key off, sustained** → Linux reads the keyed input on the ADC and runs `poweroff`. It is still
   on vehicle power, so there is no deadline.
4. **Halt** → the one-shot sees `RUN_PG` low ~2 s and pulls **U1's `EN`** low. 5 V is removed, which
   is what the CM4 datasheet requires, and the board drops to quiescent draw.
5. **Key on again** → the one-shot releases `EN`, U1 starts, the CM4 boots. Hold off while the
   rpiboot jumper is fitted, as in S8.
6. **Backstop** → if `RUN_PG` never goes low, cut at T_max (~3 min) anyway.

Note this keeps S8's 2026-09-15 rule intact: `VIN_SENSE` stays telemetry, and `poweroff` is
triggered by the **keyed input**, never by a sagging supply.

#### S8c.4 — Parasitic draw (the thing to measure)

Between key-off and the latch opening, the board draws its normal load; afterwards only the sense
dividers and the one-shot remain. Budget ~100 µA for a 100 k / 8.2 k divider (≈ 2.6 mAh/day) plus a
nanopower LDO and an ATtiny10 in standby — negligible against a tractor battery, **but measure it,
don't assume it**, and raise the divider values if it lands higher.

**Add a low-voltage cut-off (~11.5 V)** in the one-shot so a latch that fails to open, or a machine
parked for a season, cannot flatten the vehicle battery.

#### S8c.5 — Known gap: the battery isolator

If the operator keys off and immediately throws the master disconnect, power vanishes mid-shutdown.
The read-only root + overlay still protects the OS (S8); the **data partition is exposed for those
few seconds**. This is the one case §S8b's cell covers and this design does not. Judged acceptable
because the isolator is usually thrown well after key-off — revisit if field reports say otherwise.

#### S8c.6 — Open questions

1. **Miswire detection.** If both inputs land on switched 12 V the design silently degrades to
   today's abrupt cut. Have the CM4 compare the two at boot and warn; note it on the silkscreen and
   in the harness doc.
2. **Which ADC input**, and confirm the divider ratio against the keyed line's real idle voltage.
3. **Measure the parked draw** on the built board (S8c.4).
4. **Harness:** both feeds fused at the source; document wire colours and the fuse ratings.
5. Does the install base accept a second wire? If not, fall back to **§S8b**.

---

### Parts / BOM data

| ID | Was | Issue | Evidence | Fix | Status |
|---|---|---|---|---|---|
| **S12** | L8 | U19's attached part is the CM4 module, not the receptacles. | EasyEDA BOM: U19 = CM4101000 (C20754863); the DF40 receptacles are in neither netlist nor BOM. U19's footprint **is** the two DF40 land patterns: 200 SMD pads, 0.20 × 0.70 mm, 0.40 mm pitch, 50 per column, columns 3.08 mm apart, pins **1–100** and **101–200** in groups 34.00 mm apart (the CM4's own J1/J2 numbering). It also carries **4× Ø3.0 mm mounting holes** (33 × 48 mm pattern) and the 40 × 55 mm module outline. | **Route B chosen (see §S12b): swap U19 for two DF40 components; DNP + hand-solder is the fallback if stock is out at order time.** **A (workaround):** keep U19, exclude it from assembly, hand-add `J_CM_A`/`J_CM_B` to the BOM+CPL — steps and coordinates in `HARDWARE_AIO_BOM.md` §3b. **B (proper swap, see §S12b):** replace U19 with two DF40C-100DS-0.4V(51) components and move the nets. | **DONE 2026-09-17** — verified below |

### S12b — the U19 → 2× DF40 swap (route B) — **COMPLETE 2026-09-17**

**Verified against `PCB From EasyEDA/PCB_PCB_AOW-v2.0_2026-09-17.json`:**

- `U19` replaced by **`CN1`** (pads 1–100), **`CN2`** (pads 101–200) and **`CN3`** (mechanical: 4 holes at
  56.91 / 89.91 × 6.27 / 54.27 mm).
- **All 200 pads within 0.004 mm** of U19's original pad positions (rounding, not movement).
- **Every pad carries the net its U19 pin had**, except pins 25 and 58 — the deliberate S4 swap
  (`LED_DATA` ↔ `SW_REMOTE`).
- **All 14 high-speed nets connected end to end** (6 PCIe, 8 Ethernet): each forms a single group from
  the connector pad through to the M.2 socket or RJ45. No re-routing was needed.

Parts used: `Reference/EasyEDA parts/` (symbols with CM4 pin names/numbers, footprints derived from
U19's pads). Placement gotcha recorded there: EasyEDA's Y axis in this project reads **from the bottom
edge** (Y_editor = 119.00 − Y_from_top).

#### Original procedure

**DECIDED 2026-09-17: do the swap, even for a one-off build.** C597931 is marked "Hot" at JLC and may
restock by order time, and with real connector components the stock question becomes a one-click
decision at ordering: in stock → JLC places them; out of stock → mark them **DNP** and hand-solder using
`PCB From EasyEDA/Stencil_CM4_connectors/`. BOM and CPL come out correct either way, which was route A's
only purpose. C52269441 (LXWCONN) stays a drop-in substitute at order time — identical land pattern.

**U19's pad numbering is verified correct** (2026-09-17, against CM4 datasheet Figure 4, "CM4 viewed
from the top"). The connector is *not* centred on the module, which makes the check unambiguous:

- **Along the 55 mm length:** the body sits ≈14 mm from the Pin 99/100 end and ≈19 mm from the Pin 1 end
  (scaled off Figure 4). The board's pads: 13.7 mm and 18.7 mm, pin 1 at the larger-offset end. ✅
- **Row assignment:** Figure 4 shows Pin 1 above Pin 2 on the top connector (row nearer the module's
  outer edge) and Pin 101 above Pin 102 on the bottom one (the *inner* row). The board matches: pin 1 is
  1.46 mm from the outer edge, pin 101 is on the inner row. ✅

**Cross-checked against Raspberry Pi's own CM4IO KiCad** (`CM4IO.pretty/Raspberry-Pi-4-Compute-Module.kicad_mod`,
which is likewise a single 200-pad module footprint):

| Pad | CM4IO | This board |
|---|---|---|
| 1 | x −2.000, y −31.300 | x 54.87, y 22.97 |
| 2 | x **+1.080** (3.08 mm right of pin 1) | x **57.95** (3.08 mm right of pin 1) |
| 3 | 0.4 mm along, pin 1's column | 0.4 mm along, pin 1's column |
| 101 / 102 | 31.920 / 35.000 (same pattern, 34 mm over) | 88.87 / 91.95 (same pattern, 34 mm over) |

Identical handedness — **U19's footprint is correct**.

**Pin numbering is CAD bookkeeping, not physics.** The Hirose DF40 catalogue's "Recommended PCB layout"
carries **no pin numbers at all** — only dimensions (B, P = 0.4, pad 0.2, 2.38, 3.78) — and Note 4 states
**"This connector is NOT polarized."** Each contact bridges the pad directly beneath it to the module
contact directly above, 1:1 by position. Consequences:

- **Only pad positions and their nets matter.** Any numbering scheme works provided the schematic wires
  each net to the pad it belongs on (`PCB From EasyEDA/CM4_DF40_pin_map.csv` is the truth table).
- **A 180° rotation of the part at assembly is electrically harmless** — the 2 × 50 grid maps onto itself,
  so the contacts land on the same pads. The pin-1 question is about *footprint numbering in CAD*, not
  about how the connector is placed. (The module's orientation is fixed by the two connectors + mounting
  holes regardless.)
- The EasyEDA library footprint observed in practice numbers pads **1–50 down one side and 100–51 back up
  the other** — an arbitrary convention that is wrong *for this netlist*, since the nets are keyed to CM4
  pin numbers. Wiring by those numbers would put every signal on the wrong pad.

**⚠ The EasyEDA library socket footprint for C597931 is also MIRRORED** (pin 1 top-right, pin 2 top-left —
the reverse of the official land pattern). Using it as drawn would scramble every signal across the
connector, with nothing wrong-looking in the schematic. Treat library footprints for mezzanine
connectors as suspect until checked against the mating part.

**Handedness gotcha with library footprints:** a bare DF40 socket footprint may show pin 1 at the
opposite corner. Work out which:
- **Diagonally opposite** → it's 180° out; rotate it. The (51) variant has no boss/fitting nail, so the
  socket is mechanically symmetric and only the pad numbering must match the module.
- **Adjacent corner (a true mirror)** → the footprint doesn't suit a face-down module. EasyEDA won't
  mirror on the top layer (the greyed-out option flips to the bottom). Copy U19's pad array into your own
  footprint instead, keeping each pad's number, and attach C597931 to that.

**Acceptance test either way:** pin 1 of `J_CM_A` at **(54.87, 22.97) mm**, pin 1 of `J_CM_B`
(= CM4 pin 101) at **(88.87, 22.97) mm**, nets per `PCB From EasyEDA/CM4_DF40_pin_map.csv`.

**Two things to watch:**
- **Don't let the library footprint change the pads.** If EasyEDA's C597931 footprint differs (pad sizes,
  extra boss pads), edit it to match what is there now: 0.20 × 0.70 mm pads, 0.40 mm pitch, 3.08 mm row
  centres, 19.60 mm array. Same geometry keeps the PCIe/Ethernet traces attached; different geometry
  means re-routing them.
- **Re-add the mechanical items U19 carried** (step 3) — easiest as a pad-less "CM4 mechanical" footprint,
  which also keeps the module visible on the board.

Worth doing **now**: only 14 traces (6 PCIe + 8 Ethernet) currently land on U19's pads, and
the rest of the CM4 area is unrouted. After the swap the BOM, CPL and DRC are all correct with no manual editing,
and no CAM queries.

**Why routing survives:** EasyEDA tracks carry their own net names, and the new footprints go at the
*same coordinates* as the existing pads, so the 14 traces re-attach instead of needing a re-route.

1. **Footprints — derive them from U19, don't draw them** (decided 2026-09-17). Copy U19's footprint into
   the personal library twice; in one copy delete pads 101–200 → **`J_CM_A`, pads numbered 1–100**; in the
   other delete pads 1–100 → **`J_CM_B`, pads numbered 101–200**. Nothing is repositioned or renumbered,
   so the verified pad geometry carries over intact.
   **Keep CM4 numbering on connector B (101–200) rather than restarting at 1** — the schematic then reads
   the same as the CM4 datasheet pinout ("pin 92 is RUN_PG" in both), and `CM4_DF40_pin_map.csv`'s
   `cm4_pin` column *is* the pad number (`connector_pin` becomes irrelevant). EasyEDA accepts pad numbers
   101–200; they need only be unique within the footprint, and JLC places by position regardless.
   Also make a **third, pad-less "CM4 mechanical" footprint** — 40 × 55 mm outline, 4 × Ø3.0 mm holes,
   keep-out for the 1.5 mm gap — centred at **(73.41, 30.27) mm** (step 3). Silkscreen pin 1 by `J_CM_A`
   and pin 101 by `J_CM_B`.
2. **Schematic** — the library CM4 device (C20754863) is a **two-section symbol**: `CM4101000.1` /
   `.2`, i.e. `U19.1` and `U19.2`, sharing one designator and one footprint (which is why the BOM shows a
   single line and why simply changing U19's part number yields only *one* placement). The sections split
   pins 1–100 / 101–200, so the substitution is section-for-connector: **U19.1 → `J_CM_A` (1–100)**,
   **U19.2 → `J_CM_B` (101–200)**, with CM4 numbering preserved and no net regrouping. Check whether the
   sections are wired by net labels (labels survive the deletion and the new symbols pick them up) or by
   drawn wires (those will dangle). Deleting U19 removes both sections *and* the footprint's outline and
   mounting holes at once — so make the mechanical footprint first.
   Place the two symbols. Net names don't change, so the rest of
   the schematic is untouched; arrange symbol pins by function (power, GND, PCIe, Ethernet, GPIO) rather
   than pad order. Truth table: `PCB From EasyEDA/CM4_DF40_pin_map.csv` — 117 of 200 pins carry nets.
3. **PCB** — delete U19, place `J_CM_A` centred at **(56.41, 32.77) mm** and `J_CM_B` at
   **(90.41, 32.77) mm** from the board's top-left corner. Check **pin 1 of J_CM_A lands at
   (54.87, 22.97) mm** and pin 1 of J_CM_B at **(88.87, 22.97) mm** — that is the single most important
   check; a 180° error swaps pin 1 for pin 100.
4. **Re-add the mechanical items U19 carried:** 4× **Ø3.0 mm NPTH** at (56.91, 6.27), (89.91, 6.27),
   (56.91, 54.27), (89.91, 54.27); the **40 × 55 mm module outline** on silk/assembly; and a keep-out so
   nothing tall sits under the module (mezzanine gap ≈ 1.5 mm).
5. **Verify** — confirm every pad's net against the CSV, run DRC, and check the 14 high-speed traces are
   still attached (P3/P5 rework may re-route the Ethernet anyway).
6. **Re-export** netlist + BOM + PCB source; the docs regenerate with `J_CM_A`/`J_CM_B` as real parts and
   the CM4 module drops to a hand-fit line.

### Retracted

| ID | Was | Claim | Why retracted |
|---|---|---|---|
| — | F1 | "MCP251863 STBY (pin 5) floating → transceivers stuck in standby." | **Wrong.** Pin 5 on U7, U8 and U9 is on `GND` in the netlist, next to VSS (22) and GND (24), so the transceivers are in normal mode. The original review misread the pin dump. |

### Verified OK (earlier fixes still present)

- **MCP251863 grounds:** VSS (22), GND (24) and STBY (5) on `GND` for all three; VIO/VDD on +3V3, VCC on 5V_MAIN.
- **`GPIO_VREF`** (CM4 pin 78) tied to `CM4_3V3` (84/86) with C76 — July fix C1.
- **CM4 GND pin 107** on `GND` — July commit cf6be0fb.
- **All 62 capacitors have both pins connected** — the July "5 floating cap grounds" fix holds.
- **`USB_OTG_ID`** (pin 101) intentionally floating (device mode, needed for rpiboot). The July "ground it" fix only applied to the removed STM32 host link.

### Notes (no change needed)

- `GLOBAL_EN` (99), `nEXTRST` (100), `WL_nDISABLE` (89), `BT_nDISABLE` (91) are single-pin nets. All fine floating per the datasheet; expect DRC warnings.
- CS pull-ups (R74, R75) only on GPIO25/26, which default pull-low; CE0/CE1 (GPIO8/7) default high. Consistent.
- ADC128S102 needs SCLK 8–16 MHz for rated accuracy; give it its own SPI speed.
- Piezo on GPIO6 has no hardware PWM, so tones are software PWM.
- **All 28 CM4 GPIOs are allocated.** Spare capacity elsewhere: **ADC IN3–IN7** (U21 pins 7–11, tied
  to GND today) and **J1 pins 21–22** (free since RS-485 was dropped). To free an actual GPIO:
  console RX (GPIO15, keeps TX for boot messages) or the piezo (GPIO6) each free one; wire-ORing the
  three CAN `nINT` lines onto one pull-up frees two (MCP251863 INTOD gives open-drain outputs), but
  check first that the `mcp251xfd` driver handles a shared interrupt and sets INTOD.

---

## PCB layout issues

State of the board at review: 184 × 119 mm, 4-layer; 35/151 parts placed; `VIN`, `VIN_PROT`, 5 V buck,
Ethernet ×8 and PCIe ×6 routed on L1; Inner1 solid GND plane; Inner2 and bottom empty; **3 vias**.
PCB pad nets match the netlist (0 differences).

### Must fix

| ID | Was | Issue | Measured | Fix | Status |
|---|---|---|---|---|---|
| **P1** | L1 | Input TVS on a thin trace, ground via the pour only. | Feed was 0.254 mm × 4.8 mm; ground pad had no trace or vias. | **FIXED 2026-09-17.** Feed now **1.5 mm**; ground pad has **4 GND vias** (0.62 mm pad / 0.31 mm hole) at 1.78–2.14 mm from pad centre — ~0.7–1.1 mm from the pad edge — each fed by a **1.0 mm** trace 0.46–1.69 mm long, fanned out both sides. Four parallel paths, so the clamp's return inductance is now a few nH: negligible against the SMBJ24A's ~39 V clamp. Board via count 8. | **fixed** |
| **P2** | L2 | **Little GND stitching between the top pour and the inner plane.** | 7 vias on the whole board: 4 in U1's exposed pad and 3 by D2/R47. Every SMD GND pad (CN1/CN2 DF40 grounds, M.2, buck caps, D1) reaches the Inner1 plane only through the top pour. | GND stitching vias at the CM4 and M.2 GND pins beside the PCIe/Ethernet pairs — that is where return current changes layers — at each bypass cap, and along pour edges. **DEFERRED 2026-09-17: do the stitching after all parts are placed and routed**, so the vias can be put where they don't block channels. Two notes: (a) keep space free around the CN1/CN2 and M.2 ground pins next to the already-routed PCIe/Ethernet pairs — that return path needs vias there; (b) a via at each bypass cap's ground pad fits after routing, since it lands in the pad's own copper. | **deferred** |
| ~~P2a~~ | L2 | ~~No thermal vias under U1.~~ | **Retracted 2026-09-17: U1's footprint contains 4 × 0.30 mm GND vias in a 2 × 2 grid at 1.0 mm pitch inside the exposed pad** — present since 09-14. The original review counted only board-level `VIA` shapes and missed vias nested inside footprints. A 3 × 3 grid would give more thermal margin but 4 is a normal choice. | none needed | **retracted** |
| **P3** | L4 | Ethernet pair impedance. | Originally 0.377 mm / 0.10 mm gap (~66 Ω — a 50 Ω single-ended width). | **CLOSED 2026-09-18.** Re-routed to a uniform **0.200 mm width, ~0.20 mm gap, single layer, no vias, no necks**. Designer's call after ~9 hours on these pairs: as close as EasyEDA's diff-pair routing allows. **Ethernet routing is done — do not reopen.** | **closed** |
| **P7** | F2/F3 rework | Q1-area copper had to follow the S1/S2 schematic fixes. | — | **CLOSED 2026-09-18** — verified in the 09-18 PCB: Q1 pad 2 (tab) on `VIN` with a 2.0 mm trace to J1, pad 3 (source) on `VIN_PROT` at 1.5 mm, pad 1 on `PGATE`; D2 sits across `VIN_PROT`→`PGATE` with R40 and R47 clustered below the gate. | **closed** |

### Should fix

| ID | Was | Issue | Measured | Fix | Status |
|---|---|---|---|---|---|
| **P4** | L3 | Buck switching loop too large. | Return leg C1/C2 ground → D1 anode was 15 mm. | **FIXED 2026-09-17.** D1 rotated horizontal, cathode facing U1, anode alongside the cap grounds: **C2 ground → D1 anode 4.70 mm** (C1 7.37), C1 (+) → pin 2 5.89 mm, pin 8 → D1 cathode 4.50 mm. Loop path ~20–25 mm vs 40+, enclosed area down ~3×. Optional refinement: a 100 nF (C1525) right at U1 pins 2/7 as an HF bypass — the nearest ceramic is still ~6 mm from VIN. | **fixed** |
| **P5** | L5 | Ethernet intra-pair skew. | — | **CLOSED 2026-09-18: verified by hand by the designer**, segment by segment across each pair. (Automated length checks here were unreliable — the pairs route as multiple track objects joined at T-junctions, which a naive per-net copper sum miscounts.) | **closed** |
| **P8** | L7 | L2 inductor footprint vs part. | Original claim — a 10 × 10 mm part on an 11 × 10 mm footprint — was **wrong**: Sunlord's MWSA series datasheet gives MWSA1004S as **10.0 × 11.0 × 3.8 mm**, so the `L11.0-W10.0` footprint name is correct. Land pattern comparison: recommended pad 4.1 × 5.4 mm with a 4.1 mm gap (outer span 12.3 mm); drawn pads are 3.40 × 5.00 mm with a 5.50 mm gap — **outer span 12.30 mm, identical**. Pads are trimmed inward ~0.7 mm long / 0.4 mm wide; with 2.0 × 3.0 mm terminals the overlap is still ample. | Optional: grow each pad inward to 4.1 × 5.4 mm to match the manufacturer and add joint area/thermal path. | **closed — optional tweak** |
| **P9** | — | DF40 receptacle pads on the U19 footprint unverified. | U19 footprint `COMM-SMD_L55.0-W40.0_CM4101000`: 200 pads, 0.20 × 0.70 mm, 0.40 mm pitch, columns 3.08 mm apart, groups (pins 1–100 / 101–200) 34.00 mm apart. Geometry is consistent with a DF40 land pattern, but the source drawing is unverified. | Check those numbers against the DF40C-100DS-0.4V(51) datasheet land pattern and the CM4 mechanical drawing (connector spacing), since S12 relies on them. | open |

### Minor

| ID | Was | Issue | Measured | Fix | Status |
|---|---|---|---|---|---|
| **P6** | L6 | ETH1 pair gap below the design rule at the magjack escape. | Was 0.093 mm vs a 0.10 mm rule. | **CLOSED 2026-09-18** — the 09-18 re-route runs a uniform 0.200 mm width with gaps of ~0.20–0.28 mm throughout. | **closed** |

### Layout impact of schematic issues

| Schematic ID | Touches routed copper? | What changes on the PCB |
|---|---|---|
| S1, S2 | **yes** | P7 |
| S3 | no | BOM swap, same footprint |
| S4 | no | net swap at CM4 pins 58 ↔ 25 (both unrouted) |
| S6 | no | adds DNP footprints in the CAN block (not placed yet) |
| S9 | no | adds a SOT-23 near D23 |
| S10 | near CM4 | adds an 0402 at CM4 pin 92 |
| S5, S7, S8, S11 | no | CAN/GPS/field I/O not placed yet |

### Not yet routed (progress, not issues)

- **Partial:** `5V_MAIN` — buck output done; J1.3 and LEDs not.
- **Parts placed, nets unrouted:** `5V_CM` (U2 unplaced), `+3V3_NVME` (U4 unplaced), `GPIO_VREF`/`CM4_3V3`
  (78–84–86), `CM4_1V8`, `PCIE_NRST`, `PCIE_CLKREQ`, `RUN_PG`, `PI_LED_NPWR`, LED data chain, RJ45 LED `+3V3`.
- **Not placed:** CAN block, SP3232, ADC, watchdog, field I/O protection, eFuse/LDO/NVMe buck, GPS slot, piezo.
