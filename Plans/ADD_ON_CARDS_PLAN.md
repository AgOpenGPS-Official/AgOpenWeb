# Add-on cards: VT and web cards, the card pane and the drawer

**Status:** web cards (#503, #512) and the card pane (#513) are merged, with the `CLAUDE.md` section
"Web cards and the card pane". Left: the drawer, one device run for all of it, the add-on author
page (§6). Then #456 closes and this plan moves to `Completed/`.
**Date:** 2026-11-01 (from `ADDON_WEB_CARDS_PLAN.md` and `CARD_PANE_SPLIT_PLAN.md`, 2026-10-31)
**Issue:** #456 (VT: split screen with the terminals on the left or right), widened to the web cards
and the drawer.
**Purpose:** Tools and add-ons reach the operator in AgOpenWeb through cards beside the map. This
plan covers the two kinds of card, where they sit on the screen, and the drawer that gives an
add-on the whole screen for a while.

---

## 1. Two kinds of card

- **VT cards** show an ISOBUS implement's own screens (`CLAUDE.md`, "ISOBUS virtual terminal";
  `Plans/ISOBUS_VT_TRANSPORTS.md`). One to four terminals, one card each.
- **Web cards** show an add-on's own web page: the HAT's pages, a rate controller, a camera. Up
  to four cards, each its own page.

They stay separate: no shared code beyond the card frame (header, dragging, bring-to-front) and
the pane. No web pages in a VT card, no VT screens in a web card.

### Web cards

| # | Question | Decision |
|---|---|---|
| 1 | How AgOpenWeb learns about an add-on | A **list of bookmarks** (name + URL) kept on the host (`AppSettings.WebCards`), the same on every browser. Discovery later (§7). |
| 2 | How a card picks its page | A card opens on its last page or empty; its header has a picker listing the bookmarks, ＋ (another card), ↗ (open in the system browser), ⤢, ⚙ (edit the bookmarks), ✕. |
| 3 | An add-on on the AgOpenWeb box itself | `{host}` in a URL is the AgOpenWeb host as this browser reaches it (`location.hostname`), so `http://{host}/` works from the tablet and from the launcher alike. |
| 4 | Proxying through AgOpenWeb | **No.** The page loads from the add-on, on its own origin. A proxy would put the add-on on AgOpenWeb's origin (its page could then script AgOpenWeb's and send commands), needs WebSocket proxying, and breaks pages that build URLs from their own host (the HAT's do). |
| 5 | Observers (#397) | AgOpenWeb can't see inside another site's page, so a browser without control gets a cover over it: the page shows live, taps do nothing. The VT cards follow the same rule. |
| 6 | A page that refuses a frame | `X-Frame-Options` / `frame-ancestors` can't be detected reliably, so ↗ is always there. |
| 7 | The iframe | `sandbox="allow-scripts allow-same-origin allow-forms allow-popups allow-downloads"` (the origin is the add-on's, never AgOpenWeb's), `referrerpolicy="no-referrer"`. |
| 8 | Theme, language, units, control | Later: query parameters, then `postMessage` (§7). |

Host: `webcard.save|<index>\t<name>\t<url>` (index −1 adds) and `webcard.delete|<index>` need
control; the host checks the scheme (`http://`, `https://`) and the lengths; the list reaches the
browsers in the `WebCards` frame. Client: `WebCards` in `js/web-cards.js`.

## 2. Layouts

| # | Question | Decision |
|---|---|---|
| 1 | Layouts | **Floating** (the default: cards over the map, dragged and sized by hand), **Split, cards left**, **Split, cards right**. Screen & Alerts › VT & Web Cards. |
| 2 | What goes in the pane | The open **VT and web cards**, alike. The charts (Steer/Heading/XTE) stay floating over the map. |
| 3 | Several cards | **Two per tab**, stacked, in the order they were opened: 1 card fills the pane; 3 are a tab of two and a tab of one. Up to 8 (4 VT + 4 web), so up to 4 tabs, each labelled with its cards' names. |
| 4 | Card size in the pane | Set by the pane: a VT mask is the largest square that fits its slot beside the soft key columns; a web page fills its slot. No ⤢ and no dragging in the pane. |
| 5 | Pane width | Dragging the divider, ⅓ to ½ of the window. |
| 6 | No card open | The map takes the full width. |
| 7 | Where it is kept | Layout, pane width and drawer position **per browser** (`localStorage`), as the cards: the cab tablet and an office browser want different layouts. Observers choose theirs too. |
| 8 | Menu panels and dialogs | **Over the whole window**, anchored at the left nav (shifted left when the map is too narrow for them), covering the pane while open. The map holds only what lives on it. |
| 9 | Safe-area insets (notches) | The window's outer edges: the pane's outer side takes them; the map's edge beside the pane has none. |
| 10 | Tab switch | Hides and shows cards, never moves them in the page (an iframe moved in the page reloads). Switching between Floating and Split does move them, so web pages reload once then. |

### How the map gets its own area (built, #513)

The overlays on the map (status bar, HUD, light bar, side navs, bottom bar, corner pad, toasts)
are fixed-position against the window and keep clear of the safe-area insets through `--sal` /
`--sar`. In a split layout those two are the **map's** insets: the pane's width on its side, the
real inset on the other.

- `:root` has `--map-l` / `--map-r` (the pane, in the overlays' zoomed pixels); `--sal` / `--sar`
  add them; `--map-dx` moves what is centred with `left: 50%` to the map's centre; `--win-sal` /
  `--win-sar` keep the real insets for what spans the window (menu panels, dialogs, the pane).
- The canvas (`#ck`, not zoomed) is placed and sized to the map by `setMapArea(l, r)`; `resize()`
  sizes the Skia surface to it, so the camera, the projection and every map draw follow.
- Pointer events on the canvas are in window coordinates; `mapL` turns them into canvas ones, and
  the DOM letters pinned to map points add it back.
- A `ResizeObserver` shifts an open menu panel left when it would run off the window.
- No containing block (`contain`/`transform`): it would make the map one stacking context and put
  the left nav and status bar under the menu panels.

Rules for new overlays: `CLAUDE.md`, "Web cards and the card pane".

## 3. Drawer

Add-on pages that are a whole application (a planning screen, the HAT's setup, a rate
controller's set-up page) want more room than half the window for a while, then the map back. In
a split layout the pane is a **drawer**.

| # | Question | Decision |
|---|---|---|
| 1 | Positions | **Closed** (handle only), **Split** (⅓ to ½, as today), **Full** (covers the map). |
| 2 | Who moves it | **The operator, always.** Nothing opens or closes it by itself: not a U-turn, not an alarm (alarms, prompts and dialogs already show over the whole window). |
| 3 | Speed | Context, not control: above walking pace (2 km/h) the drawer can't be dragged *to* Full, and a full drawer shows a larger guidance strip, but it never closes itself (as a navigation app limits what it offers while driving). |
| 4 | Kept | Per browser, with the layout and width. |

- **Handle.** A grip on the pane's inner edge (today's divider). Dragging moves the edge freely; on
  release it snaps: under ⅓ to **Closed**, ⅓ to ½ stays where dropped (**Split**), past ½ to
  **Full**. A tap toggles Closed and the last open position. With no card open there is no drawer.
- **Closed.** The pane is hidden (`visibility`/`display`, never moved in the page), so its cards
  keep their state and pages don't reload; the map has the full width. The handle stays on the
  edge with a count of the open cards.
- **Full.** The pane covers the map's width. A **guidance strip** stays along the map side: the
  steer bar or cross-track figure, master and section state, speed; above walking pace it is taller,
  with the light bar. The map stops drawing while covered (a battery saving on a tablet) and redraws
  from the latest state when uncovered. Two cards in a tab sit side by side at this width.
- **Nothing changes on the host.** Guidance, section control, alarms and sounds run as before; this
  is the browser's layout only. Observers have their own drawer.
- **Code.** `CardPane` (`js/card-pane.js`) gets the position (`closed` / `split` / `full`);
  `setMapArea(l, r)` takes the drawer's width (0 when Closed); Full sets a flag the frame loop
  (`js/frame.js`) checks before drawing. The speed comes from the tick.

## 4. Kept where

| What | Where | Why |
|---|---|---|
| Web card bookmarks | Host, `AppSettings.WebCards` | the same list on every browser, kept with the install, not the vehicle |
| VT gateway / adapter, number of terminals | Host, `AppSettings` | the implement bus is the host's |
| Which cards are open, their page, size and place | Each browser (`localStorage`) | a tablet and a PC lay out their screens differently |
| Layout, pane width, drawer position | Each browser (`localStorage`, `cards.pane`) | the same |

## 5. Done

| PR | What |
|---|---|
| #503 | Web cards: bookmarks on the host, the card, `{host}`, the observer cover. Tested with the HAT's pages from the macOS launcher. |
| #512 | The hint in an empty web card. |
| #513 | The map's own area (§2) and the card pane: the layout setting, tabs of two, the divider, VT and web cards sized to their slots. `CLAUDE.md` section. |

## 6. Steps left

1. **Drawer** (§3): handle, the three positions and their snapping, the guidance strip, the map
   paused under Full, the speed context. `CLAUDE.md` section updated.
2. **One device run** at 1280×800 on the Galaxy Tab, iPad, and the Windows, macOS and Linux
   (WebKitGTK, on the CM4) launchers:
   - Floating and Split on both sides with 1, 2, 3 and 8 cards;
   - a real VT mask through the HAT gateway: soft keys, input editors;
   - the HAT's pages in a web card: live values (WebSocket 8082), the log viewer (8083), a
     settings page saved through the card; on iOS a plain-http LAN page in a frame
     (`NSAllowsLocalNetworking`);
   - the drawer in each position, a page keeping its state across Closed and Full, the strip at
     speed;
   - an observer.
3. **Add-on author page** (`Docs/ADD_ON_WEB_CARDS.md`): what the card gives an add-on, `{host}`,
   framing headers to avoid, what observers see, the sizes a page should work at (a half-window
   slot, a quarter, full).
4. Close #456; move this plan to `Completed/`.

## 7. Later

- **Talking to the page**, when an add-on asks, in this order:
  1. query parameters on load: `aow_theme=day|night`, `aow_lang=de`, `aow_units=metric|imperial`
     (an add-on needs no code to ignore them);
  2. `postMessage` both ways, with the target origin checked: to the page theme, language, units
     and whether this browser holds control, on change; from the page a title, an attention badge
     (an alarm while the card is closed, behind others or in a closed drawer), a preferred size.
- **Discovery:** add-ons announcing themselves by DNS-SD (e.g. `_aowcard._tcp`, TXT `path=`,
  `name=`) in the picker without typing, as the VT gateways are. SharpIsoTerm's `AowLinkBrowser`
  has `_aowlink._tcp` fixed and would need the service type as a parameter; iOS needs the type in
  `NSBonjourServices`. Not before the bookmarks are in use.
- **Moving cards between tabs**, if asked for.

## 8. Not doing

- Proxying add-ons through AgOpenWeb (§1, decision 4).
- Mixing the kinds: web pages in VT cards, VT screens in web cards.
- Add-ons sending AgOpenWeb commands. An add-on that needs guidance data talks to the modules'
  protocol (PGN / process data), not to the web client.
- Opening or closing the drawer automatically (§3, decision 2).
