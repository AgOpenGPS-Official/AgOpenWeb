# Web-UI Migration — Session Handoff (continuation prompt)

Paste the section below to continue the AgValoniaGPS web-UI migration in a fresh session.

---

**Continue the AgValoniaGPS web-UI migration.**

## Repo / branch
- Repo: `/Users/chris/Code/AgValoniaGPS3` (Avalonia/.NET 10 agricultural GPS app).
- Branch: `feature/web-ui-phase2` (off **develop** — PRs target develop, NOT master).
  Stays unmerged until field-validated; commit + push to it as we go. **develop has been
  merged in** (the §13/§14 config/state apply-gap fixes — `SectionState` class was deleted
  upstream; `SceneProjector` reads `ISectionControlService` + `ToolConfig.MaxSections`).
- Working tree clean. **Current version `26.5.58`** (we DO bump `sys/version.h` per commit now).

## What this is
Replacing the native in-cab Avalonia UI with a browser client served by an embedded
ASP.NET Core server (`Shared/AgValoniaGPS.RemoteServer`) that runs alongside the app.
Browse to `http://<host>:5174`. The **host stays the brain** (Avalonia-free
`ApplicationState` + services + `MainViewModel`); the browser is a thin client that
receives projected *state* over a binary WebSocket and sends *command ids* back through a
safe allowlist. Migration = project more state + accept more command ids + build HTML/JS.
**Do NOT port logic into JS.** End state: headless host, browser is the only UI.

- **Full plan:** `Plans/WEBUI_MIGRATION_PLAN.md` (phase list, safety/control model, cutover).
- **Seam files:** `RemoteServer/{Contracts.cs, WireCodec.cs, SceneProjector.cs,
  CoverageProjector.cs, MapBroadcaster.cs, WebSocketHub.cs, ControlAuthority.cs,
  RemoteServerHost.cs}`; client `wwwroot/{app.js, transport.js, index.html}`; command map +
  safety wiring + host-side projectors/handlers in `Platforms/AgValoniaGPS.Desktop/App.axaml.cs`.

## Done & pushed
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
`Simulators/AgValoniaGPS.VehicleSimulator/` is a virtual AiO that speaks the **real UDP/PGN
protocol** (ports line up: sim→app 9999, app→sim 8888, loopback). It streams **PGN 253**
(live WAS angle/PWM, responds to PGN 254) + GPS motion. This is the ONLY way to exercise
Smart-WAS collection, the AutoSteer Test-Mode live angle / free-drive, the steer bar, and the
wizard calibration steps — the app's *internal* simulator does NOT emit steer data, so those
read 0 without it.
- Run: app with its **internal sim OFF**, then `dotnet run --project
  Simulators/AgValoniaGPS.VehicleSimulator/AgValoniaGPS.VehicleSimulator.csproj`, set
  **Speed > 2 km/h**, engage autosteer. (GPS source is exclusive — don't run both sims.)
- The sim got QoL this session: STOP/center(`>0<`)/zero + ±0.5 nudge buttons on Speed/Wheel/
  Roll; persisted GPS pose (`vehsim.json`) with **8-digit** coords + a **Save Position** button.

## Remaining Phase-9 sub-phases (NEXT — pick one)
- **Network IO + NTRIP** — NTRIP profile mgmt + module-IP readout + subnet-change fly-out
  (PGN 202 scan / 203 reply / 201 global subnet — no per-module IP-set in AgOpen). Plan:
  `Plans/NETWORK_IO_PLAN.md` + memory `[[project_network_io_panel]]`.
- **Field operations / lifecycle** — field list / open / create / close / resume; needs a
  field-list read-frame + confirm dialogs (mostly command-driven).
- **Deferred map-tap dialogs** (Phase-8 deferred) — Tracks / QuickAB / DrawAB / FlagList /
  place-on-map. Needs **map-tap interaction** on the canvas — build that once, reuse. Hardest.
Then **Phase 10** — mop-up + headless cutover (host goes UI-less; browser is the only UI).

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
  Native icons live in `Shared/AgValoniaGPS.Views/Assets/Icons[/Config]/` — **copy the PNGs
  in** before referencing them, else broken-link. Rebuild RemoteServer to embed.
- **Frame types:** Scene=1 Tick=2 CoverageInit=3 CoverageCells=4 Status=5 ControlState=6
  Hello=7 Config=8 Profiles=9 **Wizard=10**.
- **Tier-2 gating** (`IsRestrictedCommand`, control-gated): prefixes `section.` `autosteer.`
  `youturn.` `contour.` `track.` `headland.` `smartwas.` `wizard.action`. Tier-1 (ungated):
  `sim.` `tool.` `map.` `flag.` `tram.` `display.` `config.set` `profile.*` `wizard.{open,
  next,back,skip,finish,cancel,hw,set}`. Client dims gated controls (`.rn-gated`/`.disabled`)
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
  `dotnet build Platforms/AgValoniaGPS.Desktop/...` re-embeds changed files without nuking bin.
  After any `wwwroot` change the user must **restart** the app to load the new DLL (JS-only
  edits still need the rebuild+restart to re-embed; a pure browser reload only helps if the
  file is served from disk, which it is NOT — it's embedded). Verify embed with
  `strings .../AgValoniaGPS.RemoteServer.dll | grep <marker>` if unsure.
- **Can't meaningfully run the GUI** — user runs `dotnet run --project Platforms/
  AgValoniaGPS.Desktop/...` and reports. **Build to verify compile; wait for the user to
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
  `updateHeadlandHud`/`renderSettings`) → `updateHud()`.
- `w2s(e,n)`: perspective via `perspM` (M44, column-vector/row-major) when tilted, else 2D
  ortho+rotation. Vectors draw per-vertex via `w2s`; rasters + 3D grid draw in WORLD coords
  under `canvas.concat(perspM)`. Non-color-managed surface (sRGB blend like native).
  Camera modes 0=N 1=H 2=Free 3=Map; `strokePtsSk3D` near-plane-clips polylines.

**Pick the next Phase-9 sub-phase (Network IO + NTRIP, Field ops/lifecycle, or the deferred
map-tap dialogs) and build it sub-phased per the patterns above. Use the VehicleSimulator rig
to test anything touching steer/GPS/calibration. Ask for native screenshots before building a
new panel.**
