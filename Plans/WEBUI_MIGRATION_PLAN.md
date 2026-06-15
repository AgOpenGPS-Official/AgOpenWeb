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
deferred to the very end), running **alongside** native with these guardrails.
The **Remote Actuation Safety Layer (Phase 6.5)** must land before the first
hardware-actuating panel (the section bar, Phase 7).

- **Command tiers** on the allowlist:
  - *Tier 0 — observe:* no commands (read-only panels).
  - *Tier 1 — safe actuation:* simulator drive, camera (client-side), settings &
    config writes (persisted, no live hardware). Allowed freely.
  - *Tier 2 — live actuation:* section arm on/off, U-turn enable/trigger, autosteer
    engage/disengage, nudge/snap. Allowed **only** through the safety layer.
- **Per-action confirmation** for Tier 2 (hold-to-confirm or explicit confirm tap).
- **Operator-presence / deadman:** Tier 2 requires a live, recently-interacted
  session; idle or backgrounded clients cannot actuate.
- **Connection-loss failsafe:** socket drop → host auto-disengages autosteer and
  reverts sections to safe state (configurable), independent of the client.
- **"REMOTE CONTROL ACTIVE" indicator on the native in-cab screen** whenever a
  browser holds Tier 2 authority, plus which client.
- **State echo / audit:** host confirms the resulting state back to the client
  (and logs it); the client never assumes a command took — it reflects host state.
- **Single actuator authority:** only one client may hold Tier 2 at a time;
  native always retains override.

Autosteer-engage (currently excluded from the allowlist per
`REMOTE_WEB_UI_SPLIT.md §5`) is re-enabled **only** under this layer, with sign-off.

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

Stand up what every later phase reuses. No user-visible parity yet.

- Generalize the command channel: typed `{id, args}` + `ack`/state-echo frame in
  `WireCodec`/`transport.js`; promote the `App.axaml.cs` switch to a registry with
  per-command tier metadata.
- Client UI framework v1: DOM panel/overlay layer + dialog host + on-screen
  keyboards + theming/units, layered over the existing canvas map.
- Config bridge v1: read-only structured `ConfigurationStore` projection (writes
  arrive in Phase 2).
- **Exit:** a throwaway demo panel can render host state and round-trip a Tier-1
  command with an ack; dialog host can open/close a placeholder modal.

---

## 5. The clockwise sweep (Phases 1–9)

Perimeter sweep starting top, moving clockwise: **top → right → lower-right
readouts → bottom → left.** Each phase lists native sources, what to read, what to
control, and exit criteria. Wire/command additions are incremental.

### Phase 1 — Top status bar  *(Tier 0)*
- **Native:** `Panels/StatusBarPanel.axaml`, `MainViewModel.StatusStrip.cs`,
  `MainViewModel.NetworkIO.cs`; `Panels/NetworkIoPanel.axaml`.
- **Read (wire+):** new `StatusDto` — fix quality + detail, sat count, GPS Hz,
  NTRIP connected/status/bytes, speed, worked-area, age-of-correction, network
  in/out. Most derive from `ApplicationState` + `INtripClientService`.
- **Control:** none.
- **Client:** top status bar in DOM, bound to `StatusDto`.
- **Exit:** status bar matches native readouts live; proves the "add a readout"
  loop end-to-end. *This is the template phase.*

### Phase 2 — Right nav panel + all contents  *(Tier 1 — config writes)*  ⚠ largest phase
- **Native:** `Panels/RightNavigationPanel.axaml`; the config trees:
  `Dialogs/VehicleConfigDialog.axaml`, `Dialogs/ToolConfigDialog.axaml` and all
  `Configuration/*Tab.axaml` + `*SubTab.axaml`; `Dialogs/NtripProfiles*`,
  `Dialogs/AppSettingsDialogPanel`, `Dialogs/ViewSettingsDialogPanel`,
  `Dialogs/AutoSteerConfigPanel`, `Dialogs/HotkeyConfigDialogPanel`,
  `Dialogs/SmartWasDialogPanel`. VMs: `Commands.Configuration.cs`, `Commands.Ntrip.cs`,
  `Commands.Settings.cs`, `Commands.Hotkeys.cs`, `Commands.Wizards.cs`.
- **Read/write (wire+):** full `ConfigurationStore` config bridge (read) + a
  `config.set` / `profile.save` command family (write, Tier 1 — persisted, no live
  actuation). NTRIP test-connection command.
- **Control:** NTRIP connect/test; profile load/save; settings writes.
- **Client:** the multi-tab config forms (Vehicle: dims/antenna/sources; Tool:
  type/offset/sections/hitch/switches/timing/pivot; Sections; U-turn; Tram;
  Machine/pins; App; View; AutoSteer; Hotkeys). Heavy — **sub-phase internally**:
  2a Vehicle, 2b Tool, 2c NTRIP, 2d App/View settings, 2e AutoSteer/SmartWas/Hotkeys.
- **Exit:** a vehicle+tool+NTRIP profile can be created/edited/saved from the
  browser and matches what native writes to the profile JSON. Config is foundational
  for section/guidance correctness in later phases.

### Phase 3 — Lower-right readouts: roll gauge, GPS detail, heading  *(Tier 0)*
- **Native:** `Panels/RollGaugeReadout.axaml`, `Panels/GpsDetailPanel.axaml`,
  `Panels/HeadingReadout.axaml`.
- **Read (wire+):** extend `TickDto`/`StatusDto` with roll, pitch, lat/lon,
  altitude, heading detail (heading already present).
- **Control:** none.
- **Client:** SVG/canvas roll gauge, GPS detail readout, heading readout.
- **Exit:** gauges track native live.

### Phase 4 — Camera / mode pad (tilt, zoom, heading mode)  *(Tier 1 — client-side)*
- **Native:** `Panels/CameraPadControl.axaml`; `MainViewModel.Commands.Navigation.cs`
  (camera modes, 2D/3D, pitch, grid, day/night, auto-track).
- **Read:** vehicle heading (already in Tick) for heading-up.
- **Control:** **client-owned** — drives the camera we already have (pan/zoom/
  follow/tilt). Map native modes (HeadingUp / NorthUp / Map / Free, auto-track,
  grid, day/night) onto client behaviors. Minimal host involvement.
- **Client:** on-screen pad: zoom ±, tilt (reuse `[`/`]`), 2D/3D, north-up/heading-
  up, grid, day/night. Honor **map-centric** default.
- **Exit:** pad fully drives the client camera; modes match native semantics.

### Phase 5 — Clock  *(Tier 0)*
- **Native:** `Panels/TimeReadout.axaml`.
- **Client:** small clock (host time over wire, or browser local). Quick item.
- **Exit:** time displayed; trivial.

### Phase 6 — Simulator panel  *(Tier 1 — safe actuation)*
- **Native:** `Panels/SimulatorPanel.axaml`, `Dialogs/SimCoordsDialogPanel.axaml`;
  `Commands.Simulator.cs`, `Simulator.cs`.
- **Read (wire+):** sim enabled, speed, steer angle.
- **Control (allowlist+):** `sim.enable/disable/reset/reverse/reverseDir`, speed
  up/down (have), `sim.setCoords` (teleport). Sim is hardware-safe.
- **Client:** full sim panel + the **first real dialog** (SimCoords) — stands up
  the dialog host + numeric keyboard from the framework against a live command-with-
  args.
- **Exit:** full sim control + teleport from browser; dialog framework proven.

### Phase 6.5 — Remote Actuation Safety Layer  *(gate before Tier 2)*
- Build §2: per-action confirm, deadman/presence, connection-loss failsafe (host
  auto-disengage), native "REMOTE CONTROL ACTIVE" banner, single-authority lock,
  state echo/audit. Fault-inject tested (kill socket mid-actuation).
- **Exit:** a Tier-2 stub command can only fire under confirm+presence; socket drop
  triggers the host failsafe; native shows the remote-active banner. **Required
  before Phase 7.**

### Phase 7 — Section bar  *(Tier 2 — live actuation)*
- **Native:** `Panels/SectionControlPanel.axaml`; `MainViewModel.SectionControl.cs`,
  section commands in `Commands.Track.cs`.
- **Read:** per-section ColorCode already in `TickDto`; add master auto / manual
  mode state.
- **Control (Tier 2):** `section.toggleMaster`, `section.toggleManual`,
  `section.toggle(index)` — through the safety layer.
- **Client:** color-coded section bar bound to live ColorCodes; actuation behind
  confirm.
- **Exit:** sections arm/disarm from browser, reflected by host echo; first live
  actuation, validated against native.

### Phase 8 — Bottom nav panel + all contents  *(Tier 2 — operational heart)*
- **Native:** `Panels/BottomNavigationPanel.axaml`, `Panels/FieldToolsPanel.axaml`;
  VMs `Commands.Track.cs` (AB cycle/snap/nudge, flags, tram), `YouTurn.cs`,
  `Guidance.cs`, autosteer toggle, `Headland.cs`, tram, coverage delete.
- **Read (wire+):** engaged states — `isAutoSteerEngaged`, `isYouTurnEnabled`,
  turn armed/direction/skip, nudge offset, tram mode/lane, headland on.
- **Control (Tier 2):** guidance snap/nudge, `youturn.enable/trigger/direction/skip`,
  `autosteer.engage/disengage` (re-enabled under safety layer), `flag.*`, `tram.*`,
  coverage/contour delete.
- **Client:** operational toolbar; all actuation gated.
- **Exit:** full operational control from browser, matching native, under safety
  layer. The functional core of "replace native."

### Phase 9 — Left nav panel + all contents  *(Tier 1 — field/file lifecycle)*
- **Native:** `Panels/LeftNavigationPanel.axaml`, `Panels/FileMenuPanel.axaml`,
  `Panels/FieldOperationsPanel.axaml`, `Panels/ToolsPanel.axaml`; field dialogs:
  `FieldSelection`, `StartWorkSession`, `ResumeJob`, `NewField`, `FromExistingField`,
  `FieldBuilder`, `BoundaryMap`, `KmlImport`, `IsoXmlImport`, `ImportTracks`,
  `Tracks`, `LoadVehicleTool`, AgShare upload/download. VMs: `Commands.Fields.cs`,
  `TrackManagement.cs`, `BoundaryRecording.cs`, `Commands.Boundary.cs`.
- **Read (wire+):** field list + metadata, job history, track list, recent jobs.
- **Control:** `field.open/create/close/resume`, import flows (file **upload over
  HTTP** to the host, then host imports), boundary record start/stop, track create
  /delete. Mostly Tier 1 (file/state); boundary-record drive-around may touch Tier 2.
- **Client:** field/job lifecycle UI + the heavy editor dialogs (BoundaryMap,
  DrawAB, FieldBuilder) which need **map-tap interaction** — build on the proven
  canvas map. Placed last per the sweep; the alongside native app covers field-open
  while earlier operational phases are built and tested.
- **Exit:** open/create/close/resume a field and import boundaries entirely from the
  browser; web no longer needs native for the field lifecycle.

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

Clockwise (top → right → readouts → bottom → left) is kept deliberately:
- It front-loads **config (Phase 2, right nav)** — the most foundational piece for
  section/guidance *correctness* downstream.
- The only real tension: operational panels (Phase 7 sections, Phase 8 bottom-nav
  guidance) want a **field + tracks loaded**, which lives in the **left nav (Phase 9,
  last)**. This is **not blocking** because the web runs **alongside native** — open
  a field on the native app to exercise each web operational panel as it's built.
- A counter-clockwise sweep would only pull web-native field-open earlier, at the
  cost of deferring config — a worse trade. If, during build, the alongside-native
  crutch proves annoying, pull a *minimal* `field.open/resume` action forward from
  Phase 9 into Phase 6/7 without moving the whole left-nav block.

---

## 8. Definition of done (per phase & overall)

- **Per phase:** web region matches its native twin live; new wire fields are
  state-projected (not View-scraped); new commands are tiered + (Tier 2) gated;
  validated on Desktop + iPad + laptop over WiFi; committed+pushed once confirmed.
- **Overall parity:** every item on the native feature checklist has a web
  equivalent; safety layer fault-tested; field-validated across a real session.
- **Cutover:** host runs headless, browser is sole UI, coverage on the efficient
  drain, single renderer, native UI decommissioned.
