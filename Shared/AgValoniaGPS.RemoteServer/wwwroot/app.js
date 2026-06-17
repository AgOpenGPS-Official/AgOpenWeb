// Renderer — consumes plain Scene + Tick objects via the transport interface.
// It has NO knowledge of SignalR or the wire format (see transport.js); swapping
// the transport at Phase 2 leaves this file untouched. Canvas2D for now; the
// CanvasKit swap (and client-side dead-reckoning) slot in here without touching
// the transport seam.

const ckcv = document.getElementById('ck'); // CanvasKit (Skia) — the sole renderer (matches native)
const hud = document.getElementById('hud');

// Logical (CSS-pixel) canvas size. The backing store is scaled by the device
// pixel ratio so vectors render at native resolution on hi-DPI screens (tablets,
// retina) — otherwise thin strokes look faint and shimmer when panning. All draw
// code works in these logical coordinates; the Skia canvas applies scale(dpr) per frame.
let vw = innerWidth, vh = innerHeight, dpr = 1;
// Skia/CanvasKit renderer state — declared before resize() (which calls
// recreateSkSurface) to avoid a temporal-dead-zone ReferenceError.
let CK = null, skSurface = null, skTri = null, SKP = null;
function resize() {
  dpr = Math.min(window.devicePixelRatio || 1, 2); // cap: 3× phones don't need 9× fill
  vw = innerWidth; vh = innerHeight;
  ckcv.width = Math.round(vw * dpr);
  ckcv.height = Math.round(vh * dpr);
  ckcv.style.width = vw + 'px';
  ckcv.style.height = vh + 'px';
  recreateSkSurface(); // the GL surface is tied to the canvas size
}
addEventListener('resize', resize); resize();

// ---- model (fed by the transport) ----
let scene = null;      // SceneDto
let tick = null;       // TickDto (latest — for sections/HUD)
let lastTick = null;   // { e, n, heading, speed, t } authoritative pose + receipt time, for DR
let connState = 'connecting…';
let ckStatus = 'loading…'; // CanvasKit init status (renderer migration prep)
let statusBar = null;  // top status-bar readouts (fix/age/sats/units/modules)
let config = null;     // config read-frame (vehicle config, …) for the left-nav panels
let configDirty = false; // a new config frame arrived → settings panels re-read it
let profiles = null;   // Vehicle & Tool picker hub: profile lists + active pair
let profilesDirty = false;
let fps = 0;           // smoothed client render rate (for the GPS-detail card)
let _fpsFrames = 0, _fpsT0 = 0;
// Remote actuation authority (Phase 2 safety layer): our connection id (from the
// Hello frame), the latest broadcast control state, and whether we hold control.
let myClientId = null;
let lastControl = { held: false, holderId: '', holderName: '' };
let iHoldControl = false;

// ---- coverage offscreen (Phase 2): cells painted into a cell-grid canvas,
//      blitted to world space each frame. Snapshot on connect, deltas after. ----
let cov = null;        // { cellSize, originE, originN, width, height, canvas, cctx }
let covCells = 0;

// ---- background imagery: extent from the Scene, PNG fetched over HTTP. ----
let imageryRect = null;  // { minE, minN, maxE, maxN, version }
let imageryImg = null;   // loaded <img> once ready
let imageryVer = null;   // version currently loaded (cache-bust on change)

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
const ROT_SMOOTH = 0.3; // ease map rotation toward target (smooths 10 Hz heading steps)
function isFollowMode() { return cameraMode !== 2; }
// 3D perspective tilt (Skia only — Canvas2D can't do true perspective). pitch 0 =
// top-down (identical to the ortho path); up to MAX_PITCH tilts toward the horizon.
// perspM is the world→CSS-px matrix for the current frame (null when top-down).
let pitch = 0;          // radians
let perspM = null;      // 16-elem M44 (world→CSS px) or null
const DEFAULT_PITCH = Math.PI / 3;        // 60° — the one-key tilt
const MAX_PITCH = 65 * Math.PI / 180;     // v1 cap: keeps the local field in front
const PITCH_STEP = 5 * Math.PI / 180;
const PERSP_FOV = 0.7;                     // rad, matches native SkiaMapControl

// ---- transport wiring (the only coupling point) ----
const transport = RemoteTransport.create({
  onScene(s) {
    scene = s;
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
  },
  onTick(t) {
    tick = t;
    if (t.pose) {
      lastTick = {
        e: t.pose.e, n: t.pose.n, heading: t.pose.heading, speed: t.pose.speed,
        tool: t.tool, t: performance.now(),
      };
    }
  },
  onCoverageInit(init) {
    const canvas = document.createElement('canvas');
    canvas.width = init.width;
    canvas.height = init.height;
    cov = {
      cellSize: init.cellSize, originE: init.originE, originN: init.originN,
      width: init.width, height: init.height,
      canvas, cctx: canvas.getContext('2d'),
      dirty: true, skImg: null, // Skia snapshot of the offscreen, rebuilt when dirty
    };
    covCells = 0;
  },
  onCoverageCells(msg) {
    if (!cov || !msg.cells) return;
    const c = msg.cells, H = cov.height, cctx = cov.cctx;
    let lastRgb = -1;
    for (let i = 0; i + 2 < c.length; i += 3) {
      const x = c[i], y = c[i + 1], rgb = c[i + 2];
      if (rgb !== lastRgb) { cctx.fillStyle = '#' + (rgb >>> 0 & 0xFFFFFF).toString(16).padStart(6, '0'); lastRgb = rgb; }
      cctx.fillRect(x, H - 1 - y, 1, 1); // flip: high northing at offscreen top
      covCells++;
    }
    cov.dirty = true; // offscreen changed → Skia must re-snapshot before next blit
  },
  onStatusBar(s) { statusBar = s; },
  onConfig(c) { config = c; configDirty = true; },
  onProfiles(p) { profiles = p; profilesDirty = true; },
  onHello(id) { myClientId = id; updateControlUi(); },
  onControlState(s) { lastControl = s; updateControlUi(); },
  onStatus(s) { connState = s; },
});
transport.start();

// ---- CanvasKit (Skia) renderer — built alongside Canvas2D for A/B (toggle K).
//      Phase A: vector layers at parity; coverage/imagery/tool/lightbar next.
//      (State declared near the top so resize() can call recreateSkSurface.) ----
function recreateSkSurface() {
  if (!CK) return;
  if (skSurface) { skSurface.delete(); skSurface = null; }
  // Non-color-managed surface (null colorSpace) so Skia blends semi-transparent
  // fills (section footprint, lightbar) in encoded sRGB space — matching the 2D
  // canvas. The default MakeWebGLCanvasSurface attaches an sRGB color space and
  // blends in LINEAR space, which lightens/washes out transparent colors. Build
  // via the explicit GL path; fall back to the color-managed helper if needed.
  const ctxHandle = CK.GetWebGLContext(ckcv);
  if (ctxHandle) {
    const grCtx = CK.MakeWebGLContext(ctxHandle);
    if (grCtx) skSurface = CK.MakeOnScreenGLSurface(grCtx, ckcv.width, ckcv.height, null);
  }
  if (!skSurface) skSurface = CK.MakeWebGLCanvasSurface(ckcv);
}
// Hex (#rrggbb) or rgb(a)(...) → CanvasKit color (CK.Color: r,g,b 0-255, a 0-1).
function ckColor(s) {
  if (s[0] === '#') return CK.Color(parseInt(s.slice(1, 3), 16), parseInt(s.slice(3, 5), 16), parseInt(s.slice(5, 7), 16), 1);
  const m = s.match(/\(([^)]+)\)/)[1].split(',').map(Number);
  return CK.Color(m[0], m[1], m[2], m.length > 3 ? m[3] : 1);
}
function buildSkPaints() {
  const mk = (color, w, dash) => {
    const p = new CK.Paint();
    p.setStyle(CK.PaintStyle.Stroke);
    p.setColor(ckColor(color));
    p.setStrokeWidth(w);
    p.setAntiAlias(true);
    p.setStrokeJoin(CK.StrokeJoin.Round);
    p.setStrokeCap(CK.StrokeCap.Round);
    if (dash) p.setPathEffect(CK.PathEffect.MakeDash(dash, 0));
    return p;
  };
  const fill = (color) => { const p = new CK.Paint(); p.setStyle(CK.PaintStyle.Fill); p.setColor(ckColor(color)); p.setAntiAlias(true); return p; };
  SKP = {
    boundary: mk('#46a0ff', 5), headland: mk('#5fd35f', 4),
    track: mk('#ffd24a', 4), reference: mk('#a86bff', 5, [9, 7]),
    guidance: mk('#fc56ba', 5), uturn: mk('#4df24d', 5), next: mk('#00c8c8', 4),
    // Match native (night-mode) grid: grey, ~0.31/0.47 alpha, major ~2× thickness.
    gridMinor: mk('rgba(180,180,180,0.314)', 1), gridMajor: mk('rgba(200,200,200,0.47)', 2),
    axisX: mk('rgba(204,51,51,0.275)', 1.5), axisY: mk('rgba(51,204,51,0.275)', 1.5),
    vehicle: fill('#39FF6A'),
    flagFill: fill('#FF0000'), flagOutline: mk('#101010', 1.5), // colour set per flag
  };
  // Section footprint bars: one stroke paint per ColorCode (butt cap so adjacent
  // sections abut without rounded overhang), matching the 2D SECTION_COLORS.
  SKP.section = SECTION_COLORS.map((c) => {
    const p = new CK.Paint();
    p.setStyle(CK.PaintStyle.Stroke);
    p.setColor(ckColor(c));
    p.setStrokeWidth(7);
    p.setStrokeCap(CK.StrokeCap.Butt);
    p.setAntiAlias(true);
    return p;
  });
  // Lightbar LED fill — one reusable paint, recoloured per cell (crisp rects, no AA).
  SKP.lbFill = new CK.Paint();
  SKP.lbFill.setStyle(CK.PaintStyle.Fill);
  SKP.lbFill.setAntiAlias(false);
  // Vehicle triangle in marker-local px (drawn under canvas translate+rotate).
  skTri = CK.Path.MakeFromSVGString('M 0 -14 L 9 11 L -9 11 Z');
}
if (typeof CanvasKitInit === 'function') {
  CanvasKitInit({ locateFile: f => '/vendor/' + f }).then(ck => {
    CK = ck;
    buildSkPaints();
    recreateSkSurface();
    ckStatus = skSurface ? 'ready (Skia WebGL)' : 'no WebGL surface';
  }).catch(e => { ckStatus = 'load error: ' + e.message; });
} else {
  ckStatus = 'loader missing';
}

// ---- camera controls ----
addEventListener('wheel', e => {
  e.preventDefault();
  pxPerM *= e.deltaY < 0 ? 1.1 : 0.9;
  pxPerM = Math.min(200, Math.max(0.2, pxPerM));
}, { passive: false });

let dragging = false, lastX = 0, lastY = 0;
addEventListener('pointerdown', e => { dragging = true; cameraMode = 2; lastX = e.clientX; lastY = e.clientY; });
addEventListener('pointerup', () => dragging = false);
addEventListener('pointermove', e => {
  if (!dragging) return;
  // Pan in the rotated frame: invert the screen rotation so the grabbed point
  // tracks the cursor. Reduces to the plain north-up pan at rotation 0.
  const dsx = e.clientX - lastX, dsy = e.clientY - lastY;
  camE += (-_cosRR * dsx + _sinRR * dsy) / pxPerM;
  camN += (_sinRR * dsx + _cosRR * dsy) / pxPerM;
  lastX = e.clientX; lastY = e.clientY;
});
addEventListener('keydown', e => {
  if (e.key === 'f' || e.key === 'F') cameraMode = 3; // resume map-follow
  // 3D tilt: 3 toggles between top-down and 60°, [ / ] nudge the pitch.
  else if (e.key === '3') pitch = pitch > 0.001 ? 0 : DEFAULT_PITCH;
  else if (e.key === '[') pitch = Math.max(0, pitch - PITCH_STEP);
  else if (e.key === ']') pitch = Math.min(MAX_PITCH, pitch + PITCH_STEP);
});

// ---- sim drive keys (client→host commands) ----
// Arrow keys / space for desktop; the simulator bar (#simbar, below) covers touch.
// The host maps these ids to the matching VM command on its UI thread (allowlist).
const KEY_CMD = {
  ArrowLeft: 'sim.steerLeft', ArrowRight: 'sim.steerRight',
  ArrowUp: 'sim.speedUp', ArrowDown: 'sim.speedDown', ' ': 'sim.stop',
};
addEventListener('keydown', e => {
  const cmd = KEY_CMD[e.key];
  if (cmd) { e.preventDefault(); transport.send(cmd); }
});
// ---- simulator bar (Phase 6) — mirrors the native SimulatorPanel ----
// Sim is Tier-1 (hardware-safe), so the bar's commands aren't gated by control
// authority — anyone driving the sim is fine. Panel *visibility* is client-local
// (the host's IsSimulatorPanelVisible is a native-only concept); enabled/speed/
// steer are host state, mirrored back over the Status frame.
const SIM = {
  bar: document.getElementById('simbar'), launch: document.getElementById('sim-launch'),
  enable: document.getElementById('sim-enable'), tenx: document.getElementById('sim-10x'),
  steer: document.getElementById('sim-steer'), steerVal: document.getElementById('sim-steerval'),
  speedVal: document.getElementById('sim-speedval'), gps: document.getElementById('sim-gps'),
};
let simBarOpen = true;          // client-local; bar shown on load (no menu yet)
let _steerDragging = false;     // suppress state→slider sync while the user drags
function applySimBarVisible() {
  SIM.bar.classList.toggle('open', simBarOpen);
  SIM.launch.classList.toggle('show', !simBarOpen);
}
applySimBarVisible();
SIM.bar.addEventListener('pointerdown', e => e.stopPropagation()); // don't pan the map
SIM.launch.addEventListener('pointerdown', e => e.stopPropagation());
// Generic argless commands (data-cmd) — fire on pointerdown for snappy feel.
for (const b of document.querySelectorAll('#simbar button[data-cmd]')) {
  b.addEventListener('pointerdown', e => {
    e.preventDefault(); e.stopPropagation();
    transport.send(b.dataset.cmd);
  });
}
// Steer slider → command-with-arg; optimistic local readout while dragging.
SIM.steer.addEventListener('input', () => {
  _steerDragging = true;
  const deg = parseFloat(SIM.steer.value);
  SIM.steerVal.textContent = deg.toFixed(1) + '°';
  transport.send('sim.setSteer|' + deg);
});
SIM.steer.addEventListener('change', () => { _steerDragging = false; });
// GPS button opens the SimCoords dialog (client-local open; OK sends sim.setCoords).
SIM.gps.addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation();
  if (statusBar && statusBar.simEnabled) return; // native guard: disable sim first
  openSimCoords();
});
document.getElementById('sim-close').addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation(); simBarOpen = false; applySimBarVisible();
});
SIM.launch.addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation(); simBarOpen = true; applySimBarVisible();
});

// ---- dialog host (Phase 6) — the web mirror of DialogOverlayHost ----
// One modal at a time over a dimming backdrop. SimCoords is the first card; later
// phases add more cards into the same host and reuse openDialog/closeDialog.
const dialogHost = document.getElementById('dialoghost');
function openDialog(cardId) {
  for (const c of dialogHost.querySelectorAll('.dlg-card')) c.classList.toggle('open', c.id === cardId);
  dialogHost.classList.add('open');
}
function closeDialog() { dialogHost.classList.remove('open'); }
dialogHost.querySelector('.dlg-backdrop').addEventListener('pointerdown', e => { e.stopPropagation(); closeDialog(); });
dialogHost.addEventListener('pointerdown', e => e.stopPropagation()); // keep map from panning
const dlgLat = document.getElementById('dlg-lat'), dlgLon = document.getElementById('dlg-lon');
function openSimCoords() {
  // Pre-fill with the current vehicle position (from the Status frame), matching
  // the native dialog's GetSimulatorPosition() seed.
  dlgLat.value = statusBar && statusBar.lat != null ? statusBar.lat.toFixed(8) : '';
  dlgLon.value = statusBar && statusBar.lon != null ? statusBar.lon.toFixed(8) : '';
  openDialog('dlg-simcoords');
  dlgLat.focus();
}
document.getElementById('dlg-cancel').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); closeDialog(); });
document.getElementById('dlg-ok').addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation();
  const lat = parseFloat(dlgLat.value), lon = parseFloat(dlgLon.value);
  if (Number.isFinite(lat) && Number.isFinite(lon) && Math.abs(lat) <= 90 && Math.abs(lon) <= 180)
    transport.send('sim.setCoords|' + lat + ',' + lon);
  closeDialog();
});

// ---- section bar (Phase 7) — per-section colour strip + manual toggle ----
// Read state (per-section ColorCode + master/manual mode) already rides the Tick;
// each tap cycles one section Off->Auto->On (Tier-2 — gated by control authority on
// both ends). Buttons are (re)built when the section count changes; colours update
// per frame. Delegated click handler reads the section index off data-idx.
const sectionBar = document.getElementById('sectionbar');
sectionBar.addEventListener('pointerdown', e => {
  e.stopPropagation(); // don't pan the map
  const btn = e.target.closest('button[data-idx]');
  if (btn && iHoldControl) transport.send('section.toggle|' + btn.dataset.idx);
});

// ---- bottom nav (Phase 8) — field-tools toolbar + Flags/AB-line flyouts ----
// Direct-action field tools. Tier-2 ids (data-t2: track.* / headland.* / youturn.skip*)
// fire only while we hold control (the host re-gates); the rest are Tier-1. Toggle
// READ state rides the Tick (tick.tools); AB-dependent buttons key off activeTrackName.
const bottomNav = document.getElementById('bottomnav');
const bnFlags = document.getElementById('bn-flyout-flags');
const bnAb = document.getElementById('bn-flyout-ab');
bottomNav.addEventListener('pointerdown', e => {
  e.stopPropagation(); // don't pan the map
  const btn = e.target.closest('button[data-cmd]');
  if (!btn) return;
  if (btn.hasAttribute('data-t2') && !iHoldControl) return; // gated; host re-checks
  transport.send(btn.dataset.cmd);
});
function bnToggleFly(fly, btn) {
  const open = !fly.classList.contains('open');
  bnFlags.classList.remove('open'); bnAb.classList.remove('open');
  document.getElementById('bn-flags').classList.remove('menuopen');
  document.getElementById('bn-abmenu').classList.remove('menuopen');
  if (open) { fly.classList.add('open'); btn.classList.add('menuopen'); }
}
document.getElementById('bn-flags').addEventListener('pointerdown', e => { e.stopPropagation(); bnToggleFly(bnFlags, e.currentTarget); });
document.getElementById('bn-abmenu').addEventListener('pointerdown', e => { e.stopPropagation(); bnToggleFly(bnAb, e.currentTarget); });

// ---- left nav (Phase 9) — config/settings panels via the config bridge ----
// Vertical button bar; each button toggles a non-modal panel (only one open at a
// time). Panels READ from the config frame (or existing frames, e.g. units =
// Status.isMetric) and WRITE via `config.set|key:value` (Tier-1; the host applies it
// to ConfigurationStore). Grows one entry per sub-phase.
// Navigation: top-level buttons open a panel; sub-panels (vehicle/tool config) are
// reached from the hub and carry a Back button. One panel open at a time.
const LN_NAV_PANELS = ['screenalerts', 'vehtoolhub', 'vehiclecfg', 'toolcfg', 'autosteercfg'];
function lnCloseAll() {
  for (const id of LN_NAV_PANELS) document.getElementById(id).classList.remove('open');
  document.getElementById('ln-screenalerts').classList.remove('active');
  document.getElementById('ln-vehicle').classList.remove('active');
  document.getElementById('ln-autosteer').classList.remove('active');
}
function lnOpen(panelId, navBtnId, onOpen) {
  lnCloseAll();
  document.getElementById(panelId).classList.add('open');
  if (navBtnId) document.getElementById(navBtnId).classList.add('active');
  if (onOpen) onOpen();
}
for (const id of LN_NAV_PANELS) document.getElementById(id).addEventListener('pointerdown', e => e.stopPropagation());
document.getElementById('ln-screenalerts').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (document.getElementById('screenalerts').classList.contains('open')) lnCloseAll();
  else lnOpen('screenalerts', 'ln-screenalerts', populateScreenAlerts);
});
document.getElementById('ln-vehicle').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const anyOpen = ['vehtoolhub', 'vehiclecfg', 'toolcfg'].some(id => document.getElementById(id).classList.contains('open'));
  if (anyOpen) lnCloseAll(); else lnOpen('vehtoolhub', 'ln-vehicle', refreshHub);
});
document.getElementById('ln-autosteer').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (document.getElementById('autosteercfg').classList.contains('open')) lnCloseAll();
  else lnOpen('autosteercfg', 'ln-autosteer', () => {
    // Always open collapsed (left pane only), like the native panel.
    asPanel.classList.remove('expanded');
    document.getElementById('as-expand').textContent = '▶ Full';
    populateAutoSteer(true);
  });
});
for (const b of document.querySelectorAll('.ln-back'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('vehtoolhub', 'ln-vehicle', refreshHub); });
// Units (Screen & Alerts) → config bridge write.
const saPanel = document.getElementById('screenalerts');
const uMetric = document.getElementById('sa-metric'), uImperial = document.getElementById('sa-imperial');
for (const b of saPanel.querySelectorAll('.ln-segbtn[data-units]'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('config.set|units:' + b.dataset.units); });
// Screen & Alerts toggles → config.set|display.X; action rows (theme/quality) → command.
for (const b of saPanel.querySelectorAll('.sa-tgl'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); cfgSend(b.dataset.key, b.classList.contains('active') ? '0' : '1'); });
for (const b of saPanel.querySelectorAll('.sa-act'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); transport.send(b.dataset.cmd); });
const saExtra = document.getElementById('sa-extracount');
saExtra.addEventListener('change', () => { const v = parseInt(saExtra.value); if (Number.isFinite(v)) cfgSend('display.extraGuidelinesCount', v); });
function populateScreenAlerts() {
  if (!config || !config.display) return;
  const d = config.display;
  for (const b of saPanel.querySelectorAll('.sa-tgl')) b.classList.toggle('active', !!d[b.dataset.key.split('.')[1]]);
  if (document.activeElement !== saExtra) saExtra.value = d.extraGuidelinesCount;
  document.getElementById('sa-quality').textContent = d.resolutionLabel || '—';
}
// Vehicle config panel — full native VehicleConfigDialog surface (Vehicle/GPS/Roll).
// Generic config controls keyed by data-key="<section>.<field>"; reused by later
// sub-phases. Writes via config.set; Save Profile via profile.save.
const vcPanel = document.getElementById('vehiclecfg'), vcName = document.getElementById('vc-name');
// Hitch-type labels (mirrors ConfigurationViewModel.HitchTypeOptions); value = code =
// index − 1 (code −1 → "Not available").
const HITCH_OPTS = ['Not available', 'Unknown', 'ISO 6489-3 Tractor drawbar',
  'ISO 730 Three-point-hitch semi-mounted', 'ISO 730 Three-point-hitch mounted',
  'ISO 6489-1 Hitch-hook', 'ISO 6489-2 Clevis coupling 40', 'ISO 6489-4 Piton type coupling',
  'ISO 6489-5 CUNA hitch', 'ISO 24347 Ball type hitch', 'Chassis Mounted - Self-Propelled',
  'ISO 5692-2 Pivot wagon hitch'];
const vcHitchSel = vcPanel.querySelector('.cfg-sel[data-key="vehicle.hitchType"]');
HITCH_OPTS.forEach((label, i) => { const o = document.createElement('option'); o.value = i - 1; o.textContent = label; vcHitchSel.appendChild(o); });
const vcFw = vcPanel.querySelector('.cfg-slider[data-key="gps.headingFusionWeight"]');
function cfgGet(key) { if (!config) return undefined; const p = key.split('.'); return config[p[0]] && config[p[0]][p[1]]; }
function cfgSend(key, val) { transport.send('config.set|' + key + ':' + val); }
// Tabs.
for (const t of vcPanel.querySelectorAll('.cfg-tab'))
  t.addEventListener('pointerdown', e => {
    e.stopPropagation();
    for (const b of vcPanel.querySelectorAll('.cfg-tab')) b.classList.toggle('active', b === t);
    for (const body of vcPanel.querySelectorAll('.cfg-body')) body.hidden = (body.id !== t.dataset.tab);
  });
// Generic config controls keyed by data-key="<section>.<field>" — reused by the
// Vehicle and Tool panels. Tab strips, selects, sliders and dynamic lists wire per
// panel. cfg-typebtn active state compares the config value to data-active (falling
// back to data-val) so name-valued buttons (tool type) can match an int field.
// Slider readout formatter (data-fmt): f0/f1/f2 fixed decimals, p0 = value×100 %,
// pct = value %, deg = value °. Used by generic .cfg-slider controls.
function fmtRo(v, fmt) {
  const n = Number(v);
  switch (fmt) {
    case 'f0': return n.toFixed(0);
    case 'f1': return n.toFixed(1);
    case 'f2': return n.toFixed(2);
    case 'p0': return Math.round(n * 100) + '%';
    case 'pct': return Math.round(n) + '%';
    case 'deg': return Math.round(n) + '°';
    default: return String(v);
  }
}
function wireCfgControls(panel) {
  for (const inp of panel.querySelectorAll('.cfg-num'))
    inp.addEventListener('change', () => { const v = parseFloat(inp.value); if (Number.isFinite(v)) cfgSend(inp.dataset.key, v); });
  for (const b of panel.querySelectorAll('.cfg-tgl'))
    b.addEventListener('pointerdown', e => { e.stopPropagation(); cfgSend(b.dataset.key, b.classList.contains('active') ? '0' : '1'); });
  for (const b of panel.querySelectorAll('.cfg-typebtn'))
    b.addEventListener('pointerdown', e => { e.stopPropagation(); cfgSend(b.dataset.key, b.dataset.val); });
  // .cfg-act = config.set action buttons; .rn-gated ones carry data-cmd (a gated
  // command, not a config key) and are wired separately, so exclude them here.
  for (const b of panel.querySelectorAll('.cfg-act:not(.rn-gated)'))
    b.addEventListener('pointerdown', e => { e.stopPropagation(); cfgSend(b.dataset.key, b.dataset.val); });
  for (const sel of panel.querySelectorAll('.cfg-isel'))
    sel.addEventListener('change', () => cfgSend(sel.dataset.key, sel.value));
  // Sliders with a data-fmt are generic: drag updates the readout live; the value
  // commits on release ('change') to avoid flooding the bridge. (Sliders without
  // data-fmt — e.g. the Vehicle heading-fusion slider — are wired by hand.)
  for (const s of panel.querySelectorAll('.cfg-slider[data-fmt]')) {
    const ro = panel.querySelector('.cfg-ro[data-for="' + s.dataset.key + '"]');
    s.addEventListener('input', () => { if (ro) ro.textContent = fmtRo(s.value, s.dataset.fmt); });
    s.addEventListener('change', () => cfgSend(s.dataset.key, s.value));
  }
}
function populateCfgControls(panel, force) {
  for (const inp of panel.querySelectorAll('.cfg-num')) {
    if (!force && document.activeElement === inp) continue;
    const val = cfgGet(inp.dataset.key);
    if (typeof val === 'number') inp.value = Math.round(val * 1000) / 1000;
  }
  for (const b of panel.querySelectorAll('.cfg-tgl')) {
    const on = !!cfgGet(b.dataset.key);
    b.classList.toggle('active', on);
    if (!b.dataset.keepLabel) b.textContent = on ? 'On' : 'Off';
  }
  for (const b of panel.querySelectorAll('.cfg-typebtn'))
    b.classList.toggle('active', String(cfgGet(b.dataset.key)) === (b.dataset.active != null ? b.dataset.active : b.dataset.val));
  for (const s of panel.querySelectorAll('.cfg-slider[data-fmt]')) {
    if (!force && document.activeElement === s) continue;
    const val = cfgGet(s.dataset.key);
    if (typeof val === 'number') {
      s.value = val;
      const ro = panel.querySelector('.cfg-ro[data-for="' + s.dataset.key + '"]');
      if (ro) ro.textContent = fmtRo(val, s.dataset.fmt);
    }
  }
  for (const sel of panel.querySelectorAll('.cfg-isel')) {
    if (document.activeElement === sel) continue;
    const val = cfgGet(sel.dataset.key);
    if (typeof val === 'number') sel.value = String(val);
  }
}
// Tab strip switcher: tabs + bodies share a data-strip id (so nested strips don't
// cross-toggle). Active tab → its data-tab body shown, siblings in the strip hidden.
function wireTabStrip(panel, stripId) {
  for (const t of panel.querySelectorAll('.cfg-tab[data-strip="' + stripId + '"]'))
    t.addEventListener('pointerdown', e => {
      e.stopPropagation();
      for (const b of panel.querySelectorAll('.cfg-tab[data-strip="' + stripId + '"]')) b.classList.toggle('active', b === t);
      for (const body of panel.querySelectorAll('.cfg-body[data-strip="' + stripId + '"]')) body.hidden = (body.id !== t.dataset.tab);
    });
}
wireCfgControls(vcPanel);
vcHitchSel.addEventListener('change', () => cfgSend('vehicle.hitchType', vcHitchSel.value));
vcFw.addEventListener('input', () => { document.getElementById('vc-hfw').textContent = Math.round(vcFw.value * 100) + '%'; cfgSend('gps.headingFusionWeight', vcFw.value); });
document.getElementById('vc-save').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('profile.save'); });
// Populate every control from the config frame. force (on open) fills all; otherwise
// skip the focused number input so we don't clobber what the user is typing.
function populateVehicleCfg(force) {
  if (!config || !config.vehicle) return;
  vcName.textContent = config.vehicle.name || '—';
  populateCfgControls(vcPanel, force);
  // Type-dependent measurement diagrams (mirror VehicleConfig.*ImageSource switches:
  // index by VehicleType 0 Tractor / 1 Harvester / 2 FourWD; '' = no image).
  const ty = Math.max(0, Math.min(2, cfgGet('vehicle.type') | 0));
  const setImg = (id, names) => {
    const el = document.getElementById(id), n = names[ty];
    if (!el) return;
    if (n) { const p = '/icons/' + n + '.png'; if (!el.src.endsWith(p)) el.src = p; el.style.display = ''; }
    else el.style.display = 'none';
  };
  setImg('vc-img-hitch', ['Hitch', '', 'HitchArticulated']);
  setImg('vc-img-wheelbase', ['WheelbaseTractor', 'WheelbaseHarvester', 'WheelbaseArticulated']);
  setImg('vc-img-track', ['TrackWidthTractor', 'TrackWidthHarvester', 'TrackWidthArticulated']);
  setImg('vc-img-antside', ['AntennaTractorTop', 'AntennaHarvesterTop', 'AntennaArticulatedTop']);
  setImg('vc-img-antoffset', ['AntennaTractorOffset', 'AntennaHarvesterOffset', 'AntennaArticulatedOffset']);
  vcHitchSel.value = cfgGet('vehicle.hitchType');
  const w = cfgGet('gps.headingFusionWeight') || 0;
  vcFw.value = w; document.getElementById('vc-hfw').textContent = Math.round(w * 100) + '%';
  // Dual-only fields are live only in Dual GPS mode; reverse detection only in single
  // (mirrors the native enable/disable gating).
  const dual = !!cfgGet('gps.isDualGps');
  const setEn = (key, en) => { const el = vcPanel.querySelector('[data-key="' + key + '"]'); if (el) { el.disabled = !en; el.style.opacity = en ? '1' : '0.4'; } };
  ['gps.dualHeadingOffset', 'gps.dualReverseDistance', 'gps.autoDualFix', 'gps.dualSwitchSpeed'].forEach(k => setEn(k, dual));
  setEn('gps.reverseDetection', !dual);
}

// ---- Tool config panel (full native ToolConfigDialog) ----
const tcPanel = document.getElementById('toolcfg'), tcName = document.getElementById('tc-name');
const tcHitchSel = tcPanel.querySelector('.cfg-sel[data-key="tool.hitchType"]');
HITCH_OPTS.forEach((label, i) => { const o = document.createElement('option'); o.value = i - 1; o.textContent = label; tcHitchSel.appendChild(o); });
const tcSingleColor = document.getElementById('tc-singlecolor');
wireCfgControls(tcPanel);
wireTabStrip(tcPanel, 'tc-top');
wireTabStrip(tcPanel, 'tc-sub');
wireTabStrip(tcPanel, 'tc-mac');
tcHitchSel.addEventListener('change', () => cfgSend('tool.hitchType', tcHitchSel.value));
tcSingleColor.addEventListener('change', () => cfgSend('tool.singleCoverageColor', tcSingleColor.value.slice(1)));
document.getElementById('tc-resetpins').addEventListener('pointerdown', e => { e.stopPropagation(); cfgSend('machine.resetPins', '1'); });
document.getElementById('tc-save').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('profile.save'); });
// PinFunction enum labels (mirror MachineConfig.PinFunction).
const PIN_FUNCS = ['None', 'Sec1', 'Sec2', 'Sec3', 'Sec4', 'Sec5', 'Sec6', 'Sec7', 'Sec8', 'Sec9', 'Sec10',
  'Sec11', 'Sec12', 'Sec13', 'Sec14', 'Sec15', 'Sec16', 'HydUp', 'HydDown', 'TramLeft', 'TramRight', 'GeoStop'];
const _tcBuilt = { sw: -1, ze: -1, sc: false, pins: false };
function tcDynInput(parent, idx, label, type, onChange) {
  const c = document.createElement('div'); c.className = 'tc-cell';
  const sp = document.createElement('span'); sp.textContent = label;
  const inp = document.createElement(type === 'pin' ? 'select' : 'input');
  if (type === 'pin') PIN_FUNCS.forEach((l, fi) => { const o = document.createElement('option'); o.value = fi; o.textContent = l; inp.appendChild(o); });
  else { inp.type = type; if (type === 'number') inp.step = '1'; }
  inp.dataset.idx = idx;
  inp.addEventListener('change', () => onChange(inp));
  c.appendChild(sp); c.appendChild(inp); parent.appendChild(c);
}
function tcShow(name, on) { for (const el of tcPanel.querySelectorAll('[data-show="' + name + '"]')) el.hidden = !on; }
function hex6(v) { return '#' + ((v >>> 0) & 0xFFFFFF).toString(16).padStart(6, '0'); }
function populateToolCfg(force) {
  if (!config || !config.tool) return;
  const t = config.tool;
  tcName.textContent = 'Tool: ' + (profiles ? profiles.activeTool : '—');
  populateCfgControls(tcPanel, force);
  if (document.activeElement !== tcHitchSel) tcHitchSel.value = t.hitchType;
  const hi = document.getElementById('tc-img-hitch');
  const hp = '/icons/' + (['ToolHitchPageFront', 'ToolHitchPageRear', 'ToolHitchPageTBT', 'ToolHitchPageTrailing'][t.type] || 'ToolHitchPageRear') + '.png';
  if (!hi.src.endsWith(hp)) hi.src = hp;
  // Conditional visibility by tool type (0 front, 1 rear, 2 TBT, 3 trailing).
  const trailing = t.type === 3, tbt = t.type === 2, trailtbt = trailing || tbt;
  tcShow('rigid', !trailing); tcShow('trailtbt', trailtbt); tcShow('tbt', tbt); tcShow('notrailtbt', !trailtbt);
  tcShow('individual', t.isSectionsNotZones); tcShow('zones', !t.isSectionsNotZones);
  tcShow('multicolor', t.isMultiColoredSections); tcShow('singlecolor', !t.isMultiColoredSections);
  tcShow('worksw', t.isWorkSwitchEnabled); tcShow('steersw', t.isSteerSwitchEnabled);
  const wsi = document.getElementById('tc-img-worksw');
  wsi.src = '/icons/' + (t.isWorkSwitchActiveLow ? 'SwitchActiveClosed' : 'SwitchActiveOpen') + '.png';
  // Dynamic lists — rebuild on count change, fill values (skip the focused control).
  const nSec = Math.max(1, Math.min(16, t.numSections));
  if (_tcBuilt.sw !== nSec) {
    const g = document.getElementById('tc-sectionwidths'); g.innerHTML = '';
    for (let i = 0; i < nSec; i++) tcDynInput(g, i, 'S' + (i + 1), 'number', inp => { const v = parseFloat(inp.value); if (Number.isFinite(v)) cfgSend('tool.sectionWidth', i + ',' + v); });
    _tcBuilt.sw = nSec;
  }
  for (const inp of document.querySelectorAll('#tc-sectionwidths input')) if (force || document.activeElement !== inp) inp.value = Math.round(t.sectionWidths[+inp.dataset.idx]);
  const nZone = Math.max(1, Math.min(8, t.zones));
  if (_tcBuilt.ze !== nZone) {
    const g = document.getElementById('tc-zoneends'); g.innerHTML = '';
    for (let i = 1; i <= nZone; i++) tcDynInput(g, i, 'Zone ' + i, 'number', inp => { const v = parseInt(inp.value); if (Number.isFinite(v)) cfgSend('tool.zoneEnd', i + ',' + v); });
    _tcBuilt.ze = nZone;
  }
  for (const inp of document.querySelectorAll('#tc-zoneends input')) if (force || document.activeElement !== inp) inp.value = t.zoneRanges[+inp.dataset.idx];
  if (!_tcBuilt.sc) {
    const g = document.getElementById('tc-sectioncolors'); g.innerHTML = '';
    for (let i = 0; i < 16; i++) tcDynInput(g, i, 'S' + (i + 1), 'color', inp => cfgSend('tool.sectionColor', i + ',' + inp.value.slice(1)));
    _tcBuilt.sc = true;
  }
  for (const inp of document.querySelectorAll('#tc-sectioncolors input')) if (document.activeElement !== inp) inp.value = hex6(t.sectionColors[+inp.dataset.idx]);
  if (document.activeElement !== tcSingleColor) tcSingleColor.value = hex6(t.singleCoverageColor);
  if (!_tcBuilt.pins) {
    const g = document.getElementById('tc-pins'); g.innerHTML = '';
    for (let i = 0; i < 24; i++) tcDynInput(g, i, 'Pin ' + (i + 1), 'pin', sel => cfgSend('machine.pin', i + ',' + sel.value));
    _tcBuilt.pins = true;
  }
  for (const sel of document.querySelectorAll('#tc-pins select')) if (document.activeElement !== sel) sel.value = config.machine.pinAssignments[+sel.dataset.idx];
  document.getElementById('tc-totalwidth').textContent = 'Total width: ' + (t.totalWidth || 0).toFixed(2) + ' m';
}

// ---- AutoSteer config panel (Phase 9) — full native 9-tab surface + live Test Mode ----
// Field edits ride the generic config bridge (config.set|autosteer.*). Hardware-push
// actions (Send&Save / Zero-WAS / Reset / free-drive) are gated autosteer.* commands —
// routed through AutoSteerConfigViewModel host-side so PGN/free-drive logic isn't dupd.
const asPanel = document.getElementById('autosteercfg');
const asResetConfirm = document.getElementById('as-resetconfirm');
wireCfgControls(asPanel);
wireTabStrip(asPanel, 'as-left');   // left pane: 4 tabs
wireTabStrip(asPanel, 'as-right');  // right pane: 5 tabs
function hideAsResetConfirm() { asResetConfirm.hidden = true; }
// Generic gated action buttons (Zero-WAS, Send&Save, OK, free-drive) → send only while
// we hold control; the host re-checks (IsRestrictedCommand).
for (const b of asPanel.querySelectorAll('.rn-gated[data-cmd]'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); rnSend(b.dataset.cmd); });
// OK (green check) = Send&Save then close the panel.
document.getElementById('as-apply').addEventListener('pointerdown', e => { e.stopPropagation(); lnCloseAll(); });
document.getElementById('as-close').addEventListener('pointerdown', e => { e.stopPropagation(); lnCloseAll(); });
// Expand / collapse (native IsFullMode): collapsed = left pane only.
document.getElementById('as-expand').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const exp = asPanel.classList.toggle('expanded');
  e.currentTarget.textContent = exp ? '◀ Less' : '▶ Full';
});
// Reset → inline confirm bar (mirrors native ResetToDefaults confirmation).
document.getElementById('as-reset').addEventListener('pointerdown', e => { e.stopPropagation(); asResetConfirm.hidden = false; });
document.getElementById('as-reset-no').addEventListener('pointerdown', e => { e.stopPropagation(); hideAsResetConfirm(); });
document.getElementById('as-reset-yes').addEventListener('pointerdown', e => { e.stopPropagation(); hideAsResetConfirm(); rnSend('autosteer.reset'); });
// Algorithm mode switch (Pure Pursuit ↔ Stanley) — native puts this on the Algorithm
// tab as a single tap-to-toggle button.
document.getElementById('as-modeswitch').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (config && config.autosteer) cfgSend('autosteer.isStanleyMode', config.autosteer.isStanleyMode ? '0' : '1');
});
// Satellite-dialog launchers — defined by their own sub-phases (Smart-WAS / Wizard).
document.getElementById('as-smartwas').addEventListener('pointerdown', e => { e.stopPropagation(); if (typeof openSmartWas === 'function') openSmartWas(); });
document.getElementById('as-wizard').addEventListener('pointerdown', e => { e.stopPropagation(); if (typeof openSteerWizard === 'function') openSteerWizard(); });

function asSetText(id, t) { const el = document.getElementById(id); if (el) el.textContent = t; }
// Dim/disable gated controls when this browser doesn't hold control.
function updateAsGated() {
  for (const el of asPanel.querySelectorAll('.rn-gated')) el.classList.toggle('disabled', !iHoldControl);
}
function populateAutoSteer(force) {
  if (!config || !config.autosteer) return;
  const a = config.autosteer;
  populateCfgControls(asPanel, force);
  // Mode title + tab icon track Pure Pursuit / Stanley.
  asSetText('as-modetitle', a.isStanleyMode ? 'Stanley' : 'Pure Pursuit');
  const modeIc = document.getElementById('as-modetab-ic');
  if (modeIc) modeIc.src = a.isStanleyMode ? '/icons/ModeStanley.png' : '/icons/ModePurePursuit.png';
  const msw = document.getElementById('as-modeswitch');
  if (msw) { msw.textContent = a.isStanleyMode ? 'Stanley' : 'Pure Pursuit'; msw.classList.toggle('stanley', a.isStanleyMode); }
  asSetText('as-wasoffset', a.wasOffset);
  // Conditional sections (data-show) mirror native enable/visibility gating.
  const show = (name, on) => { for (const el of asPanel.querySelectorAll('[data-show="' + name + '"]')) el.style.display = on ? '' : 'none'; };
  show('purepursuit', !a.isStanleyMode);
  show('stanley', a.isStanleyMode);
  show('turnsensor', a.turnSensorEnabled);
  show('pressuresensor', a.pressureSensorEnabled);
  show('currentsensor', a.currentSensorEnabled);
  updateAsGated();
}
// Live steer telemetry — the status bar (Set/Act/Err) + Test-mode angle + free-drive
// button state. Refreshed every status frame.
function renderAutoSteerLive() {
  if (!statusBar) return;
  const set = statusBar.setSteerAngle || 0, act = statusBar.actualSteerAngle || 0;
  asSetText('as-stat-set', set.toFixed(1) + '°');
  asSetText('as-stat-act', act.toFixed(1) + '°');
  asSetText('as-stat-err', (set - act).toFixed(1) + '°');
  asSetText('as-live-angle', act.toFixed(1) + '°');
  const fdBtn = document.getElementById('as-fd-toggle'), fdIc = document.getElementById('as-fd-ic');
  if (fdBtn) {
    const on = !!statusBar.steerFreeDrive;
    fdBtn.classList.toggle('on', on);
    if (fdIc) fdIc.src = on ? '/icons/SteerDriveOn.png' : '/icons/SteerDriveOff.png';
  }
}

// ---- Smart WAS Calibration modal (Phase 9) — launched from the AutoSteer panel.
// Live stats ride the Status frame; Start/Stop/Reset/Apply are gated smartwas.* cmds.
const swBackdrop = document.getElementById('smartwas-backdrop');
function openSmartWas() { swBackdrop.classList.add('open'); populateSmartWas(); }
function closeSmartWas() { swBackdrop.classList.remove('open'); }
swBackdrop.addEventListener('pointerdown', e => { if (e.target === swBackdrop) closeSmartWas(); });
document.querySelector('#smartwas-backdrop .sw-modal').addEventListener('pointerdown', e => e.stopPropagation());
for (const b of swBackdrop.querySelectorAll('.rn-gated[data-cmd]'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); rnSend(b.dataset.cmd); });
document.getElementById('sw-close').addEventListener('pointerdown', e => { e.stopPropagation(); closeSmartWas(); });
function populateSmartWas() {
  if (!statusBar) return;
  const collecting = !!statusBar.swCollecting;
  asSetText('sw-status', collecting ? 'Collecting' : 'Stopped');
  asSetText('sw-samples', (statusBar.swSamples || 0) + ' / 200 min');
  asSetText('sw-mean', (statusBar.swMean || 0).toFixed(2) + '°');
  asSetText('sw-median', (statusBar.swMedian || 0).toFixed(2) + '°');
  asSetText('sw-stddev', (statusBar.swStdDev || 0).toFixed(2) + '°');
  const cpd = (config && config.autosteer && config.autosteer.countsPerDegree) || 0;
  const counts = Math.round((statusBar.swOffsetDeg || 0) * cpd);
  asSetText('sw-offset', (statusBar.swOffsetDeg || 0).toFixed(2) + '° (' + counts + ' counts)');
  asSetText('sw-confidence', Math.round(statusBar.swConfidence || 0) + '%');
  // Button enable: gated by control + per-button state (start when stopped, stop when
  // collecting, apply when a valid calibration exists).
  const gate = (id, ok) => { const el = document.getElementById(id); if (el) el.classList.toggle('disabled', !(iHoldControl && ok)); };
  gate('sw-reset', true);
  gate('sw-start', !collecting);
  gate('sw-stop', collecting);
  gate('sw-apply', !!statusBar.swValid);
}

function renderSettings() {
  if (statusBar) {
    const metric = !!statusBar.isMetric;
    uMetric.classList.toggle('active', metric);
    uImperial.classList.toggle('active', !metric);
  }
  // Re-read the open config panel(s) when a fresh config frame arrives.
  if (configDirty) {
    configDirty = false;
    if (vcPanel.classList.contains('open')) populateVehicleCfg(false);
    if (tcPanel.classList.contains('open')) populateToolCfg(false);
    if (saPanel.classList.contains('open')) populateScreenAlerts();
    if (asPanel.classList.contains('open')) populateAutoSteer(false);
  }
  // AutoSteer live telemetry rides every status frame (not just config changes).
  if (asPanel.classList.contains('open')) renderAutoSteerLive();
  // Smart-WAS stats refresh while its modal is open.
  if (swBackdrop.classList.contains('open')) populateSmartWas();
  // Re-read the hub when a fresh profiles frame arrives.
  if (profilesDirty) { profilesDirty = false; if (document.getElementById('vehtoolhub').classList.contains('open')) refreshHub(); }
}

// ---- Vehicle & Tool picker hub (Phase 9) — mirrors LoadVehicleToolDialogViewModel ----
const HUB = {
  summary: document.getElementById('vth-summary'),
  curVeh: document.getElementById('vth-curveh'), curTool: document.getElementById('vth-curtool'),
  vehList: document.getElementById('vth-vehlist'), toolList: document.getElementById('vth-toollist'),
  vehPrev: document.getElementById('vth-vehprev'), toolPrev: document.getElementById('vth-toolprev'),
  vehName: document.getElementById('vth-vehname'), toolName: document.getElementById('vth-toolname'),
  status: document.getElementById('vth-status'), load: document.getElementById('vth-load'),
};
function hubSend(cmd, arg) { transport.send(arg == null ? cmd : cmd + '|' + arg); }
function fillHubList(sel, entries, activeName) {
  const prev = sel.value;
  sel.innerHTML = '';
  for (const e of entries) {
    const o = document.createElement('option');
    o.value = e.name; o.textContent = e.name + (e.name === activeName ? '  ●' : '');
    sel.appendChild(o);
  }
  sel.value = entries.some(e => e.name === prev) ? prev : activeName;
}
function hubPreview(entries, name) { const e = entries.find(x => x.name === name); return e ? e.preview : ''; }
function hubUpdatePreviews() {
  if (!profiles) return;
  HUB.vehPrev.textContent = hubPreview(profiles.vehicles, HUB.vehList.value);
  HUB.toolPrev.textContent = hubPreview(profiles.tools, HUB.toolList.value);
  HUB.summary.textContent = 'Selected: ' + (HUB.vehList.value || profiles.activeVehicle) +
    ' / ' + (HUB.toolList.value || profiles.activeTool);
  HUB.load.disabled = (HUB.vehList.value === profiles.activeVehicle && HUB.toolList.value === profiles.activeTool);
}
function refreshHub() {
  if (!profiles) return;
  HUB.curVeh.textContent = 'Current: ' + profiles.activeVehicle;
  HUB.curTool.textContent = 'Current: ' + profiles.activeTool;
  fillHubList(HUB.vehList, profiles.vehicles, profiles.activeVehicle);
  fillHubList(HUB.toolList, profiles.tools, profiles.activeTool);
  hubUpdatePreviews();
}
HUB.vehList.addEventListener('change', () => { HUB.vehName.value = HUB.vehList.value; hubUpdatePreviews(); });
HUB.toolList.addEventListener('change', () => { HUB.toolName.value = HUB.toolList.value; hubUpdatePreviews(); });
for (const b of document.querySelectorAll('#vehtoolhub .hub-cbtn'))
  b.addEventListener('pointerdown', e => {
    e.stopPropagation();
    const kind = b.dataset.kind, act = b.dataset.act;
    const list = kind === 'vehicle' ? HUB.vehList : HUB.toolList;
    const nameInput = kind === 'vehicle' ? HUB.vehName : HUB.toolName;
    if (act === 'new') {
      const nm = (nameInput.value || '').trim();
      if (!nm) { HUB.status.textContent = 'Type a name, then New'; return; }
      hubSend('profile.new', kind + '\t' + nm); HUB.status.textContent = 'Creating ' + nm + '…';
    } else if (act === 'delete') {
      const nm = list.value;
      if (nm && confirm('Delete ' + kind + " profile '" + nm + "'?")) hubSend('profile.delete', kind + '\t' + nm);
    } else if (act === 'rename') {
      const old = list.value, nw = (nameInput.value || '').trim();
      if (!old || !nw) { HUB.status.textContent = 'Select a profile, type a new name, then Rename'; return; }
      hubSend('profile.rename', kind + '\t' + old + '\t' + nw);
    } else if (act === 'reset') {
      if (confirm('Reset Default ' + kind + ' profile?')) hubSend('profile.reset', kind);
    }
  });
HUB.load.addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (HUB.load.disabled || !profiles) return;
  hubSend('profile.load', (HUB.vehList.value || profiles.activeVehicle) + '\t' + (HUB.toolList.value || profiles.activeTool));
  lnCloseAll(); // native closes the picker on load
});
document.getElementById('vth-vehconfig').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (HUB.vehList.value) hubSend('profile.configureVehicle', HUB.vehList.value);
  lnOpen('vehiclecfg', 'ln-vehicle', () => populateVehicleCfg(true));
});
document.getElementById('vth-toolconfig').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (HUB.toolList.value) hubSend('profile.configureTool', HUB.toolList.value);
  lnOpen('toolcfg', 'ln-vehicle', () => populateToolCfg(true));
});

// ---- remote actuation control (Phase 2 safety layer) ----
// Take/Release single-holder control + a Tier-2 stub. Only the holder may
// actuate; the holder must heartbeat (presence) or the host revokes it (deadman).
// Control is implicit and by connection order: the first browser to connect is the
// controller (server-assigned); others observe. No take/release UI. A status line
// shows which role this browser has.
const ctlStatus = document.getElementById('ctl-status');
function updateControlUi() {
  iHoldControl = lastControl.held && lastControl.holderId === myClientId;
  if (typeof updateAsGated === 'function') updateAsGated(); // re-gate AutoSteer actions
  if (!ctlStatus) return;
  if (iHoldControl) {
    ctlStatus.textContent = '● Controlling'; ctlStatus.style.color = '#39FF6A';
  } else if (lastControl.held) {
    ctlStatus.textContent = '● Observing — another browser has control'; ctlStatus.style.color = '#ff7a3d';
  } else {
    ctlStatus.textContent = '○ No controller'; ctlStatus.style.color = '#9fb3cc';
  }
}
const controlEl = document.getElementById('control');
if (controlEl) controlEl.addEventListener('pointerdown', e => e.stopPropagation());
// Heartbeat keeps our hold alive while we control; a lapse trips the host deadman
// (disengage autosteer + sections).
setInterval(() => { if (iHoldControl) transport.send('control.presence'); }, 500);
updateControlUi();

// Right-nav toolbar actuation (Phase 3b). Each button sends its Tier-2 command,
// but only while we hold control — the host hub enforces the same gate. No
// per-action confirm: holding control IS the deliberate gate (matches the native
// single-tap), and the deadman + failsafe cover a lost controller.
function rnSend(cmd) { if (iHoldControl) transport.send(cmd); }
// NB: use a direct lookup here, not the RN object — RN is a `const` defined later
// in the render section, so referencing it here would hit the temporal dead zone
// and throw at load (stuck "connecting…").
const rnRoot = document.getElementById('rightnav');
if (rnRoot) {
  rnRoot.addEventListener('pointerdown', e => e.stopPropagation()); // don't pan the map
  const wireRn = (id, cmd) => { const el = document.getElementById(id); if (el) el.addEventListener('click', () => rnSend(cmd)); };
  wireRn('rn-contour', 'contour.toggle');
  wireRn('rn-manual', 'section.manual');
  wireRn('rn-auto', 'section.master');
  wireRn('rn-youturn', 'youturn.toggle');
  wireRn('rn-dir', 'youturn.direction');
  wireRn('rn-uturn-l', 'youturn.manualLeft');
  wireRn('rn-uturn-r', 'youturn.manualRight');
  wireRn('rn-steer', 'autosteer.toggle');
}

// ---- render ----
// True 3D is only meaningful under Skia (CanvasKit M44). When inactive, perspM is
// null and everything uses the plain ortho projection below.
function active3D() { return CK && pitch > 0.001; }
// Build the world→CSS-px screen matrix, mirroring the native camera model
// (SkiaMapControl.BuildPerspectiveScreenMatrix): view = T(0,0,-distance)·Rx(-pitch)
// ·T(-cam), a FOV-PERSP_FOV perspective, then an NDC(-1..1, y-up)→pixel(y-down)
// viewport. distance is derived from pxPerM so pitch=0 matches the ortho scale
// exactly (no jump when tilting in). CanvasKit M44 is column-vector / row-major /
// radians, so multiply right-to-left: T(-cam) acts first.
function buildScreenMatrix() {
  if (!CK) return null;
  const halfW = vw / 2, halfH = vh / 2;
  const tanHalf = Math.tan(PERSP_FOV / 2);
  const distance = vh / (2 * tanHalf * pxPerM); // scale-continuity with ortho at pitch 0
  const near = 1, far = Math.max(5000, distance * 4);
  const view = CK.M44.multiply(
    CK.M44.translated([0, 0, -distance]),
    CK.M44.rotated([1, 0, 0], -pitch),
    CK.M44.rotated([0, 0, 1], -mapRotation), // map rotation (HeadingUp etc.)
    CK.M44.translated([-camE, -camN, 0]));
  // Perspective (column-vector, row-major) — transpose of System.Numerics
  // CreatePerspectiveFieldOfView, so w' = -z.
  const yS = 1 / tanHalf, xS = yS / (vw / vh), nf = 1 / (near - far);
  const proj = [
    xS, 0, 0, 0,
    0, yS, 0, 0,
    0, 0, far * nf, near * far * nf,
    0, 0, -1, 0,
  ];
  const viewport = [
    halfW, 0, 0, halfW,
    0, -halfH, 0, halfH,
    0, 0, 1, 0,
    0, 0, 0, 1,
  ];
  return CK.M44.multiply(viewport, proj, view);
}
function updatePerspective() { perspM = active3D() ? buildScreenMatrix() : null; }
// Near-plane clip a ground segment for the perspective path. perspM's w (bottom row,
// z=0) is positive in front of the camera; CanvasKit's drawLine doesn't clip the
// behind part (it perspective-divides by a negative w → a mirrored ghost line), so
// we clip in world space first. Returns [e1,n1,e2,n2] fully in front, or null.
function clipNear(e1, n1, e2, n2) {
  const M = perspM, EPS = 1.0; // matches the projection near plane
  const w1 = M[12] * e1 + M[13] * n1 + M[15];
  const w2 = M[12] * e2 + M[13] * n2 + M[15];
  if (w1 >= EPS && w2 >= EPS) return [e1, n1, e2, n2];
  if (w1 < EPS && w2 < EPS) return null;
  const t = (EPS - w1) / (w2 - w1);
  const ce = e1 + (e2 - e1) * t, cn = n1 + (n2 - n1) * t;
  return w1 < EPS ? [ce, cn, e2, n2] : [e1, n1, ce, cn];
}
// Map-mode auto-pan: keep the vehicle inside a centred safe zone, smoothing the
// camera toward it (snap if it leaves the viewport). Mirrors native ApplyAutoPan
// (rotation 0 in Map mode). World units; rotation 0.
function applyAutoPan(rp) {
  const viewHalfW = (vw / 2) / pxPerM, viewHalfH = (vh / 2) / pxPerM;
  const relE = rp.e - camE, relN = rp.n - camN;
  if (Math.abs(relE) > viewHalfW || Math.abs(relN) > viewHalfH) { camE = rp.e; camN = rp.n; return; }
  const safeW = viewHalfW * AUTO_PAN_SAFE, safeH = viewHalfH * AUTO_PAN_SAFE;
  let panE = 0, panN = 0;
  if (relE > safeW) panE = relE - safeW; else if (relE < -safeW) panE = relE + safeW;
  if (relN > safeH) panN = relN - safeH; else if (relN < -safeH) panN = relN + safeH;
  camE += panE * AUTO_PAN_SMOOTH; camN += panN * AUTO_PAN_SMOOTH;
}
// Per-frame camera update (run once, from skFrame()): apply the follow mode (camera
// position + map rotation), precompute the screen rotation, rebuild perspM.
function updateCamera() {
  const rp = renderPose();
  let target = mapRotation; // Free (mode 2): hold rotation
  if (rp) {
    if (cameraMode === 0) { camE = rp.e; camN = rp.n; target = 0; }                // NorthUp
    else if (cameraMode === 1) { camE = rp.e; camN = rp.n; target = -rp.heading; } // HeadingUp
    else if (cameraMode === 3) { target = 0; applyAutoPan(rp); }                   // Map
  } else if (cameraMode !== 2) target = 0;
  // Ease toward the target along the shortest angular path — turns the 10 Hz
  // heading steps (HeadingUp) into continuous rotation.
  let d = target - mapRotation;
  d -= 2 * Math.PI * Math.round(d / (2 * Math.PI));
  mapRotation += d * ROT_SMOOTH;
  const rR = -mapRotation;
  _cosRR = Math.cos(rR); _sinRR = Math.sin(rR);
  updatePerspective();
  return rp;
}
// Apply perspM (row-major, M·v) to a ground point (e,n,0,1); perspective divide.
function applyM(M, e, n) {
  const x = M[0] * e + M[1] * n + M[3];
  const y = M[4] * e + M[5] * n + M[7];
  const w = M[12] * e + M[13] * n + M[15];
  return [x / w, y / w];
}
function w2s(e, n) {
  if (perspM) return applyM(perspM, e, n);
  // 2D ortho with map rotation (screen rotation = −mapRotation; _cosRR/_sinRR
  // precomputed per frame in updateCamera). Reduces to the old north-up form at
  // rotation 0.
  const rE = e - camE, rN = n - camN;
  const x = rE * _cosRR - rN * _sinRR, y = rE * _sinRR + rN * _cosRR;
  return [vw / 2 + x * pxPerM, vh / 2 - y * pxPerM];
}
// On-screen heading at a ground point: the heading direction projected through the
// current matrix (so the vehicle marker aligns with the tilted ground, not screen
// up). Returns the world heading unchanged when top-down.
function screenHeading(e, n, h) {
  // Project a short step along the heading and read the on-screen angle. Works for
  // 2D (now rotation-aware via w2s) and 3D; returns h when north-up + top-down.
  const a = w2s(e, n), b = w2s(e + Math.sin(h) * 3, n + Math.cos(h) * 3);
  return Math.atan2(b[0] - a[0], -(b[1] - a[1])); // 0 = up, clockwise
}
// Section display palette by ColorCode — matches the native
// SectionColorCodeToBackgroundConverter exactly.
const SECTION_COLORS = [
  'rgba(242,51,51,0.85)',  // 0 off          (red)
  'rgba(247,247,0,0.9)',   // 1 manual on    (yellow)
  'rgba(0,242,0,0.85)',    // 2 auto on      (green)
  'rgba(0,222,222,0.85)',  // 3 turning off  (cyan)
  'rgba(255,165,0,0.9)',   // 4 turning on   (orange)
  'rgba(150,150,150,0.5)', // 5 auto off     (gray)
];
// Tool/section footprint: each section is a bar from its left to right edge,
// perpendicular to the tool heading, green when on / grey when off. Section
// spans come from the Scene (static layout); the tool pose comes from the Tick.
// Transform matches SectionControlService.GetSectionWorldPosition:
//   edge = (toolE,toolN) + (sin,cos)(toolHeading + π/2) * span.
// Dead-reckon the tool pose between ticks (same scheme as renderPose for the
// vehicle), so the footprint glides with the tractor instead of snapping to each
// 10 Hz tick. Extrapolate along the tool heading at the reported speed.
function renderTool() {
  if (!lastTick || !lastTick.tool) return null;
  let dt = (performance.now() - lastTick.t) / 1000;
  dt = Math.min(Math.max(dt, 0), 0.5);
  const tl = lastTick.tool;
  return {
    e: tl.e + lastTick.speed * Math.sin(tl.heading) * dt,
    n: tl.n + lastTick.speed * Math.cos(tl.heading) * dt,
    heading: tl.heading,
  };
}
// Dead-reckon the pose from the last authoritative tick: extrapolate along the
// heading at the reported speed. dt is clamped so motion freezes (rather than
// flies off) if ticks stall — same spirit as the app's own stale-pose cap.
function renderPose() {
  if (!lastTick) return null;
  let dt = (performance.now() - lastTick.t) / 1000;
  dt = Math.min(Math.max(dt, 0), 0.5);
  return {
    e: lastTick.e + lastTick.speed * Math.sin(lastTick.heading) * dt,
    n: lastTick.n + lastTick.speed * Math.cos(lastTick.heading) * dt,
    heading: lastTick.heading,
    speed: lastTick.speed,
  };
}
// Cross-track error as "12 cm R" (R = right of line, +xte). Centimetres so the
// magnitude reads at a glance; the lightbar carries the live feel.
function xteText(xte) {
  if (xte == null) return '—';
  const cm = Math.abs(xte) * 100;
  const side = xte > 0.005 ? 'R' : xte < -0.005 ? 'L' : '·';
  return `${cm.toFixed(0)} cm ${side}`;
}

// Lightbar readout text → DOM overlay (the LED strip itself is drawn by lightbarSk).
// Updated every frame from the latest tick; hidden when guidance is off.
const lbEl = document.getElementById('lb');
function updateLightbarText() {
  if (!tick || !tick.guidanceActive) { lbEl.style.display = 'none'; return; }
  const xte = tick.crossTrackError || 0;
  const onLine = Math.abs(xte) < 0.05;
  const arrow = onLine ? '●' : xte > 0 ? '◀' : '▶'; // arrow = steer direction
  lbEl.textContent = `${arrow} ${(Math.abs(xte) * 100).toFixed(0)} cm   ${tick.lineLabel || ''}`;
  lbEl.style.display = 'block';
}

// Top status bar — DOM overlay mirroring the native StatusBarPanel: a two-line
// left stack (Fix dot + text + Age on top, a rotating Field/Stats/AB-line below
// with a pause toggle), and a right cluster (aggregate Modules dot + per-module
// popup, then big Speed + unit). Updated each frame from the latest StatusDto plus
// the live tick. Element refs cached once.
const SB = {
  bar: document.getElementById('statusbar'),
  pause: document.getElementById('sb-pause'),
  fixDot: document.getElementById('sb-fixdot'), fix: document.getElementById('sb-fix'),
  age: document.getElementById('sb-age'), rot: document.getElementById('sb-rot'),
  speed: document.getElementById('sb-speed'), unit: document.getElementById('sb-unit'),
  hdg: document.getElementById('sb-hdg'),
  modAgg: document.getElementById('sb-modagg'), modBtn: document.getElementById('sb-modbtn'),
  modPop: document.getElementById('sb-modpop'),
  mGps: document.getElementById('sb-gps'), mImu: document.getElementById('sb-imu'),
  mAs: document.getElementById('sb-as'), mMa: document.getElementById('sb-ma'),
  dGps: document.getElementById('sb-gps-d'), dImu: document.getElementById('sb-imu-d'),
  dAs: document.getElementById('sb-as-d'), dMa: document.getElementById('sb-ma-d'),
  // GPS-detail card (Phase 5).
  fixBtn: document.getElementById('sb-fixbtn'), gpsCard: document.getElementById('sb-gpscard'),
  gcLat: document.getElementById('gc-lat'), gcLon: document.getElementById('gc-lon'),
  gcElev: document.getElementById('gc-elev'), gcSats: document.getElementById('gc-sats'),
  gcHdop: document.getElementById('gc-hdop'), gcFix: document.getElementById('gc-fix'),
  gcAge: document.getElementById('gc-age'), gcHdg: document.getElementById('gc-hdg'),
  gcRoll: document.getElementById('gc-roll'), gcFps: document.getElementById('gc-fps'),
};
// GPS fix-quality → dot colour (matches the native FixQualityToColor intent).
function fixColor(q) {
  switch (q) {
    case 4: return '#22c55e';  // RTK fixed — green
    case 5: return '#f59e0b';  // RTK float — amber
    case 2: return '#38bdf8';  // DGPS — blue
    case 1: return '#eab308';  // GPS — yellow
    default: return '#ef4444'; // no/invalid fix — red
  }
}
// Aggregate module colour — green only when every CONFIGURED module is present,
// amber if some, red if none (mirrors MainViewModel.ModuleStatusKind).
function moduleAggColor(s) {
  let conf = 0, pres = 0;
  const tally = (c, ok) => { if (c) { conf++; if (ok) pres++; } };
  tally(s.gpsConf, s.gpsOk); tally(s.imuConf, s.imuOk);
  tally(s.autoSteerConf, s.autoSteerOk); tally(s.machineConf, s.machineOk);
  if (conf === 0 || pres === 0) return '#ef4444';
  return pres === conf ? '#22c55e' : '#f59e0b';
}
// Area/rate formatting — matches MainViewModel.FormatArea / WorkRateDisplay.
function fmtArea(m2, metric) {
  const ha = m2 * 0.0001;
  return metric ? ha.toFixed(2) + ' ha' : (ha * 2.47105).toFixed(2) + ' ac';
}
function fmtRate(haPerHr, metric) {
  return metric ? haPerHr.toFixed(1) + ' ha/hr' : (haPerHr * 2.47105).toFixed(1) + ' ac/hr';
}
// Workable area (m²) from the Scene the client already has: headland polygon if
// present, else the outer boundary (shoelace) — matches WorkableAreaSqM.
function shoelace(pts) {
  let a = 0;
  for (let i = 0; i < pts.length; i++) { const j = (i + 1) % pts.length; a += pts[i].e * pts[j].n - pts[j].e * pts[i].n; }
  return Math.abs(a / 2);
}
function workableAreaSqM() {
  if (scene && scene.headland && scene.headland.length >= 3) return shoelace(scene.headland);
  if (scene && scene.boundaries && scene.boundaries.length && scene.boundaries[0].length >= 3)
    return shoelace(scene.boundaries[0]);
  return 0;
}
// Active track heading (deg, 0 = north) from its first two Scene points.
function activeTrackHeadingDeg() {
  if (!scene || !tick || !tick.activeTrackName) return null;
  const tr = scene.tracks.find(t => t.name === tick.activeTrackName);
  if (!tr || tr.points.length < 2) return null;
  const a = tr.points[0], b = tr.points[1];
  return Math.atan2(b.e - a.e, b.n - a.n) * 180 / Math.PI;
}
// Rotating bottom line — the three native pages (Field / Stats / AB-line).
let sbPage = 0, sbPaused = false;
function rotatingLineText() {
  const s = statusBar; if (!s) return '';
  if (sbPage === 0) { // Field
    if (!scene || !scene.hasField) return 'No field';
    return 'Field: ' + (scene.fieldName || '—') + (s.jobName ? '  Job: ' + s.jobName : '');
  }
  if (sbPage === 1) { // Stats
    if (!scene || !scene.hasField) return '—';
    const workable = workableAreaSqM(), worked = s.workedAreaSqM || 0;
    const leftPct = workable > 0 ? ((workable - worked) * 100 / workable) : 100;
    const haPerHr = (lastTick ? lastTick.speed : 0) * 3600 * toolWidthM() / 10000;
    return 'Done ' + fmtArea(worked, s.isMetric) + '  Left ' + leftPct.toFixed(0) + '%  Rate ' + fmtRate(haPerHr, s.isMetric);
  }
  const name = tick && tick.activeTrackName; // AB-line page
  if (!name) return 'No AB Line';
  const h = activeTrackHeadingDeg();
  if (h == null) return 'AB Line: ' + name;
  const primary = ((h % 360) + 360) % 360, recip = (primary + 180) % 360;
  return 'AB Line: ' + name + '  ' + primary.toFixed(1) + '°, ' + recip.toFixed(1) + '°';
}
setInterval(() => { if (!sbPaused) sbPage = (sbPage + 1) % 3; }, 5000); // native 5 s cycle
SB.pause.addEventListener('click', () => { sbPaused = !sbPaused; SB.pause.textContent = sbPaused ? '▶' : '❚❚'; });
// Bar interaction must not pan the map (the camera listens on window pointerdown).
SB.bar.addEventListener('pointerdown', e => e.stopPropagation());
// Fullscreen toggle — hides the browser tabs/URL bar on tablets. Works on a user
// gesture over plain HTTP (no PWA install needed). Prefixed fallback for older Android.
const fsBtn = document.getElementById('sb-fs');
if (fsBtn) {
  const fsEl = () => document.fullscreenElement || document.webkitFullscreenElement;
  // Use 'click' (not pointerdown): requestFullscreen needs a durable user activation
  // that pointerdown doesn't reliably grant on Android Chrome (entering took 2–3 taps).
  // exitFullscreen needs no activation, so that always worked first tap.
  fsBtn.addEventListener('click', () => {
    if (fsEl()) { (document.exitFullscreen || document.webkitExitFullscreen).call(document); return; }
    const el = document.documentElement, req = el.requestFullscreen || el.webkitRequestFullscreen;
    if (req) { try { const p = req.call(el); if (p && p.catch) p.catch(() => {}); } catch (_) {} }
  });
  fsBtn.addEventListener('pointerdown', e => e.stopPropagation()); // don't also pan the map
  const sync = () => { fsBtn.textContent = fsEl() ? '🗗' : '⛶'; };
  document.addEventListener('fullscreenchange', sync);
  document.addEventListener('webkitfullscreenchange', sync);
}
SB.modBtn.addEventListener('pointerdown', e => {
  e.stopPropagation();
  SB.modPop.style.display = SB.modPop.style.display === 'block' ? 'none' : 'block';
});
// Fix dot toggles the GPS-detail card (mutually exclusive with the modules popup).
SB.fixBtn.addEventListener('pointerdown', e => {
  e.stopPropagation();
  const show = SB.gpsCard.style.display !== 'block';
  SB.gpsCard.style.display = show ? 'block' : 'none';
  if (show) SB.modPop.style.display = 'none';
});
addEventListener('pointerdown', () => {
  if (SB.modPop) SB.modPop.style.display = 'none';
  if (SB.gpsCard) SB.gpsCard.style.display = 'none';
  // Close bottom-nav flyouts on any outside click (clicks inside #bottomnav
  // stopPropagation, so they don't reach here).
  const ff = document.getElementById('bn-flyout-flags'), af = document.getElementById('bn-flyout-ab');
  if (ff) ff.classList.remove('open');
  if (af) af.classList.remove('open');
  document.getElementById('bn-flags')?.classList.remove('menuopen');
  document.getElementById('bn-abmenu')?.classList.remove('menuopen');
  if (typeof lnCloseAll === 'function') lnCloseAll(); // close any left-nav panel
});

function renderStatusBar() {
  const s = statusBar;
  if (!s) { SB.bar.style.display = 'none'; return; }
  SB.bar.style.display = 'flex';
  SB.fixDot.style.background = fixColor(s.fixQuality);
  SB.fix.textContent = s.fixText || '—';
  SB.age.textContent = 'Age ' + (s.age != null ? s.age.toFixed(1) : '—');
  SB.rot.textContent = rotatingLineText();
  // Speed from the live tick (m/s), formatted per the host's unit preference.
  const mps = lastTick ? lastTick.speed : 0;
  SB.speed.textContent = (mps * (s.isMetric ? 3.6 : 2.236936)).toFixed(1);
  SB.unit.textContent = s.isMetric ? 'km/h' : 'mph';
  // Modules: aggregate dot + per-module popup rows.
  SB.modAgg.style.background = moduleAggColor(s);
  const dot = (el, ok) => { el.style.background = ok ? '#22c55e' : '#6b7280'; };
  dot(SB.mGps, s.gpsOk); dot(SB.mImu, s.imuOk); dot(SB.mAs, s.autoSteerOk); dot(SB.mMa, s.machineOk);
  SB.dGps.textContent = (s.fixText || '—') + (s.sats ? ' (' + s.sats + ' sats)' : '');
  SB.dImu.textContent = s.imuIp || 'Not detected';
  SB.dAs.textContent = s.autoSteerIp || 'Not detected';
  SB.dMa.textContent = s.machineIp || 'Not detected';
  // Heading readout (right of speed) — Tick heading is radians (0 = N, CW).
  const hdgDeg = lastTick ? (lastTick.heading * 180 / Math.PI) : null;
  SB.hdg.textContent = hdgDeg != null ? fmtHdg(hdgDeg) : '—';
  // GPS-detail card — populated only while open (the fix dot toggles it).
  if (SB.gpsCard.style.display === 'block') {
    SB.gcLat.textContent = s.lat != null ? s.lat.toFixed(7) : '—';
    SB.gcLon.textContent = s.lon != null ? s.lon.toFixed(7) : '—';
    SB.gcElev.textContent = s.altitude != null ? s.altitude.toFixed(1) : '—';
    SB.gcSats.textContent = s.sats != null ? s.sats : '—';
    SB.gcHdop.textContent = s.hdop != null ? s.hdop.toFixed(2) : '—';
    SB.gcFix.textContent = s.fixText || '—';
    SB.gcAge.textContent = s.age != null ? s.age.toFixed(1) : '—';
    SB.gcHdg.textContent = hdgDeg != null ? (((hdgDeg % 360) + 360) % 360).toFixed(1) + '°' : '—';
    SB.gcRoll.textContent = (tick && typeof tick.roll === 'number') ? tick.roll.toFixed(1) + '°' : '—';
    SB.gcFps.textContent = fps.toFixed(0);
  }
}
// Heading in AgOpen 000.0° form (constant width). Input degrees, any sign.
function fmtHdg(deg) {
  const d = ((deg % 360) + 360) % 360;
  return d.toFixed(1).padStart(5, '0') + '°';
}
// Simulator bar (Phase 6) — reflect host sim state onto the bar each frame.
function renderSimBar() {
  const s = statusBar;
  if (!s || !simBarOpen) return;
  SIM.enable.classList.toggle('on', !!s.simEnabled);
  SIM.tenx.classList.toggle('on', !!s.sim10x);
  // Steer: reflect the host value unless the user is mid-drag (don't fight them).
  if (!_steerDragging) {
    const deg = s.simSteerAngle || 0;
    SIM.steer.value = deg;
    SIM.steerVal.textContent = deg.toFixed(1) + '°';
  }
  // Speed readout: apply 10× then format per units (mirrors SimulatorSpeedDisplay).
  const eff = (s.simSpeedKph || 0) * (s.sim10x ? 10 : 1);
  SIM.speedVal.textContent = s.isMetric ? eff.toFixed(1) + ' kph' : (eff * 0.621371).toFixed(1) + ' mph';
  // GPS (teleport) only while sim disabled — matches the native IsEnabled binding.
  SIM.gps.disabled = !!s.simEnabled;
  SIM.gps.style.opacity = s.simEnabled ? '0.4' : '1';
}
// Section bar (Phase 7). Rebuild rows when the count changes; split rows like the
// native RebuildSectionRows: ceil(n/16) rows, top rows get the extra when uneven.
let _secCount = -1;
function buildSectionRows(n) {
  sectionBar.innerHTML = '';
  const rows = Math.ceil(n / 16);             // 1..4
  const base = Math.floor(n / rows), rem = n % rows; // first `rem` rows get +1
  let idx = 0;
  for (let r = 0; r < rows; r++) {
    const count = base + (r < rem ? 1 : 0);
    const row = document.createElement('div');
    row.className = 'sec-row';
    for (let k = 0; k < count; k++, idx++) {
      const b = document.createElement('button');
      b.className = 'sec-btn';
      b.dataset.idx = idx;
      b.textContent = idx + 1;                // 1-based section number
      row.appendChild(b);
    }
    sectionBar.appendChild(row);
  }
}
function renderSectionBar() {
  // Native gate: field open AND a master (auto or manual) engaged.
  const secs = tick && tick.sections;
  const visible = !!(scene && scene.hasField && tick && tick.op
    && (tick.op.sectionManual || tick.op.sectionAuto) && secs && secs.length);
  sectionBar.classList.toggle('open', visible);
  if (!visible) { _secCount = -1; return; } // force a rebuild when it reappears
  if (secs.length !== _secCount) { buildSectionRows(secs.length); _secCount = secs.length; }
  for (const b of sectionBar.querySelectorAll('button[data-idx]'))
    b.style.background = SECTION_COLORS[secs[+b.dataset.idx]] || SECTION_COLORS[0];
  sectionBar.classList.toggle('locked', !iHoldControl); // dim when we can't actuate
}
// Bottom nav (Phase 8). Swap icons only on change; reflect Tick toggle state.
function bnIcon(img, name) { const p = '/icons/' + name; if (img && !img.src.endsWith(p)) img.src = p; }
const TRAM_ICONS = ['TramOff.png', 'TramAll.png', 'TramLines.png', 'TramOuter.png'];
function renderBottomNav() {
  const visible = !!(scene && scene.hasField); // native gate: IsFieldOpen
  bottomNav.classList.toggle('open', visible);
  if (!visible) { bnFlags.classList.remove('open'); bnAb.classList.remove('open'); return; }
  // AB-line-dependent buttons appear only with an active track.
  const hasTrack = !!(tick && tick.activeTrackName);
  for (const el of bottomNav.querySelectorAll('.bn-abdep')) el.classList.toggle('hide', !hasTrack);
  const t = (tick && tick.tools) || {};
  document.getElementById('bn-skipnum').textContent = t.skipRows || 0;
  bnIcon(document.getElementById('bn-skip-ic'), t.skipRowsOn ? 'YouSkipOn.png' : 'YouSkipOff.png');
  // Icon shows the PAINTING state, not the raw flag: sectionInHeadland (=Tool.
  // IsHeadlandSectionControl) true means sections AUTO-OFF in the headland (NOT
  // painted) → the "Off" icon; false means sections paint the headland → "On".
  bnIcon(document.getElementById('bn-secheadland-ic'), t.sectionInHeadland ? 'HeadlandSectionOff.png' : 'HeadlandSectionOn.png');
  bnIcon(document.getElementById('bn-headland-ic'), t.headlandOn ? 'HeadlandOn.png' : 'HeadlandOff.png');
  bnIcon(document.getElementById('bn-tram-ic'), TRAM_ICONS[t.tramMode || 0]);
  document.getElementById('bn-autotrack').classList.toggle('active', !!t.autoTrack);
  bottomNav.classList.toggle('locked', !iHoldControl);
}

// Right-nav operational toolbar (Phase 3a: read-only live indicators; 3b wires the
// Tier-2 commands). Colour-coded from the Tick's operational state.
const RN = {
  root: document.getElementById('rightnav'),
  contourI: document.getElementById('rn-contour-i'), manualI: document.getElementById('rn-manual-i'),
  autoI: document.getElementById('rn-auto-i'), youturnI: document.getElementById('rn-youturn-i'),
  dir: document.getElementById('rn-dir'), dirArrow: document.getElementById('rn-dir-arrow'),
  dirDist: document.getElementById('rn-dir-dist'), manualTurn: document.getElementById('rn-manualturn'),
  steerI: document.getElementById('rn-steer-i'), readonly: document.getElementById('rn-readonly'),
};
// Swap an icon only when it actually changes (no per-frame churn).
function rnIcon(img, name) { if (img && !img.src.endsWith(name)) img.src = '/icons/' + name; }
function renderRightNav() {
  if (!RN.root) return;
  const op = tick && tick.op;
  if (!op || !scene || !scene.hasField) { RN.root.style.display = 'none'; return; }
  RN.root.style.display = 'flex';
  // Dim + hint when we don't hold control (buttons are no-ops then; the host also gates).
  RN.root.classList.toggle('locked', !iHoldControl);
  if (RN.readonly) RN.readonly.textContent = iHoldControl ? '' : 'observing';
  // State carried by the icon image (native uses the same On/Off/Gray PNGs).
  rnIcon(RN.contourI, op.contour ? 'ContourOn.png' : 'ContourOff.png');
  rnIcon(RN.manualI, op.sectionManual ? 'ManualOn.png' : 'ManualOff.png');
  rnIcon(RN.autoI, op.sectionAuto ? 'SectionMasterOn.png' : 'SectionMasterOff.png');
  rnIcon(RN.youturnI, op.youturn ? 'YouTurnYes.png' : 'YouTurnNo.png');
  // U-turn direction + distance-to-trigger — shown only when auto U-turn is on.
  RN.dir.style.display = op.youturn ? '' : 'none';
  RN.dirArrow.textContent = op.turnLeft ? '↰' : '↱';
  RN.dirDist.textContent = op.distToTrigger > 0 ? op.distToTrigger.toFixed(0) + ' m' : '';
  // Manual U-turn buttons — visible while steering on a non-closed track (native rule).
  RN.manualTurn.style.display = (op.autoSteer && !op.trackClosed) ? 'flex' : 'none';
  // AutoSteer 3-state icon: grey (no track) / off-ready / on-engaged.
  rnIcon(RN.steerI, !op.autoSteerAvail ? 'AutoSteerGray.png' : op.autoSteer ? 'AutoSteerOn.png' : 'AutoSteerOff.png');
}

// ---- Lower-right cluster (Phase 4): roll gauge + camera/mode pad + clock ----
const rollBar = document.getElementById('roll-bar');
const rollDeg = document.getElementById('roll-deg');
function renderRoll() {
  const r = (tick && typeof tick.roll === 'number') ? tick.roll : 0;
  if (rollBar) rollBar.setAttribute('transform', 'rotate(' + r.toFixed(2) + ' 100 40)');
  if (rollDeg) rollDeg.textContent = r.toFixed(1);
}
const cpMode = document.getElementById('cp-mode');
function camModeLabel() { return cameraMode === 1 ? 'H' : cameraMode === 0 ? 'N' : cameraMode === 3 ? 'M' : 'C'; }
function renderCampad() {
  if (!cpMode) return;
  cpMode.textContent = camModeLabel();          // H / N / M / C (native labels)
  cpMode.classList.toggle('free', cameraMode === 2);
}
// Camera pad drives the client-owned camera (pitch / zoom / follow) — no host commands.
(function wireCampad() {
  const pad = document.getElementById('campad');
  if (!pad) return;
  pad.addEventListener('pointerdown', e => e.stopPropagation()); // don't pan the map
  document.getElementById('cp-tiltup').addEventListener('click', () => { pitch = Math.max(0, pitch - PITCH_STEP); });
  document.getElementById('cp-tiltdown').addEventListener('click', () => {
    pitch = Math.min(MAX_PITCH, pitch + PITCH_STEP);
  });
  document.getElementById('cp-zoomin').addEventListener('click', () => { pxPerM = Math.min(200, pxPerM * 1.2); });
  document.getElementById('cp-zoomout').addEventListener('click', () => { pxPerM = Math.max(0.2, pxPerM * 0.83); });
  // Center: cycle the four native modes H → N → M → C → H; recenter on follow modes.
  document.getElementById('cp-mode').addEventListener('click', () => {
    cameraMode = cameraMode === 1 ? 0 : cameraMode === 0 ? 3 : cameraMode === 3 ? 2 : 1;
    if (cameraMode !== 2) { const rp = renderPose(); if (rp) { camE = rp.e; camN = rp.n; } }
  });
})();
// Clock — browser-local 24h HH:MM:SS.
const clockEl = document.getElementById('clock');
function tickClock() {
  if (!clockEl) return;
  const d = new Date(), p = n => String(n).padStart(2, '0');
  clockEl.textContent = p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds());
}
setInterval(tickClock, 1000); tickClock();

// Reference grid + origin axes (procedural, client-side — no wire). Spacing is a
// tool-width multiple on a 1-2-5 series keyed to zoom, matching the native grid
// (#417). Axes through the field origin (local 0,0): X (N=0) red, Y (E=0) green.
// Matches native NiceStep125 exactly: ≤1 → 1, then 2, 5, 10, 20 … (no 1.5 step).
function niceStep125(x) {
  if (x <= 1) return 1;
  const pow = Math.pow(10, Math.floor(Math.log10(x)));
  const f = x / pow;
  return (f <= 2 ? 2 : f <= 5 ? 5 : 10) * pow;
}
function toolWidthM() {
  const ts = scene && scene.toolSections;
  if (ts && ts.length) {
    let lo = Infinity, hi = -Infinity;
    for (const s of ts) { if (s.left < lo) lo = s.left; if (s.right > hi) hi = s.right; }
    if (hi > lo) return hi - lo;
  }
  return 6;
}
// HUD text (DOM). Built each frame from the latest tick/scene.
function updateHud(rp) {
  const spd = rp ? (rp.speed * 3.6).toFixed(1) + ' km/h' : '—';
  // "on" = codes 1 (manual on) / 2 (auto on) / 3 (turning off, still flowing).
  const secs = tick && tick.sections
    ? tick.sections.filter(c => c >= 1 && c <= 3).length + '/' + tick.sections.length : '—';
  const guid = tick && tick.guidanceActive
    ? `guidance: ${tick.lineLabel || '—'}  xte: ${xteText(tick.crossTrackError)}`
    : 'guidance: off';
  hud.textContent =
    `${connState}\n` +
    (scene ? `field: ${scene.fieldName}\nboundaries: ${scene.boundaries.length}  tracks: ${scene.tracks.length}\n` : 'waiting for scene…\n') +
    `speed: ${spd}   sections on: ${secs}\n${guid}\nzoom: ${pxPerM.toFixed(1)} px/m   cam: ${camModeLabel()}  (DR)\n` +
    `coverage: ${cov ? `${cov.width}x${cov.height} @ ${cov.cellSize.toFixed(2)}m, ${covCells} cells` : '—'}\n` +
    `imagery: ${imageryImg ? 'loaded' : imageryRect ? 'loading…' : 'none'}\n` +
    `canvaskit: ${ckStatus}   tilt: ${(pitch * 180 / Math.PI).toFixed(0)}°`;
}

// ---- Skia (CanvasKit) render — Phase A: vector layers at parity. Works in CSS
//      px (canvas.scale(dpr)), reusing w2s. Coverage/imagery/tool/lightbar TODO. ----
// CanvasKit 0.41: Path is immutable (no moveTo/reset) — build via MakeFromCmds
// (flat [VERB,x,y,...]) for polylines; drawLine for single segments.
function strokePtsSk(canvas, pts, close, paint) {
  if (!pts || pts.length < 2) return;
  if (perspM) { strokePtsSk3D(canvas, pts, close, paint); return; }
  const cmds = [];
  for (let i = 0; i < pts.length; i++) {
    const xy = w2s(pts[i].e, pts[i].n);
    cmds.push(i === 0 ? CK.MOVE_VERB : CK.LINE_VERB, xy[0], xy[1]);
  }
  if (close) cmds.push(CK.CLOSE_VERB);
  const path = CK.Path.MakeFromCmds(cmds);
  if (path) { canvas.drawPath(path, paint); path.delete(); }
}
// Perspective path for strokePtsSk: a vertex behind the tilted camera (w < EPS)
// projects through w2s with a negative w → a mirrored ghost segment (the same bug
// clipNear() fixes for single grid segments). So walk the polyline in WORLD space,
// split it at every near-plane crossing, and stroke each continuous front-facing run
// in screen space — only ever feeding w2s points that are in front of the camera.
function strokePtsSk3D(canvas, pts, close, paint) {
  const M = perspM, EPS = 1.0;
  const n = pts.length;
  const wOf = (p) => M[12] * p.e + M[13] * p.n + M[15];
  // World point where segment a→b crosses the near plane (a, b straddle it).
  const cross = (a, b, wa, wb) => {
    const t = (EPS - wa) / (wb - wa);
    return [a.e + (b.e - a.e) * t, a.n + (b.n - a.n) * t];
  };
  let run = [];
  const flush = () => {
    if (run.length >= 2) {
      const cmds = [];
      for (let i = 0; i < run.length; i++)
        cmds.push(i === 0 ? CK.MOVE_VERB : CK.LINE_VERB, run[i][0], run[i][1]);
      const path = CK.Path.MakeFromCmds(cmds);
      if (path) { canvas.drawPath(path, paint); path.delete(); }
    }
    run = [];
  };
  const segCount = close ? n : n - 1;
  for (let i = 0; i < segCount; i++) {
    const a = pts[i], b = pts[(i + 1) % n];
    const wa = wOf(a), wb = wOf(b);
    const aFront = wa >= EPS, bFront = wb >= EPS;
    if (aFront && bFront) {                       // whole segment in front
      if (run.length === 0) run.push(w2s(a.e, a.n));
      run.push(w2s(b.e, b.n));
    } else if (aFront && !bFront) {               // exits behind: clip end, break run
      if (run.length === 0) run.push(w2s(a.e, a.n));
      const c = cross(a, b, wa, wb);
      run.push(w2s(c[0], c[1]));
      flush();
    } else if (!aFront && bFront) {               // enters from behind: start at clip
      flush();
      const c = cross(a, b, wa, wb);
      run.push(w2s(c[0], c[1]));
      run.push(w2s(b.e, b.n));
    } else {                                      // wholly behind: nothing to draw
      flush();
    }
  }
  flush();
}
function segSk(canvas, e1, n1, e2, n2, paint) {
  const a = w2s(e1, n1), b = w2s(e2, n2);
  canvas.drawLine(a[0], a[1], b[0], b[1], paint);
}
// Field flags — filled dot (0.8 m radius like native, min 4 px) + dark outline,
// coloured by the flag's hex. Skips flags behind the tilted camera (near-plane).
function drawFlagsSk(canvas, flags) {
  if (!flags || !flags.length) return;
  const r = Math.max(4, 0.8 * pxPerM);
  for (const fl of flags) {
    if (perspM && (perspM[12] * fl.e + perspM[13] * fl.n + perspM[15]) < 1.0) continue; // behind camera
    const xy = w2s(fl.e, fl.n);
    SKP.flagFill.setColor(ckColor(fl.color || '#FF0000'));
    canvas.drawCircle(xy[0], xy[1], r, SKP.flagFill);
    canvas.drawCircle(xy[0], xy[1], r, SKP.flagOutline);
  }
}
function drawGridSk(canvas) {
  const G = 2000;
  let halfW = (vw / 2) / pxPerM, halfH = (vh / 2) / pxPerM;
  if (perspM) { halfW = Math.max(halfW, 180); halfH = Math.max(halfH, 180); } // tilt sees farther
  if (mapRotation !== 0) { const d = Math.hypot(halfW, halfH); halfW = d; halfH = d; } // cover rotated corners
  const minE = Math.max(camE - halfW, -G), maxE = Math.min(camE + halfW, G);
  const minN = Math.max(camN - halfH, -G), maxN = Math.min(camN + halfH, G);
  if (minE >= maxE || minN >= maxN) return;
  const toolW = toolWidthM();
  // Spacing = tool-width × NiceStep125(viewSpan / (toolW·30)) — same as native.
  const spacing = toolW * niceStep125(Math.max(vw, vh) / pxPerM / (toolW * 30));
  const major = k => k % 10 === 0 ? SKP.gridMajor : SKP.gridMinor;
  const gm = 6; // gridMult = BaseStrokeMult 3 × GridExtraStrokeMult 2

  if (perspM) {
    // 3D: draw in WORLD coords under the perspective matrix so Skia GPU-clips at the
    // near plane. (Per-vertex screen projection garbles any line crossing behind the
    // tilted camera — that's why the vertical lines vanished.) Stroke widths in world
    // metres (native formula: a 0.3 px floor + a 0.05 m world-thickness floor).
    const wpp = 1 / pxPerM;
    SKP.gridMinor.setStrokeWidth(Math.max(0.3 * wpp, 0.05) * gm);
    SKP.gridMajor.setStrokeWidth(Math.max(0.6 * wpp, 0.1) * gm);
    SKP.axisX.setStrokeWidth(Math.max(0.9 * wpp, 0.15) * gm);
    SKP.axisY.setStrokeWidth(Math.max(0.9 * wpp, 0.15) * gm);
    canvas.save();
    canvas.concat(perspM);
    const line = (e1, n1, e2, n2, paint) => {
      const c = clipNear(e1, n1, e2, n2);
      if (c) canvas.drawLine(c[0], c[1], c[2], c[3], paint);
    };
    for (let k = Math.ceil(minE / spacing); k * spacing <= maxE; k++)
      line(k * spacing, minN, k * spacing, maxN, major(k));
    for (let k = Math.ceil(minN / spacing); k * spacing <= maxN; k++)
      line(minE, k * spacing, maxE, k * spacing, major(k));
    if (0 >= minE && 0 <= maxE) line(0, minN, 0, maxN, SKP.axisY);
    if (0 >= minN && 0 <= maxN) line(minE, 0, maxE, 0, SKP.axisX);
    canvas.restore();
    return;
  }
  // 2D top-down: screen-space; the same thickness expressed in CSS px.
  SKP.gridMinor.setStrokeWidth(Math.max(0.3, 0.05 * pxPerM) * gm);
  SKP.gridMajor.setStrokeWidth(Math.max(0.6, 0.1 * pxPerM) * gm);
  SKP.axisX.setStrokeWidth(Math.max(0.9, 0.15 * pxPerM) * gm);
  SKP.axisY.setStrokeWidth(Math.max(0.9, 0.15 * pxPerM) * gm);
  for (let k = Math.ceil(minE / spacing); k * spacing <= maxE; k++)
    segSk(canvas, k * spacing, minN, k * spacing, maxN, major(k));
  for (let k = Math.ceil(minN / spacing); k * spacing <= maxN; k++)
    segSk(canvas, minE, k * spacing, maxE, k * spacing, major(k));
  if (0 >= minE && 0 <= maxE) segSk(canvas, 0, minN, 0, maxN, SKP.axisY);
  if (0 >= minN && 0 <= maxN) segSk(canvas, minE, 0, maxE, 0, SKP.axisX);
}
function vehicleSk(canvas, p) {
  const xy = w2s(p.e, p.n);
  canvas.save();
  canvas.translate(xy[0], xy[1]);
  canvas.rotate(screenHeading(p.e, p.n, p.heading) * 180 / Math.PI, 0, 0); // degrees; ground-aligned under tilt
  if (skTri) canvas.drawPath(skTri, SKP.vehicle);
  canvas.restore();
}
// Background imagery as an SkImage — decoded once from the loaded <img> and
// cached, re-decoded only when the imagery version changes (same trigger as the
// 2D path's <img> swap). drawImageRectOptions with Linear filtering = the 2D
// path's imageSmoothingEnabled=true.
let skImagery = null, skImageryVer = null;
function drawImagerySk(canvas) {
  if (!imageryImg || !imageryRect) return;
  if (skImageryVer !== imageryVer || !skImagery) {
    if (skImagery) skImagery.delete();
    skImagery = CK.MakeImageFromCanvasImageSource(imageryImg);
    skImageryVer = imageryVer;
  }
  if (!skImagery) return;
  const r = imageryRect;
  if (perspM) {
    drawImageWorldSk(canvas, skImagery, r.minE, r.minN, r.maxE, r.maxN, CK.FilterMode.Linear);
    return;
  }
  const tl = w2s(r.minE, r.maxN), br = w2s(r.maxE, r.minN); // (minE,maxN)→(maxE,minN)
  const dest = CK.LTRBRect(tl[0], tl[1], br[0], br[1]);
  const src = CK.LTRBRect(0, 0, skImagery.width(), skImagery.height());
  canvas.drawImageRectOptions(skImagery, src, dest, CK.FilterMode.Linear, CK.MipmapMode.None, null);
}
// Draw a north-up image over a world rect under the perspective matrix. The image
// (top row = high northing) is placed via a px→world affine, then perspM warps it
// — Skia does perspective-correct sampling AND near-plane clipping on the GPU, so
// it stays correct even when part of the rect falls behind the tilted camera.
function drawImageWorldSk(canvas, img, minE, minN, maxE, maxN, filter) {
  const w = img.width(), h = img.height();
  const imgToWorld = [(maxE - minE) / w, 0, minE, 0, -(maxN - minN) / h, maxN, 0, 0, 1];
  canvas.save();
  canvas.concat(perspM);
  canvas.concat(imgToWorld);
  canvas.drawImageOptions(img, 0, 0, filter, CK.MipmapMode.None, null);
  canvas.restore();
}
// Coverage offscreen → SkImage, re-snapshotted only when the cell grid changed
// (cov.dirty). Nearest filtering = the 2D path's imageSmoothingEnabled=false, so
// cells stay crisp instead of blurring when zoomed in. The snapshot lives on the
// cov object so a new coverage-init naturally starts with a fresh (null) image.
function drawCoverageSk(canvas) {
  if (!cov) return;
  if (cov.dirty || !cov.skImg) {
    if (cov.skImg) cov.skImg.delete();
    cov.skImg = CK.MakeImageFromCanvasImageSource(cov.canvas);
    cov.dirty = false;
  }
  if (!cov.skImg) return;
  const cs = cov.cellSize;
  const minE = cov.originE, minN = cov.originN;
  const maxE = cov.originE + cov.width * cs, maxN = cov.originN + cov.height * cs;
  if (perspM) {
    drawImageWorldSk(canvas, cov.skImg, minE, minN, maxE, maxN, CK.FilterMode.Nearest);
    return;
  }
  const tl = w2s(minE, maxN), br = w2s(maxE, minN);
  const dest = CK.LTRBRect(tl[0], tl[1], br[0], br[1]);
  const src = CK.LTRBRect(0, 0, cov.width, cov.height);
  canvas.drawImageRectOptions(cov.skImg, src, dest, CK.FilterMode.Nearest, CK.MipmapMode.None, null);
}
// Tool/section footprint — section bars perpendicular to the (dead-reckoned) tool
// heading, coloured by ColorCode. Same geometry as the 2D toolFootprint().
function toolFootprintSk(canvas) {
  const t = renderTool();
  if (!t || !scene || !scene.toolSections || !scene.toolSections.length) return;
  if (!t.e && !t.n) return;
  const perp = t.heading + Math.PI / 2;
  const ps = Math.sin(perp), pc = Math.cos(perp);
  const secs = (tick && tick.sections) || [];
  for (let i = 0; i < scene.toolSections.length; i++) {
    const span = scene.toolSections[i];
    const a = w2s(t.e + ps * span.left, t.n + pc * span.left);
    const b = w2s(t.e + ps * span.right, t.n + pc * span.right);
    canvas.drawLine(a[0], a[1], b[0], b[1], SKP.section[secs[i]] || SKP.section[5]);
  }
}
// Lightbar — screen-space LED strip, identical logic/geometry to the 2D
// lightbar(). The cm/label text lives in the HUD div (no CanvasKit font bundled),
// so here we draw the LEDs plus a directional arrow triangle. Drawn in CSS px
// (under renderSkia's scale(dpr)), so it must run before canvas.restore().
function lightbarSk(canvas) {
  if (!tick || !tick.guidanceActive) return;
  const xte = tick.crossTrackError || 0;        // + = right of line
  const SEG = 15, W = 18, H = 16, GAP = 4, PER = 0.05;
  const mid = (SEG - 1) / 2;
  const totalW = SEG * (W + GAP) - GAP;
  const x0 = (vw - totalW) / 2, top = 54; // below the top status bar
  const lit = Math.min(Math.round(Math.abs(xte) / PER), mid);
  const onLine = Math.abs(xte) < PER;
  const steerLeft = xte > 0; // lights point the way to STEER (native convention)
  const litCol = steerLeft ? '#ff7a3d' : '#39FF6A';
  const p = SKP.lbFill;
  for (let i = 0; i < SEG; i++) {
    const idx = i - mid;
    const x = x0 + i * (W + GAP);
    let on = false, col = '#1c2230';
    if (idx === 0) { on = onLine; col = on ? '#39FF6A' : '#2a3550'; }
    else if (steerLeft ? (idx < 0 && -idx <= lit) : (idx > 0 && idx <= lit)) { on = true; col = litCol; }
    p.setColor(ckColor(on ? col : '#1c2230'));
    canvas.drawRect(CK.XYWHRect(x, top, W, H), p);
  }
  // Directional arrow under the strip: ◀ steer-left / ▶ steer-right / ● on-line.
  const cx = vw / 2, ay = top + H + 11;
  p.setColor(ckColor(onLine ? '#39FF6A' : litCol));
  if (onLine) {
    canvas.drawCircle(cx, ay, 5, p);
  } else {
    const d = steerLeft ? -1 : 1; // tip points the steer direction
    const tri = CK.Path.MakeFromCmds([
      CK.MOVE_VERB, cx + d * 8, ay,
      CK.LINE_VERB, cx - d * 6, ay - 6,
      CK.LINE_VERB, cx - d * 6, ay + 6,
      CK.CLOSE_VERB,
    ]);
    if (tri) { canvas.drawPath(tri, p); tri.delete(); }
  }
}
function renderSkia(canvas, rp) {
  canvas.clear(ckColor('#0f1115'));
  canvas.save();
  canvas.scale(dpr, dpr); // work in CSS px so w2s + stroke widths match
  drawImagerySk(canvas); // bottom layer
  drawCoverageSk(canvas);
  drawGridSk(canvas);
  if (scene) {
    for (const ring of scene.boundaries) strokePtsSk(canvas, ring, true, SKP.boundary);
    // Headland line shows only when the headland is ON — mirrors the native
    // SetHeadlandVisible gate (IsHeadlandOn). The bottom-nav headland button drives it.
    if (scene.headland && tick && tick.tools && tick.tools.headlandOn)
      strokePtsSk(canvas, scene.headland, true, SKP.headland);
    const activeName = tick ? tick.activeTrackName : null;
    for (const tr of scene.tracks)
      strokePtsSk(canvas, tr.points, false, (activeName && tr.name === activeName) ? SKP.reference : SKP.track);
    if (scene.nextTrack) strokePtsSk(canvas, scene.nextTrack, false, SKP.next);
    if (scene.uTurnPath) strokePtsSk(canvas, scene.uTurnPath, false, SKP.uturn);
    if (scene.guidanceLine) strokePtsSk(canvas, scene.guidanceLine, false, SKP.guidance);
    drawFlagsSk(canvas, scene.flags);
  }
  toolFootprintSk(canvas);
  if (rp) vehicleSk(canvas, rp);
  lightbarSk(canvas); // screen-space overlay, still inside the dpr scale
  canvas.restore();
}
// The single render loop. Window rAF (not surface.requestAnimationFrame) so it
// survives surface recreation on resize and runs even before CanvasKit finishes
// loading (DOM overlays update regardless; the GL map draws once skSurface exists).
function skFrame() {
  // Client render rate over a ~500 ms window (shown on the GPS-detail card).
  const _now = performance.now();
  if (_fpsT0 === 0) _fpsT0 = _now;
  else { _fpsFrames++; const dt = _now - _fpsT0; if (dt >= 500) { fps = _fpsFrames * 1000 / dt; _fpsFrames = 0; _fpsT0 = _now; } }
  const rp = updateCamera(); // follow mode + rotation + perspM, once per frame
  if (skSurface) {
    try { renderSkia(skSurface.getCanvas(), rp); skSurface.flush(); }
    catch (e) { ckStatus = 'render err: ' + (e && e.message || e); }
  }
  // DOM overlays (independent of the GL surface).
  updateLightbarText();
  renderStatusBar();
  renderSimBar();
  renderSectionBar();
  renderBottomNav();
  renderSettings();
  renderRightNav();
  renderRoll();
  renderCampad();
  updateHud(rp);
  requestAnimationFrame(skFrame);
}
skFrame();
