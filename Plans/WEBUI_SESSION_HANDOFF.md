# Web-UI Migration — Session Handoff (continuation prompt)

Paste the section below to continue the AgOpenWeb web-UI migration in a fresh session.

---

**Continue the AgOpenWeb web-UI migration.**

## Repo / branch
- Repo: `/Users/chris/Code/AgOpenWeb3` (Avalonia/.NET 10 agricultural GPS app).
- Branch: `feature/web-ui-phase2` (off **develop** — PRs target develop, NOT master).
  Stays unmerged until field-validated; commit + push to it as we go. **develop has been
  merged in** (the §13/§14 config/state apply-gap fixes — `SectionState` class was deleted
  upstream; `SceneProjector` reads `ISectionControlService` + `ToolConfig.MaxSections`).
- Working tree clean. **Current version `26.5.95`** (we DO bump `sys/version.h` per commit now).

## ⏭ NEXT SESSION — START HERE: Phase 10 (headless cutover) — the LAST migration item
Everything else in the migration is done. Phase 10 = the host goes **UI-less** (no Avalonia
window); the browser becomes the only UI. This is also the **prerequisite for the appliance
deployment** (you can't cleanly daemonize a windowed Avalonia app — a headless generic-host +
Kestrel process is what runs as a systemd service / Windows Service).
- **Deployment decision reached this session:** the headless host should run as a **service/
  daemon** on the cab-PC / one-box-SBC targets (auto-start on boot, `Restart=always`, no display
  needed since rendering is in the browser, dedicated unprivileged user in the `can`/`dialout`
  groups, `After=network-online`, `WatchdogSec`+`sd_notify`, journald). The **UI is always a
  separate layer** — a remote tablet browser OR a local **chromium-kiosk** at `localhost`. The
  co-resident **mobile** case is NOT a daemon — it's an app (Android foreground service; iOS
  foregrounded app + Guided Access). Not yet written into `DEPLOYMENT_PATTERNS.md` — offered, do it
  if asked. See `[[project_deployment_patterns]]`.
- Phase 10 scope: strip the Avalonia window/UI from the host startup path so it boots as a plain
  .NET host (services + `RemoteServerHost`), confirm Desktop still serves the browser headless,
  then the per-platform packaging (systemd unit, etc.). `RemoteServerHost.StartAsync` is already
  the seam; it's only wired from Desktop `App.axaml.cs` today.

## ⭐ STATUS (2026-06-19, session 2): map-motion + quality/parity polish DONE (v26.5.93–95)
Continuation after Phase MT. All committed + pushed to `feature/web-ui-phase2`.
- **iPad full-screen chrome (v26.5.93)** — the "X pill" was the **browser's Full-Screen-mode exit
  control** (Safari AND Chrome), NOT ours and NOT removable from web code → solved by launching via
  **Add to Home Screen** (standalone, no X). The **iOS status bar overlap** fixed with
  `env(safe-area-inset-*)` offsets (`:root --sat/--sab/--sal/--sar`; map stays full-bleed). Also
  removed the green test **HUD** + bottom-left **help text**, and promoted **Operator/Observer/
  Disconnected** into the status bar (`#sb-role`, left of Modules; driven by control state + `connState`).
- **Smooth map motion (v26.5.94)** — THE fix for the background "jump while driving". Root cause was
  NOT precision — it was the **dead-reckoning re-anchor snap** (client extrapolated, then each tick
  hard-snapped the anchor to a latency/jitter-delayed pose → whole world jumped ≈latency×speed).
  Replaced extrapolation with **INTERPOLATION**: render `RENDER_DELAY=120 ms` in the past, lerp
  position+heading between the two most recent real poses (`prevTick`→`lastTick`). Deleted the
  `drOff`/`smoothHdg` smoothing apparatus. Also added **camera-relative rendering** (subtract
  camE,camN in f64 before the f32 M44 — kills floating-origin sub-pixel jitter, mirrors native
  `relX = _vehicleX - _cameraX`) and **imagery/ground mipmaps** + the **grid visibility toggle**.
  ⚠ Debugging lesson: a per-frame DOM `textContent` diagnostic overlay *itself* caused background
  jitter (forced reflow) — observer effect; removed it.
- **Residual U-turn jitter — LEFT ALONE (decided):** the U-turn entry/exit endpoints jitter slightly;
  **native does it too** (it's in the host U-turn generation, regenerated from the live pose — NOT a
  web renderer bug). Only visible zoomed way in; imperceptible at normal zoom. Real fix would be
  host-side U-turn anchor smoothing (helps native too) — parked.
- **Quality button + front wheels (v26.5.95)** — quality (`DisplayResolutionMultiplier`) now drives
  the web like native: **coverage** (new shared `CoverageMapService.RebuildDisplayForResolutionChange()`
  — the rebuild previously lived only in the native `SkiaMapControl`, so the web never saw it; the
  command now calls both) AND **background imagery** (projected the numeric multiplier; `drawImagerySk`
  downsamples its LOD by it — mirrors native's **Apple composite path = iOS *and* macOS**, so the Mac
  desktop shows it too). Added the **steerable front-wheel sprite** (`FrontWheels.png` + new Tick
  field `VehicleSteerAngle` = sim-slider-or-WAS like native `ApplyResults`; drawn at both front-axle
  ends rotated by −steer in the +Y-forward frame).
- **Docs:** `DEPLOYMENT_PATTERNS.md` gained the **Uno Q two-chip RPC-bridge** realization (+ .NET
  binding caveat) and the **x86-64 backend** pattern (Pattern B/D).

## ⭐ STATUS (2026-06-19): Phase MT DONE + motion-smoothness pass DONE — only Phase 10 (headless cutover) remains.
- **Motion smoothness (v26.5.92)** — web tool/pose/coverage now glide like native (verified
  native + Mac/Android/iPad browsers at once). See memory `[[project_webui_motion_smoothness]]`:
  project the render-pull DEAD-RECKONED pose+tool+hitch (`VehicleState.Render*`, written in
  `MainViewModel.OnRenderPullTick`), NOT the control-loop/GPS-anchored snapshots (those step at
  GPS rate / make client DR snap back = staccato). Coverage diff moved OFF the 100Hz control-loop
  thread to the broadcaster loop + a server-dedicated incremental drain
  (`CoverageMapService.GetNewCoverageBitmapCellsServer` / `_newCellsServer`,
  `CoverageProjector.IncrementalDelta`) — the old O(~56M-cell) scan on the control thread stalled
  real-time control in ~500ms bursts.
- **Nav bars unified (v26.5.91)** — bottom + right match the left bar (edge-docked, translucent
  bordered, borderless 56×56 icon buttons); semantic state colours kept.

## ⭐ STATUS (2026-06-19): Phase MT is DONE — only Phase 10 (headless cutover) remains.
- **Field Builder — ALL 4 stages complete** (v26.5.86–89): Tracks tab, Headland tab +
  multi-line builder, Tram tab + systems editor, on-map point Edit (drag track/headland
  points). See memory `[[project_headland_lines_model]]` for the headland-lines model
  (multi-line overshoot-and-cross → closed loop; single line can't enclose; DON'T connect
  lines to the boundary — they must cross each other). Tram systems live in
  `ConfigStore.Tram.Systems`; tram LINES need ALL FOUR `ITramLineService` collections
  projected (outer+inner boundary tracks, parallel, boundary-extra), not just ParallelTramLines.
- **Import-KML boundary picker — DONE** (v26.5.90): Boundary menu → Import-KML → picks a
  KML/KMZ from `Documents/AgOpenWeb/Import` (FieldOps `KmlFiles` list) → host parses +
  imports into the open field. Cmd `boundary.importKmlFile|<name>`.
- **Fixed latent native bugs along the way:** `BuildTramLinesCommand` null-deref when the
  last tram system is deleted with no track selected (now null-safe).
- **NEXT = Phase 10 — headless cutover** (host goes UI-less; browser is the only UI). This is
  the only remaining migration item.

## What this is
Replacing the native in-cab Avalonia UI with a browser client served by an embedded
ASP.NET Core server (`Shared/AgOpenWeb.RemoteServer`) that runs alongside the app.
Browse to `http://<host>:5174`. The **host stays the brain** (Avalonia-free
`ApplicationState` + services + `MainViewModel`); the browser is a thin client that
receives projected *state* over a binary WebSocket and sends *command ids* back through a
safe allowlist. Migration = project more state + accept more command ids + build HTML/JS.
**Do NOT port logic into JS.** End state: headless host, browser is the only UI.

- **Full plan:** `Plans/WEBUI_MIGRATION_PLAN.md` (phase list, safety/control model, cutover).
- **Seam files:** `RemoteServer/{Contracts.cs, WireCodec.cs, SceneProjector.cs,
  CoverageProjector.cs, MapBroadcaster.cs, WebSocketHub.cs, ControlAuthority.cs,
  RemoteServerHost.cs}`; client `wwwroot/{app.js, transport.js, index.html}`; command map +
  safety wiring + host-side projectors/handlers in `Platforms/AgOpenWeb.Desktop/App.axaml.cs`.

## Done & pushed
- **Phase MT — map-tap interaction (session 2026-06-18 → -19), v26.5.81–86:**
  - **`s2w` foundation** (v26.5.81) — screen→world unprojection: invert the `perspM` M44 +
    ray-cast the ground plane (`s2w(px,py)` in `app.js`; round-trip verified < 1e-9 m at
    pitch 0 AND 60°). Plus the reusable **`startMapTap`/`endMapTap`** primitive (tap-vs-pan
    5 px slop, `#maptap-hint` pill, Esc-cancel, `gestureOnMap` guard). EVERY map-tap feature
    builds on these — do not write a 2D-ortho inverse.
  - **Place flag at tapped point** (v26.5.82) — `flag.placeAt|e,n` → `PlaceFlagAtWorldPosition`.
  - **Quick AB / Draw AB + full native-parity track flyout + Tracks manager** (v26.5.83) —
    rebuilt the bottom-nav AB flyout to mirror `BottomNavigationPanel` (Tracks-manager /
    Auto-track / Quick-AB selector · boundary-edge / Draw-AB selector · cycle/smooth/delete ·
    nudges). Quick-AB selector (A+/Drive AB/Record Curve), Draw-AB selector (Straight/Curve
    map-tap + From-Boundary), Tracks manager (list+activate/delete/swap/visibility, Scene
    `TrackList` frame field). **SoT fix:** `State.Field.Tracks` now mirrors `SavedTracks` via
    one ctor `CollectionChanged` subscription (created tracks reach ApplicationState). Map
    render = active-track-only (native draws only ActiveTrack); reference drawn after the
    magenta DisplayLine. **Operator gating** rename ("Controlling"→"Operator") + track
    data/creation un-gated, guidance changes gated.
  - **Boundary draw-on-map (Bing satellite)** (v26.5.84) — port of `BoundaryMapDialog`:
    `/sattile/{quadkey}` host proxy (keyless Virtual-Earth tiles, same-origin/no-CORS);
    `drawSatelliteSk` slippy-map underlay (E/N↔lat/lon↔web-mercator-tile↔quadkey, zoom from
    pxPerM); `boundary.fromMapPoints` builds the boundary from tapped E/N; host-side imagery
    capture (`BoundaryImageryCapture.cs`, SkiaSharp composite of covering tiles → field
    background via `SaveBackgroundImage`). `ImageVersion` folds in file mtime for re-draw.
  - **Flag list** (v26.5.85) — `FlagListDialogPanel` port: colour swatch (10-colour picker),
    inline rename, distance+bearing, locate (pan), delete; Place Here / on Map / Delete All.
    `FlagMarker`/`FlagDto` carry name; flag fingerprint includes name+colour.
  - **Field Builder — STAGE 1 of 4** (v26.5.86, this commit) — `FieldBuilderDialogPanel` is a
    big Tracks/Headland/Tram editor; **decided: reuse the MAIN map + s2w for drawing/editing**
    (NOT native's separate full-screen canvas), Field Builder = a NO_SCRIM control panel
    (`#fieldbuilder`, opens from Field Tools). **Stage 1 done = shell + Tracks tab**: track
    list (reuses Scene `trackList`) + Add (Free-Draw AB/Curve/A+ via existing flows; From-
    Boundary) + Rename (`track.rename`) + Delete + Delete-All (`track.deleteAll`). **Headland
    / Tram tabs = placeholders; track Edit = stage-4 placeholder.** REMAINING: **stage 2**
    Headland tab + building (line/curve/from-boundary offset; `HeadlandSegments` exists),
    **stage 3** Tram tab + editor (`TramSystems` = `ConfigStore.Tram.Systems`), **stage 4**
    on-map Edit sessions (load track/headland points → drag/redraw/save). Native code-behind
    `FieldBuilderDialogPanel.axaml.cs` (~2300 lines) is the reference for the draw/edit session.
  - **Bug/parity fixes folded into v26.5.86:** (a) **recorded path won't delete** — root
    cause `LoadRecPathFromField` re-adds `RecPath.txt` every field open; fix: deleting a
    `RecordedPath` track (or Delete-All) now also deletes `RecPath.txt`
    (`DeleteContourTrackCommand`/`DeleteAllTracksCommand`). (b) **Extra guidelines** now
    render on web (`drawExtraGuidelinesSk`, offset ±toolW·i, zoom-gated). (c) **Auto-track
    persists** across restarts — `IsAutoTrackEnabled` backed by `ConfigStore.Display.AutoTrack`
    ↔ `AppSettings.AutoTrack`, saved on toggle, mirrored to `State.FieldTools`.
- **Phase MT remaining (the migration is NOT done until these ship):** Field Builder stages
  2–4 (Headland building, Tram systems+editor, on-map Edit), the **Import-KML boundary
  picker** (project `AvailableKmlFiles` + import; `bm-importkml` stub), then **Phase 10**
  headless cutover. (On-map *track* point-editing has no standalone native feature — it lives
  inside Field Builder's Edit; user confirmed.)
- **Established map-tap patterns (reuse):** `startMapTap({hint,onTap})` for single/multi-tap;
  client-side point buffer drawn live (`drawSatBoundarySk`/draw preview) + ship captured
  points to host on finish (host runs the real native save path — NO geometry in JS); the
  shared `#draw-toolbar` (Set Point/Undo/Finish/Cancel) dispatches to whichever flow is
  active (`abFlow`/`bndDraw`/`satBnd`); index-based list edits (tracks/flags) over the Scene
  projection; new wire fields appended at the END of a frame (decode in lockstep).
- **Session 2026-06-18 additions (all 8 left-nav buttons present + map-render parity):**
  - **Map-render parity pass** (`v26.5.74–80`) — the web now matches native's field/vehicle
    rendering. **Ground texture** (`drawGroundTextureSk`): tiled `GroundTextureDark.png`
    repeating shader, 50 m tiles, bottom layer, gated on `Display.FieldTextureVisible` (NOTE:
    CanvasKit `makeShaderOptions` localMatrix is **texel→world**, inverse of native's
    `SkShader.CreateBitmap` → scale is `50/W`, not `W/50`). **Tractor sprite**
    (`TractorAoG.png`, `vehicleSk`): world-sized on the ground via native `BitmapTractorSize`
    anchors (track-width/wheelbase), replaces the triangle (still the fallback). **Hitch**
    (`drawHitchSk`): computed client-side from pose+config — mounted tools draw two converging
    arms (`trackWidth×0.3` spread), trailing/TBT a tongue line; hitch pivot rides new Tick
    `HitchE/HitchN`. **Section footprint**: filled 2 m rects + black outline + 0.05 m gap
    (was thin lines). **Line colours/weights**: snapped to native paints + `updateLineWidths()`
    sets px = worldMetres × pxPerM each frame (native widths × strokeMult 3) so weights scale
    with zoom. **Inner/outer boundary**: Scene carries parallel `BoundaryInner` flag → outer
    orange-red, inner yellow. Known small gaps left out: front-wheel steer sprite, antenna dot,
    Svenn arrow, reference-dash world-scaling.
  - **Field Tools** (`v26.5.66–73`) — `#fieldtools` fly-out + the non-map-tap surface:
    **Offset Fix** (`#offsetfix` D-pad + manual N/E, `offset.*`), **Delete Applied Area**
    (browser-confirm → extracted `DeleteAppliedAreaConfirmed()`; FIX: it no longer resets
    guidance/nudge/U-turn, native too), **Import Tracks** (`#importtracks`, FieldTools frame),
    **Recorded Path** (`#recpath`, RecordedPath frame, host provider; record/playback, live
    dots, native icons, NO_SCRIM), **Boundary** (`#boundarymenu` + `#boundaryplayer`, Boundary
    frame, host provider; drive-around recording, native icons, live line+dots, NO_SCRIM).
    Deferred: Field Builder + Boundary/AB Draw-on-Map (Phase MT), Import-KML picker.
  - **Renderer unified on perspM** (`v26.5.66–67`) — `active3D()` keys on CanvasKit presence,
    not pitch; top-down = pitch 0 on the one M44. Removed the dead 2D ortho path (fixed
    imagery/coverage skewing under HeadingUp at pitch 0). See the NOTE at the bottom.
  - **Tools** (`v26.5.65`) — fly-out (`#tools`) at native left-nav position 3. **Steer Wizard** =
    shortcut to the existing host-driven wizard (`openSteerWizard`). **Log Viewer** = shortcut to
    the App Log Viewer panel with **parent-aware Back** (`logViewerParent` → Tools or File menu).
    **Roll Correction** (`#rollcorr` chain panel) = the wizard's roll-cal piece standalone: live
    roll gauge (off Tick `roll`) + Invert toggle (`config.set|roll.isRollInvert`) + **Zero Roll**
    (new host cmd `roll.zeroCalibrate` = `Ahrs.RollZero += live roll`, mirrors
    `RollCalibrationStepViewModel.ZeroRollCommand`, Tier-1) + offset readout. **3 diagnostic charts**
    (Steer/Heading/XTE) = floating draggable canvas cards (`.chart-card`), faithful port of native
    `ChartControl` (same scales/colours/autoscale/legend/20 s window); scalars stream on the **Tick**
    (4 new floats `ChartSetSteer/ChartActualSteer/ChartPwm/ChartImuHeading`; XTE + GPS heading already
    on Tick), browser keeps the rolling display buffer (thin-client precedent = SteerAngleError).
- **Session 2026-06-17 additions (left nav was 6/8 buttons):**
  - **Network IO + NTRIP** (`0ee454d7`, v26.5.59) — module checkboxes/status/IP, Scan (PGN 202),
    subnet change (PGN 201, gated), Host IPs, NTRIP status + Profiles/Editor dialogs. Wire frame
    **NtripProfiles=11**; shared `NtripConnectionTester`.
  - **Unified non-modal nav model** (`86ac6e6d`, v26.5.60) — dimming modals eliminated;
    transparent light-dismiss scrim; watch-tractor panels opt out; see `[[project_unified_nav_model]]`.
  - **Field Operations** (`d8299d26`/`fbc9556b`/`b71796e2`, v26.5.61–63) — Fields-and-Jobs lifecycle +
    New Field/From Existing/ISO-XML/KML + cross-field Resume Job + AgShare Upload/Download/Settings.
    Wire frames **FieldOps=12, AgShare=13**; host `AgShareRemote.cs` + `EnsureRemoteStartWorkSession`.
  - **File / Application Menu** (`79697583`, v26.5.64) — App Settings (units/kbd/fs/elev moved out of
    Screen & Alerts + App Directories), Language, Reset All, View All Settings (near-fullscreen),
    Log Viewer, Hotkeys, Help, About, Bug Report. Wire frame **AppInfo=14**; sim show/hide persists
    via `PersistentAppState.SimulatorPanelVisible` (Status `simPanelVisible` + `sim.togglePanel`).
  - **NEXT: 2 buttons remain — Tools, Field Tools** (see the inventory table below).
- **Phases 1–8** (status bar, control-authority safety layer, right-nav toolbar, lower-right
  cluster + Skia-only renderer, GPS-detail card, simulator panel + dialog host, section bar,
  bottom nav/field tools) — see git history. CanvasKit is the SOLE renderer (`skFrame` loop).
- **Phase 9 (left nav: config/setup) — large, multi-commit, MOSTLY done:**
  - **9a** left-nav shell + config bridge (`config.set`, units).
  - **Vehicle & Tool config** — picker **hub** + full Vehicle dialog + full Tool dialog
    (all tabs, diagrams, per-section/zone/colour/24-pin editors). Built to FULL native depth.
  - **Screen & Alerts** — full panel; **+ button icons + "Next Boundary Dist" rename**
    (renamed from "Headland Dist" — the value is distance to whichever boundary is approached,
    outer when no headland; internal `HeadlandDistanceVisible` flag unchanged).
  - **Next-boundary distance HUD** — `#headland-hud` top-centre overlay; `HeadlandProximity-
    Distance`/`Warning` on the Tick from `FieldState`.
  - **AutoSteer config panel** (`5789a1e6`) — full native `AutoSteerConfigPanel`: title bar,
    left pane (4 horizontal icon tabs Pure-Pursuit/Sensor/Deadzone/Gain) + Set/Act/Err status
    bar with the **expand toggle**, expanded reveals **Test Mode** (free-drive) + a right pane
    (5 icon tabs) + Smart-WAS / Defaults / Send+Save / OK actions. `AutoSteerConfigDto` on the
    Config frame; steer telemetry on the Status frame; hardware-push actions route through the
    real `AutoSteerConfigViewModel` (gated `autosteer.*`).
  - **Smart-WAS dialog** (`1431a131`) — modal; live stats from `ISmartWasCalibrationService.
    GetSnapshot()` on the Status frame; Start/Stop/Reset/Apply through `SmartWasViewModel`.
  - **Steer Wizard** (`d10af123`) — **host-driven**: the real `SteerWizardViewModel` runs on
    the host; a **Wizard frame (wire type 10)** streams nav state + status bar + a calibration
    live-blob each tick while open; the browser forwards nav (`wizard.next/back/skip/finish/
    cancel`), hardware level (`wizard.hw`), and **gated** calibration actions
    (`wizard.action|<Cmd>`); editable values reuse `config.set`. Host glue in `App.axaml.cs`
    drives the VM by **generic reflection** (`SetWizardProp`/`InvokeWizardAction`/
    `BuildWizardDto`); `MapBroadcaster.WizardProvider`. All 15 steps render incl. a map-style
    roll gauge on Roll-Cal.
  - **Light Bar / Steer Bar / master rework** (`906ef7b6`) — studied AgOpen + Twol: one top
    bar, master (`GuidanceBarOn`, was dead) + mutually-exclusive mode (Light=cross-track,
    Steer=steer-angle **error** = actual WAS − commanded, dead-zone ±0.5/0.2°, ±12° scale).
    Fixed native `LightBarPanel`/`SkiaMapControl`/callers + web (SteerAngleError on Tick).

## ⚠ Test rig — VehicleSimulator (you need this to test the AutoSteer/calibration surface)
`Simulators/AgOpenWeb.VehicleSimulator/` is a virtual AiO that speaks the **real UDP/PGN
protocol** (ports line up: sim→app 9999, app→sim 8888, loopback). It streams **PGN 253**
(live WAS angle/PWM, responds to PGN 254) + GPS motion. This is the ONLY way to exercise
Smart-WAS collection, the AutoSteer Test-Mode live angle / free-drive, the steer bar, and the
wizard calibration steps — the app's *internal* simulator does NOT emit steer data, so those
read 0 without it.
- Run: app with its **internal sim OFF**, then `dotnet run --project
  Simulators/AgOpenWeb.VehicleSimulator/AgOpenWeb.VehicleSimulator.csproj`, set
  **Speed > 2 km/h**, engage autosteer. (GPS source is exclusive — don't run both sims.)
- The sim got QoL this session: STOP/center(`>0<`)/zero + ±0.5 nudge buttons on Speed/Wheel/
  Roll; persisted GPS pose (`vehsim.json`) with **8-digit** coords + a **Save Position** button.

## Remaining Phase-9 sub-phases — FULL left-nav inventory (8 native buttons)
The native left nav (`LeftNavigationPanel.axaml`) has **8 buttons**. Web status:
| # | Native button | Web status |
|---|---|---|
| 1 | File / Application Menu | ✅ built (`#filemenu` + App Settings/Language/View All/Log Viewer/Hotkeys/Help/About/Bug Report) |
| 2 | Screen & Alerts | ✅ built (`#screenalerts`) |
| 3 | Tools | ✅ built (`#tools` + Steer Wizard/Log Viewer shortcuts + Roll Correction `#rollcorr` + Steer/Heading/XTE chart cards) |
| 4 | Vehicle / Tool Configuration | ✅ built (`#vehtoolhub`/`#vehiclecfg`/`#toolcfg`) |
| 5 | Field Operations | ✅ built (`#fieldops` + Fields-and-Jobs + creation + Resume Job + AgShare) |
| 6 | Field Tools | ✅ built (non-MT): `#fieldtools` + Offset Fix + Delete Applied + Import Tracks + Recorded Path + Boundary menu/player. ⏳ remaining = **Field Builder + Boundary Draw-on-Map (Phase MT)** + Import-KML picker (follow-up) |
| 7 | AutoSteer Configuration | ✅ built (`#autosteercfg` + Smart-WAS + Wizard) |
| 8 | Network IO | ✅ built (`#networkio` + NTRIP) |

**All 8 left-nav buttons are now present.** Field Tools' non-map-tap surface is done
(v26.5.66–73); what's left inside it is Phase-MT map-tap work + the Import-KML picker.
(File/App Menu done v26.5.64 — fly-out + App
Settings [units/kbd/fullscreen/elev migrated OUT of Screen & Alerts + App Directories] + Language
+ Reset All + View All Settings [read-only tree from the config frame] + Log Viewer [AppInfo logs,
level filter] + Hotkeys [list + click-to-capture] + Help [external links] + About + Bug Report.
Wire frame **AppInfo=14**; host write `app.*` + `AgShareRemote`-style bug-report dump.)
- **Field Operations** ✅ DONE (v26.5.61–63). Fly-out (Fields and Jobs / Resume Last / Resume Job /
  Drive In / Close + AgShare Upload/Download/API + status pill); Fields-and-Jobs chain panel; New
  Field / From Existing / From ISO-XML / From KML creation panels; cross-field Resume Job picker;
  AgShare Settings/Upload/Download. Wire frames **FieldOps=12**, **AgShare=13**. Host: lifecycle via
  host-driven `EnsureRemoteStartWorkSession`; creation via `RemoteCreateFrom*`; AgShare orchestration
  replicated host-side in Desktop `AgShareRemote.cs` (services aren't DI-registered) → writes
  `ApplicationState.AgShare`. Remaining pick:
- ~~**Network IO + NTRIP**~~ ✅ **DONE (v26.5.59, pending device test).** Full `NetworkIoPanel`
  parity: module present-checkbox + status dot + IP (GPS/AutoSteer/Machine/IMU), Scan for
  Modules (`net.scan`, PGN 202), global subnet change (`net.subnet`, PGN 201 — **Tier-2 gated**,
  client confirm), Host IPs readout, NTRIP status/bytes + a full **NTRIP Profiles** modal
  (add/edit/delete/set-default) + **Edit Profile** modal (host/port/mount/user/pass, Test
  Connection, field-association checkboxes, auto-connect, default). Read side: new Status-frame
  fields (GpsIp/ModuleSubnet/HostIps/Ntrip*) + new **NtripProfiles frame (wire type 11)**. Write
  side: `net.*` → `IUdpCommunicationService`; `ntrip.*` (save/delete/setDefault/test) →
  `INtripProfileService` directly (mirrors `ApplyProfileCommand`); Test Connection reuses the new
  shared `NtripConnectionTester` (also now used by the native VM — no duplicated TCP probe),
  result projected via `ConnectionState.NtripTestStatus`. SceneProjector gained
  `IUdpCommunicationService` + `INtripProfileService` (passed through `RemoteServerHost.StartAsync`
  → updated the Desktop call). Plan: `Plans/NETWORK_IO_PLAN.md` + memory `[[project_network_io_panel]]`.
- **File / Application Menu** (`Panels/FileMenuPanel.axaml`) — owns Application Settings:
  Language · Reset All Settings · **App Settings (modal)** · View All Settings · Log Viewer ·
  Hotkeys · Simulator · Help · About · Bug Report Dump. ⚠ A subset of App Settings (units /
  on-screen keyboard / start-fullscreen / elevation-log) currently lives **inside the web
  Screen & Alerts panel** with a "moves to its own dialog" note — building this means
  migrating those four OUT, per `[[project_screen_alerts_settings_ia]]` (non-modal Screen &
  Alerts vs modal App Settings).
- ~~**Tools**~~ ✅ **DONE (v26.5.65).** Steer Wizard + Log Viewer = shortcuts; Roll Correction =
  standalone `#rollcorr` chain panel (wizard roll-cal piece; `roll.zeroCalibrate` host cmd); Steer/
  Heading/XTE = floating draggable canvas chart cards (port of native `ChartControl`; 4 chart scalars
  added to the Tick frame, browser-side rolling buffer).
- **Field Operations** (`Panels/FieldOperationsPanel.axaml`) — field lifecycle: Fields and Jobs
  · Resume Last Job · Resume Job · Drive In · Close · AgShare Upload/Download/Settings. Needs a
  field-list read-frame + confirm dialogs (mostly command-driven).
- **Field Tools** (`Panels/FieldToolsPanel.axaml`) — Field Builder · Boundary · Delete Applied
  Area · Import Tracks · Recorded Path · Offset Fix. The launcher items are form/command panels,
  BUT **the boundary work and several others are map-tap features** (see the Map-Tap phase below) —
  Field Tools is NOT complete until those are built.

## Phase MT — Map-tap interaction (REQUIRED; the migration is NOT done until this ships)
The map-tap features need the operator to **tap a location on the map canvas** to act, unlike the
form/command panels. They ALL depend on one missing piece: **screen→world unprojection** — turn a
tap (x,y px) into a field coordinate (E,N). The renderer only does world→screen (`w2s`) today.
Build the inverse ONCE, reuse everywhere:
- Flat/top-down: invert pan/zoom/rotation. **3D tilt: invert the perspective M44 + ray-cast onto the
  ground plane** (the hard part — `perspM` is column-vector/row-major; see Renderer notes).
Features that need it (across Field Tools + bottom-nav + Tracks):
- **Boundary** — record/draw/edit field boundaries by tapping (the bulk of Field Tools).
- **Quick AB / Draw AB** — set an AB line by tapping A then B; draw curves by tapping points.
- **Place flag on map** — drop a flag at a tapped point (note: "place flag here" at the vehicle
  already works in the bottom-nav; only the arbitrary-point placement needs map-tap).
- **Flag list** — pick/locate/move flags on the map.
- **Tracks** — on-map point picking for track create/edit.
This is its own phase. Treat the web migration as INCOMPLETE until Phase MT is done.

**Unprojection — build this FIRST (the foundation everything else reuses).** The renderer is
now unified on ONE matrix: `perspM` (CanvasKit M44, world→CSS-px) at every pitch — there is
NO separate 2D ortho path anymore. So `s2w(px,py)` must invert `perspM`, NOT a 2D transform:
- A tap is a CSS-px point on the near→far ray. Build the ray in world space and intersect the
  **ground plane z=0**. `perspM` maps world (E,N,0,1)→clip; to invert, take the full 4×4
  (before the `.Matrix` 3×3 collapse `w2s` uses), invert it, and unproject two clip-space
  points (near z, far z) at the tap's NDC (x = 2·px/vw−1, y = 1−2·py/vh), then solve for the
  t where the ray crosses N̂·P=0 (ground). Return (E,N). `buildScreenMatrix()` is the place to
  also expose the 4×4 + its inverse (cache per frame in `updateCamera`).
- Verify by round-trip: `s2w(w2s(e,n)) ≈ (e,n)` for points across the view at pitch 0 AND 60°.
- Watch: taps on the **DOM overlays** (panels, toolbars, sim bar) must not fall through to the
  map; the map canvas pointer handler should ignore events the overlays consumed (the panels
  already `stopPropagation`).
Once `s2w` round-trips, the map-tap features are mostly UI: an on-canvas "tap to add point"
mode per feature, a client-side point buffer drawn live (reuse `strokePtsSk`/marker draws),
and a command that ships the captured points to the host (mirror native's
`BoundaryMapDialog`/`FieldBuilder` *save* paths — DON'T port the geometry math to JS).

Then **Phase 10** — mop-up + headless cutover (host goes UI-less; browser is the only UI).

## Navigation model — SINGLE UNIFIED MODEL (LOCKED 2026-06-17)
**There is ONE navigation model. Dimming modals are eliminated as a category.** Everything is
a chain panel; "only one panel on screen at a time." Decision rationale: the only thing a
dimming modal did that the chain didn't was *block* the map + persistent toolbars — and a
**transparent light-dismiss scrim** already gives that safety (the outside tap is *consumed* to
close the panel, so the background never receives it) **without** hiding the field. So the modal
adds nothing. Native already proves the mechanism — chain dialogs use *"a fully transparent
light-dismiss scrim (not a darkening backdrop)"* (`NtripProfilesDialogPanel.axaml`).

The model:
- **One panel visible at a time.** Opening a child REPLACES the parent (not stacked). Source of
  truth = `MainViewModel.Navigation.Chain.cs` (`OpenChainDialog`/`PushChainDialog`/`NavigateBack`/
  `CloseChain`). Web = `ln-panel` in `LN_NAV_PANELS`, opened via `lnOpen` (which `lnCloseAll`s first).
- **Header chrome:** fly-out / chain-root = **Title + ✕** (`FloatingPanel`); deeper chain panel =
  **← Back + Title + ✕** (`DialogChrome`). Back reopens the specific parent; ✕ closes the whole
  chain to the map. Web: plain closers tagged `.ln-closex` → `lnCloseAll`; chain Back/✕ get
  parent-aware handlers.
- **Transparent light-dismiss scrim** behind the open panel: map fully visible, outside-tap closes
  the chain and is **consumed** (does not actuate the background). This is the blocking-safety,
  minus the dimming.
- **Watch-the-tractor exception** (Smart-WAS, AutoSteer free-drive test, wizard cal steps): these
  need to pan/zoom the map to watch the tractor, so they **opt OUT of light-dismiss** — no scrim,
  map fully interactive, panel persists, closes only via the header. The only deliberate
  non-dismiss surface type.
- **Steer Wizard** stays full-screen — it's a guided multi-step flow with its own gauges, not a
  map view; "blocking" isn't the issue there.

**Migration to the locked model — DONE (v26.5.60, pending device test):**
1. ✅ **Transparent light-dismiss scrim** (`#ln-scrim`, z30): shown behind open chain panels by
   `lnOpen` (hidden for `NO_SCRIM` watch-tractor panels), tap closes the chain + is **consumed**
   (`stopPropagation` → no map pan). Removed the `lnCloseAll()` from the global window-pointerdown
   closer. Z-restack: scrim 30 / panels 31 / left-nav bar 32 (one-tap panel switching) / dialoghost 50.
2. ✅ **Shared confirm** (`showConfirm(title,msg,cb)` → `#dlg-confirm` card in the now-transparent
   dialog host): replaced all four browser `confirm()` calls (hub delete + reset, NTRIP delete,
   subnet change). Backdrop tap / Cancel = no action. (AutoSteer reset keeps its in-context
   `.as-confirm` inline bar — already non-modal; left as the nicer in-panel pattern.)
3. ✅ **Smart-WAS → watch-tractor chain panel** (`#smartwas`, `ln-panel`, in `NO_SCRIM`): map stays
   interactive, header-only close (← Back → AutoSteer, ✕ → map). **SimCoords** now uses the
   transparent dialog-host backdrop (light-dismiss, no dim).
NTRIP Profiles + Editor already were chain panels. Steer Wizard stays full-screen. **No dimming
modals remain** in the web client.
Open sub-detail (not blocking): a confirm currently shows a centered card with a transparent scrim
regardless of origin — fine since all current confirms originate inside a panel; revisit anchor if a
persistent-toolbar confirm is ever added.

**Native divergence:** native still uses dimming modals for Smart-WAS / confirmations. This unified
model is a **web-led improvement** (web is the end-state UI). Whether to backport to native is open.

## Established patterns (reuse these — they're the template now)
- **Config bridge (settings panels):** structured read-frame (a `*Dto` projection of
  `ConfigStore`) seeded on connect + re-sent on a **fingerprint** change (broadcaster);
  writes via `config.set|<section>.<field>:<value>` (indexed arrays `key:i,val`) handled in
  `ApplyConfigSet`. Generic client helpers: `wireCfgControls`/`populateCfgControls`/
  `wireTabStrip`/`fmtRo`; data attrs `data-key`/`data-show`/`data-active`/`data-fmt`;
  `.cfg-num`/`.cfg-tgl`/`.cfg-typebtn`/`.cfg-slider[data-fmt]`/`.cfg-isel`.
- **Host-driven dialogs/wizards:** route hardware/stateful actions through the REAL child VM
  (`AutoSteerConfigViewModel`/`SmartWasViewModel`/`SteerWizardViewModel`) via an
  `EnsureXxxViewModel()` on `MainViewModel` — never reimplement PGN/calibration logic in JS.
  The wizard adds a Wizard frame + reflection-driven `wizard.set`/`wizard.action` +
  `MapBroadcaster.WizardProvider`.
- **Icons:** the `/icons/{file}` endpoint serves **embedded** `RemoteServer/wwwroot/icons/*`.
  Native icons live in `Shared/AgOpenWeb.Views/Assets/Icons[/Config]/` — **copy the PNGs
  in** before referencing them, else broken-link. Rebuild RemoteServer to embed.
- **Frame types:** Scene=1 Tick=2 CoverageInit=3 CoverageCells=4 Status=5 ControlState=6
  Hello=7 Config=8 Profiles=9 Wizard=10 NtripProfiles=11 FieldOps=12 AgShare=13 AppInfo=14
  **FieldTools=15 (Import Tracks, projector-built) RecordedPath=16 (host-driven provider)
  Boundary=17 (host-driven provider)**. Tick grew `HitchE/HitchN` (hitch pivot) + the 4 chart
  scalars; Scene grew a parallel `BoundaryInner` bool list (outer ring first, then inner holes).
  The wire is decoded **positionally** — append new fields at the END of a frame's encode +
  decode (never insert mid-frame), and keep `WireCodec`↔`transport.js` field order in lockstep.
- **Tier-2 gating** (`IsRestrictedCommand`, control-gated): prefixes `section.` `autosteer.`
  `youturn.` `contour.` `track.` `headland.` `smartwas.` `wizard.action`, plus the exact id
  `net.subnet` (restarts every module). Tier-1 (ungated): `sim.` `tool.` `map.` `flag.` `tram.`
  `display.` `config.set` (incl. `conn.*` module-present) `profile.*` `net.scan` `ntrip.*`
  `wizard.{open,next,back,skip,finish,cancel,hw,set}`. Client dims gated controls (`.rn-gated`/`.disabled`)
  off `iHoldControl`; the host re-gates.
- **Command-with-args:** host `CommandHandler` is `Action<string,string>` (id, arg); client
  sends `transport.send('id|arg')`. Arg-carrying ids handled in the `switch` above the ICommand
  map in `App.axaml.cs`.

## Projection / SoT pattern (the source of most bugs)
Runtime UI state often lives in the **VM as plain fields**, not `ApplicationState`. Recipe:
find where the VM holds it → if a live SoT exists in `ApplicationState` (`*State`, runtime) or
`ConfigStore` (`*Config`, persisted), **project that and make the VM a pass-through**; only
mirror into `ApplicationState` if there's no SoT. Watch for **same-named-but-different** fields
and **dead VM duplicates** (grep the CONSUMER before mirroring). See memory
`[[project_state_model_sot]]`. Config = `ConfigStore`; don't mirror dead VM config fields.
⚠ Some `Display.*` flags were dead on NATIVE too (config→renderer apply gap) — that was the
separate §13 audit work (now merged from develop), **not** the web migration.

## Workflow rules
- **Embedded assets:** `wwwroot/*` (incl. `icons/*`) are `EmbeddedResource` in RemoteServer.
  `dotnet build Platforms/AgOpenWeb.Desktop/...` re-embeds changed files without nuking bin.
  After any `wwwroot` change the user must **restart** the app to load the new DLL (JS-only
  edits still need the rebuild+restart to re-embed; a pure browser reload only helps if the
  file is served from disk, which it is NOT — it's embedded). Verify embed with
  `strings .../AgOpenWeb.RemoteServer.dll | grep <marker>` if unsure.
- **Can't meaningfully run the GUI** — user runs `dotnet run --project Platforms/
  AgOpenWeb.Desktop/...` and reports. **Build to verify compile; wait for the user to
  confirm before committing. One commit + push per verified fix.** Bump `sys/version.h` each.
- **Match native by screenshot, not just AXAML** — this session the AutoSteer panel took 3
  passes because AXAML alone hid the tab orientation + the collapsed/expanded modes. Ask for
  screenshots of any non-trivial native panel before building it.
- `node --check wwwroot/app.js` catches syntax but NOT TDZ — watch `const` ordering.
- DI/ctor changes touch all 3 platforms; the RemoteServer is **Desktop-only** so far
  (`RemoteServerHost.StartAsync` is called only from Desktop `App.axaml.cs`).

## Renderer notes (CanvasKit, app.js)
- Skia-only. `skFrame()` single loop: `updateCamera()` → `renderSkia()` → DOM overlays
  (`renderStatusBar`/`renderRightNav`/`renderRoll`/`renderCampad`/`updateLightbarText`/
  `updateHeadlandHud`/`renderSettings`/`renderCharts`/`renderRollCorr`/`renderOffsetFix`) →
  `updateHud()`.
- **ONE projection: `perspM` (M44, column-vector/row-major) at every pitch** — top-down is
  just pitch 0 (built for scale-continuity in `buildScreenMatrix`). `active3D()` returns
  `!!CK`. There is NO 2D ortho branch left; `w2s` = `applyM(perspM,…)` and every helper
  assumes `perspM` is set (null only before CanvasKit loads, when nothing draws).
- `renderSkia` layer order: clear → `drawGroundTextureSk` (tiled shader) → `drawImagerySk` →
  `drawCoverageSk` → `drawGridSk` → scene vectors (boundaries[outer/inner]/headland/tracks/
  next/uturn/guidance via `strokePtsSk`) → flags → `drawRecordingMarkersSk` →
  `drawBoundaryRecordingSk` → `drawHitchSk` → `toolFootprintSk` (filled section rects+outline)
  → `vehicleSk` (tractor sprite) → `lightbarSk`. `updateLineWidths()` (first in `renderSkia`)
  sets vector px = worldMetres × pxPerM each frame so weights scale with zoom like native.
- Rasters/ground/sprite draw in WORLD coords under `canvas.concat(perspM)` (Skia GPU does
  perspective + near-plane clip); polylines stroke per-vertex via `strokePtsSk`→`strokePtsSk3D`
  which near-plane-clips in world space. Non-color-managed surface (sRGB blend like native).
  Camera modes 0=N 1=H 2=Free 3=Map. `isTyping()` guards global hotkeys (tilt/sim keys) so
  typing in inputs doesn't trigger them.

**Remaining to finish the migration (all REQUIRED — there is no "later"):**
1. **Phase MT — map-tap interaction.** Build `s2w` (screen→world: invert `perspM` + ray-cast
   the ground plane — see the Phase MT section above; the renderer is unified on perspM so do
   NOT write a 2D-ortho inverse) FIRST, round-trip-verify it, then: Boundary Draw-on-Map +
   Draw-on-Satellite (inner), **Field Builder** (on-map track/headland/tram editor — the big
   one; native `FieldBuilderDialogPanel`), Quick AB / Draw AB / A+, place-flag-at-point, flag
   list, on-map track create/edit. Get native screenshots before each panel.
2. **Import-KML boundary picker** — list sub-dialog (project `AvailableKmlFiles` + the import
   flow); not map-tap, can land independently. (Field Tools `bm-importkml` is the disabled stub.)
3. **Phase 10** — headless cutover (host goes UI-less; browser is the only UI).

**Working rules recap:** build to verify compile → user runs `dotnet run --project Platforms/
AgOpenWeb.Desktop/...` and reports → **wait for confirm, then one commit+push per verified
fix, bump `sys/version.h`**. `wwwroot` is embedded → restart after JS/HTML edits. Tier-2 gate
the actuation-ish ids; data/geometry capture is Tier-1. Use the **VehicleSimulator** rig for
steer/GPS/calibration. Host-driven provider pattern (Wizard/RecordedPath/Boundary) for
VM-coupled read state; projector pattern for ApplicationState/disk-derived state.**
