# EasyEDA parts — CM4 mezzanine connectors (S12b)

Generated 2026-09-17 from this project's own exports, so no geometry or pin data was retyped:

- **pins** from the CM4 device's two sections (`U19.1` / `U19.2`) in `SCH_AOW-v2.0_2026-09-17.json`
- **pads** from the `U19` footprint in `PCB_PCB_AOW-v2.0_2026-09-14.json`

| File | Contents |
|---|---|
| `SYM_CM4_DF40_A.json` | symbol, pins **1–100** with CM4 names (1 `GND`, 99 `GLOBAL_EN`…), prefix `CN`, package `CM4_DF40_A`, C597931 |
| `SYM_CM4_DF40_B.json` | symbol, pins **101–200** (101 `USB_OTG_ID`…), package `CM4_DF40_B` |
| `FP_CM4_DF40_A.json` | footprint, pads **1–100**; pin 1 at (54.87, 22.97) mm from the board's top-left |
| `FP_CM4_DF40_B.json` | footprint, pads **101–200**; pin 101 at (88.87, 22.97) mm |
| `FP_CM4_MECHANICAL.json` | 4 × Ø3.0 mm holes (33 × 48 mm pattern) + 40 × 55 mm outline that U19 carried |

**Import footprints first** (File → Open in EasyEDA Standard, then save into the personal library under
the name in the file), then the symbols, so their `package` references resolve. Then place the two
devices, delete `CN1`/`CN2`, and delete `U19` once the mechanical footprint is placed.

Keeping CM4 numbering on connector B (101–200) is deliberate: the schematic then reads the same as the
CM4 datasheet pinout, and `../PCB From EasyEDA/CM4_DF40_pin_map.csv` is the truth table (cm4_pin,
cm4_pin_name, net, pad coordinates).

## Import notes (verified 2026-09-17)

- The files open in EasyEDA Standard's **Symbol / Footprint editor**, where shapes are loose by design —
  the document *is* the component. **File → Save** with name (`CM4_DF40_A` / `_B`), prefix `CN` and the
  matching package puts it in the personal library. Save the footprints first so the symbols can link them.
- **⚠ On opening a symbol, EasyEDA defaults its footprint to LCSC's own Hirose footprint** (the supplier
  fields carry C597931, which auto-links it). **Re-point the package to `CM4_DF40_A` / `CM4_DF40_B`.**
  LCSC's footprint is mirrored *and* numbered 1–50/100–51, so leaving it linked would put every signal on
  the wrong pad with no visible symptom in the schematic.

> **Format note.** The shapes and coordinates come from EasyEDA's own data, but the document wrapper
> (head fields, canvas string) is reconstructed. If EasyEDA rejects a file, the fallback is editing the
> schematic export directly — pin positions stay put, so attached wires survive.
