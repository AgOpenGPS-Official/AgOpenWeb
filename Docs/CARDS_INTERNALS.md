# VT cards and web cards: how they work

The inside of the two card kinds and the card pane, for work on AgOpenWeb itself. For add-on developers: [ADD_ON_CARDS.md](ADD_ON_CARDS.md). Both are switched off for now (`FeatureSwitches`).

## ISOBUS virtual terminal (VT card)
The VT cards show ISOBUS implements' own screens. `Shared/AgOpenWeb.VirtualTerminal`
(`IVirtualTerminalService`) runs SharpIsoTerm's terminal stack on the implement bus through
an AOW link gateway (`AppSettings.VtGateway`, port 2323; the HAT's `aow-linkd`) or an slcan USB
adapter on the device itself (`AppSettings.VtAdapter`, a CANable; not on iOS). Gateways announcing
themselves by DNS-SD (`_aowlink._tcp`) are listed in the card's gear by name (`AowLinkBrowser`); one
picked from the list is kept by name too (`VtGatewayName`) and followed to a new address. One to four
terminals (`AppSettings.VtCount`), as displays in one cab, each implement choosing its terminal in
its own menu, one card each. Their messages and pictures go to the browsers as `Vt` (with the
terminal) and `VtImage` frames (`VirtualTerminalRemote`), the painter is in `js/vt.js`, and each
card sends `vt.*` commands naming its terminal. Every head has it (iOS: device builds). SharpIsoTerm
comes as its NuGet package from its GitHub release, pinned by version and SHA-256 in
`Shared/AgOpenWeb.VirtualTerminal/AgOpenWeb.VirtualTerminal.csproj` and fetched by the build into
`nuget-local/` (a package source in `nuget.config`); to take a new SharpIsoTerm, change both to its
release's. To work on SharpIsoTerm itself, build against a checkout instead:
`dotnet build Platforms/AgOpenWeb.Desktop/AgOpenWeb.Desktop.csproj -p:SharpIsoTermDir=$HOME/Code/SharpIsoTerm`
(an absolute path; its native libraries built with its `native/build.sh`). The service
is disposed with the backend: on Android and iOS the process outlives it, and the library allows
one stack per process.

## Web cards and the card pane
**Web cards** show an add-on's own web page (the HAT's pages, a rate controller's) in a card, picked
from a bookmark list kept on the host (`AppSettings.WebCards`, `webcard.save` / `webcard.delete`,
the `WebCards` frame); `{host}` in a URL is the AgOpenWeb host as the browser reaches it. The page
loads from the add-on on its own origin, never through AgOpenWeb; observers get a cover over it.
Which cards are open, and where, is the browser's (`localStorage`). `WebCards` in `js/web-cards.js`; plan:
`Plans/ADD_ON_CARDS_PLAN.md`.

**Card pane** (#456): Screen & Alerts › VT & Web Cards sets Floating (the default), Left or Right,
per browser. In a split layout the open VT and web cards sit in a pane on that side, two to a
tab, and the map has the rest of the window (`CardPane` in `js/card-pane.js`; cards call `track`, `shown`
and `untrack`). The map's area is set by `setMapArea(l, r)`; what that means for new code:
- An overlay on the map keeps clear of the pane through `--sal` / `--sar` (the map's insets: the
  pane's width on its side, else the safe area), a full-width bar uses `--map-l` / `--map-r`, and
  one centred with `left: 50%` adds `var(--map-dx)`. Something spanning the window (a menu panel,
  a dialog) uses `--win-sal` / `--win-sar`.
- `vw` is the map's width, not the window's, and the canvas starts at `mapL`: a pointer's
  `clientX` is `clientX - mapL` on the map (`s2w`, hit tests), and a DOM label placed from `w2s`
  adds `mapL`.
- A card moved in the page reloads its iframe, so the pane hides and shows cards on a tab switch
  and moves them only when the layout changes.
- The pane is a **drawer**: Closed (the handle on the window's edge, with the count of open
  cards), Split (⅓ to ½) or Full (over the map, which the frame loop then doesn't draw:
  `CardPane.coversMap`; a guidance strip along the top keeps speed, distance from the line,
  AutoSteer and sections, plus the light/steer bar above walking pace). Only the operator moves
  it (a drag of the handle snaps on release, a tap toggles Closed); above 2 km/h it can't be
  pulled to Full; it appears Closed when the first card opens and never closes by itself. Full sets both map insets, so everything on the
  map is off-screen. Plan: `Plans/ADD_ON_CARDS_PLAN.md`.
