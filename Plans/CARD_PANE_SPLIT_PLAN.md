# Card pane: split screen

**Status:** steps 1 and 2 merged (#513, 2026-10-31), docs in `CLAUDE.md`; the device checks (step 3) are left, then this moves to `Completed/`.
**Date:** 2026-10-31
**Issue:** #456 (VT: split screen with the terminals on the left or right), widened to the web
cards (`Plans/ADDON_WEB_CARDS_PLAN.md`).
**Purpose:** A VT mask or an add-on's page floating over the map covers a good part of it on a
10" tablet. In a split layout the screen is divided: a **card pane** on one side holding the
open VT and web cards, and the map in the rest.

---

## 1. Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Layouts | **Floating** (today, the default), **Split, cards left**, **Split, cards right**. |
| 2 | What goes in the pane | The open **VT cards and web cards**, alike. The charts (Steer/Heading/XTE) stay floating over the map. |
| 3 | Several cards | **Two per tab**, stacked, in the order they were opened: 1 card fills the pane; 3 are a tab of two and a tab of one. Up to 8 (4 VT + 4 web), so up to 4 tabs. A tab is labelled with its cards' names. Moving cards between tabs: later, if asked for. |
| 4 | Card size in the pane | Set by the pane: a VT mask is the largest square that fits its slot beside the soft key columns; a web page fills its slot. No ⤢ and no dragging in the pane. |
| 5 | Pane width | Dragging the divider, ⅓ to ½ of the window. |
| 6 | No card open | The map takes the full width again. |
| 7 | Where it is kept | Layout and pane width **per browser** (`localStorage`), as the card sizes: the cab tablet and an office browser want different layouts. Observers choose theirs too. |
| 8 | Menu panels and dialogs | **Over the whole window**, anchored at the left nav (shifted left when the map is too narrow for them), covering the pane while open. The map only holds what lives on it. |
| 9 | Safe-area insets (notches) | The window's outer edges: the pane's outer side takes them; the map's edge beside the pane has none. |
| 10 | Tab switch | Hides and shows cards; never moves them in the page (an iframe moved in the page reloads). Switching between Floating and Split does move them, so web pages reload once then. |

## 2. How the map gets its own area

The overlays on the map (status bar, HUD, light bar, side navs, bottom bar, corner pad, toasts)
are fixed-position against the window, and already keep clear of the window's safe-area insets
through `--sal` / `--sar`. In split mode those two become the **map's** insets: the pane's
width on its side, the real safe-area inset on the other (and none on the pane side). The
overlays centred on the window (`left: 50%`) centre on the map with `--map-cx`. So:

- `:root` gets `--map-l` / `--map-r` (the pane, in the overlays' zoomed pixels), `--sal` /
  `--sar` add them, `--map-cx` is the map's centre; `--win-sal` / `--win-sar` keep the real
  insets for what spans the window (menu panels, dialogs, the pane).
- The canvas (`#ck`, not zoomed) is placed and sized to the map; `resize()` sizes the Skia
  surface to it, so the camera, the projection and every map draw follow.
- Touches on the canvas arrive in window coordinates; one offset (the map's left edge) turns
  them into canvas coordinates. The DOM letters pinned to map points add it back.
- Menu panels (`.ln-panel`) take their maximum width from the window, and a `ResizeObserver`
  shifts an open panel left when it would run off the window's right edge.
- No containing block (`contain`/`transform`) is used: it would make the map one stacking
  context and put the left nav and status bar under the menu panels.

## 3. Steps

1. **Map area** (§2). Floating must be unchanged: same positions, same hit tests. Done (#513);
   `setMapArea(l, r)` from the console shows an empty map area for checks.
2. **Card pane**: the layout setting (Screen & Alerts › VT & Web Cards: Floating, Left, Right), the pane with its
   tabs and divider, VT and web cards moved into it and sized to their slots. Done (#513).
3. **Check** at 1280×800 on the macOS, Windows and Linux launchers, iPad and Galaxy Tab: 1, 2, 3
   and 8 cards, both sides, VT soft keys and input editors, a web page's controls, an observer.
4. **Docs:** `CLAUDE.md` ("Web cards and the card pane"). Done; this plan moves to `Completed/`
   after step 3.
