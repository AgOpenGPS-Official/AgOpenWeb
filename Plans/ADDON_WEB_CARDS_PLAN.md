# Add-on web cards

**Status:** step 1 merged (#503, with the hint line #512 and the card pane #513); `CLAUDE.md` section done. Left: the heads (step 2) and a page for add-on authors (step 3).
**Date:** 2026-10-31
**Purpose:** Tools and add-ons reach the operator in AgOpenWeb two ways. An ISOBUS implement
draws its screens in a VT card (`CLAUDE.md`, "ISOBUS virtual terminal"). An add-on with a web
UI of its own (the HAT's pages, a rate controller, a camera) gets a **web card**: a card on the
map showing the add-on's page. This plan covers the web cards. They are separate from the VT
cards: no shared code beyond the card frame every card uses.

First test: the HAT's web pages (AgOpenWeb-HAT `web/`, port 80, live data over WebSockets on
8082 and 8083 at the page's own host).

---

## 1. Decisions

| # | Question | Decision |
|---|---|---|
| 1 | How AgOpenWeb learns about an add-on | A **list of bookmarks** (name + URL), kept on the host. Discovery by DNS-SD may come later (§6). |
| 2 | How a card picks its page | A card opens empty or on its last page; its header has a picker listing the bookmarks. |
| 3 | An add-on on the AgOpenWeb box itself | `{host}` in a URL is the AgOpenWeb host as this browser reaches it (`location.hostname`), so `http://{host}/` works from the tablet and from the launcher alike. |
| 4 | Proxying through AgOpenWeb | **No.** The page is loaded from the add-on itself. A proxy would put the add-on on AgOpenWeb's origin (its page could then script AgOpenWeb's and send commands), needs WebSocket proxying, and breaks pages that build URLs from their own host (the HAT's do). |
| 5 | Observers (#397) | AgOpenWeb can't see inside another site's page, so for a browser without control the card is covered: the page shows live, taps do nothing. Same rule as the VT cards. |
| 6 | Theme, language, units, control | Not in the first step. Later, by query parameters and then `postMessage` (§5). |

## 2. What the operator sees

- **Left nav › Web** (a new button, always shown) opens a web card. Up to four cards, as the
  VT cards: each its own page, size and place.
- **Card header:** the bookmark picker, Open in browser (↗), Size (⤢), Close (✕). Dragging,
  bring-to-front and the sizes work as the VT cards' (`wireCardDrag`, `raiseCard`).
- **Body:** the page in an iframe. Before a bookmark is picked, or when the list is empty, the
  body says how to add one.
- **Bookmarks** are edited from the card (a gear, as the VT gateway): add, edit, delete; name
  and URL. Editing needs control. URLs must start with `http://` or `https://`.
- **Open in browser** opens the page in the system browser (`window.open`, which every shell
  already sends to the system browser). It is the way out when a page refuses to be shown in a
  frame (`X-Frame-Options`, `frame-ancestors`): the browser gives no reliable way to detect that,
  so the button is always there.

What is kept where:

| What | Where | Why |
|---|---|---|
| The bookmarks | Host, `AppSettings.WebCards` (name, URL) | the same list on every browser, kept with the install, not the vehicle |
| Which cards are open, their page, size, place | Each browser (`localStorage`) | a tablet and a PC lay out their screens differently; the VT cards do the same |

## 3. Pieces

**Host**
- `AppSettings.WebCards` (`List<WebCardBookmark>`: `Name`, `Url`), through `ConfigurationService`
  like the other app settings.
- Sent to the browsers in the Config frame (`Contracts.cs` → `WireCodec.cs` → `transport.js`,
  appended), or a small frame of its own if the Config frame should stay as it is.
- Commands: `webcard.save|<index>\t<name>\t<url>` (index −1 adds) and `webcard.delete|<index>`.
  Both need control (not in `ObserverIds`). The host checks the scheme and the lengths.

**Client** (`index.html`, `app.js`)
- A card template like `vt-card-tpl`; header picker, ↗, ⤢, gear, ✕; body iframe and cover.
- `{host}` replaced with `location.hostname` when the card loads the page.
- The iframe: `sandbox="allow-scripts allow-same-origin allow-forms allow-popups allow-downloads"`
  (the add-on keeps its own origin; `allow-same-origin` is needed for its storage and WebSockets
  and is safe because the origin is the add-on's, never AgOpenWeb's), `referrerpolicy="no-referrer"`.
- The observer cover: a transparent layer over the iframe while `iHoldControl` is false.
- Per-browser state in `localStorage`, wrapped in try/catch as the VT size is.
- English text with `tr()`; `Tools/i18n-extract.py`.

**Heads:** nothing. iframes work in WebView2, WKWebView, WebKitGTK and the Android WebView, and
AgOpenWeb sends no header that blocks framing. To check on each head: the launcher on Linux
(WebKitGTK), and iOS, where WKWebView loads plain-http LAN pages under App Transport Security's
`NSAllowsLocalNetworking` (already set in `Info.plist`; confirm a LAN address loads in a frame).

## 4. Steps

1. **Bookmarks and one card kind** (§2, §3). Test against the HAT on the bench (`http://{host}/`
   with AgOpenWeb on the CM4, and the CM4's address from a tablet): the home page's live values
   (WebSocket 8082), the log viewer (8083), a settings page saved through the card.
2. **Heads:** Galaxy Tab, iPad, Windows launcher, macOS launcher, Linux launcher on the CM4.
3. **Docs:** `CLAUDE.md` (a short "Add-on web cards" section beside the VT one) and a page for
   add-on authors: what the card gives them, `{host}`, framing headers to avoid, what observers
   see.

## 5. Later: talking to the page

When an add-on wants to match AgOpenWeb, in this order:
1. **Query parameters** on load: `aow_theme=day|night`, `aow_lang=de`, `aow_units=metric|imperial`.
   No code in the add-on needed to ignore them.
2. **`postMessage`**, both ways, with the target origin checked:
   - to the page: theme, language, units, whether this browser holds control, on change;
   - from the page: a title, an attention badge (an alarm while the card is closed or behind
     others), a preferred size.

Neither changes the bookmarks or the cards; both can come when an add-on asks.

## 6. Later: discovery

Add-ons announcing themselves by DNS-SD (e.g. `_aowcard._tcp`, TXT `path=`, `name=`) could
appear in the picker without typing, as the VT gateways do. The browser for that,
SharpIsoTerm's `AowLinkBrowser`, has `_aowlink._tcp` fixed; it would need the service type as a
parameter (a SharpIsoTerm change). iOS would need the new type in `NSBonjourServices`. Not before
the bookmarks are in use.

## 7. Not doing

- Proxying add-ons through AgOpenWeb (§1.4).
- Showing web pages in the VT cards, or VT screens in web cards.
- Add-ons sending AgOpenWeb commands. An add-on that needs guidance data talks to the modules'
  protocol (PGN / process data), not to the web client.
