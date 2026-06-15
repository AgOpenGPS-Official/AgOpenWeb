# Web-UI Session Handoff — CanvasKit renderer migration (Phase A)

Paste-ready prompt to continue the AgValoniaGPS web-UI work in a fresh session.

---

**Continue the AgValoniaGPS web-UI work (CanvasKit renderer migration, Phase A).**

## Repo / branch
- Repo: `/Users/chris/Code/AgValoniaGPS3` (Avalonia/.NET 10 agricultural GPS app, 13 commits into `feature/web-ui-phase2`, off `develop`).
- PRs target **develop**, not master. The web-UI branch stays unmerged until field-validated; commit+push to it as we go.
- **Uncommitted right now:** `Shared/AgValoniaGPS.RemoteServer/wwwroot/app.js` + `index.html` — the in-progress CanvasKit Phase A work (see below). Everything else is committed/pushed.

## What the web UI is
An embedded ASP.NET Core server (`Shared/AgValoniaGPS.RemoteServer`) runs **alongside** the Avalonia app, streaming the live map to a browser client over a **binary WebSocket** (no SignalR/CDN — fully offline). Browse to `http://localhost:5174`. Architecture per `Plans/REMOTE_WEB_UI_SPLIT.md`.
- **Server→client:** `SceneProjector`/`CoverageProjector` read `ApplicationState` + Avalonia-free services and project to a wire contract (`Contracts.cs`); `WireCodec` encodes little-endian binary frames; `WebSocketHub` fans them out; `MapBroadcaster` runs a 10 Hz loop + coverage events.
- **client→host:** `WebSocketHub.CommandHandler` → `App.axaml.cs` maps command ids to VM commands on the UI thread via a **safe allowlist** (sim drive controls only; remote autosteer-engage excluded pending safety sign-off).
- **Client** (`wwwroot/app.js` + `transport.js`): `transport.js` is the only file that knows the wire (the seam). `app.js` is the renderer + camera (client-owned pan/zoom/follow) + dead-reckoning.
- **Working in Canvas2D today:** imagery (BackPic.png over HTTP `/backpic.png` + extent in Scene), grid + red/green origin axes, coverage, boundaries/headland/tracks, guidance HUD (lightbar), tool/section footprint (6-state native colors), U-turn arc (green) + next pass (cyan) + current line (magenta), dead-reckoned vehicle+tool, sim drive buttons/keys. All track native with no perceptible lag.

## Recurring lesson (the projection pattern)
Runtime map data is almost always **pushed to `IMapService` (the View), not in `ApplicationState`**. The recipe for each map element: find where the VM pushes it to the map → mirror it into state (we did this for `GuidanceState.DisplayLine`, section on/off via `SectionControlService.SectionStates[i].IsOn`/`.ColorCode`, and `FieldState.Imagery`) → project it. Audit §12.7 (`Plans/CONFIG_STATE_AUDIT.md`) notes `State.Sections` per-section flags are dead — a separate TODO.

## IN PROGRESS — CanvasKit (WASM Skia) renderer swap
**Why:** Canvas2D can't do true 3D perspective (CSS-tilt experiment proved it just foreshortens the same view, doesn't extend FOV). User decided: get CanvasKit (true `SkM44` perspective, matching native) working **before** Phase 3 (porting the 20+ panels/dialogs). CanvasKit 0.41.1 is **bundled locally** (`wwwroot/vendor/canvaskit.{js,wasm}`, served at `/vendor/...`, wasm as `application/wasm`) — confirmed loading (`canvaskit: ready (Skia WebGL)`).

**Phase A approach (in progress, uncommitted):** Build the Skia renderer **alongside** Canvas2D on a second canvas `#ck`, toggle with **`K`** (HUD shows `renderer: 2d/skia`), zero regression. Make Skia primary once at parity, then **Phase B** = add `SkM44` perspective for real 3D + move HUD/lightbar to flat screen-space overlay.

**Current Phase A state:** vector layers ported (grid, axes, boundaries, headland, tracks, guidance, U-turn, next, vehicle). **NOT yet ported:** coverage, imagery, tool/section footprint, lightbar (still 2D-only — missing in Skia mode, expected). The Skia render is wrapped in try/catch that surfaces errors to the `canvaskit:` HUD line and falls back to 2D.

**The exact next step:** Just fixed a CanvasKit API bug (0.41 `Path` is **immutable** — no `moveTo`/`lineTo`/`reset`; use `canvas.drawLine` for segments, `CK.Path.MakeFromCmds([VERB,x,y,...])` for polylines, `CK.Path.MakeFromSVGString` for the static vehicle triangle) and rebuilt. **Awaiting user test: press `K`, does the Skia vector rendering match 2D?** If yes → port coverage + imagery (as `SkImage`s; coverage offscreen is a 2D canvas → `CK.MakeImageFromCanvasImageSource` cached on a dirty flag, decode imagery PNG once), then tool footprint, then make Skia primary, then Phase B (3D). If the `canvaskit:` HUD shows an error, fix per the message. The CanvasKit type defs: fetch `https://cdn.jsdelivr.net/npm/canvaskit-wasm@0.41.1/types/index.d.ts` — **read them, don't guess the API**.

## Critical workflow rules (memory + this session)
- **"Clean build" = `rm -rf bin/obj`**, NOT `dotnet clean` (stale artifacts burn hours).
- **Client assets are EmbeddedResource** → any `wwwroot/*` (JS/HTML) change needs a **clean rebuild** of RemoteServer+Desktop to re-embed. Pattern: `rm -rf Shared/AgValoniaGPS.RemoteServer/{bin,obj} Platforms/AgValoniaGPS.Desktop/{bin,obj}` then `dotnet build Platforms/AgValoniaGPS.Desktop/AgValoniaGPS.Desktop.csproj`. C#-only changes don't need the clean.
- **JS TDZ trap (hit twice):** `resize()` runs immediately at load; anything it references (renderer state) must be `let`-declared **above** `resize`. A JS throw at load leaves HUD stuck at "connecting…".
- Cannot run the GUI app — **the user runs `dotnet run --project Platforms/AgValoniaGPS.Desktop/AgValoniaGPS.Desktop.csproj` and reports**; wait for their confirmation before committing (one commit+push per verified fix, not per iteration). Bump `sys/version.h` patch only for develop-bound fixes (web-UI branch commits haven't been bumping).
- Full test suite (1503) must stay green for any change touching Shared (Models/Services/ViewModels); RemoteServer/JS-only changes don't affect tests. `dotnet test AgValoniaGPS.sln`.
- DI changes touch all 3 platforms; the web server is Desktop-only so far (`App.axaml.cs` starts `RemoteServerHost`).

**Start by asking the user whether the `K` Skia toggle now renders the vector layers correctly, then continue Phase A accordingly.**
