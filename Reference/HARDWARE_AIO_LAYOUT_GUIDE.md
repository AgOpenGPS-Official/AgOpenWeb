# AiO board — PCB layout guide

> Layout constraints for the AgOpenWeb AiO board (**CM4-only carrier, 2026-09-14 netlist**).
> Companion to `HARDWARE_AIO_ISSUES.md` (open issues), `HARDWARE_AIO_NETLIST.md` (wiring) and `HARDWARE_AIO_BOM.md`. Numbers from the
> **Raspberry Pi CM4 datasheet**, PCIe CEM 4.0, the M.2 (PCIe) spec, and the MCP251863 / ADC128S102
> datasheets. Read before placing parts.

---

## 0. Open issues first

All schematic (`S#`) and layout (`P#`) issues, with status, are in **`HARDWARE_AIO_ISSUES.md`**. Settle
these before continuing placement:

- **Rework in routed copper:** P7 (Q1 area, for S1/S2), P1 (TV1), P2 (thermal + stitching vias),
  P3/P5/P6 (Ethernet impedance, skew, clearance), P4 (buck loop), P8 (L2 land pattern).
- **Changes parts/nets in unplaced areas:** S3, S4, S6, S9, S10, S12/P9.

**Layout progress (2026-09-14):** 35 of 151 parts placed; `VIN`/`VIN_PROT`, the 5 V buck, Ethernet and
PCIe routed. Source + Gerbers in `PCB From EasyEDA/`.

Already applied from the July review and still present: **`GPIO_VREF` (78) tied to `CM4_3V3`
(84/86) with C76**. The July `USB_OTG_ID` → GND fix is intentionally **not** in this revision:
without the STM32 host link, pin 101 floats (device mode) for rpiboot.

---

## 1. Stackup

**4-layer, controlled impedance** (JLCPCB JLC7628 or equivalent 1.6 mm):

```
L1  Signal — high-speed (PCIe, GbE, USB) + top components
L2  GROUND — solid, unbroken (return reference for all L1 high-speed)
L3  POWER  — 5V_MAIN / 5V_CM / +3V3 / +3V3_NVME pours
L4  Signal — low-speed / bottom components
```

- Route **every** high-speed pair on **L1 over the solid L2 ground** — no plane splits, no reference
  changes mid-route.
- 6-layer only if the CM4 DF40 escape + M.2 routing gets cramped (the official CM4IO is 6-layer).
- Use JLC's impedance calculator for the exact trace width/gap per target below.

## 2. Impedance & length-match targets

| Signal | Pins | Z_diff | Intra-pair skew | Coupling caps |
|---|---|---|---|---|
| **PCIe TX** (CM4→M.2) | U19-122/124 → U5-49/47 | **85 Ω** | ≤ 0.13 mm (5 mil) | on CM4 — **none on carrier** |
| **PCIe RX** (M.2→CM4) | U5-43/41 → U19-116/118 | **85 Ω** | ≤ 0.13 mm | on **SSD** — **none on carrier** |
| **PCIe REFCLK** | U19-110/112 → U5-55/53 | **85 Ω** | ≤ 0.13 mm | on CM4 — none |
| **Ethernet MDI ×4** | U19-12/10, 4/6, 11/9, 3/5 → L1 | **100 Ω** | ≤ 0.13 mm | none (magnetics in jack) |
| **USB2** (CM4 → H3 header) | U19-103/105 → H3-2/4 | **90 Ω** | ≤ 0.13 mm (HS-capable) | none |
| PCIe nRST / CLKREQ | U19-109/102 | 50 Ω s.e. | — | low-speed |
| CAN FD bus pairs | U7/8/9 pins 3/4 → D5/6/7 → J1 | **120 Ω** diff (loose) | keep H/L together | none |

> **PCIe Z note:** PCIe CEM = **85 Ω**; the CM4 datasheet groups PCIe with its "90 Ω" pairs. On a
> short carrier link the ±10 % tolerance overlaps — **target 85 Ω** and you satisfy both.

## 3. PCIe — the critical net (route first)

- **Shortest possible run** — place the M.2 socket hard off the CM4's PCIe-connector edge (§6).
- Reference over **solid L2 ground** the whole way; no plane splits or slots under the pairs.
- **Minimize vias** (ideal 0, else ≤2/trace); GND stitching vias next to any signal via.
- **Intra-pair skew ≤ 0.13 mm** — correct at the source with a short serpentine; x1 = single lane.
- No stubs, no test points on the pairs. Aggressors ≥ 3–5× trace width away.
- **Coupling caps: NONE on the carrier.** CM4 has TX + REFCLK coupling on-module; the SSD has its own
  TX coupling. Carrier caps would put two in series (~50 nF) and violate the 75 nF PCIe minimum.

## 4. Ethernet / USB

- **Ethernet:** 100 Ω, keep the 4 MDI pairs short from the CM4 to L1 (HR911130C). Auto-MDIX → pair
  order not critical, but keep each pair together and P→+, N→−. C22 (1 nF / 2 kV) right at the L1 P1
  centre tap; shield tabs → GND.
- **USB2 → H3:** route as a 90 Ω pair even though it's only a provisioning header. rpiboot runs at
  USB 2.0 High-Speed. Keep H3 near the CM4's USB pins and the stub short.

## 5. SPI peripherals, CAN & analog

- **SPI0 bus** (SCLK/MOSI/MISO) fans out to U7, U8, U9 and U21. Daisy-route it (not a star),
  CM4 → U21 → U7 → U8 → U9 or whatever order is shortest. Keep SCLK away from the CAN clock and switch
  nodes. At ≤ 20 MHz no length matching is needed, but a 22–33 Ω series R at the CM4 on SCLK is cheap
  insurance if the run is long (not in the current netlist).
- **CAN clock X1 (40 MHz):** place X1 central to U7/U8/U9. R64/R65/R66 (33 Ω) sit **at X1**, one trace
  per chip, each over solid ground. Keep these traces short and away from SPI and the bucks.
- **MCP251863 internal links:** TXCAN (15) → TXD (23) and RXD (28) → RXCAN (16) are external traces.
  Keep them short on L1 under the part.
- **Per-chip decoupling:** 100 nF at VDD (14), VIO (1) and VCC (25). The netlist has C69–C71 / C77–C79
  on +3V3 and C48–C50 on 5V_MAIN; place one of each group at each chip.
- **CAN bus:** transceiver → D5/D6/D7 TVS → J1, bus pair routed together, TVS as close to J1 as possible.
- **ADC U21:** VA (pin 2) is also the **reference** — give it its own 1 µF + 100 nF right at the pin
  (datasheet: within 1 cm) and feed it from a quiet tap of 5V_MAIN, not the path to the LEDs or piezo.
  Put the RC filters (R72/C90, R73/C91) and the VIN_SENSE cap C30 right at the ADC inputs. AGND/DGND to
  the solid ground plane directly under the part.

## 6. Floorplan / placement

- **CM4 (U19) anchors everything.** Orient so its PCIe/GbE/USB edge (high-pin DF40: 116/118/122/124
  PCIe, Ethernet pairs, 103/105 USB) faces the M.2 + RJ45.
- **M.2 socket (U5) immediately off that edge, card pointing away from the CM4** — it can't go under
  the module (mezzanine standoff ~1.5 mm). Budget the 2242 card keep-out + standoff; the 5.5 mm socket
  must clear the lid.
- **RJ45 (L1)** on the front edge near the CM4 Ethernet pins.
- **CAN block (U7–U9, X1, D5–D7) and SP3232 (U12 + D9–D12) between the CM4 GPIO edge and J1** — keeps
  field runs short. ADC U21 and its RC filters near J1.4/J1.6 but on the quiet side of the board.
- **Power tree (U1, D1, L2, C1–C7, U2, U3, U4, L3) near J1.1**, away from the ADC and the CAN clock.
- **Watchdog U20 + SW1** near CM4 pin 92; the `RUN_PG` trace should be short.
- **SK6812 LEDs (D19–D22) + U22** wherever the front-panel light pipes need them; keep U22 close to
  D19 and the 5 V LED current loop tight.
- Front edge is fixed: ATS-26 (J1) · RJ45 (L1) · status LEDs · reset probe-hole — single row.

## 7. Power & decoupling

- **Buck hot loops tight** (U1, U4): input cap → switch → inductor → output cap → GND, minimal loop
  area. Keep FB nodes (FB1/FB5) away from switch nodes (SW_BUCK1/SW5).
- **CM4 5V** (`5V_CM`, pins 77/79/81/83/85/87): wide pour + bulk (C12, C19–C21) right at the CM4.
- **NVMe rail** (`+3V3_NVME`): C13–C18 near the M.2 3.3 V pins; C34 at the socket.
- **Distributed 100 nF** per power pin (MCP251863 ×3, SP3232, ADC, watchdog, AHCT125, each SK6812).
- **Hold-up caps C6/C7** near the buck input.

## 8. Front-panel mechanical

118 × 44 mm usable, single horizontal row: `[ ATS-26 44.5×35.5 ] [ RJ45 16 ] [ status LED window ]
(reset probe-hole)`. Removing the OLED frees ~35 mm of panel width compared with the July plan.
Antennas exit the **back** on U.FL pigtails, so there are no board-level RF traces.

## 9. Pre-route checklist

- [ ] S1/S2 schematic fixes applied and P7 reworked at Q1
- [ ] Decide S3/S4/S6/S9/S10 (they change parts or nets) and S12 (DF40 receptacles)
- [ ] DF40 connector pads on the U19 footprint checked against the CM4 datasheet mechanical drawing (P9)
- [ ] Impedance stackup ordered from JLC; 85/100/90 Ω geometries from their calculator
- [x] CM4 + M.2 placed and PCIe pairs routed (done 2026-09) — skew OK (≤ 0.08 mm), ≈ 89 Ω estimate
- [ ] Ethernet re-routed at 100 Ω width/gap and length-tuned (P3, P5, P6)
- [x] Solid L2 ground under every high-speed pair, no splits (checked 2026-09-14)
- [ ] GND stitching vias at CM4/M.2 GND pins and bypass caps; thermal vias under U1 (P2)
- [ ] Power tree: TV1 onto wide VIN_PROT copper (P1), buck loop tightened (P4)
- [ ] L2 inductor land pattern matches MWSA1004S (P8)
- [ ] ADC VA tapped from a quiet point
- [ ] 40 MHz clock traces short and star-fed from X1
- [ ] M.2 card keep-out + standoff reserved; socket Z-height clears the lid
