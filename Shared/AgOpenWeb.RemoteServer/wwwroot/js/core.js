// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// The map canvas and its area, the model the transport feeds, units, render and camera state, the transport wiring.

// Renderer — consumes plain Scene + Tick objects via the transport interface.
// It has NO knowledge of SignalR or the wire format (see transport.js); swapping
// the transport at Phase 2 leaves this file untouched. Canvas2D for now; the
// CanvasKit swap (and client-side dead-reckoning) slot in here without touching
// the transport seam.

const ckcv = document.getElementById('ck'); // CanvasKit (Skia) — the sole renderer (matches native)

// Logical (CSS-pixel) canvas size. The backing store is scaled by the device
// pixel ratio so vectors render at native resolution on hi-DPI screens (tablets,
// retina) — otherwise thin strokes look faint and shimmer when panning. All draw
// code works in these logical coordinates; the Skia canvas applies scale(dpr) per frame.
let vw = innerWidth, vh = innerHeight, dpr = 1;
// The map's area in the window: the card pane (CardPane, split layout) takes mapL screen pixels
// on the left or mapR on the right, else both are 0 and the map is the whole window. The canvas
// covers the map only; pointer events arrive in window pixels, so map code takes x - mapL.
let mapL = 0, mapR = 0;
// Skia/CanvasKit renderer state — declared before resize() (which calls
// recreateSkSurface) to avoid a temporal-dead-zone ReferenceError.
let CK = null, skSurface = null, skTri = null, SKP = null;
let grCtx = null; // WebGL GrDirectContext — hoisted so the coverage render target shares it
function resize() {
  dpr = Math.min(window.devicePixelRatio || 1, 2); // cap: 3× phones don't need 9× fill
  vw = Math.max(1, innerWidth - mapL - mapR); vh = innerHeight;
  ckcv.width = Math.round(vw * dpr);
  ckcv.height = Math.round(vh * dpr);
  ckcv.style.left = mapL + 'px';
  ckcv.style.width = vw + 'px';
  ckcv.style.height = vh + 'px';
  recreateSkSurface(); // the GL surface is tied to the canvas size
}
addEventListener('resize', resize); resize();
// Give the map the window less the card pane (screen pixels each side). The overlays follow
// through --map-l / --map-r, in their zoomed pixels.
function setMapArea(l, r) {
  l = Math.max(0, Math.round(l)); r = Math.max(0, Math.round(r));
  if (l === mapL && r === mapR) return;
  mapL = l; mapR = r;
  const root = document.documentElement.style;
  root.setProperty('--map-l', (l / uiZoom) + 'px');
  root.setProperty('--map-r', (r / uiZoom) + 'px');
  resize();
  dispatchEvent(new Event('maparea'));
}

// ---- model (fed by the transport) ----
let scene = null;      // SceneDto
let tick = null;       // TickDto (latest — for sections/HUD)
let lastTick = null;
const reverseBadge = document.getElementById('reverse-badge');   // newest authoritative pose (HUD readouts + guards)
// Ring of recent authoritative poses (oldest→newest). The render INTERPOLATES between
// the two that bracket the playback head. A MULTI-pose buffer (not just prev+last) is
// what lets RENDER_DELAY exceed the ~100 ms pose interval without the playhead falling
// off the old end → that off-the-end clamp was the 10 Hz "judder".
const poseBuf = [];
const POSE_BUF_MAX = 12; // ~1.2 s @ 10 Hz — plenty of bracket for the delay + WiFi jitter
// Render slightly in the PAST so every frame interpolates between two REAL poses instead of
// extrapolating/predicting past the latest. This is what kills the per-tick re-anchor snap
// (the whole-world jump at speed) AND the straight-line turn deviation — both are
// extrapolation artifacts. Cost: a small constant display lag (~RENDER_DELAY × speed). Must
// exceed the pose interval (~100 ms @ 10 Hz) plus arrival jitter so we rarely run out of buffer.
// Buffer depth behind the newest pose. Kept SMALL (low display lag) so the dead-reckoned
// vehicle sprite stays aligned with the host-accumulated coverage — a large delay made the
// tractor visibly trail its own coverage. WiFi inter-arrival spikes that would otherwise
// drain the buffer are bridged by EXTRAPOLATION (sample(), below) rather than a freeze, so
// we get smoothness without the lag. ~130 ms covers the pose interval + interpolation room.
const RENDER_DELAY = 130; // ms
// Max coverage cells rasterized per frame. A full-coverage snapshot (sent by the host on
// grid bounds-expansion / reconnect / field reload) can be hundreds of thousands of cells;
// draining it all in one frame is a multi-hundred-ms main-thread block that froze the map
// then jumped the tractor forward (issue #34). We instead drain up to this many cells per
// frame and resume the rest next frame (progress stored on batch.off), so a big snapshot
// paints over a handful of frames with no visible hitch. ~40k rects ≈ 1-2 ms/frame.
const COV_DRAIN_BUDGET = 40000;
// Max time we'll extrapolate past the newest pose when the buffer underruns (WiFi spike /
// dropped pose) before settling to a hold — bridges spikes without flying off on a real
// dropout. At field speed 200 ms is well under a pose-pair's worth of travel.
const EXTRAP_CAP_MS = 200;
// Smoothed client↔host clock offset: host_time ≈ client_time − clockOffset. Estimated from the
// host monotonic timestamp on each Tick (EMA, so one jittery arrival can't shift the timeline).
let clockOffset = null;
let connState = 'connecting…';
let ckStatus = 'loading…'; // CanvasKit init status (renderer migration prep)
let statusBar = null;  // top status-bar readouts (fix/age/sats/units/modules)
let config = null;     // config read-frame (vehicle config, …) for the left-nav panels
let configDirty = false; // a new config frame arrived → settings panels re-read it
let mapIsDay = false;  // day/night theme (mirrors config.display.isDayMode) — drives the
                       // CSS palette ([data-theme]) and the Skia map clear/grid colours
let profiles = null;   // Vehicle & Tool picker hub: profile lists + active pair
let profilesDirty = false;
let ntripProfiles = null; // Network IO: saved NTRIP profiles + associable fields
let ntripMounts = { seq: 0, forService: '', status: '', items: [] }; // the mount points last asked for in the profile editor
let ntripDirty = false;
let fieldOps = null;   // Field Operations: field list + jobs + suggestions + import files
let fieldOpsDirty = false;
let agShare = null;    // AgShare: settings + cloud action status/results
let agShareDirty = false;
let appInfo = null;    // File/App menu: version, languages, directories, hotkeys, logs
let appInfoDirty = false;
let fieldTools = null; // Field Tools read-frame: import-track field list
let fieldToolsDirty = false;
let recPath = null;    // Recorded Path read-frame: rec files + record/play state
let recPathTab = 0;    // client-side tab: 0 Record, 1 Playback
let recSelFile = null; // client-side selected saved path
let boundary = null;   // Boundary read-frame: menu list + drive-around recording state
let wizard = null;     // Steer Wizard frame (host-driven); null when not open
let wizardDirty = false;
let fps = 0;           // smoothed client render rate (for the GPS-detail card)
let _fpsFrames = 0, _fpsT0 = 0;
let _diagT0 = 0;       // throttle (~4 Hz) for the marker-gated diag line's DOM writes
let linkRttMs = null;  // EMA of server↔client round-trip (ms), from diag.ping/PONG
// Remote actuation authority (Phase 2 safety layer): our connection id (from the
// Hello frame), the latest broadcast control state, and whether we hold control.
let myClientId = null;
let lastControl = { held: false, holderId: '', holderName: '' };
let iHoldControl = false;

// ---- Units (issue #65) -------------------------------------------------------
// Everything the host sends and accepts is METRIC — vehicle/tool geometry in m,
// section widths / nudge / coverage margin in cm, speeds in km/h, areas in ha.
// Imperial is a *display/input boundary* conversion only, mirroring the native
// UnitConversion helper: convert when filling a control or formatting a readout,
// convert back before sending. Never store or transmit imperial.
//
// Markup contract (index.html):
//   <span class="u" data-unit="m"></span>   → unit label, text filled by applyUnits()
//   <input data-unit="m" …>                 → value converted on populate + on send
// The `step` attribute in the HTML is the METRIC step; imperial swaps in impStep
// (captured once into data-mstep so a unit flip is reversible). impStep matches dec so
// a written value always lands on the step grid — spinners are hidden by CSS, so step
// only drives input validation here, and a mismatch would flag a valid entry.
const UNIT_DEFS = {
  m:   { metric: 'm',    imp: 'ft',  toImp: v => v * 3.280839895, fromImp: v => v / 3.280839895, dec: 2, impStep: 0.01 },
  cm:  { metric: 'cm',   imp: 'in',  toImp: v => v / 2.54,        fromImp: v => v * 2.54,        dec: 1, impStep: 0.1 },
  kmh: { metric: 'kph', imp: 'mph', toImp: v => v * 0.621371192, fromImp: v => v / 0.621371192, dec: 1, impStep: 0.1 },
  km:  { metric: 'km',   imp: 'mi',  toImp: v => v * 0.621371192, fromImp: v => v / 0.621371192, dec: 2, impStep: 0.01 },
  ha:  { metric: 'ha',   imp: 'ac',  toImp: v => v * 2.471053815, fromImp: v => v / 2.471053815, dec: 2, impStep: 0.01 },
};
// The status frame is the single source of truth for the unit system; before the
// first frame arrives we assume metric (matches the host default).
function isMetric() { return !statusBar || statusBar.isMetric !== false; }
function unitLabel(u) { const d = UNIT_DEFS[u]; return d ? (isMetric() ? d.metric : d.imp) : (u || ''); }
function toDisplayUnit(v, u) { const d = UNIT_DEFS[u]; return (!d || isMetric()) ? v : d.toImp(v); }
function fromDisplayUnit(v, u) { const d = UNIT_DEFS[u]; return (!d || isMetric()) ? v : d.fromImp(v); }
// Round a display value to the precision that unit deserves (2 dp for m/ft, 1 for
// cm/in, …). Metric keeps the old 3-dp behaviour so stored values aren't truncated.
function roundDisplay(v, u) {
  const d = UNIT_DEFS[u];
  if (!d || isMetric()) return Math.round(v * 1000) / 1000;
  const p = Math.pow(10, d.dec);
  return Math.round(v * p) / p;
}
// "12.3 ft" — value (metric in) + unit suffix, formatted for the active system.
function fmtUnit(v, u, dec) {
  const d = UNIT_DEFS[u];
  const n = toDisplayUnit(v, u);
  return n.toFixed(dec != null ? dec : (d ? d.dec : 1)) + ' ' + unitLabel(u);
}
// Fill every `.u` label and re-step every `[data-unit]` input for the active system.
// Cheap enough to run on any unit flip (a few dozen nodes).
function applyUnits() {
  for (const el of document.querySelectorAll('span.u[data-unit]')) el.textContent = unitLabel(el.dataset.unit);
  for (const inp of document.querySelectorAll('input[data-unit]')) {
    if (inp.dataset.mstep == null) inp.dataset.mstep = inp.getAttribute('step') || '';
    const d = UNIT_DEFS[inp.dataset.unit];
    if (!d) continue;
    if (isMetric()) { if (inp.dataset.mstep) inp.step = inp.dataset.mstep; }
    else inp.step = String(d.impStep);
    // min/max are metric bounds; drop them in imperial rather than convert (the host
    // clamps anyway) so a valid imperial entry isn't rejected by the browser.
    if (inp.dataset.mmin == null) inp.dataset.mmin = inp.getAttribute('min') || '';
    if (isMetric()) { if (inp.dataset.mmin) inp.setAttribute('min', inp.dataset.mmin); }
    else if (inp.dataset.mmin) inp.removeAttribute('min');
  }
}
// Read a number input as a METRIC value: parse what the user typed (a display value)
// and convert back. Fields whose metric step is a whole unit are integer-backed
// host-side (nudge distance), so round after converting —
// otherwise 8" → 20.32 cm would fail the host's int parse and silently do nothing.
function readUnitInput(inp) {
  const v = parseFloat(inp.value);
  if (!Number.isFinite(v)) return NaN;
  const u = inp.dataset.unit;
  if (!u || isMetric()) return v;
  const m = fromDisplayUnit(v, u);
  return Number(inp.dataset.mstep || inp.getAttribute('step')) >= 1 ? Math.round(m) : Math.round(m * 1000) / 1000;
}
// Fill a number input from a stored METRIC value, converting for display. A whole-unit
// metric step means an integer-backed field (cm counts), so metric shows no decimals.
function writeUnitInput(inp, metricVal) {
  const u = inp.dataset.unit;
  if (!u) { inp.value = Math.round(metricVal * 1000) / 1000; return; }
  if (isMetric() && Number(inp.dataset.mstep || inp.getAttribute('step')) >= 1) { inp.value = Math.round(metricVal); return; }
  inp.value = roundDisplay(toDisplayUnit(metricVal, u), u);
}
// Unit flips arrive on the Status frame (the host persists the setting, then echoes
// it). Relabel + force every open panel to re-read so values convert immediately.
let _lastMetric = null;
function syncUnits() {
  const m = isMetric();
  if (m === _lastMetric) return;
  _lastMetric = m;
  applyUnits();
  onUnitsChanged();
}
// Everything unit-bearing that isn't redrawn every frame: mark the read-frames dirty so
// the open panels re-populate through the (now converting) populate path, and drop the
// wizard's step cache so its hand-built markup is rebuilt with the new labels.
function onUnitsChanged() {
  configDirty = true;
  fieldOpsDirty = true;
  _wzKey = '';
  if (document.getElementById('fieldbuilder').classList.contains('open')) { renderFbHeadland(); renderFbTram(); }
  if (document.getElementById('boundarymenu').classList.contains('open')) renderBoundaryMenu();
  if (document.getElementById('boundaryplayer').classList.contains('open')) renderBoundaryPlayer();
}
applyUnits();   // fill the static labels now (metric default) — the first Status frame corrects it


// ---- coverage offscreen (Phase 2): cells painted into a cell-grid canvas,
//      blitted to world space each frame. Snapshot on connect, deltas after. ----
let cov = null;        // { cellSize, originE, originN, width, height, canvas, cctx }
let covCells = 0;
let coverageEdges = null;   // crisp worked-area perimeter polylines (server-fed, field-local m)
let covEdgeRgb = 0x98fb98;  // last coverage colour seen → the perimeter stroke matches the fill
const EDGE_OFF = new URLSearchParams(location.search).has('noedge'); // ?noedge → A/B the edge off

// ---- ground texture: the tiled field backdrop. Two variants — a dark (night) and a
//      light (day) version of the same texture — picked by mapIsDay so the field reads
//      against the matching theme. Drawn as a repeating shader in world space, gated on
//      Display.FieldTextureVisible. Each decoded to an SkImage on first use. ----
const groundImgNight = new Image(), groundImgDay = new Image();
let groundReadyNight = false, groundReadyDay = false;
let skGroundNight = null, skGroundDay = null;
groundImgNight.onload = () => { groundReadyNight = true; };
groundImgDay.onload = () => { groundReadyDay = true; };
groundImgNight.src = '/icons/GroundTextureDark.png';
groundImgDay.src = '/icons/GroundTextureLight.png';

// ---- tractor sprite (TractorAoG.png) — drawn world-sized on the ground, replacing the
//      fallback triangle. Sized from the vehicle track-width/wheelbase like native. ----
const tractorImg = new Image();
let tractorReady = false, skTractor = null;
tractorImg.onload = () => { tractorReady = true; };
tractorImg.src = '/icons/TractorAoG.png';
// Steerable front-wheel sprite (single wheel, drawn at both front-axle ends, rotated by
// the live wheel angle) — matches native SkiaMapControl's FrontWheels.png.
const frontWheelImg = new Image();
let frontWheelReady = false, skFrontWheel = null;
frontWheelImg.onload = () => { frontWheelReady = true; };
frontWheelImg.src = '/icons/FrontWheels.png';
// Harvester + articulated body sprites (issue #88) — AgOpenGPS's AoG brand textures,
// drawn with CVehicle's per-type layout in vehicleSk. Lazily turned into SkImages.
function loadSprite(src) {
  const s = { img: new Image(), ready: false, sk: null };
  s.img.onload = () => { s.ready = true; };
  s.img.src = src;
  return s;
}
function skSprite(s) { return s.ready ? (s.sk || (s.sk = CK.MakeImageFromCanvasImageSource(s.img))) : null; }
const harvesterSpr = loadSprite('/icons/HarvesterAoG.png');
const artFrontSpr = loadSprite('/icons/ArticulatedFrontAoG.png');
const artRearSpr = loadSprite('/icons/ArticulatedRearAoG.png');

// ---- background imagery: extent from the Scene, PNG fetched over HTTP. ----
let imageryRect = null;  // { minE, minN, maxE, maxN, version }
let imageryImg = null;   // loaded <img> once ready
let imageryVer = null;   // version currently loaded (cache-bust on change)

// ---- field geo origin (lat/lon at E=0,N=0) — for the satellite-tile underlay. ----
let originLat = 0, originLon = 0;
let satEnabled = false;  // true while drawing a boundary on satellite imagery

// ---- client-owned camera (never crosses the wire) ----
let pxPerM = 4.0;
let camE = 0, camN = 0;
// Camera follow mode, matching native SkiaMapControl: 0 NorthUp (lock to vehicle),
// 1 HeadingUp (lock + rotate map to heading), 2 Free (manual pan holds), 3 Map
// (map-centric auto-pan with a safe zone). Default Map, like native.
let cameraMode = 3;
let mapRotation = 0;          // radians the map is rotated (−heading in HeadingUp)
let _cosRR = 1, _sinRR = 0;   // cos/sin of the screen rotation (−mapRotation), per frame
const AUTO_PAN_SAFE = 0.65, AUTO_PAN_SMOOTH = 0.15; // match native tuning
// Map-rotation easing (HeadingUp). ADAPTIVE: heavily damp SMALL heading errors so the
// whole heading-up map doesn't wobble with autosteer line-holding dither, but stay
// responsive for LARGE errors (real headland turns). alpha ramps MIN→MAX as the heading
// error grows to ROT_SMOOTH_FULL rad. (A single fixed value either wobbles on the line or
// lags the turns.)
const ROT_SMOOTH_MIN = 0.035; // ~0.5 s settle — kills line-holding dither (~0.2-0.3°)
const ROT_SMOOTH_MAX = 0.35;  // crisp on real turns (≈ the old fixed 0.3)
const ROT_SMOOTH_FULL = 0.12; // rad (~7°) error at which we're fully responsive
function isFollowMode() { return cameraMode !== 2; }
// 3D perspective tilt (Skia only — Canvas2D can't do true perspective). pitch 0 =
// top-down (identical to the ortho path); up to MAX_PITCH tilts toward the horizon.
// perspM is the world→CSS-px matrix for the current frame (null when top-down).
let pitch = 0;          // radians
let perspM = null;      // 16-elem M44 (world→CSS px) or null
let perspMInv = null;   // cached 4×4 inverse of perspM (CSS px→world ray); null until built
const DEFAULT_PITCH = Math.PI / 3;        // 60° — the one-key tilt
const MAX_PITCH = 65 * Math.PI / 180;     // v1 cap: keeps the local field in front
const PITCH_STEP = 5 * Math.PI / 180;
// Persist the 3D tilt + zoom across reloads (issue #35: tilting to 3D was lost on
// restart). The camera is client-owned live, but its last tilt+zoom are stored
// HOST-side (appstate.json, via the view.save command) and replayed to every client
// in the connection seed (onViewPrefs). This restores identically on any client —
// browser or WebView — independent of browser localStorage behaviour.
// The follow mode (N/H/M) rides the same path (#176); Free (2) is a temporary "I panned"
// state and is never saved — the last followed mode stays saved.
let _savedPitch = pitch, _savedZoom = pxPerM, _savedMode = cameraMode, _viewSaveT = 0;
function applyViewPrefs(p, z, m) {
  if (typeof p === 'number' && isFinite(p)) pitch = Math.max(0, Math.min(MAX_PITCH, p));
  if (typeof z === 'number' && isFinite(z)) pxPerM = Math.min(200, Math.max(0.2, z));
  if (m === 0 || m === 1 || m === 3) cameraMode = m;
  _savedPitch = pitch; _savedZoom = pxPerM; _savedMode = cameraMode; // hydrated == saved: no echo-back
}
function maybeSaveView() {
  const mode = cameraMode === 2 ? _savedMode : cameraMode;
  if (pitch === _savedPitch && pxPerM === _savedZoom && mode === _savedMode) return;
  const now = performance.now();
  if (now - _viewSaveT < 800) return;        // debounce: at most ~1.25 writes/s while adjusting
  _viewSaveT = now; _savedPitch = pitch; _savedZoom = pxPerM; _savedMode = mode;
  transport.send('view.save|' + pitch + '|' + pxPerM + '|' + mode); // host writes it to appstate.json
}
const PERSP_FOV = 0.7;                     // rad, matches native SkiaMapControl

// ---- transport wiring (the only coupling point) ----
const transport = RemoteTransport.create({
  onScene(s) {
    scene = s;
    originLat = s.originLat; originLon = s.originLon; // for the satellite underlay geo math
    if (isFollowMode() && (!tick || !tick.pose) && s.boundaries.length && s.boundaries[0].length) {
      const r = s.boundaries[0];
      camE = r.reduce((a, p) => a + p.e, 0) / r.length;
      camN = r.reduce((a, p) => a + p.n, 0) / r.length;
    }
    // Background imagery: (re)load the PNG only when the version changes.
    if (s.imagery) {
      imageryRect = s.imagery;
      if (s.imagery.version !== imageryVer) {
        imageryVer = s.imagery.version;
        const img = new Image();
        img.onload = () => { imageryImg = img; };
        img.src = '/backpic.png?v=' + s.imagery.version;
      }
    } else {
      imageryRect = null; imageryImg = null; imageryVer = null;
    }
    if (saPanel.classList.contains('open')) populateScreenAlerts(); // Aerial Imagery follows the field
    // Keep the Tracks manager list live (activate/add/delete/hide change the Scene).
    if (document.getElementById('dlg-tracks').classList.contains('open')) renderTracksList();
    if (document.getElementById('fieldbuilder').classList.contains('open') && fbTab === 'tracks'
        && !document.querySelector('#fb-tracklist .flg-nameedit')) renderFbTracks();
    if (document.getElementById('fieldbuilder').classList.contains('open') && fbTab === 'headland'
        && document.getElementById('fb-addhl').hidden
        && !document.querySelector('#fb-hllist .flg-nameedit')) renderFbHeadland();
    if (document.getElementById('fieldbuilder').classList.contains('open') && fbTab === 'tram') {
      if (document.getElementById('fb-tramedit').hidden) renderFbTram(); else populateTramEdit();
    }
    // Refresh the flag list on add/delete/rename/recolour, unless the user is mid-edit.
    if (document.getElementById('dlg-flags').classList.contains('open')
        && !document.querySelector('.flg-nameedit') && !document.querySelector('.flg-colorpick'))
      renderFlagList();
  },
  onTick(t) {
    tick = t;
    reverseBadge.classList.toggle('show', !!(t.op && t.op.reverse)); // #125
    if (t.pose) {
      lastTick = {
        e: t.pose.e, n: t.pose.n, heading: t.pose.heading, speed: t.pose.speed,
        tool: t.tool, goal: t.goal, t: performance.now(), hostT: t.hostMs,
      };
      // Keep the buffer strictly increasing in host time. The 30 Hz render-pull vs 10 Hz
      // broadcast can occasionally resend the same pose (equal hostT) — a duplicate/
      // backward stamp would break the bracket search, so replace rather than append.
      const prev = poseBuf[poseBuf.length - 1];
      if (prev && typeof t.hostMs === 'number' && typeof prev.hostT === 'number' && t.hostMs <= prev.hostT)
        poseBuf[poseBuf.length - 1] = lastTick;
      else {
        poseBuf.push(lastTick);
        if (poseBuf.length > POSE_BUF_MAX) poseBuf.shift();
      }
      // Track the client↔host clock offset (EMA). The interp SPAN comes from the
      // jitter-free host timestamps; this offset only aligns the playback timeline, so a
      // single late/early arrival shifts it by ≤5% instead of warping the whole frame.
      if (typeof t.hostMs === 'number') {
        const raw = lastTick.t - t.hostMs;
        // Very slow EMA (0.01): the client↔host clock barely drifts (ppm), so adapting
        // slowly keeps the playhead velocity steady — a faster EMA chased arrival jitter and
        // left residual position stutter (offline sim: 0.02→12%, 0.01→6% spread). The
        // RENDER_DELAY buffer absorbs the slower settle. Errs high (more lag) = edge-safe.
        clockOffset = (clockOffset === null) ? raw : clockOffset + 0.01 * (raw - clockOffset);
      }
    }
    pushChartData(t);
  },
  onCoverageInit(init) {
    const cs = init.cellSize;
    // An empty reset grid means the field closed: drop the raster and the perimeter.
    if (init.reset && (init.width <= 0 || init.height <= 0)) {
      if (cov) {
        if (cov.skImg) cov.skImg.delete();
        if (cov.surface) cov.surface.delete();
        if (cov.covPaint) cov.covPaint.delete();
      }
      cov = null; covCells = 0; coverageEdges = null;
      return;
    }
    // Grid GROWTH at the same cell size (host bounds-expansion, reset=false): the added ground is
    // empty, so the host resends nothing. Keep the coverage we already have, re-anchor it into the
    // larger grid, and carry on with incremental deltas — no rescan, no repaint, no flicker. A
    // reset init (new field / reload / cell-size change) always falls through to a clean rebuild.
    if (!init.reset && cov && Math.abs(cov.cellSize - cs) < 1e-9 &&
        (init.width !== cov.width || init.height !== cov.height ||
         Math.abs(init.originE - cov.originE) > 1e-9 || Math.abs(init.originN - cov.originN) > 1e-9)) {
      reanchorCoverage(init);
      return;
    }
    // Fresh field / resolution (cell-size) change: tear down and rebuild. A full snapshot follows.
    if (cov) {
      if (cov.skImg) cov.skImg.delete();
      if (cov.surface) cov.surface.delete();
      if (cov.covPaint) cov.covPaint.delete();
    }
    cov = makeCovGrid(init);
    covCells = 0;
  },
  onCoverageCells(msg) {
    if (!cov || !msg.cells) return;
    // Queue with arrival time; the actual rasterization is delayed by RENDER_DELAY in
    // drawCoverageSk (both render paths) so freshly-laid coverage appears on the SAME
    // playback timeline as the render-delayed tool/pose. Drawing it on arrival put the
    // real-time coverage front AHEAD of the delayed tool sprite, so coverage poked out in
    // front of the tool at speed (gap = speed × RENDER_DELAY; hidden under the tool below
    // ~5 km/h, visible above). arrival + RENDER_DELAY matches the host-timeline pose
    // playback exactly (same socket/latency), so the front now tracks the tool at any speed.
    cov.pending.push({ cells: msg.cells, t: performance.now() });
  },
  onCoverageEdge(polylines) { coverageEdges = polylines; }, // crisp worked-area perimeter (~2 Hz)
  onStatusBar(s) { statusBar = s; syncUnits(); if (typeof applySimBarVisible === 'function') applySimBarVisible(); syncUnsavedCov(); syncDualHeadingWarning(s); syncGpsLost(s); },
  onConfig(c) { config = c; configDirty = true; applyTheme(c && c.display && c.display.isDayMode); applyUiScale(c && c.display && c.display.uiScale); },
  onProfiles(p) { profiles = p; profilesDirty = true; },
  onNtripProfiles(p) { ntripProfiles = p; ntripDirty = true; },
  onNtripMountpoints(m) { ntripMounts = m; renderNtripMounts(); },
  onFieldOps(f) { fieldOps = f; fieldOpsDirty = true; },
  onAgShare(a) { agShare = a; agShareDirty = true; },
  onAppInfo(a) { appInfo = a; appInfoDirty = true; I18n.setLanguage(a.currentLanguage); },
  onFieldTools(f) { fieldTools = f; fieldToolsDirty = true; if (document.getElementById('importtracks').classList.contains('open')) renderImportTracks(); },
  onRecordedPath(r) { recPath = r; if (document.getElementById('recpath').classList.contains('open')) renderRecPath(); },
  onBoundary(b) {
    boundary = b;
    if (document.getElementById('boundarymenu').classList.contains('open')) renderBoundaryMenu();
    if (document.getElementById('boundaryplayer').classList.contains('open')) renderBoundaryPlayer();
  },
  onWizard(w) { wizard = w; wizardDirty = true; },
  onHello(id) { myClientId = id; updateControlUi(); claimSeatIfFree(); Vt.render(); WebCards.render(); },
  onControlState(s) { lastControl = s; updateControlUi(); claimSeatIfFree(); Vt.render(); WebCards.render(); },
  onWebCards(list) { WebCards.setBookmarks(list); },
  onSound(id) { Sounds.play(id); },
  onPrompt(p) { hostPrompt = p; renderHostPrompt(); },
  onToast(msg) { showToast(msg); },
  onHardwareMessage(text, seconds, warning) { showHardwareMessage(text, seconds, warning); },
  onDrivePick(fields) { showDrivePick(fields); },
  onVt(m, t) { Vt.message(m, t); },
  onVtImage(id, w, h, rgba) { Vt.image(id, w, h, rgba); },
  // Round-trip link probe reply: token is the performance.now() we sent in diag.ping, so
  // RTT = now − token measures the pure server↔client link (one client clock, no skew).
  onPong(token) {
    const rtt = performance.now() - parseFloat(token);
    if (rtt >= 0 && rtt < 60000) linkRttMs = (linkRttMs === null) ? rtt : linkRttMs + 0.3 * (rtt - linkRttMs);
  },
  onStatus(s) { connState = s; renderRole(); claimSeatIfFree(); },
  // Persisted web-camera view (issue #35): restore last tilt+zoom from the host seed.
  onViewPrefs(pitch, zoom, mode) { applyViewPrefs(pitch, zoom, mode); },
});

function recreateSkSurface() {
  if (!CK) return;
  if (skSurface) { skSurface.delete(); skSurface = null; }
  // Reuse the GrDirectContext across resizes. Resizing the canvas does NOT lose its WebGL
  // context, and rebuilding grCtx would orphan every GPU-resident SkImage created on the old
  // context — the coverage render target (MakeRenderTarget(grCtx, …)) plus the ground/tractor/
  // imagery sprites — so those layers vanish after a resize (coverage being the obvious one).
  // Only the on-screen surface (render target sized to the canvas) needs rebuilding.
  // Non-color-managed surface (null colorSpace) so Skia blends semi-transparent fills
  // (section footprint, lightbar) in encoded sRGB space — matching the 2D canvas. The default
  // MakeWebGLCanvasSurface attaches an sRGB color space and blends in LINEAR space, which
  // lightens/washes out transparent colors. Build via the explicit GL path; fall back to the
  // color-managed helper if needed.
  if (!grCtx) {
    const ctxHandle = CK.GetWebGLContext(ckcv);
    if (ctxHandle) grCtx = CK.MakeWebGLContext(ctxHandle);
    if (grCtx) {
      maxTextureSide = readMaxTextureSide();
      reportMaxTexture();
      upgradeCovToGpu(); // a grid built before the GPU context existed moves onto it
    }
  }
  if (grCtx) skSurface = CK.MakeOnScreenGLSurface(grCtx, ckcv.width, ckcv.height, null);
  if (!skSurface) skSurface = CK.MakeWebGLCanvasSurface(ckcv);
}
