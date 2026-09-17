# CM4 connector stencil (J_CM_A / J_CM_B only)

A **small stencil covering only the two DF40 pad arrays**, so it can sit flat on a board that already has
the rest of its SMT parts fitted. No need to cut up the full-board stencil.

Generated from `Gerber_TopPasteMaskLayer.GTP` of the 2026-09-14 export; aperture geometry is untouched
(0.70 × 0.20 mm, 200 apertures, 100 per connector).

| File | Contents |
|---|---|
| `Stencil_CM4_TopPaste.GTP` | the 200 paste apertures, original coordinates |
| `Stencil_CM4_Outline.GKO` | 49 × 32 mm rectangle around them (6 mm margin) |

**Order as:** stencil only, frameless, **0.1 mm thickness** — not the default 0.12 mm. Area ratio for a
0.20 × 0.70 mm aperture is 0.78 at 0.1 mm but 0.65 at 0.12 mm, below the 0.66 rule of thumb for reliable
release.

Coordinates match the board's Gerbers, so the piece aligns by eye against the pads and silkscreen.
Check the M.2 socket (x ≈ 100 mm) sits outside the 6 mm margin before ordering if placement changes.
