# AiO board — PCB layout guide

> Layout constraints for the AgOpenWeb AiO board (CM4 + STM32G473 carrier). Companion to the
> per-sheet netlists in `Netlists From EasyEDA/` and the BOM. Numbers sourced from the **Raspberry Pi
> CM4 datasheet** (pinout §4), PCIe CEM 4.0, and the M.2 (PCIe) spec. Read before placing parts.

---

## 0. Schematic corrections to apply first (found during layout prep)

| # | Pin | Issue | Fix |
|---|---|---|---|
| **C1** | U19-78 `GPIO_VREF` | floating → **all CM4 GPIO dead** (UART console, Eth LEDs) | tie **78 ↔ 84 ↔ 86** (`CM4_3V3`) + 100 nF bypass on 84/86 |
| **C2** | U19-101 `USB_OTG_ID` | floating = USB **device** mode → CM4 can't host the STM32 | **101 → GND** (grounded = host) |
| C3 | U19-99 `GLOBAL_EN` | floating OK (100 k PU to 5V) | optional: STM GPIO for clean power-off |
| C4 | U19-92 `RUN_PG` / 93 `nRPIBOOT` | floating OK (10 k PU) | optional: 2-pin header for reset + recovery-boot |
| C5 | U19-89/91 `WL_/BT_nDisable` | floating = wireless **enabled** | ✔ leave floating (intended) |

C1 + C2 are mandatory. `CM4_3V3` (pins 84/86) is the CM4's own 300 mA/pin, 600 mA-total 3.3 V
output — used **only** as the GPIO_VREF reference here (we have our own +3V3 rail from U3).

---

## 1. Stackup

**4-layer, controlled impedance** (JLCPCB JLC7628 or equivalent 1.6 mm):

```
L1  Signal — high-speed (PCIe, GbE, USB) + top components
L2  GROUND — solid, unbroken (return reference for all L1 high-speed)
L3  POWER  — 5V / 3V3 / 5V_CM pours
L4  Signal — low-speed / bottom components
```

- Route **every** high-speed pair on **L1 over the solid L2 ground** — no plane splits, no reference
  changes mid-route. This is the whole reason for 4-layer.
- 6-layer only if the CM4 DF40 escape + M.2 routing gets cramped (the official CM4IO is 6-layer). For
  a single PCIe lane + GbE on a compact board, 4-layer is proven.
- Use JLC's impedance calculator for the exact trace width/gap per target below — don't eyeball.

## 2. Impedance & length-match targets

| Signal | Pins | Z_diff | Intra-pair skew | Coupling caps |
|---|---|---|---|---|
| **PCIe TX** (CM4→M.2) | U19-122/124 → U5-49/47 | **85 Ω** | ≤ 0.13 mm (5 mil) | on CM4 — **none on carrier** |
| **PCIe RX** (M.2→CM4) | U5-43/41 → U19-116/118 | **85 Ω** | ≤ 0.13 mm | on **SSD** — **none on carrier** |
| **PCIe REFCLK** | U19-110/112 → U5-55/53 | **85 Ω** | ≤ 0.13 mm | on CM4 — none |
| **Ethernet MDI ×4** | U19 pairs → L1 | **100 Ω** | ≤ 0.13 mm | none (magnetics in jack) |
| **USB2** (CM4↔STM32) | U19-103/105 ↔ U7-45/46 | **90 Ω** | ≤ 2.5 mm (FS, relaxed) | none |
| PCIe nRST / CLKREQ | U19-109/102 | 50 Ω s.e. | — | low-speed, route normal |

> **PCIe Z note:** PCIe CEM = **85 Ω**; the CM4 datasheet groups PCIe with its "90 Ω" pairs. On a
> short carrier link the ±10 % tolerance overlaps — **target 85 Ω** and you satisfy both.

## 3. PCIe — the critical net (route first)

The CM4↔M.2 link decides the floorplan. Rules:

- **Shortest possible run** — place M.2 socket hard off the CM4's PCIe-connector edge (§5).
- Reference over **solid L2 ground** the whole way; **no** plane splits or slots under the pairs.
- **Minimize vias** (ideal 0, else ≤2/trace); put GND stitching vias next to any signal via.
- **Intra-pair skew ≤ 0.13 mm** — correct mismatch at its source with a short serpentine, keep P/N
  tightly coupled elsewhere. (x1 = single lane, so no lane-to-lane matching needed.)
- No stubs, no test points on the pairs. Keep aggressors ≥ 3–5× trace width away.
- **Coupling caps: NONE on the carrier.** CM4 has TX + REFCLK coupling on-module; the M.2 SSD has
  its own TX coupling. The CM4 "external AC coupling required" note on RX is satisfied by the SSD —
  adding carrier caps makes 2-in-series (~50 nF) and violates the PCIe 75 nF minimum. Route direct.

## 4. Ethernet / USB

- **Ethernet:** 100 Ω, keep the 4 MDI pairs short from the CM4 to L1 (HR911130C). Gigabit auto-MDIX
  → pair order not critical, but keep each pair together and P→+, N→−. C22 (1 nF) at the P1 CT right
  by the jack; magnetics + Bob-Smith are internal. Shield tabs → GND.
- **USB2:** 90 Ω pair, CM4-103/105 ↔ STM32-45/46. Only Full-Speed (12 Mbps) so matching is relaxed,
  but still route as a coupled pair over ground. Remember **C2**: ground USB_OTG_ID for host mode.

## 5. Floorplan / placement

- **CM4 (U19) anchors everything.** Orient so its PCIe/GbE/USB connector edge (high-pin DF40, incl.
  116/118/122/124 PCIe, Ethernet pairs, 103/105 USB) faces the M.2 + RJ45.
- **M.2 socket (U5) immediately off that edge, card pointing AWAY from the CM4** — it can't go under
  the module (mezzanine standoff ~1.5 mm << socket+card height). Budget the full 2242/2280 card
  keep-out + far-end standoff now. Socket = 5.5 mm Z-height variant (91302-55-…) — clear the lid.
- **RJ45 (L1)** on the front edge near the CM4 Ethernet pins.
- **STM32 (U7) by the ATS-26 (J1)**, with CAN (U8–10) and serial (U11/U12) transceivers between it
  and the connector — keeps the field I/O runs short.
- **Power tree (U1/U4 + bulk) near VIN** (J1-1). Hold-up caps + buck.
- Front edge is fixed (§7.4): ATS-26 · OLED · RJ45 · reset probe-hole · page-rocker slot — single row.

## 6. Power & decoupling

- **Buck hot loops tight** (U1, U4): input cap → FET → inductor → output cap → GND, minimal loop
  area. Keep FB nodes (FB1/FB5) away from the switch nodes (SW1/SW5).
- **CM4 5V** (`5V_CM`, pins 77/79/81/83/85/87): wide pour + bulk (C19–21) right at the CM4; fed
  through the U2 eFuse (STM-gated via PI_PWR_EN).
- **Distributed 100 nF** per power pin (STM32 VDD, transceivers, CM4). Analog rail `+3V3A` stays
  ferrite-isolated (L4) with its own decoupling near the STM ADC.
- Crystal X1: keep the 8 MHz loop small, guard-ground the two caps, away from switchers.

## 7. Front-panel mechanical (§7.4)

118 × 44 mm usable, single horizontal row:
`[ ATS-26 44.5×35.5 ] [ OLED 1.3″ 35×33 ] [ RJ45 16 ]  (reset probe-hole)` + page-rocker slot in the
~11 mm strip over the OLED (machined slot in the panel PCB, flying leads to J_PAGE/SW2). ATS-26 is a
hand-soldered right-angle flange part; RJ45 + all connectors register on this one edge so case length
stays non-critical. Antennas exit the **back** on U.FL pigtails → no board-level RF traces.

## 8. Pre-route checklist

- [ ] C1 (GPIO_VREF) + C2 (USB_OTG_ID) schematic fixes applied
- [ ] Impedance stackup ordered from JLC; 85/100/90 Ω geometries from their calculator
- [ ] CM4 + M.2 placed and PCIe pairs routed/matched **before** anything else
- [ ] Solid L2 ground under every high-speed pair, no splits
- [ ] Buck loops minimized, FB isolated
- [ ] M.2 card keep-out + standoff reserved; socket Z-height clears the lid
