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
let ntripProfiles = null; // Network IO: saved NTRIP profiles + associable fields
let ntripDirty = false;
let fieldOps = null;   // Field Operations: field list + jobs + suggestions + import files
let fieldOpsDirty = false;
let agShare = null;    // AgShare: settings + cloud action status/results
let agShareDirty = false;
let wizard = null;     // Steer Wizard frame (host-driven); null when not open
let wizardDirty = false;
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
  onNtripProfiles(p) { ntripProfiles = p; ntripDirty = true; },
  onFieldOps(f) { fieldOps = f; fieldOpsDirty = true; },
  onAgShare(a) { agShare = a; agShareDirty = true; },
  onWizard(w) { wizard = w; wizardDirty = true; },
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
let _confirmCb = null;
function openDialog(cardId) {
  for (const c of dialogHost.querySelectorAll('.dlg-card')) c.classList.toggle('open', c.id === cardId);
  dialogHost.classList.add('open');
}
function closeDialog() { dialogHost.classList.remove('open'); _confirmCb = null; }
// Shared confirm (unified nav model) — replaces browser confirm() + the native
// ShowConfirmationDialog. Transparent light-dismiss scrim (backdrop tap / Cancel = no
// action); Confirm runs the callback. Hosted in the dialog host (now a transparent leaf).
function showConfirm(title, message, onConfirm) {
  _confirmCb = onConfirm || null;
  document.getElementById('dlgc-title').textContent = title || 'Confirm';
  document.getElementById('dlgc-msg').textContent = message || '';
  openDialog('dlg-confirm');
}
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
document.getElementById('dlgc-cancel').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); closeDialog(); });
document.getElementById('dlgc-ok').addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation();
  const cb = _confirmCb; closeDialog(); if (cb) cb();
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
const LN_NAV_PANELS = ['screenalerts', 'vehtoolhub', 'vehiclecfg', 'toolcfg', 'autosteercfg', 'networkio', 'ntripprofiles', 'ntripeditor', 'smartwas', 'fieldops', 'fieldsandjobs', 'newfield', 'fromexisting', 'isoimport', 'kmlimport', 'resumejob', 'agsettings', 'agupload', 'agdownload'];
// Watch-the-tractor panels opt OUT of the light-dismiss scrim — the map must stay
// interactive (pan/zoom to follow the tractor while capturing). They close only via
// the header (Back / ✕).
const NO_SCRIM = new Set(['smartwas']);
const lnScrim = document.getElementById('ln-scrim');
function lnCloseAll() {
  for (const id of LN_NAV_PANELS) document.getElementById(id).classList.remove('open');
  lnScrim.classList.remove('open');
  document.getElementById('ln-screenalerts').classList.remove('active');
  document.getElementById('ln-vehicle').classList.remove('active');
  document.getElementById('ln-autosteer').classList.remove('active');
  document.getElementById('ln-network').classList.remove('active');
  document.getElementById('ln-fieldops').classList.remove('active');
}
function lnOpen(panelId, navBtnId, onOpen) {
  lnCloseAll();
  document.getElementById(panelId).classList.add('open');
  if (!NO_SCRIM.has(panelId)) lnScrim.classList.add('open'); // transparent light-dismiss
  if (navBtnId) document.getElementById(navBtnId).classList.add('active');
  if (onOpen) onOpen();
}
// Outside tap on the scrim closes the chain and is CONSUMED (stopPropagation keeps the
// map from panning / the background from actuating).
lnScrim.addEventListener('pointerdown', e => { e.stopPropagation(); lnCloseAll(); });
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
document.getElementById('ln-network').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (document.getElementById('networkio').classList.contains('open')) lnCloseAll();
  else lnOpen('networkio', 'ln-network', renderNetworkIo);
});
document.getElementById('ln-fieldops').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const anyOpen = ['fieldops', 'fieldsandjobs', 'newfield'].some(id => document.getElementById(id).classList.contains('open'));
  if (anyOpen) lnCloseAll(); else lnOpen('fieldops', 'ln-fieldops', renderFieldOps);
});
for (const b of document.querySelectorAll('.ln-back'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('vehtoolhub', 'ln-vehicle', refreshHub); });
// Standard header close (X) → close the chain to the map. Tagged .ln-closex so the
// NTRIP chain Back/X buttons (which need parent-aware nav) keep their own handlers.
for (const b of document.querySelectorAll('.ln-closex'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); lnCloseAll(); });
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

// ---- Smart WAS Calibration — chain sub-panel of AutoSteer (watch-the-tractor; no
// scrim, map stays interactive). Live stats ride the Status frame; Start/Stop/Reset/
// Apply are gated smartwas.* cmds. Opening REPLACES AutoSteer; ← Back reopens it
// (sw-back), ✕ → map (sw-x carries .ln-closex → generic close).
const swPanel = document.getElementById('smartwas');
function openSmartWas() { lnOpen('smartwas', 'ln-autosteer', populateSmartWas); }
document.getElementById('sw-back').addEventListener('pointerdown', e => {
  e.stopPropagation();
  lnOpen('autosteercfg', 'ln-autosteer', () => populateAutoSteer(true));
});
for (const b of swPanel.querySelectorAll('.rn-gated[data-cmd]'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); rnSend(b.dataset.cmd); });
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

// ---- Network IO (Phase 9) — modules / scan / subnet / host IPs / NTRIP. Reads ride
// the Status frame; writes are config.set (module-present, Tier-1), net.scan (Tier-1),
// net.subnet (Tier-2 gated), and ntrip.* profile CRUD. The NTRIP editor keeps its
// editing buffer client-side; Save sends every field at once. ----
const nioPanel = document.getElementById('networkio');
nioPanel.addEventListener('pointerdown', e => e.stopPropagation());
for (const cb of nioPanel.querySelectorAll('.nio-chk'))
  cb.addEventListener('change', () => cfgSend(cb.dataset.key, cb.checked ? '1' : '0'));
document.getElementById('nio-scan').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('net.scan'); });
const nioO1 = document.getElementById('nio-o1'), nioO2 = document.getElementById('nio-o2'), nioO3 = document.getElementById('nio-o3');
const nioSubnetBtn = document.getElementById('nio-subnet');
nioSubnetBtn.addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (!iHoldControl) return; // gated; host re-checks
  const o1 = clampOct(nioO1.value), o2 = clampOct(nioO2.value), o3 = clampOct(nioO3.value);
  showConfirm('Change Module Subnet',
    'Set ALL connected modules to subnet ' + o1 + '.' + o2 + '.' + o3 + '.x and restart them?',
    () => transport.send('net.subnet|' + o1 + '.' + o2 + '.' + o3));
});
document.getElementById('nio-ntprofiles').addEventListener('pointerdown', e => { e.stopPropagation(); openNtripProfiles(); });
function clampOct(v) { let n = parseInt(v); if (!Number.isFinite(n)) n = 0; return Math.max(0, Math.min(255, n)); }
function nioDotColor(configured, ok) { return !configured ? '#6b7280' : (ok ? '#22c55e' : '#ef4444'); }
let _lastModuleSubnet = '';
function renderNetworkIo() {
  const s = statusBar; if (!s) return;
  const setMod = (cfgId, dotId, ipId, configured, ok, ip) => {
    const cb = document.getElementById(cfgId);
    if (document.activeElement !== cb) cb.checked = !!configured;
    document.getElementById(dotId).style.background = nioDotColor(configured, ok);
    document.getElementById(ipId).textContent = ip || '—';
  };
  setMod('nio-gps-cfg', 'nio-gps-dot', 'nio-gps-ip', s.gpsConf, s.gpsOk, s.gpsIp);
  setMod('nio-as-cfg', 'nio-as-dot', 'nio-as-ip', s.autoSteerConf, s.autoSteerOk, s.autoSteerIp);
  setMod('nio-ma-cfg', 'nio-ma-dot', 'nio-ma-ip', s.machineConf, s.machineOk, s.machineIp);
  setMod('nio-imu-cfg', 'nio-imu-dot', 'nio-imu-ip', s.imuConf, s.imuOk, s.imuIp);
  // Seed the subnet octets from the detected /24 when it changes (a PGN 203 reply),
  // unless the operator is editing that box — mirrors the native ModuleSubnet binding.
  if (s.moduleSubnet && s.moduleSubnet !== _lastModuleSubnet) {
    _lastModuleSubnet = s.moduleSubnet;
    const p = s.moduleSubnet.split('.');
    if (p.length === 3) {
      if (document.activeElement !== nioO1) nioO1.value = p[0];
      if (document.activeElement !== nioO2) nioO2.value = p[1];
      if (document.activeElement !== nioO3) nioO3.value = p[2];
    }
  }
  if (!nioO1.value && document.activeElement !== nioO1) nioO1.value = 192;
  if (!nioO2.value && document.activeElement !== nioO2) nioO2.value = 168;
  if (!nioO3.value && document.activeElement !== nioO3) nioO3.value = 5;
  document.getElementById('nio-hostips').textContent = s.hostIps || '—';
  document.getElementById('nio-ntrip-dot').style.background = s.ntripConnected ? '#22c55e' : '#6b7280';
  document.getElementById('nio-ntrip-status').textContent = s.ntripStatus || 'Not Connected';
  document.getElementById('nio-ntrip-bytes').textContent = Math.floor((s.ntripBytes || 0) / 1024).toLocaleString() + ' KB';
  nioSubnetBtn.classList.toggle('disabled', !iHoldControl);
}

// NTRIP Profiles — chain sub-panel of Network IO (mirrors NtripProfilesDialogPanel).
// Native chain model: opening REPLACES the parent (lnOpen closes everything else);
// Back reopens the parent fly-out (Network IO); Close → map.
let ntSelId = null;
function openNtripProfiles() { lnOpen('ntripprofiles', 'ln-network', renderNtripList); }
document.getElementById('nt-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('networkio', 'ln-network', renderNetworkIo); });
document.getElementById('nt-x').addEventListener('pointerdown', e => { e.stopPropagation(); lnCloseAll(); });
document.getElementById('nt-add').addEventListener('pointerdown', e => { e.stopPropagation(); openNtripEditor(null); });
document.getElementById('nt-edit').addEventListener('pointerdown', e => { e.stopPropagation(); const p = selectedNtProfile(); if (p) openNtripEditor(p); });
document.getElementById('nt-del').addEventListener('pointerdown', e => {
  e.stopPropagation(); const p = selectedNtProfile();
  if (p) showConfirm('Delete NTRIP Profile', "Delete NTRIP profile '" + p.name + "'?",
    () => { transport.send('ntrip.delete|' + p.id); ntSelId = null; });
});
document.getElementById('nt-default').addEventListener('pointerdown', e => {
  e.stopPropagation(); const p = selectedNtProfile(); if (p) transport.send('ntrip.setDefault|' + p.id);
});
function selectedNtProfile() { return (ntripProfiles && ntripProfiles.profiles.find(p => p.id === ntSelId)) || null; }
function renderNtripList() {
  const list = document.getElementById('nt-list');
  list.innerHTML = '';
  const profs = ntripProfiles ? ntripProfiles.profiles : [];
  if (!profs.length) { list.innerHTML = '<div style="padding:18px;color:#8294ab;text-align:center">No profiles</div>'; return; }
  for (const p of profs) {
    const row = document.createElement('div');
    row.className = 'nio-ntrow' + (p.id === ntSelId ? ' sel' : '');
    row.innerHTML = '<div><div class="nt-name"></div><div class="nt-mount"></div></div>'
      + '<div class="nt-caster"></div><div class="nt-def">' + (p.isDefault ? '★' : '') + '</div>';
    row.querySelector('.nt-name').textContent = p.name;
    row.querySelector('.nt-mount').textContent = p.mountPoint;
    row.querySelector('.nt-caster').textContent = p.casterHost;
    row.addEventListener('pointerdown', ev => { ev.stopPropagation(); ntSelId = p.id; renderNtripList(); });
    list.appendChild(row);
  }
}

// Edit NTRIP Profile — chain sub-panel of the profiles list (mirrors
// NtripProfileEditorPanel). Opening REPLACES the list; Back reopens the list; Close →
// map; Save returns to the list (native NavigateBack). Client-side buffer.
let nteBuf = null, _nteTestActive = false;
function openNtripEditor(p) {
  nteBuf = p
    ? { id: p.id, associatedFields: (p.associatedFields || []).slice() }
    : { id: '', associatedFields: [] };
  document.getElementById('nte-title').textContent = p ? 'Edit NTRIP Profile' : 'New NTRIP Profile';
  document.getElementById('nte-name').value = p ? p.name : 'New Profile';
  document.getElementById('nte-host').value = p ? p.casterHost : '';
  document.getElementById('nte-port').value = p ? p.casterPort : 2101;
  document.getElementById('nte-mount').value = p ? p.mountPoint : '';
  document.getElementById('nte-user').value = p ? p.username : '';
  document.getElementById('nte-pass').value = p ? p.password : '';
  document.getElementById('nte-auto').checked = p ? !!p.autoConnect : true;
  document.getElementById('nte-default').checked = p ? !!p.isDefault : false;
  document.getElementById('nte-teststatus').textContent = '';
  _nteTestActive = false;
  lnOpen('ntripeditor', 'ln-network', renderNteFields);
}
document.getElementById('nte-back').addEventListener('pointerdown', e => { e.stopPropagation(); openNtripProfiles(); });
document.getElementById('nte-x').addEventListener('pointerdown', e => { e.stopPropagation(); lnCloseAll(); });
function renderNteFields() {
  const box = document.getElementById('nte-fields');
  box.innerHTML = '';
  const fields = ntripProfiles ? ntripProfiles.availableFields : [];
  if (!fields.length) { box.innerHTML = '<div style="color:#8294ab;padding:4px">No fields</div>'; return; }
  for (const f of fields) {
    const lbl = document.createElement('label');
    const cb = document.createElement('input'); cb.type = 'checkbox';
    cb.checked = nteBuf.associatedFields.includes(f);
    cb.addEventListener('change', () => {
      if (cb.checked) { if (!nteBuf.associatedFields.includes(f)) nteBuf.associatedFields.push(f); }
      else nteBuf.associatedFields = nteBuf.associatedFields.filter(x => x !== f);
    });
    lbl.appendChild(cb); lbl.appendChild(document.createTextNode(f));
    box.appendChild(lbl);
  }
}
document.getElementById('nte-test').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const host = document.getElementById('nte-host').value.trim();
  const port = document.getElementById('nte-port').value || '2101';
  const mount = document.getElementById('nte-mount').value.trim();
  const user = document.getElementById('nte-user').value, pass = document.getElementById('nte-pass').value;
  const st = document.getElementById('nte-teststatus');
  if (!host) { st.textContent = 'Error: Caster host is required'; return; }
  if (!mount) { st.textContent = 'Error: Mount point is required'; return; }
  _nteTestActive = true;
  st.textContent = 'Testing connection...';
  transport.send('ntrip.test|' + [host, port, mount, user, pass].join('\t'));
});
document.getElementById('nte-save').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (!nteBuf) return;
  const fields = [
    nteBuf.id || '',
    document.getElementById('nte-name').value.trim(),
    document.getElementById('nte-host').value.trim(),
    document.getElementById('nte-port').value || '2101',
    document.getElementById('nte-mount').value.trim(),
    document.getElementById('nte-user').value,
    document.getElementById('nte-pass').value,
    document.getElementById('nte-auto').checked ? '1' : '0',
    document.getElementById('nte-default').checked ? '1' : '0',
    nteBuf.associatedFields.join(','),
  ];
  transport.send('ntrip.save|' + fields.join('\t'));
  openNtripProfiles(); // native SaveNtripProfileCommand ends in NavigateBack → list
});

// ---- Field Operations (Phase 9) — field/job lifecycle. Fly-out launches the
// Fields-and-Jobs chain panel (fields table + jobs + new-job form) and New Field.
// Reads ride the FieldOps frame; writes are field.* commands (host-driven via the real
// StartWorkSessionDialogViewModel). Tier-1 (data management); deletes confirm client-side.
const JOB_STATUS = ['In progress', 'Done', 'Abandoned'];
let fjSelField = null, fjSelJob = null;

// Field Operations fly-out: status pill + Close gating from the live scene/status.
function renderFieldOps() {
  const has = !!(scene && scene.hasField);
  const pill = document.getElementById('fo-current');
  if (has) {
    const fld = (scene && scene.fieldName) || '—';
    const job = statusBar && statusBar.jobName;
    pill.textContent = 'Current: ' + fld + (job ? ' / ' + job : '');
    pill.style.display = 'block';
  } else pill.style.display = 'none';
  document.getElementById('fo-close').classList.toggle('disabled', !has);
}
document.getElementById('fo-fields').addEventListener('pointerdown', e => { e.stopPropagation(); openFieldsAndJobs(); });
document.getElementById('fo-resumelast').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('field.resumeLast'); lnCloseAll(); });
document.getElementById('fo-resumejob').addEventListener('pointerdown', e => { e.stopPropagation(); openResumeJob(); });
document.getElementById('fo-drivein').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('field.driveIn'); lnCloseAll(); });
document.getElementById('fo-close').addEventListener('pointerdown', e => { e.stopPropagation(); if (scene && scene.hasField) { transport.send('field.close'); lnCloseAll(); } });

// Fields-and-Jobs chain panel (mirrors StartWorkSessionDialogPanel).
function openFieldsAndJobs() {
  if (fieldOps && !fjSelField)
    fjSelField = fieldOps.activeField || (fieldOps.fields[0] && fieldOps.fields[0].name) || null;
  lnOpen('fieldsandjobs', 'ln-fieldops', renderFieldsAndJobs);
}
document.getElementById('fj-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('fieldops', 'ln-fieldops', renderFieldOps); });
function fjJobsArr() { return fieldOps ? fieldOps.jobs.filter(j => j.fieldName === fjSelField) : []; }
function renderFieldsAndJobs() {
  const fl = document.getElementById('fj-fieldlist'); fl.innerHTML = '';
  for (const f of (fieldOps ? fieldOps.fields : [])) {
    const row = document.createElement('div');
    row.className = 'fj-frow' + (f.name === fjSelField ? ' sel' : '');
    row.innerHTML = '<span class="fj-fname"></span><span class="fj-fnum"></span><span class="fj-fnum"></span>';
    row.querySelector('.fj-fname').textContent = f.name;
    const nums = row.querySelectorAll('.fj-fnum');
    nums[0].textContent = f.hasDistance ? f.distanceKm.toFixed(1) : '—';
    nums[1].textContent = f.areaHa.toFixed(1);
    row.addEventListener('pointerdown', ev => { ev.stopPropagation(); fjSelField = f.name; fjSelJob = null; renderFieldsAndJobs(); });
    fl.appendChild(row);
  }
  const sel = !!fjSelField;
  document.getElementById('fj-jobside').style.display = sel ? 'block' : 'none';
  document.getElementById('fj-empty').style.display = sel ? 'none' : 'block';
  if (sel) {
    const jl = document.getElementById('fj-joblist'); jl.innerHTML = '';
    for (const j of fjJobsArr()) {
      const row = document.createElement('div');
      row.className = 'fj-jrow' + (j.taskName === fjSelJob ? ' sel' : '');
      row.innerHTML = '<div class="fj-jtop"><span class="fj-jname"></span><span class="fj-jwt"></span><span class="fj-jst"></span></div><div class="fj-jsub"></div>';
      row.querySelector('.fj-jname').textContent = j.taskName;
      row.querySelector('.fj-jwt').textContent = j.workType;
      row.querySelector('.fj-jst').textContent = JOB_STATUS[j.status] || '';
      row.querySelector('.fj-jsub').textContent = 'Last opened: ' + j.lastOpened;
      row.addEventListener('pointerdown', ev => { ev.stopPropagation(); fjSelJob = j.taskName; renderFieldsAndJobs(); });
      jl.appendChild(row);
    }
    const dl = document.getElementById('fj-wtlist'); dl.innerHTML = '';
    for (const w of (fieldOps ? fieldOps.workTypes : [])) { const o = document.createElement('option'); o.value = w; dl.appendChild(o); }
    const tn = document.getElementById('fj-taskname');
    if (!tn.value && document.activeElement !== tn) tn.value = isoDate();
  }
}
function isoDate() { const d = new Date(), p = n => String(n).padStart(2, '0'); return d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate()); }
function isoTime() { const d = new Date(), p = n => String(n).padStart(2, '0'); return p(d.getHours()) + '-' + p(d.getMinutes()); }
// New-job form helpers
document.getElementById('fj-uselast').addEventListener('pointerdown', e => { e.stopPropagation(); const j = fjJobsArr()[0]; if (j) document.getElementById('fj-notes').value = j.notes; });
document.getElementById('fj-date').addEventListener('pointerdown', e => { e.stopPropagation(); const tn = document.getElementById('fj-taskname'); tn.value = (tn.value ? tn.value + '_' : '') + isoDate(); });
document.getElementById('fj-time').addEventListener('pointerdown', e => { e.stopPropagation(); const tn = document.getElementById('fj-taskname'); tn.value = (tn.value ? tn.value + '_' : '') + isoTime(); });
// Footer actions
document.getElementById('fj-openonly').addEventListener('pointerdown', e => { e.stopPropagation(); if (fjSelField) { transport.send('field.openOnly|' + fjSelField); lnCloseAll(); } });
document.getElementById('fj-deletefield').addEventListener('pointerdown', e => {
  e.stopPropagation(); if (!fjSelField) return;
  showConfirm('Delete Field', "Delete field '" + fjSelField + "' and all its jobs?", () => { transport.send('field.deleteField|' + fjSelField); fjSelField = null; fjSelJob = null; });
});
document.getElementById('fj-resumejob').addEventListener('pointerdown', e => { e.stopPropagation(); if (fjSelField && fjSelJob) { transport.send('field.resumeJob|' + fjSelField + '\t' + fjSelJob); lnCloseAll(); } });
document.getElementById('fj-startjob').addEventListener('pointerdown', e => {
  e.stopPropagation(); if (!fjSelField) return;
  const wt = document.getElementById('fj-worktype').value.trim();
  const notes = document.getElementById('fj-notes').value;
  const task = document.getElementById('fj-taskname').value.trim();
  transport.send('field.startJob|' + [fjSelField, wt, notes, task].join('\t'));
  lnCloseAll();
});
document.getElementById('fj-deletejob').addEventListener('pointerdown', e => {
  e.stopPropagation(); if (!fjSelField || !fjSelJob) return;
  const job = fjSelJob;
  showConfirm('Delete Job', "Delete job '" + job + "' from field '" + fjSelField + "'?", () => { transport.send('field.deleteJob|' + fjSelField + '\t' + job); fjSelJob = null; });
});
// New Field chain panel (Creation column)
document.getElementById('fj-newfield').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('newfield', 'ln-fieldops', () => { document.getElementById('nf-name').value = ''; renderNewField(); }); });
document.getElementById('fj-fromexisting').addEventListener('pointerdown', e => { e.stopPropagation(); openFromExisting(); });
document.getElementById('fj-iso').addEventListener('pointerdown', e => { e.stopPropagation(); openImport('isoimport', 'iso-file', 'isoFiles'); });
document.getElementById('fj-kml').addEventListener('pointerdown', e => { e.stopPropagation(); openImport('kmlimport', 'kml-file', 'kmlFiles'); });
// Show the origin the field will be created at — the live GPS, or the host's
// no-fix fallback (40.7128, -74.0060) so the readout matches what gets written.
function renderNewField() {
  const hasFix = statusBar && (statusBar.lat || statusBar.lon);
  const lat = hasFix ? statusBar.lat : 40.7128, lon = hasFix ? statusBar.lon : -74.0060;
  document.getElementById('nf-pos').textContent = lat.toFixed(8) + ', ' + lon.toFixed(8);
  document.getElementById('nf-poshint').textContent = hasFix
    ? 'The field is created at this position.'
    : 'No GPS fix — a default origin will be used.';
}
document.getElementById('nf-back').addEventListener('pointerdown', e => { e.stopPropagation(); openFieldsAndJobs(); });
document.getElementById('nf-create').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const name = document.getElementById('nf-name').value.trim();
  if (!name) return;
  transport.send('field.new|' + name);
  fjSelField = name;
  openFieldsAndJobs(); // back to the list; the new field appears once the frame updates
});

// Helper: fill a <select> with options from a string array (preserving the choice).
function fillSelect(sel, items) {
  const prev = sel.value;
  sel.innerHTML = '';
  for (const it of items) { const o = document.createElement('option'); o.value = it; o.textContent = it; sel.appendChild(o); }
  if (items.includes(prev)) sel.value = prev;
}

// From Existing — clone a field (source + new name + copy toggles).
document.getElementById('fe-back').addEventListener('pointerdown', e => { e.stopPropagation(); openFieldsAndJobs(); });
for (const b of document.querySelectorAll('#fromexisting .cfg-tgl'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); b.classList.toggle('active'); });
function openFromExisting() {
  lnOpen('fromexisting', 'ln-fieldops', () => {
    fillSelect(document.getElementById('fe-source'), (fieldOps ? fieldOps.fields : []).map(f => f.name));
    document.getElementById('fe-name').value = '';
    for (const b of document.querySelectorAll('#fromexisting .cfg-tgl')) b.classList.add('active'); // all copy on by default
  });
}
document.getElementById('fe-create').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const src = document.getElementById('fe-source').value;
  const name = document.getElementById('fe-name').value.trim();
  if (!src || !name) return;
  const on = id => document.getElementById(id).classList.contains('active') ? '1' : '0';
  transport.send('field.fromExisting|' + [src, name, on('fe-flags'), on('fe-mapping'), on('fe-headland'), on('fe-lines')].join('\t'));
  fjSelField = name; openFieldsAndJobs();
});

// From ISO-XML / From KML — import a field from a file in the Import folder.
function openImport(panelId, selId, fileArr) {
  lnOpen(panelId, 'ln-fieldops', () => {
    fillSelect(document.getElementById(selId), fieldOps ? fieldOps[fileArr] : []);
    document.getElementById(panelId === 'isoimport' ? 'iso-name' : 'kml-name').value = '';
  });
}
document.getElementById('iso-back').addEventListener('pointerdown', e => { e.stopPropagation(); openFieldsAndJobs(); });
document.getElementById('kml-back').addEventListener('pointerdown', e => { e.stopPropagation(); openFieldsAndJobs(); });
document.getElementById('iso-create').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const file = document.getElementById('iso-file').value, name = document.getElementById('iso-name').value.trim();
  if (!file || !name) return;
  transport.send('field.fromIsoXml|' + file + '\t' + name); fjSelField = name; openFieldsAndJobs();
});
document.getElementById('kml-create').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const file = document.getElementById('kml-file').value, name = document.getElementById('kml-name').value.trim();
  if (!file || !name) return;
  transport.send('field.fromKml|' + file + '\t' + name); fjSelField = name; openFieldsAndJobs();
});

// Cross-field Resume Job picker (all jobs, recency order from the host).
function openResumeJob() { lnOpen('resumejob', 'ln-fieldops', renderResumeJob); }
document.getElementById('rj-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('fieldops', 'ln-fieldops', renderFieldOps); });
function renderResumeJob() {
  const list = document.getElementById('rj-list'); list.innerHTML = '';
  const jobs = fieldOps ? fieldOps.jobs : [];
  if (!jobs.length) { list.innerHTML = '<div class="fj-empty">No jobs yet.</div>'; return; }
  for (const j of jobs) {
    const row = document.createElement('div');
    row.className = 'fj-jrow';
    row.innerHTML = '<div class="fj-jtop"><span class="fj-jname"></span><span class="fj-jwt"></span><span class="fj-jst"></span></div><div class="fj-jsub"></div>';
    row.querySelector('.fj-jname').textContent = j.taskName;
    row.querySelector('.fj-jwt').textContent = j.fieldName;
    row.querySelector('.fj-jst').textContent = JOB_STATUS[j.status] || '';
    row.querySelector('.fj-jsub').textContent = 'Last opened: ' + j.lastOpened;
    row.addEventListener('pointerdown', ev => { ev.stopPropagation(); transport.send('field.resumeJob|' + j.fieldName + '\t' + j.taskName); lnCloseAll(); });
    list.appendChild(row);
  }
}

// ---- AgShare cloud sync (Settings / Upload / Download) — chain sub-panels of Field
// Operations. Settings via config.set conn.agShare*; actions via agshare.* with results
// (status / cloud list) riding the AgShare frame. Launched from the fly-out's AgShare row.
document.getElementById('fo-agupload').addEventListener('pointerdown', e => { e.stopPropagation(); openAgUpload(); });
document.getElementById('fo-agdownload').addEventListener('pointerdown', e => { e.stopPropagation(); openAgDownload(); });
document.getElementById('fo-agapi').addEventListener('pointerdown', e => { e.stopPropagation(); openAgSettings(); });

// Settings
function openAgSettings() { lnOpen('agsettings', 'ln-fieldops', renderAgSettings); }
document.getElementById('ag-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('fieldops', 'ln-fieldops', renderFieldOps); });
function renderAgSettings() {
  if (!agShare) return;
  const sv = document.getElementById('ag-server'), kv = document.getElementById('ag-key');
  if (document.activeElement !== sv) sv.value = agShare.serverUrl || '';
  if (document.activeElement !== kv) kv.value = agShare.apiKey || '';
  document.getElementById('ag-enabled').classList.toggle('active', !!agShare.enabled);
  document.getElementById('ag-teststatus').textContent = agShare.status || '';
}
document.getElementById('ag-server').addEventListener('change', () => cfgSend('conn.agShareServer', document.getElementById('ag-server').value.trim()));
document.getElementById('ag-key').addEventListener('change', () => cfgSend('conn.agShareApiKey', document.getElementById('ag-key').value.trim()));
document.getElementById('ag-enabled').addEventListener('pointerdown', e => { e.stopPropagation(); cfgSend('conn.agShareEnabled', e.currentTarget.classList.contains('active') ? '0' : '1'); });
document.getElementById('ag-test').addEventListener('pointerdown', e => {
  e.stopPropagation();
  cfgSend('conn.agShareServer', document.getElementById('ag-server').value.trim());
  cfgSend('conn.agShareApiKey', document.getElementById('ag-key').value.trim());
  transport.send('agshare.test');
});

// Upload
let aguSel = new Set();
function openAgUpload() { aguSel = new Set(); lnOpen('agupload', 'ln-fieldops', renderAgUpload); }
document.getElementById('agu-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('fieldops', 'ln-fieldops', renderFieldOps); });
function renderAgUpload() {
  const list = document.getElementById('agu-list'); list.innerHTML = '';
  for (const f of (agShare ? agShare.localFields : [])) {
    const row = document.createElement('label'); row.className = 'agu-row';
    const cb = document.createElement('input'); cb.type = 'checkbox'; cb.checked = aguSel.has(f.name);
    cb.addEventListener('change', () => { if (cb.checked) aguSel.add(f.name); else aguSel.delete(f.name); });
    const nm = document.createElement('span'); nm.className = 'agu-name'; nm.textContent = f.name;
    const bd = document.createElement('span'); bd.className = 'agu-bd ' + (f.hasBoundary ? 'ok' : 'no'); bd.textContent = f.hasBoundary ? 'Has boundary' : 'No boundary';
    row.appendChild(cb); row.appendChild(nm); row.appendChild(bd); list.appendChild(row);
  }
  document.getElementById('agu-status').textContent = agShare ? (agShare.status || '') : '';
}
document.getElementById('agu-selall').addEventListener('pointerdown', e => { e.stopPropagation(); for (const f of (agShare ? agShare.localFields : [])) aguSel.add(f.name); renderAgUpload(); });
document.getElementById('agu-selnone').addEventListener('pointerdown', e => { e.stopPropagation(); aguSel.clear(); renderAgUpload(); });
document.getElementById('agu-public').addEventListener('pointerdown', e => { e.stopPropagation(); e.currentTarget.classList.toggle('active'); });
document.getElementById('agu-upload').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (!aguSel.size) return;
  const pub = document.getElementById('agu-public').classList.contains('active') ? '1' : '0';
  transport.send('agshare.upload|' + [pub, ...aguSel].join('\t'));
});

// Download
let agdSel = null;
function openAgDownload() { agdSel = null; lnOpen('agdownload', 'ln-fieldops', () => { transport.send('agshare.fetch'); renderAgDownload(); }); }
document.getElementById('agd-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('fieldops', 'ln-fieldops', renderFieldOps); });
function renderAgDownload() {
  const list = document.getElementById('agd-list'); list.innerHTML = '';
  const cf = agShare ? agShare.cloudFields : [];
  if (!cf.length) list.innerHTML = '<div class="fj-empty">No cloud fields (Refresh to load).</div>';
  for (const f of cf) {
    const row = document.createElement('div'); row.className = 'fj-jrow' + (f.id === agdSel ? ' sel' : '');
    row.innerHTML = '<div class="fj-jtop"><span class="fj-jname"></span><span class="fj-jwt"></span></div>';
    row.querySelector('.fj-jname').textContent = f.name;
    row.querySelector('.fj-jwt').textContent = f.areaHa.toFixed(2) + ' ha';
    row.addEventListener('pointerdown', ev => { ev.stopPropagation(); agdSel = f.id; renderAgDownload(); });
    list.appendChild(row);
  }
  document.getElementById('agd-status').textContent = agShare ? (agShare.status || '') : '';
}
document.getElementById('agd-refresh').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('agshare.fetch'); });
document.getElementById('agd-download').addEventListener('pointerdown', e => { e.stopPropagation(); if (agdSel) transport.send('agshare.download|' + agdSel); });
document.getElementById('agd-force').addEventListener('pointerdown', e => { e.stopPropagation(); e.currentTarget.classList.toggle('active'); });
document.getElementById('agd-downloadall').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('agshare.downloadAll|' + (document.getElementById('agd-force').classList.contains('active') ? '1' : '0')); });

// ---- Steer Wizard (Phase 9) — full-screen, host-driven. The host runs the real
// SteerWizardViewModel; we rebuild #wz-content per step from the Wizard frame, edit via
// the existing config.set bridge, and forward nav (wizard.*) + gated calibration
// (wizard.action). Editable values display from the Config frame. ----
const wzOverlay = document.getElementById('wizard-overlay');
const wzContent = document.getElementById('wz-content');
let _wzKey = ''; // stepKind|index of the currently-built content (rebuild on change)
function openSteerWizard() { _wzKey = ''; wizard = null; transport.send('wizard.open'); wzOverlay.classList.add('open'); }
function wzClose(send) { if (send) transport.send('wizard.cancel'); wzOverlay.classList.remove('open'); wizard = null; _wzKey = ''; }
document.getElementById('wz-cancel').addEventListener('pointerdown', e => { e.stopPropagation(); wzClose(true); });
document.getElementById('wz-skip').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('wizard.skip'); });
document.getElementById('wz-back').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('wizard.back'); });
document.getElementById('wz-next').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (wizard && wizard.isLast) { transport.send('wizard.finish'); wzClose(false); }
  else transport.send('wizard.next');
});
// Delegated content events: data-cfg="key:val" (config.set), data-hw="n" (hardware
// level), data-act="Cmd" (gated wizard.action), data-tgl="key" (toggle), data-cfgnum
// (number input) on change.
wzContent.addEventListener('pointerdown', e => {
  const el = e.target.closest('[data-cfg],[data-hw],[data-act],[data-tgl]'); if (!el) return;
  e.stopPropagation();
  if (el.dataset.cfg) { const i = el.dataset.cfg.indexOf(':'); cfgSend(el.dataset.cfg.slice(0, i), el.dataset.cfg.slice(i + 1)); }
  else if (el.dataset.hw != null) transport.send('wizard.hw|' + el.dataset.hw);
  else if (el.dataset.act) rnSend('wizard.action|' + el.dataset.act);
  else if (el.dataset.tgl) cfgSend(el.dataset.tgl, cfgGet(el.dataset.tgl) ? '0' : '1');
});
wzContent.addEventListener('change', e => {
  const inp = e.target.closest('[data-cfgnum]'); if (!inp) return;
  const v = parseFloat(inp.value); if (Number.isFinite(v)) cfgSend(inp.dataset.cfgnum, v);
});
// Per-step content. Editable values read from the Config frame; live values get ids
// (data-live) refreshed each frame. esc() guards interpolated strings.
function esc(s) { return String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }
function wzVal(key, dflt) { const v = cfgGet(key); return typeof v === 'number' ? v : (dflt || 0); }
function wzNum(label, sub, key, step, unit) {
  return '<div class="wz-fld"><label>' + esc(label) + '</label><div class="sub">' + esc(sub || '') + '</div>' +
    '<input class="wz-num" data-cfgnum="' + key + '" type="number" step="' + (step || '0.01') + '" value="' + wzVal(key) + '"><span class="wz-unit">' + esc(unit || '') + '</span></div>';
}
function wzSeg(label, sub, key, opts) { // opts: [[text,val],...]
  return '<div class="wz-row"><div class="lbl">' + esc(label) + (sub ? '<div class="sub">' + esc(sub) + '</div>' : '') + '</div><div class="wz-seg">' +
    opts.map(o => '<button data-cfg="' + key + ':' + o[1] + '" data-activekey="' + key + '" data-activeval="' + o[1] + '">' + esc(o[0]) + '</button>').join('') + '</div></div>';
}
function wzTgl(label, sub, key) {
  return '<div class="wz-row"><div class="lbl">' + esc(label) + (sub ? '<div class="sub">' + esc(sub) + '</div>' : '') + '</div><button class="wz-tgl" data-tgl="' + key + '" data-tglkey="' + key + '">Off</button></div>';
}
function wzLive(lbl, key) { return '<div class="wz-live"><div class="big" data-live="' + key + '">—</div><div class="lbl">' + esc(lbl) + '</div></div>'; }
function buildWizardContent(w) {
  const cfg = config || {}, veh = cfg.vehicle || {}, ast = cfg.autosteer || {}, roll = cfg.roll || {};
  const ty = Math.max(0, Math.min(2, (veh.type | 0)));
  const head = '<div class="wz-h1">' + esc(w.title) + '</div><div class="wz-desc">' + esc(w.description) + '</div>';
  switch (w.stepKind) {
    case 'welcome': return head + '<div class="wz-center"><div class="wz-okcard"><div class="t">AutoSteer</div><div class="s">Configuration Wizard</div></div></div>';
    case 'vehicleType': {
      const c = (i, ic, t) => '<div class="wz-card" data-cfg="vehicle.type:' + i + '" data-activekey="vehicle.type" data-activeval="' + i + '"><img src="/icons/' + ic + '.png"><div class="t">' + t + '</div></div>';
      return head + '<div class="wz-cards">' + c(0, 'WheelbaseTractor', 'Tractor') + c(1, 'WheelbaseHarvester', 'Harvester') + c(2, 'WheelbaseArticulated', '4WD / Articulated') + '</div>';
    }
    case 'hardware': {
      const c = (i, ic, t, s) => '<div class="wz-card" data-hw="' + i + '" data-activehw="' + i + '"><img src="/icons/' + ic + '.png"><div class="t">' + t + '</div><div class="s">' + s + '</div></div>';
      return head + '<div class="wz-cards">' + c(0, 'NavigationSettings', 'GPS Only', 'Light bar guidance only') + c(1, 'AutoSteerConf', 'AutoSteer', 'Motor/valve, WAS, IMU') + c(2, 'Settings48', 'Full Setup', 'Steering + section control') + '</div>';
    }
    case 'dimensions':
      return head + '<div class="wz-panels"><div class="wz-panel"><img class="dia" src="/icons/' + ['WheelbaseTractor', 'WheelbaseHarvester', 'WheelbaseArticulated'][ty] + '.png">' + wzNum('Wheelbase', 'Front to rear axle distance', 'vehicle.wheelbase', '0.01', 'm') + '</div>' +
        '<div class="wz-panel"><img class="dia" src="/icons/' + ['TrackWidthTractor', 'TrackWidthHarvester', 'TrackWidthArticulated'][ty] + '.png">' + wzNum('Track Width', 'Left to right wheel center', 'vehicle.trackWidth', '0.01', 'm') + '</div></div>';
    case 'antenna':
      return head + '<div class="wz-panels"><div class="wz-panel"><img class="dia" src="/icons/' + ['AntennaTractorOffset', 'AntennaHarvesterOffset', 'AntennaArticulatedOffset'][ty] + '.png">' +
        wzNum('Pivot Distance', 'Antenna to rear axle (+ = ahead)', 'vehicle.antennaPivot', '0.01', 'm') + wzNum('Antenna Height', 'Above ground level', 'vehicle.antennaHeight', '0.01', 'm') + '</div>' +
        '<div class="wz-panel"><img class="dia" src="/icons/' + ['AntennaTractorTop', 'AntennaHarvesterTop', 'AntennaArticulatedTop'][ty] + '.png"><div class="wz-fld"><label>Lateral Offset</label><div class="sub">From centerline (+ = right)</div><div class="wz-seg">' +
        '<button data-cfg="vehicle.antennaSide:left">Left</button><button data-cfg="vehicle.antennaSide:center">Center</button><button data-cfg="vehicle.antennaSide:right">Right</button></div></div></div></div>';
    case 'hwconfig':
      return head + '<div class="wz-rows">' +
        wzSeg('Steer Enable Method', null, 'autosteer.externalEnable', [['None', 0], ['Switch', 1], ['Button', 2]]) +
        wzSeg('Motor Driver', null, 'autosteer.motorDriver', [['IBT2', 0], ['Cytron', 1]]) +
        wzSeg('A/D Converter', null, 'autosteer.adConverter', [['Differential', 0], ['Single', 1]]) +
        wzTgl('Invert Steer Enable Relay', 'Inverts the relay that enables the motor', 'autosteer.invertRelays') +
        wzTgl('Danfoss Valve', 'Enable for Danfoss hydraulic steering', 'autosteer.danfossEnabled') + '</div>';
    case 'roll':
      // Reuse the map's roll gauge: a green bar that rotates around (100,40) by the
      // live roll, with pink scale marks and the degree readout.
      return head + '<div class="wz-center"><svg width="260" height="104" viewBox="0 0 200 80">' +
        '<path fill="none" stroke="#E91E90" stroke-width="2.5" stroke-linecap="round" d="M 70,12 A 22,28 0 0 0 70,68 M 59,20 L 51,20 M 52,30 L 44,30 M 48,40 L 40,40 M 52,50 L 44,50 M 59,60 L 51,60"/>' +
        '<path fill="none" stroke="#E91E90" stroke-width="2.5" stroke-linecap="round" d="M 130,12 A 22,28 0 0 1 130,68 M 141,20 L 149,20 M 148,30 L 156,30 M 152,40 L 160,40 M 148,50 L 156,50 M 141,60 L 149,60"/>' +
        '<rect id="wz-roll-bar" x="50" y="38" width="100" height="4" rx="2" fill="#2ECC71"/>' +
        '<circle cx="100" cy="40" r="3" fill="#fff"/>' +
        '<text id="wz-roll-deg" x="100" y="32" text-anchor="middle" fill="#fff" font-size="24" font-weight="bold" font-family="system-ui,sans-serif">0.0</text>' +
        '</svg></div>' +
        '<div class="wz-rows">' + wzTgl('Invert Roll', null, 'roll.isRollInvert') +
        '<div class="wz-row"><div class="lbl">Zero Roll</div><button class="wz-testbtn" data-act="ZeroRoll">Zero Roll</button></div>' +
        '<div class="wz-row"><div class="lbl">Roll Zero Offset</div><div class="lbl"><span data-live="rollzero">—</span></div></div></div>';
    case 'was':
      return head + wzLive('Live Steer Angle', 'angle') +
        '<div class="wz-rows"><div class="wz-row"><div class="lbl">Zero WAS</div><button class="wz-testbtn" data-act="ZeroWas">Zero WAS</button></div>' +
        wzTgl('Invert WAS', 'Enable if steering reads backwards', 'autosteer.invertWas') +
        '<div class="wz-row"><div class="lbl">WAS Offset</div><div class="lbl"><span data-live="wasoffset">—</span> counts</div></div></div>';
    case 'motor':
      return head + wzLive('Live Steer Angle', 'angle') + '<div class="wz-center"><button class="wz-testbtn" data-act="StartTest">Start Motor Test</button>' +
        '<div class="wz-desc" data-live="phase"></div><div class="wz-desc" data-live="result"></div></div>';
    case 'maxangle':
      return head + wzLive('Live Steer Angle', 'angle') + '<div class="wz-center"><button class="wz-testbtn" data-act="StartTest">Start Max Angle Test</button>' +
        '<div class="wz-desc" data-live="phase"></div><div class="wz-desc" data-live="result"></div></div>';
    case 'cpd':
      return head + '<div class="wz-prereq"><div class="ttl">Prerequisites</div><div class="it">GPS: <b data-live="fix">—</b></div><div class="it">Speed: <b data-live="speed">—</b> (aim for ~5 km/h)</div></div>' +
        wzLive('Live Steer Angle', 'angle') + '<div class="wz-center"><button class="wz-testbtn" data-act="StartRecording" id="wz-recbtn">Record</button></div>' +
        '<div class="wz-rows">' + wzNum('Counts Per Degree', null, 'autosteer.countsPerDegree', '1', '') + '</div>';
    case 'ackermann':
      return head + '<div class="wz-prereq"><div class="ttl">Prerequisites</div><div class="it">GPS: <b data-live="fix">—</b></div><div class="it">Speed: <b data-live="speed">—</b></div></div>' +
        wzLive('Live Steer Angle', 'angle') + '<div class="wz-center"><button class="wz-testbtn" data-act="StartRecording" id="wz-recbtn">Record</button></div>' +
        '<div class="wz-rows">' + wzNum('Ackermann', '100 = neutral', 'autosteer.ackermann', '1', '') + '</div>';
    case 'gains':
      return head + '<div class="wz-rows">' + wzTgl('Guidance Algorithm (Stanley)', 'Pure Pursuit default; Stanley more responsive at low speed', 'autosteer.isStanleyMode') +
        wzNum('Proportional Gain (Kp)', 'Start at 10, increase for faster correction', 'autosteer.proportionalGain', '1', '') +
        wzNum('Integral Gain (Ki)', 'Start at 0, only increase for drift', 'autosteer.integralGain', '0.01', '') +
        wzNum('Steer Response Hold', 'Look-ahead (higher = smoother)', 'autosteer.steerResponseHold', '0.1', '') +
        wzNum('Side Hill Compensation', 'Degrees per degree of roll (0-1.0)', 'autosteer.sideHillCompensation', '0.01', '') + '</div>';
    case 'speed':
      return head + '<div class="wz-rows">' +
        wzNum('Min Steer Speed', 'Engage above this speed', 'autosteer.minSteerSpeed', '0.1', 'km/h') +
        wzNum('Max Steer Speed', 'Safety cutoff speed', 'autosteer.maxSteerSpeed', '0.1', 'km/h') +
        wzTgl('Turn Sensor', 'Steering wheel encoder', 'autosteer.turnSensorEnabled') +
        wzTgl('Pressure Sensor', 'Hydraulic stall detection', 'autosteer.pressureSensorEnabled') +
        wzTgl('Current Sensor', 'Motor current obstruction detection', 'autosteer.currentSensorEnabled') +
        wzTgl('Steer In Reverse', 'Allow steering while reversing', 'autosteer.steerInReverse') +
        wzNum('Deadzone Heading', 'Heading error tolerance', 'autosteer.deadzoneHeading', '0.01', 'deg') + '</div>';
    case 'finish':
      return head + '<div class="wz-center"><div class="wz-okcard"><div class="ok">OK</div><div class="t">Configuration saved!</div><div class="s">Click Finish to close the wizard</div></div></div>';
    default: return head;
  }
}
function renderWizard() {
  const w = wizard;
  if (!w) return;
  // Chrome (every frame).
  asSetText('wz-step', 'Step ' + (w.stepIndex + 1) + ' of ' + w.totalSteps);
  document.getElementById('wz-progressbar').style.width = (w.totalSteps ? (w.stepIndex + 1) / w.totalSteps * 100 : 0) + '%';
  asSetText('wz-was', (w.statusWas || 0).toFixed(1) + '°'); asSetText('wz-roll', (w.statusRoll || 0).toFixed(1) + '°');
  asSetText('wz-gps', w.statusGps || '—'); asSetText('wz-speed', (w.statusSpeed || 0).toFixed(1) + ' km/h'); asSetText('wz-pwm', w.statusPwm | 0);
  const next = document.getElementById('wz-next');
  next.textContent = w.isLast ? 'Finish' : 'Next';
  next.classList.toggle('disabled', !(w.isLast || w.canNext));
  document.getElementById('wz-back').classList.toggle('disabled', !w.canBack);
  document.getElementById('wz-skip').style.display = w.canSkip ? '' : 'none';
  // Rebuild content only on step change.
  const key = w.stepKind + '|' + w.stepIndex;
  if (key !== _wzKey) { _wzKey = key; wzContent.innerHTML = buildWizardContent(w); }
  // Active states (selection cards / segments / toggles) + live values, every frame.
  for (const el of wzContent.querySelectorAll('[data-activekey]'))
    el.classList.toggle('active', String(cfgGet(el.dataset.activekey)) === el.dataset.activeval);
  for (const el of wzContent.querySelectorAll('[data-activehw]'))
    el.classList.toggle('active', w.hardwareLevel === +el.dataset.activehw);
  for (const el of wzContent.querySelectorAll('[data-tglkey]')) {
    const on = !!cfgGet(el.dataset.tglkey); el.classList.toggle('on', on); el.textContent = on ? 'On' : 'Off';
  }
  for (const el of wzContent.querySelectorAll('[data-act]')) el.classList.toggle('disabled', !iHoldControl);
  const live = (k, v) => { for (const el of wzContent.querySelectorAll('[data-live="' + k + '"]')) el.textContent = v; };
  live('angle', (w.liveAngle || 0).toFixed(1) + '°'); live('roll', (w.liveRoll || 0).toFixed(2) + '°');
  live('error', (w.liveError || 0).toFixed(1)); live('phase', w.testPhase || ''); live('result', w.testResult || '');
  live('fix', w.fixLabel || (w.statusGps || '—')); live('speed', (w.statusSpeed || 0).toFixed(1) + ' km/h');
  live('rollzero', wzVal('roll.rollZero').toFixed(2)); live('wasoffset', wzVal('autosteer.wasOffset') | 0);
  // Roll gauge (roll-calibration step): rotate the bar by the live roll, like the map.
  const rb = document.getElementById('wz-roll-bar');
  if (rb) { const rv = w.liveRoll || w.statusRoll || 0; rb.setAttribute('transform', 'rotate(' + rv.toFixed(2) + ' 100 40)'); const rd = document.getElementById('wz-roll-deg'); if (rd) rd.textContent = rv.toFixed(1); }
  const rec = document.getElementById('wz-recbtn');
  if (rec) { rec.textContent = w.testActive ? 'Stop' : 'Record'; rec.dataset.act = w.testActive ? 'StopRecording' : 'StartRecording'; }
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
  if (swPanel.classList.contains('open')) populateSmartWas();
  // Steer Wizard: re-render while open (host-driven; live every frame).
  wizardDirty = false;
  if (wzOverlay.classList.contains('open')) renderWizard();
  // Re-read the hub when a fresh profiles frame arrives.
  if (profilesDirty) { profilesDirty = false; if (document.getElementById('vehtoolhub').classList.contains('open')) refreshHub(); }
  // Network IO panel: module/NTRIP readouts ride the Status frame → refresh each frame.
  if (nioPanel.classList.contains('open')) renderNetworkIo();
  // NTRIP test result rides the Status frame while a test is in flight.
  if (_nteTestActive && document.getElementById('ntripeditor').classList.contains('open') && statusBar)
    document.getElementById('nte-teststatus').textContent = statusBar.ntripTestStatus || '';
  // Re-read the NTRIP list/editor fields when a fresh profiles frame arrives.
  if (ntripDirty) {
    ntripDirty = false;
    if (document.getElementById('ntripprofiles').classList.contains('open')) renderNtripList();
    if (document.getElementById('ntripeditor').classList.contains('open')) renderNteFields();
  }
  // Field Operations: status pill rides scene/status; Fields-and-Jobs lists re-read on a
  // fresh FieldOps frame.
  if (document.getElementById('fieldops').classList.contains('open')) renderFieldOps();
  if (document.getElementById('newfield').classList.contains('open')) renderNewField();
  if (fieldOpsDirty) {
    fieldOpsDirty = false;
    if (document.getElementById('fieldsandjobs').classList.contains('open')) renderFieldsAndJobs();
    if (document.getElementById('resumejob').classList.contains('open')) renderResumeJob();
  }
  if (agShareDirty) {
    agShareDirty = false;
    if (document.getElementById('agsettings').classList.contains('open')) renderAgSettings();
    if (document.getElementById('agupload').classList.contains('open')) renderAgUpload();
    if (document.getElementById('agdownload').classList.contains('open')) renderAgDownload();
  }
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
      if (nm) showConfirm('Delete Profile', 'Delete ' + kind + " profile '" + nm + "'?",
        () => hubSend('profile.delete', kind + '\t' + nm));
    } else if (act === 'rename') {
      const old = list.value, nw = (nameInput.value || '').trim();
      if (!old || !nw) { HUB.status.textContent = 'Select a profile, type a new name, then Rename'; return; }
      hubSend('profile.rename', kind + '\t' + old + '\t' + nw);
    } else if (act === 'reset') {
      showConfirm('Reset Profile', 'Reset Default ' + kind + ' profile?', () => hubSend('profile.reset', kind));
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
  const cfg = config && config.autosteer;
  if (!tick || !tick.guidanceActive || !cfg || !cfg.guidanceBarOn) { lbEl.style.display = 'none'; return; }
  if (cfg.steerBarEnabled) {
    // Steer bar: steer-angle error (deg) with dead-zone.
    let err = tick.steerAngleError || 0;
    const dz = (tick.op && tick.op.autoSteer) ? 0.5 : 0.2;
    if (Math.abs(err) < dz) err = 0;
    const arrow = err === 0 ? '> 0 <' : err > 0 ? '◀' : '▶';
    lbEl.textContent = err === 0 ? '> 0 <' : `${arrow} ${Math.abs(err).toFixed(1)}°`;
  } else {
    // Light bar: cross-track distance.
    const xte = tick.crossTrackError || 0;
    const onLine = Math.abs(xte) < 0.05;
    const arrow = onLine ? '●' : xte > 0 ? '◀' : '▶'; // arrow = steer direction
    lbEl.textContent = `${arrow} ${(Math.abs(xte) * 100).toFixed(0)} cm   ${tick.lineLabel || ''}`;
  }
  lbEl.style.display = 'block';
}

// Headland-distance HUD → DOM overlay (mirrors the native SkiaMapControl HUD).
// Shown only when Display.HeadlandDistanceVisible is on and a live distance ≥ 0
// (−1 = no headland / not driving). Metric/imperial follows the status bar.
const hlHud = document.getElementById('headland-hud');
function updateHeadlandHud() {
  const on = config && config.display && config.display.headlandDistanceVisible;
  const d = tick ? tick.headlandDist : -1;
  if (!on || d == null || d < 0) { hlHud.style.display = 'none'; return; }
  const metric = !statusBar || statusBar.isMetric;
  hlHud.textContent = metric ? d.toFixed(1) + ' m' : (d * 3.28084).toFixed(0) + ' ft';
  hlHud.classList.toggle('warn', !!(tick && tick.headlandWarn));
  hlHud.style.display = 'block';
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
  // Left-nav panels now close via the transparent #ln-scrim (which consumes the tap),
  // not this global handler — so a panel dismiss no longer also pans the map.
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
  // GuidanceBarOn is the master; SteerBarEnabled picks the mode (steer-angle error vs
  // cross-track). Mirrors the native LightBarPanel.
  const cfg = config && config.autosteer;
  if (!tick || !tick.guidanceActive || !cfg || !cfg.guidanceBarOn) return;
  const SEG = 15, W = 18, H = 16, GAP = 4;
  const mid = (SEG - 1) / 2;
  const steerMode = !!cfg.steerBarEnabled;
  let val, PER, onThresh;
  if (steerMode) {
    val = tick.steerAngleError || 0;                          // actual − commanded (deg)
    const dz = (tick.op && tick.op.autoSteer) ? 0.5 : 0.2;    // AgOpen dead-zone
    if (Math.abs(val) < dz) val = 0;
    PER = 12 / mid; onThresh = dz;                            // ±12° full deflection
  } else {
    val = tick.crossTrackError || 0; PER = 0.05; onThresh = 0.05; // + = right of line
  }
  const totalW = SEG * (W + GAP) - GAP;
  const x0 = (vw - totalW) / 2, top = 54; // below the top status bar
  const lit = Math.min(Math.round(Math.abs(val) / PER), mid);
  const onLine = Math.abs(val) < onThresh;
  const steerLeft = val > 0; // lights point the way to STEER (native convention)
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
  updateHeadlandHud();
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
