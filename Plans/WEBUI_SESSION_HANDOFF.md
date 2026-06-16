# Web-UI Migration — Session Handoff (continuation prompt)

Paste the section below to continue the AgValoniaGPS web-UI migration in a fresh session.

---

**Continue the AgValoniaGPS web-UI migration.**

## Repo / branch
- Repo: `/Users/chris/Code/AgValoniaGPS3` (Avalonia/.NET 10 agricultural GPS app).
- Branch: `feature/web-ui-phase2` (off **develop** — PRs target develop, NOT master).
  Stays unmerged until field-validated; commit + push to it as we go.
- Working tree is clean; Phases 1–4 are committed + pushed.

## What this is
Replacing the native in-cab Avalonia UI with a browser client served by an embedded
ASP.NET Core server (`Shared/AgValoniaGPS.RemoteServer`) that runs alongside the app.
Browse to `http://localhost:5174`. The **host stays the brain** (Avalonia-free
`ApplicationState` + services + `MainViewModel`); the browser is a thin client that
receives projected *state* over a binary WebSocket and sends *command ids* back through
a safe allowlist. Migration = project more state + accept more command ids + build
HTML/JS. Do NOT port logic into JS. End state: headless host, browser is the only UI.

- **Full plan:** `Plans/WEBUI_MIGRATION_PLAN.md` (read it — phase list, as-shipped
  safety/control model, cutover).
- **Seam files:** `RemoteServer/{Contracts.cs, WireCodec.cs, SceneProjector.cs,
  CoverageProjector.cs, MapBroadcaster.cs, WebSocketHub.cs, ControlAuthority.cs,
  RemoteServerHost.cs}`; client `wwwroot/{app.js, transport.js, index.html}`; command
  map + safety wiring in `Platforms/AgValoniaGPS.Desktop/App.axaml.cs`.

## Done so far (committed)
- **Phase 1 — top status bar** (28f215f6): `StatusDto` frame; pause + two-line stack
  (fix/age over rotating Field/Stats/AB), aggregate Modules popup, speed.
- **Phase 2 — remote actuation safety layer** (72c2d057, refined in 49c685cf):
  `ControlAuthority`. **As-shipped model: ONE operator, headless target.** Control is
  implicit by connection order — first browser to connect controls, others observe;
  NO take/release UI, NO per-action confirm, NO native banner. Tier-2 ids
  (`section.`/`autosteer.`/`youturn.`/`contour.`) gated by the hub; deadman heartbeat
  (~1.5 s) + on controller-loss the host disengages autosteer + sections.
- **Phase 3 — right-nav operational toolbar** (3a 000b6105, 3b 49c685cf): contour,
  section master/manual, U-turn auto/dir/manual, autosteer — live-mirroring + actuation
  through the gate, native PNG icons served from `wwwroot/icons` via `/icons/{file}`.
- **Phase 4 — lower-right cluster + camera + Skia-only** (8e1ae2cf): roll gauge,
  camera/mode pad, clock; full 4-mode camera (H/N/M/C) with real map rotation; and
  **CanvasKit is now the SOLE renderer** — the Canvas2D path + `K` toggle are deleted,
  one `skFrame` loop drives the GL map + DOM overlays. Grid matched to native.

## The projection pattern (recurring; the source of most bugs so far)
Runtime UI state usually lives in the **VM as plain fields**, NOT in `ApplicationState`.
Recipe for each element: find where the VM holds it → mirror it into `ApplicationState`
in the VM setter → project it. Watch for **same-named-but-different** fields:
- toolbar `IsAutoSteerEngaged` is a **VM** field ≠ `ConnectionState.IsAutoSteerEngaged`
  (hardware module).
- toolbar `IsContourModeOn` (VM) ≠ `GuidanceState.IsContourMode` (pipeline "driving a
  contour").
- roll: `RollDegrees` (VM) → mirrored to `VehicleState.Roll` (`ImuRoll` is dead).
- section master/manual + youturn-enabled → mirrored to `ApplicationState.Operation`.
Always grep the AXAML binding + the VM setter to find the RIGHT source; the early
exploration agents mislabeled several things (right vs left nav, the lower-right cluster).
**Read MainWindow.axaml for placement and each panel's own AXAML for contents first.**

## NEXT SESSION — start here
1. **Boundary/track near-plane clipping in 3D (deferred bug fix — do first).**
   In `wwwroot/app.js`, grid lines are now near-plane-clipped via `clipNear()` so they
   don't ghost/vanish behind the tilted camera. The field **boundaries/tracks/headland/
   guidance/uturn/next** still project per-vertex through `w2s` (`strokePtsSk`) with no
   near-clip — so at extreme tilt a vertex running behind the camera produces a mirrored
   ghost segment (same class of bug we just fixed for the grid). Fix: near-plane-clip the
   polylines before projecting — i.e., walk each polyline and split/emit sub-segments
   where it crosses the near plane (reuse the `clipNear()` math; `perspM` bottom row
   gives w, EPS = 1.0). Grid lines were simple 2-point segments; polylines need the
   walk. Apply in `strokePtsSk` (or a 3D variant) when `perspM` is active.
2. **Phase 5 — status-bar completion:** the **heading readout** (top strip, right of
   speed; heading already on the Tick) and the **GPS-detail card** (popup toggled by the
   strip's fix dot: lat/lon, altitude, sats, age — extend Status/Tick). These were
   deferred from Phase 1; native: `Panels/HeadingReadout.axaml`, `Panels/GpsDetailPanel.axaml`.
3. Then continue the clockwise sweep: **Phase 6** simulator (+ first real dialog, stands
   up the dialog host), **7** section bar, **8** bottom nav (field tools), **9** left nav
   (config trees + NTRIP + field/file lifecycle — the big one), **10** mop-up + headless
   cutover. See the plan doc.

## Workflow rules (important)
- **Embedded assets:** `wwwroot/*` (and `wwwroot/icons/*`) are `EmbeddedResource` in
  RemoteServer. A plain `dotnet build Platforms/AgValoniaGPS.Desktop/...` re-embeds
  changed files WITHOUT nuking bin — use that so you don't kill the user's running app.
  Only `rm -rf bin/obj` if assets genuinely go stale, and tell the user to stop the app
  first. After any `wwwroot` change the user must **restart** the app to load the new DLL.
  Verify embed with `strings .../AgValoniaGPS.RemoteServer.dll | grep <marker>`.
- **Cannot run the GUI app meaningfully** — the user runs `dotnet run --project
  Platforms/AgValoniaGPS.Desktop/AgValoniaGPS.Desktop.csproj` and reports. Wait for their
  confirmation before committing. One commit + push per verified fix, not per iteration.
- **`node --check wwwroot/app.js`** catches syntax but NOT temporal-dead-zone errors —
  watch const ordering (a TDZ throw at load freezes the client at "connecting…").
- Tests must stay green for Models/Services/ViewModels changes: `dotnet test
  Tests/AgValoniaGPS.UI.Tests/` and `Tests/AgValoniaGPS.Models.Tests/` (RemoteServer/JS
  changes don't affect tests). DI changes touch all 3 platforms, but the web server is
  Desktop-only so far.
- No `sys/version.h` bump on this branch (web-UI commits haven't been bumping).

## Renderer notes (CanvasKit, app.js)
- Skia-only. `skFrame()` is the single loop: `updateCamera()` → `renderSkia()` (if
  `skSurface`) → DOM overlays (`renderStatusBar`/`renderRightNav`/`renderRoll`/
  `renderCampad`/`updateLightbarText`) → `updateHud()`.
- `w2s(e,n)`: perspective via `perspM` when tilted, else 2D-ortho-with-rotation.
  `perspM` (world→CSS px) is column-vector/row-major (CanvasKit M44). Vectors draw
  per-vertex via `w2s`; rasters (imagery/coverage) and the 3D grid draw in WORLD coords
  under `canvas.concat(perspM)` so Skia GPU-clips at the near plane.
- Non-color-managed surface (`MakeOnScreenGLSurface(..., null)`) so transparent fills
  blend like native (sRGB), not washed-out linear.
- Camera modes 0=N 1=H 2=Free 3=Map (default); `mapRotation` eased by `ROT_SMOOTH`.

**Start by fixing the boundary/track near-plane clipping (item 1), then Phase 5.**
