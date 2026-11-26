# The web client

The web client is the whole UI: plain HTML, CSS and JavaScript in
`Shared/AgOpenWeb.RemoteServer/wwwroot`, served by the host and shown by every head's web view
(or any browser on the LAN). There is no framework and no build step: the browser loads the
files as they are in the repo.

```
wwwroot/
├── index.html      the page: every panel's markup, then the scripts in load order
├── css/            the styles, one file per area
├── js/             the client, one file per feature
├── transport.js    the WebSocket and the binary frames (pairs with WireCodec.cs)
├── i18n.js         the translation loader; i18n/<lang>.json the strings
├── vendor/         CanvasKit (Skia in WebAssembly), bundled for offline use
├── icons/, sounds/
└── manifest.webmanifest
```

## The screen it is made for

A 10" tablet in landscape: the reference is the Panasonic Toughpad FZ-G1 (10.1", 1920×1200 at
150% scaling, so 1280×800 CSS pixels). Layout is judged and fixed on that screen and larger
landscape ones; smaller screens, portrait and phones are out of scope. A handheld screen would
want a layout of its own with fewer, larger controls, not this one made smaller. The web client
harness checks the target screen (`Tests/AgOpenWeb.WebClientScenarios.Tests`, S1).

It is used with a finger. A `title` (a tooltip) shows only under a mouse, never on a tablet, so
the words of a button go on the button (an icon with its words under it), and its title only
says more. The boundary menu and its recorder had icons with titles only, and the recorder had
one title for the field's boundary and an obstacle: on a tester's field (20 Nov 2026) obstacles
driven round with the field's button replaced its boundary. They have their words now, and the
recorder's title says which it records, from the host (`MainViewModel.IsRecordingObstacle`, the
Boundary frame's last byte). Test: `BoundaryRecorderPageTests`.

## Where things are

| File | Holds |
|---|---|
| `js/core.js` | The map canvas and its area, the model the transport feeds (`scene`, `tick`, `config`, `statusBar`…), units, render and camera state, the transport wiring, the tool width |
| `js/alerts.js` | Host prompts and failure notices, alert sounds |
| `js/skia-setup.js` | Map theme, UI scale, the placement of the corner pad, panels and HUD, the Skia paints |
| `js/map-input.js` | Camera controls (pan, zoom, tilt), map-tap capture, simulator drive keys, configured hotkeys |
| `js/sim-bar.js` | The simulator bar |
| `js/dialogs.js` | The dialog host and its cards: simulator coordinates, the lat/lon line, the shared confirm |
| `js/osk.js` | The on-screen keyboard; the numeric-field filter |
| `js/bars.js` | Unsaved-coverage guard, section bar, bottom nav, the on-screen U-turn / lateral buttons |
| `js/cfg-controls.js` | The config controls every settings panel uses (`cfgGet`, `cfgSend`, `wireCfgControls`, `populateCfgControls`…) |
| `js/tracks.js` | Flags, Ref Nudge, AB line creation, drawing on the map, the tracks manager |
| `js/left-nav.js` | The left nav and its menu panels (`lnOpen`, `lnCloseAll`, chain panels) |
| `js/field-builder.js` | Field Builder (tracks, headland, tram), KML boundary, offset fix |
| `js/settings-panels.js` | Screen & Alerts, vehicle configuration |
| `js/config-panels.js` | Tool configuration, AutoSteer, Smart WAS |
| `js/network.js` | Network IO, NTRIP profiles and settings |
| `js/fields.js` | Field Operations (fields and jobs), AgShare |
| `js/app-menu.js` | The application menu: settings and tools, language, About, bug report |
| `js/wizards.js` | Steer wizard, vehicle and tool picker |
| `js/control.js` | Control authority (operator / observer, #397) and the right nav's actions |
| `js/projection.js` | The screen matrix, `w2s` / `s2w` |
| `js/overlays.js` | The per-frame DOM overlays: light bar text, HUDs, status bar, GPS and field cards, the bars' state, U-turn indicator |
| `js/hud-tools.js` | Lower-right cluster, the clock, charts, the tool panels (roll correction, offset fix, import tracks, recorded path, boundary recording), card dragging |
| `js/card-pane.js` | The card pane (split layout, #456) and its drawer: Closed / Split / Full, the guidance strip |
| `js/vt.js` | The VT painter and VT cards |
| `js/web-cards.js` | Web cards |
| `js/map-draw.js` | Skia map layers: lines and tracks, previews, flags, grid, hitch, vehicle, ground, imagery |
| `js/map-layers.js` | Satellite tiles, coverage, tool footprint, light bar and steer bar |
| `js/frame.js` | `renderSkia`, the frame loop and its DOM updates; the start |

The styles follow the same areas: `css/base.css` (safe areas, the map's area, the theme
palette), `map-hud.css`, `panels.css`, `dialogs.css`, `map-side.css`, `cards.css`,
`tool-panels.css`. `index.html` starts with a contents comment for the markup.

Each file starts with a line saying what it holds. To find where something is handled, search
for the element's `id` or the command id (`transport.send('…`) across `js/`.

## How the scripts load

The files in `js/` are **classic scripts**, listed in `index.html` in load order. They share
one global scope: a top-level `function`, `const` or `let` in one file is visible in all the
others, and from the browser console (`transport`, `config`, `tick`, `iHoldControl`). There are
no ES modules, no `import` and no workers; CI checks that (`CodePathContinuityTests`).

Two rules follow from loading several files in order:

1. **Code that runs while the page loads may only use what earlier files declare.** A function
   *called* later (from a listener, a timer, the frame loop) can live in any file. A function
   declaration may move to an earlier file at any time; moving one later needs a check that
   nothing earlier calls it while loading.
2. **Anything asynchronous starts in `js/frame.js`**, at the end, before the first frame: the
   connection (`transport.start()`), CanvasKit, the frame loop. Between two script files the
   browser may run a callback (a WebSocket message, a timer, a promise), and that callback would
   find later files not loaded yet. In one big file this couldn't happen; at the end of the last
   file it still can't.

A new file goes in `js/` with a lower-case name, the licence header and its one-line purpose,
and a `<script src="/js/<name>.js">` in `index.html` at its place in the order. CI fails if a
file in `js/` or `css/` isn't loaded by `index.html`, or if `index.html` loads one that
doesn't exist.

## Adding a panel

1. **Markup** in `index.html`, beside the panels it belongs with: a `<div class="ln-panel">` for
   a menu panel (`ln-hdr` header with title and ✕), written in English.
2. **Style** in the `css/` file for that area (`panels.css` for a menu panel). The page has one
   typeface, `system-ui` (the body sets it, buttons and boxes inherit it): give a rule a size and a
   weight, not a font of its own (numbers may use `ui-monospace`, as `.cfg-num`). Text with no font
   of its own was in the browser's serif (Times New Roman) and buttons in its Arial before the body
   set one (Roll Correction, GPS Offset Fix, Recorded Path, the chart titles). Test: `PageFontTests`.
   `.ln-panel`'s max-width and max-height keep a panel on the screen at a bigger UI size, and it
   scrolls: give it no `overflow: hidden` and no max-height of its own. A panel with bars that
   must stay (AutoSteer's title and Set/Act/Err bars) scrolls the part between them (`.as-body`).
   Test: `PanelFitTests`.
3. **Code** in the `js/` file for that feature, or a new one: open it with
   `lnOpen('<id>', '<nav button id>', populate)`, fill it from `config` / `statusBar` / `scene`,
   send changes with `cfgSend('<section>.<key>', value)` (a `config.set`) or
   `transport.send('<command>|<arg>')`.
4. **Host:** a new command is routed in `RemoteServerWiring` (see `CLAUDE.md`, "Adding a web
   command"); new state comes in a frame (`Contracts.cs`, `WireCodec.cs` / `transport.js`).
   A command is one text message, of any length up to 1 MB (`WebSocketHub.MaxCommandBytes`:
   a boundary drawn on the map or a curve's points are tens of kB); a longer one is dropped
   whole and logged.
5. **Text:** `tr('…')` for strings the script builds; then `Tools/i18n-extract.py` and commit
   `en.json` (`Docs/TRANSLATIONS.md`).

Every command needs control: an observer (a browser without control) sees everything and
changes nothing (#397).

## The implement's picture on the map

Tool › Type › *Picture on the map* (`ToolConfig.Picture`, the `ToolPicture` enum, `tool.picture`,
saved with the tool profile, the Config frame's i32 after `totalWidth`) draws a top view of the
implement instead of the 2 m section bars and the hitch line. It is drawn to scale at the tool's
real hitch points: the tool position the host sends (`renderTool`) and the vehicle's hitches. With
*Sections only* (the default) the map is as before. Nothing but the drawing reads the setting.

The setting is a list (`tc-picsel`, filled in `populateToolCfg`) of the pictures made for the tool
type and the vehicle type chosen. Each picture is a row of `IMPLEMENT_PICTURES` in
`js/map-draw.js`: its `ToolPicture` value, the tool type and the vehicle types it is made for, its
draw function, its name and its note. A picture chosen for another type is not drawn, and the
list says so. A combine carries only a header; an articulated tractor pulls the big implements.
A new picture is a `ToolPicture` value, its draw function (and icons, if any) and a row;
`ImplementPictureListTests` checks there is one row per value.

- **From pictures**: the semi-mounted seed drill (`DrillBody.png`, `DrillHeadstock.png`) and the
  front sprayer boom with its rear tank (`SprayerBoom.png`, `SprayerTank.png`), 200 px per metre,
  each one's extent in metres in its draw call. They are a tester's own machines, drawn from their
  measures and photos.
- **From shapes**, sized from the tool's width and hitch lengths: the grain and maize headers, a
  manure spreader, a centre-pivot mower conditioner, disc harrows, a roller, ploughs (mounted,
  semi-mounted, disc), a fertiliser spreader, a rear side mower, five rakes, two air seeders and
  two precision planters. They are drawn in metres with y backwards: `implTractorFrame` from the
  rear axle for what rides on the tractor, `implTrailFrame` from the pin for a trailing tool.

The part that works (bodies, discs, cutter bar, beaters, openers) is always drawn on the line the
map paints, and the frame, arms and wheels fit round it. Over a picture the section bars become a
thin strip where it works (`implementStrip`, used by `toolFootprintSk` in `js/map-layers.js`).
A draw function that throws is caught: the section bars are drawn instead, never a blank map.

A plough has as many bodies as its width takes, about 40 cm each (a disc plough a disc every
30 cm), within what is made: 2 to 7 mounted, 5 to 12 semi-mounted, 2 to 6 discs. The ploughs
and the mower conditioner change side by themselves (`implWorkLeft`): they work towards the
ground still to do, read from the pass label (↑ A to B, ↓ B to A) and the way the passes last
changed. A pass counts after 10 m on it within a quarter of the tool's width of its line, or at
once when a U-turn lands on it. As a U-turn starts the plough turns over (`implSideNow`, eased
over about 3 s). Only the picture changes side: the painted strip stays where the tool settings
put it.

Colours are from the tester's machines where there were photos, else the implement's colour
(below). A picture never carries a maker's name.

Tool › Type › *Colour on the map* (`ToolConfig.PictureColor`, 0xRRGGBB, red by default:
`ToolConfig.DefaultPictureColor`; `tool.pictureColor` as hex, saved with the tool profile, the
Config frame's i32 after the picture) is shown once a picture is chosen. Every picture drawn from
shapes is drawn in it: its paint (`IC.red`, `IC.green`, `IC.yellow`) in that colour and its darker
parts (`IC.redD`, ...) in a shade of it (`implRecolour`, applied in `ip`); tyres, discs, tines and
the grey frames and tanks keep theirs. A combine's header takes it too. The seed drill and the
front sprayer are drawn from photos and keep their colours, and the panel says so. The list is
plain colours by name (`IMPLEMENT_COLOURS` in `js/config-panels.js`), no maker's name, and a
picker beside it for any other. A new tool profile, and one saved without a colour, has red.
Tests: `ToolPictureProfileTests`.

## Checking a change

- `node --check` on the files you changed (CI runs it on all of them).
- `python3 Tools/i18n-extract.py --check` if you added text.
- Run the Desktop head `--headless` and open `http://localhost:5174` (see `CLAUDE.md`,
  "Verifying a change in the running app"); the browser console shows load errors with the file
  and line.
- On a tablet over WiFi if the change touches loading or start-up.
