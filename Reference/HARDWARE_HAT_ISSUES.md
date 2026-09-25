# AgOpenWeb HAT — Issues and Checks

> Open decisions, checks still to do, and results so far.
>
> **Part of the HAT document set:**
> - `HARDWARE_HAT_DESIGN.md`: why
> - `HARDWARE_HAT_NETLIST.md`: wiring
> - `HARDWARE_HAT_BOM.md`: parts
> - this file: open items
>
> Ref designators match across all four.
>
> IDs: **H#** = a design decision or open item; **B#** = a bench check.
> Status: **open**, **decided**, **pass**, **fail**.

## 1. Open decisions and items

| ID | Item | Proposal | Status |
|---|---|---|---|
| **H1** | Vehicle power path | ~~J4 on the HAT, switched through Q2 to the IO board~~ → **12 V straight to the IO board's J20**. The HAT switches only its own 5 V (Q2) and holds the CM4 off via `GLOBAL_EN` (Q1). J4, J5 and the reverse-polarity stage removed; J3.1 becomes a 12 V sense wire. | **decided 2026-09-25:** direct power (design §4) |
| **H2** | CAN transceiver current | The 5 V budget assumes ~70 mA per MCP251863 transceiver when dominant (design §4.7). Check the datasheet figure. | open |
| **H3** | GNSS board dimensions | Measure on real boards: the EMAX's square hole pattern (36.0 × 36.0 on H5/H9/H10; its odd fourth hole is unused) and its left-edge connector; the UM982EB hole pattern (34.3 × 64.6 in the part, matching v5.0; another footprint says 34.0 × 64.75). With Ø 3.3 holes and M3 screws there's only ~±0.15 mm of play. | open |
| **H4** | GNSS UART logic level | Confirm both boards' TX1/RX1 are 3.3 V, not shifted to 5 V. | open |
| **H6** | NVMe standby draw | The 150 µA IO-board figure (B13) — check whether an NVMe in the PCIe slot adds to it; the slot's 3.3 V converter runs from 12 V. | open |
| **H5** | LCSC stock and fees | Re-check stock at order time (the 2026-09-23 figures are a snapshot), especially J7 (C22436146, ~1.1k). Confirm whether "Pref-Ext" parts carry the extended fee. | open |

## 2. Bench checks

### 2.1 Done: CM4 IO Board, Raspberry Pi OS, bootloader 2023-01-11 (`8ba17717`), 2026-09-23

| ID | Check | Result |
|---|---|---|
| **B1** | `CM4_3V3` (40-pin header pin 1) falls at halt with `POWER_OFF_ON_HALT=1`, `WAKE_ON_GPIO=0` | **pass.** Drops to 0 V. With the stock `POWER_OFF_ON_HALT=0` it stays up, so the EEPROM setting is required. The IO board schematic also shows `RUN_PG` at 0 V while halted. |
| **B2** | `CM4_3V3` doesn't dip across `sudo reboot` or a `RUN_PG` reset | **pass.** No dip on reboot, or on a ~0.2 s tap of the IO board's J1 pad 3 (`RUN_PG`) to pad 2, which simulates U10's ~210 ms pulse. The 1.2–2.6 s hold is pure margin. |
| **B13** | IO board current at 12 V with the CM4 halted (`POWER_OFF_ON_HALT=1`) | **pass (2026-09-25): ~150 µA**, which made direct power viable (H1). NVMe fitted? See H6. |
| **B3** | A `GLOBAL_EN` pulse restarts a halted CM4 with 5 V still on | **pass.** A short tap and a multi-second hold (it boots on release) both work. Done through the IO board's J2 pins 13–14, its own wake button, which pulls `GLOBAL_EN` to `RUN_PG` (0 V while halted). |

Setup note: writing the EEPROM from Linux on a CM4 needs a `[cm4]` block in `config.txt`, and on
this bench that lost the saved Wi-Fi profile. Use `rpiboot` instead (design §7).

### 2.2 To do before trusting it in a tractor

| ID | Check | Why | Status |
|---|---|---|---|
| **B4** | **Crank dip on the IO board:** run the IO board + HAT from a bench supply stepped down to 8, 7, 6 V for 1–5 s | the IO board is **rated down to 7.5 V**; a hard crank may reset it | open |
| **B5** | **Meter the IO board's J20** before building the J5 lead: pin 1 should read +12 V (barrel jack powered) | J20's pin order is the reverse of floppy-cable colours | open |
| **B6** | **Latch off-state:** key off, CM4 halted → `5V_MAIN` at 0 V and `GLOBAL_EN` held low (the CM4 stays off) | | open, needs the built HAT |
| **B7** | **Standby draw** of the whole system with the key off, compared with the ≈ 0.31 mA estimate (design §4.5) | | open, needs the built HAT |
| **B14** | **Battery connect with the key off:** the CM4 must not boot (Q1 holds `GLOBAL_EN` low from power-up). If it does start briefly, it should shut itself down | power-up race between the IO board's 5 V and Q1 | open, needs the built HAT |
| **B8** | **Low-voltage cut-off:** trips at 11.5 V ± 0.2 V, only 6–13 s after key-off | | open, needs the built HAT |
| **B9** | **Restart:** key off → on during shutdown, and a UI shutdown with the key on, both restart the CM4 | block E | open, needs the built HAT |
| **B10** | **Crank test on a machine** with the keyed feed on IGN/RUN: no dropouts, no false trips | | open |
| **B11** | **Shutdown time** on the production image (key-off → halt) | the key monitor and the UI should know it | open |
| **B12** | **5 V current** on the built system (the CM4 busy, CAN traffic, GNSS running) against the ≈ 1.6 A typical / 2.77 A worst estimate | | open |
