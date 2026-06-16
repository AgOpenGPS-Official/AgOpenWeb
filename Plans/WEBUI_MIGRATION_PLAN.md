# Web UI Migration Plan — Native → Browser, to Full Parity

Status: **planning** · Branch: `feature/web-ui-phase2` (spike proven) · Owner: chris

The browser client has been proven viable: live map at full parity (boundaries,
tracks, coverage, imagery, implement/section footprint, lightbar), Phase A/B
CanvasKit (true 3D perspective tilt), dead-reckoned pose, sim drive — validated
over WiFi on a Windows laptop and iPad. This document phases the **systematic
migration of the remaining native UI to the web**, to the point where the
browser can fully replace the native in-cab UI.

The native UI is the **reference model** — we are not reinventing functionality,
we are bringing the web to parity with it, region by region.

---

## 1. Guiding principles

1. **The host stays the brain; the browser is a thin client.** All logic stays in
   the Avalonia-free `ApplicationState` + services + `MainViewModel`. Migration =
   (a) project more *state* over the wire, (b) accept more *command ids* through a
   safe allowlist, (c) build HTML/JS that renders the state and sends the commands.
   We do **not** port business logic into JavaScript.
   - Seam files (already in place): `Shared/AgValoniaGPS.RemoteServer/Contracts.cs`,
     `WireCodec.cs`, `SceneProjector.cs`, `CoverageProjector.cs`, `MapBroadcaster.cs`,
     `WebSocketHub.cs`, `RemoteServerHost.cs`; client `wwwroot/{transport.js,app.js}`;
     command map in `Platforms/AgValoniaGPS.Desktop/App.axaml.cs`.
2. **Project state, not the View.** Runtime map data is pushed to `IMapService`
   (a View), *not* always present in `ApplicationState`. For each new element:
   find where the VM pushes it to the View → mirror it into state → project it.
   (Already done for `GuidanceState.DisplayLine`, section on/off via
   `SectionControlService`, `FieldState.Imagery`.)
3. **Alongside native through the whole migration.** The native UI keeps running
   and stays the source of truth until the web reaches *field-validated* full
   parity. Every web panel is built and tested side-by-side against its native
   twin. Cutover to headless-only is the final phase, not an interim risk.
4. **Wire contract stays deliberate, versioned, and state-shaped** — never a 1:1
   VM mirror. Camera is client-owned and never crosses the wire.
5. **Map-centric is non-negotiable.** Default view auto-pans a held camera; never
   lock to the vehicle every frame. (Carries over from native UX rules.)
6. **One renderer end-state.** Skia/CanvasKit becomes the default once parity is
   lived-in; the 2D-canvas fallback and `K`/`3`/`[`/`]` dev toggles retire at cutover.
7. **Validate each phase on the real fleet** (Desktop + iPad + laptop over WiFi)
   before moving on. Build to verify compile; wait for field confirmation; one
   commit+push per verified increment.

---

## 2. Actuation & safety model (decision: *enable actuation early*)

Web actuation is enabled progressively as operational panels are built (not
deferred to the very end). The **Remote Actuation Safety Layer (Phase 2)** lands
before the first hardware-actuating panel — the right-nav operational toolbar
(Phase 3).

**As-shipped model (Phases 2+3, commit 49c685cf).** The premise is **ONE operator
and a HEADLESS host** (no native cab UI). The earlier draft of this section assumed
two operators + a native banner + take/release + per-action confirm — all wrong;
the shipped model is deliberately simpler:

- **Command tiers** on the allowlist:
  - *Tier 0 — observe:* no commands (read-only panels).
  - *Tier 1 — safe actuation:* simulator drive, camera (client-side), settings &
    config writes (persisted, no live hardware).
  - *Tier 2 — live actuation:* sections, U-turn, autosteer, contour — gated by
    `ControlAuthority` in the hub (dropped unless the sender is the controller).
- **Control is implicit, by connection order.** The first browser to connect is the
  controller (server auto-`Acquire` on connect); later clients observe-only. **No
  take-over, no Take Control / Release UI, no per-action confirm** — being the
  controller is the gate (matches the native single-tap).
- **Deadman + connection-loss failsafe.** The controller heartbeats (~2 Hz); if it
  disconnects or the heartbeat lapses (~1.5 s), the host **disengages autosteer and
  turns sections off** so the machine never keeps actuating with no interface.
  Control is **not** handed off — reload to reconnect as controller when none held.
- **No ownership tracking.** One operator, so the failsafe simply disengages what's
  active on control-loss (no "remote-vs-cab" distinction).
- **Control state lives in the browser** (Controlling / Observing / No controller) —
  there is no native banner; the end-state host is headless.

Autosteer-engage (was excluded from the allowlist per `REMOTE_WEB_UI_SPLIT.md §5`)
is enabled under this gate.

---

## 3. Cross-cutting workstreams (run throughout, not a phase)

- **Wire-contract evolution.** Each phase adds fields to existing DTOs or a new
  versioned DTO. Keep the `SceneFingerprint` change-detection cheap; keep
  geometry `f32`, live pose `f64`.
- **Command-channel evolution.** Generalize the text allowlist to typed
  command + args + an ack/echo frame. Keep unknown-id = silently ignored.
- **Client UI framework.** A DOM overlay system: panel chrome, draggable floating
  panels, the modal **dialog host** (mirror of `DialogOverlayHost` / one-dialog-at-
  a-time `UIState` machine), on-screen **numeric & alphanumeric keyboards** for
  touch, day/night theming, units (metric/imperial). Built incrementally as the
  first panel of each kind needs it; reused after.
- **Config bridge.** A structured, read+write projection of `ConfigurationStore`
  (vehicle/tool/guidance/NTRIP/hotkeys/display) so config dialogs are generic
  forms over typed keys rather than bespoke endpoints.
- **Testing/validation.** Side-by-side vs the native twin each phase; fleet test
  over WiFi; safety-layer fault-injection (kill the socket mid-actuation).

---

## 4. Phase 0 — Foundation (prerequisite scaffolding)

The reusable scaffolding every later phase needs. These are built **lazily at
their first-use phase** rather than all up front (Phase 1's read-only status bar
needed none of them):

- **Command channel generalization** — typed `{id, args}` + `ack`/state-echo frame
  in `WireCodec`/`transport.js`; promote the `App.axaml.cs` switch to a registry
  with per-command tier metadata. → built in **Phase 2** (the safety layer needs it).
- **Client UI framework** — DOM panel/overlay layer + dialog host + on-screen
  keyboards + theming/units. → dialog host first needed in **Phase 7** (SimCoords).
- **Config bridge** — structured read/write `ConfigurationStore` projection. → built
  in **Phase 10** (left-nav config trees, the first config writes).

---

## 5. The clockwise sweep (Phases 1–9)

Perimeter sweep starting top, moving clockwise: **top status bar → right nav
(operational) → lower-right readouts → camera pad → clock → simulator → section
bar → bottom nav (field tools) → left nav (config + setup + fields).** The Remote
Actuation Safety Layer (Phase 2) is a foundation inserted before the first
actuation panel — the right nav, which is almost entirely Tier-2 live control.
Each phase lists native sources, what to read, what to control, and exit criteria.

> **Panel reality (verified from the AXAML, corrected 2026-06-15):** the **right**
> nav is the *operational actuation toolbar* used while working a field (contour,
> section master/manual, U-turn, autosteer). The **left** nav is the *config/setup/
> field hub* (File, Screen & Alerts, Tools, Vehicle/Tool config, Field Operations,
> Field Tools, AutoSteer config, Network IO/NTRIP). The big config trees live on the
> LEFT (Phase 10), not the right.

### Phase 1 — Top status bar  *(Tier 0)*  ✅ DONE (commit 28f215f6)
- **Native:** `Panels/StatusBarPanel.axaml`, `MainViewModel.StatusStrip.cs`.
- **Shipped:** `StatusDto` (frame 5) — fix quality/text, age, sats, units, module
  health+IPs+configured flags, job name, worked area. Client status bar: pause +
  two-line stack (Fix/Age over rotating Field/Stats/AB-line), aggregate Modules dot
  + per-module popup, big Speed. Established the add-a-panel loop.
- **Deferred:** heading (Phase 4 readouts), battery (n/a remote), on-map field-stats
  / GPS-detail toggles (later).

### Phase 2 — Remote Actuation Safety Layer  *(foundation — gate for all Tier 2)*  ✅ DONE (49c685cf)
- **Shipped (§2 as-shipped model):** `ControlAuthority` (single controller, implicit
  by connection order — server auto-`Acquire` on connect, later clients observe);
  Hello + ControlState frames; hub drops Tier-2 unless the sender is the controller;
  deadman heartbeat (~1.5 s) + connection-loss failsafe (host disengages autosteer +
  sections). No native banner, no take/release UI, no per-action confirm (headless,
  one operator).
- **Why now:** the next panel clockwise (right nav) is almost entirely Tier-2
  actuation — it can't ship without this.

### Phase 3 — Right nav: operational toolbar  *(Tier 2 — live actuation)*  ✅ DONE (3a 000b6105, 3b 49c685cf)
- **Native:** `Panels/RightNavigationPanel.axaml`; VMs `Commands.Track.cs`
  (sections, contour), `MainViewModel.YouTurn.cs`, `SectionControl.cs`,
  `Guidance.cs`, autosteer toggle.
- **Controls (verified):** Contour mode; Section **Manual** (all-on); Section
  **Master** (all-auto); YouTurn auto-arm; U-turn direction; Manual U-turn L/R;
  **AutoSteer engage/disengage**.
- **Read (wire+):** contour-mode on; section master/manual mode; youturn enabled;
  turn armed + direction + distance-to-trigger; autosteer 3-state (disabled/ready/
  engaged); has-active-track (the autosteer-available gate).
- **Control (Tier 2, via Phase 2):** `contour.toggle`, `section.master`,
  `section.manual`, `youturn.toggle/direction/manualLeft/manualRight`,
  `autosteer.engage/disengage` (re-enabled under the safety layer).
- **Client:** the right-edge button stack bound to live state (3-state autosteer
  colour, U-turn distance readout); actuation behind confirm.
- **Exit:** the operational toolbar drives a field session from the browser,
  matching native, under the safety layer. The core of "enable actuation early."

### Phase 4 — Lower-right cluster: roll gauge + camera/mode pad + clock  *(Tier 0 / client-side)*
> **Corrected from the AXAML (the inventory mis-grouped this):** the bottom-right
> `StackPanel` holds the roll gauge + camera pad; the clock sits just left of it.
> GPS-detail and heading are NOT here — see Phase 5.
- **Native (MainWindow bottom-right + TimeReadout):** `Panels/RollGaugeReadout.axaml`,
  `Panels/CameraPadControl.axaml`, `Panels/TimeReadout.axaml`; camera modes in
  `Commands.Navigation.cs`.
- **Roll gauge** — read (wire+): roll (+ pitch) added to the Tick; SVG/canvas gauge.
- **Camera/mode pad** (tilt / zoom / mode-cycle) — **client-owned**: drives the
  camera we already have (pan/zoom/tilt/follow). Map native modes (HeadingUp /
  NorthUp / Map / Free, 2D/3D, grid, day/night, auto-track) onto client behaviour;
  honor **map-centric** default. On-screen pad UI.
- **Clock** — host time (or browser local). Trivial.
- **Exit:** the bottom-right cluster matches native; the pad fully drives the camera.

### Phase 5 — Status-bar completion: heading readout + GPS-detail card  *(Tier 0)*
> Status-bar elements deferred from Phase 1 (corrected from the AXAML — they are NOT
> in the lower-right cluster).
- **Heading readout** — sits in the top strip right of speed (`Panels/HeadingReadout.axaml`;
  heading is already on the Tick). Add the widget to the web status bar.
- **GPS-detail card** (`Panels/GpsDetailPanel.axaml`) — an on-map popup toggled by
  the strip's fix dot: lat/lon, altitude, sats, age (extend Status/Tick).
- **Exit:** heading shows in the status bar; tapping the fix dot opens a GPS-detail card.

### Phase 6 — Simulator panel  *(Tier 1 — safe actuation)*
- **Native:** `Panels/SimulatorPanel.axaml`, `Dialogs/SimCoordsDialogPanel.axaml`;
  `Commands.Simulator.cs`, `Simulator.cs`.
- **Read (wire+):** sim enabled, speed, steer angle.
- **Control:** `sim.enable/disable/reset/reverse/reverseDir`, speed up/down (have),
  `sim.setCoords` (teleport). Sim is hardware-safe.
- **Client:** full sim panel + the **first real dialog** (SimCoords) — stands up the
  dialog host + numeric keyboard against a live command-with-args.
- **Exit:** full sim control + teleport from browser; dialog framework proven.

### Phase 7 — Section bar  *(Tier 2 — per-section)*
- **Native:** `Panels/SectionControlPanel.axaml`; `SectionControl.cs`, section
  commands in `Commands.Track.cs`.
- **Read:** per-section ColorCode already in `TickDto`; master/manual mode from
  Phase 3.
- **Control (Tier 2):** `section.toggle(index)` — through the safety layer.
- **Client:** colour-coded section bar bound to live ColorCodes; per-section
  actuation behind confirm.
- **Exit:** individual sections arm/disarm from browser, reflected by host echo.

### Phase 8 — Bottom nav: field tools  *(mixed tiers)*
- **Native:** `Panels/BottomNavigationPanel.axaml`, `Panels/FieldToolsPanel.axaml`;
  VMs `Commands.Track.cs` (AB cycle/snap/nudge, flags, tram), `Headland.cs`,
  coverage/contour delete. *(Verify exact split BottomNav vs FieldTools when reached.)*
- **Read (wire+):** nudge offset, tram mode/lane, headland on, flag list.
- **Control:** guidance snap/nudge (Tier 2), `youturn.trigger` (Tier 2), `flag.*`
  + `tram.*` display (Tier 1), headland toggles, coverage delete.
- **Client:** the bottom field-tools toolbar; actuation gated.
- **Exit:** AB/flag/tram/headland field tools usable from the browser.

### Phase 9 — Left nav: config + setup + field/file hub  *(Tier 1)*  ⚠ largest phase
- **Native:** `Panels/LeftNavigationPanel.axaml` and everything it opens:
  - File Menu (`FileMenuPanel`), Screen & Alerts (`ScreenAlertsPanel` + `AppSettings`),
    Tools (`ToolsPanel`).
  - **Vehicle/Tool Configuration** picker hub → `VehicleConfigDialog`/`ToolConfigDialog`
    + all `Configuration/*Tab.axaml` + `*SubTab.axaml`.
  - **AutoSteer Config** (`AutoSteerConfigPanel`, steer wizard, `SmartWas`).
  - **Network IO** (modules + **NTRIP** profiles) — `NetworkIoPanel`, `NtripProfiles*`.
  - Field Operations + Field Tools launchers; field-lifecycle dialogs
    (`FieldSelection`, `StartWorkSession`, `ResumeJob`, `NewField`, `FromExistingField`,
    `FieldBuilder`, `BoundaryMap`, `KmlImport`, `IsoXmlImport`, `ImportTracks`,
    `Tracks`, `LoadVehicleTool`, AgShare).
  - **Bottom-nav items deferred from Phase 8** (the toolbar shell + all direct-action
    tools shipped in Phase 8; these need a dialog or interactive map-mode that lands
    here). All live in `BottomNavigationPanel.axaml`:
    - **AB-line flyout — Tracks dialog** (`ShowTracksDialogCommand` → `Tracks` dialog,
      manage/select saved AB lines).
    - **AB-line flyout — Quick AB creator** (`ShowQuickABSelectorCommand` → mode
      selector for drive-in AB creation).
    - **AB-line flyout — Draw AB** (`ShowDrawABDialogCommand` → **map-tap** point
      placement; build on the canvas map interaction).
    - **Flags flyout — Place Flag On Map** (`PlaceFlagOnClickCommand` → **map-tap**
      single-shot place mode).
    - **Flags flyout — Flag List** (`ShowFlagListCommand` → flag manager dialog).
    These re-use the Phase-6 dialog host; the two map-tap modes share the editor map
    interaction built for BoundaryMap/DrawAB/FieldBuilder above.
  - VMs: `Commands.Configuration.cs`, `Commands.Ntrip.cs`, `Commands.Settings.cs`,
    `Commands.Hotkeys.cs`, `Commands.Wizards.cs`, `Commands.Fields.cs`,
    `TrackManagement.cs`, `BoundaryRecording.cs`, `Commands.Boundary.cs`.
- **New infra:** the **config bridge** — a structured read/write projection of
  `ConfigurationStore` + a tiered `config.set` / `profile.save` command family (first
  time the client writes config). Field list/job-history read; `field.open/create/
  close/resume`; import via HTTP upload then host-side import.
- **Heavy — sub-phase:** 10a config bridge → 10b Vehicle config → 10c Tool config →
  10d AutoSteer config → 10e Network IO/NTRIP → 10f App/Screen & Alerts settings →
  10g Field Operations/lifecycle → 10h Field Tools/boundary editors + import/export.
  Editor dialogs (BoundaryMap, DrawAB, FieldBuilder) need **map-tap interaction** —
  build on the proven canvas map.
- **Exit:** create/edit a vehicle+tool+NTRIP profile, change settings, and open/
  create/close/resume a field + import boundaries entirely from the browser — web no
  longer needs native for setup or the field lifecycle.

---

## 6. Phase 10 — Mop-up, parity sweep & cutover

- **Remaining dialogs/panels not yet built:** charts (XTE/steer/heading via a JS
  charting layer), `ScreenAlertsPanel`/alerts overlay, `MapBannersPanel`,
  `FieldStatsPanel`, `FlagList`, `RecordedPath`, `TramSettings`, `HeadlandBuilder`,
  `QuickABSelector`, `OffsetFix`, `LogViewer`, `BugReport`, `About`, `Help`,
  `Language`, `UnsavedCoverage`, `Error`/`Confirmation`/`NumericInput` generics.
- **Parity audit:** walk the §"Web UI parity checklist" (from the feature
  inventory) until every native action has a web equivalent; fault-inject the
  safety layer once more.
- **Cutover to headless-only** (`REMOTE_WEB_UI_SPLIT.md` end-state):
  - Switch `CoverageProjector` from the alongside-mode non-draining diff to the
    efficient single-consumer `GetNewCoverageBitmapCells` drain (no native renderer
    competing for it).
  - Make Skia/CanvasKit the default renderer; retire the 2D fallback + dev toggles.
  - Run the host headless (no Avalonia window); browser is the only UI.
  - Decommission native panels/dialogs once field-validated across the fleet.

---

## 7. Sequencing rationale (clockwise, with the dependency note)

Clockwise (top → right → readouts → camera → clock → sim → section bar → bottom →
left) is kept deliberately:
- It front-loads the **operational toolbar (Phase 3, right nav)** — the "enable
  actuation early" centerpiece — right after standing up the safety layer (Phase 2).
- The tension: operational panels (Phase 3 right nav, Phase 7 section bar, Phase 8
  field tools) want a **field + tracks + config loaded**, but field lifecycle and
  the config trees live in the **left nav (Phase 9, last)**. This is **not blocking**
  because the web runs **alongside native** — open a field and load vehicle/tool
  config on the native app to exercise each web operational panel as it's built.
- Config *correctness* matters for section/guidance geometry, but since native owns
  config until Phase 10, the alongside app supplies a valid config throughout. If
  the crutch proves annoying, pull a *minimal* `field.open/resume` + read-only config
  view forward without moving the whole left-nav block.

---

## 8. Definition of done (per phase & overall)

- **Per phase:** web region matches its native twin live; new wire fields are
  state-projected (not View-scraped); new commands are tiered + (Tier 2) gated;
  validated on Desktop + iPad + laptop over WiFi; committed+pushed once confirmed.
- **Overall parity:** every item on the native feature checklist has a web
  equivalent; safety layer fault-tested; field-validated across a real session.
- **Cutover:** host runs headless, browser is sole UI, coverage on the efficient
  drain, single renderer, native UI decommissioned.
