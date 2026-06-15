// Renderer — consumes plain Scene + Tick objects via the transport interface.
// It has NO knowledge of SignalR or the wire format (see transport.js); swapping
// the transport at Phase 2 leaves this file untouched. Canvas2D for now; the
// CanvasKit swap (and client-side dead-reckoning) slot in here without touching
// the transport seam.

const cv = document.getElementById('c');
const ctx = cv.getContext('2d');
const ckcv = document.getElementById('ck'); // CanvasKit (Skia) renderer canvas
const hud = document.getElementById('hud');

// Logical (CSS-pixel) canvas size. The backing store is scaled by the device
// pixel ratio so vectors render at native resolution on hi-DPI screens (tablets,
// retina) — otherwise thin strokes look faint and shimmer when panning. All draw
// code works in these logical coordinates; the dpr scale is baked into ctx (2D)
// or the Skia canvas (scale(dpr) per frame).
let vw = innerWidth, vh = innerHeight, dpr = 1;
// Skia/CanvasKit renderer state — declared before resize() (which calls
// recreateSkSurface) to avoid a temporal-dead-zone ReferenceError.
let CK = null, skSurface = null, skTri = null, SKP = null;
let useSkia = false;
function resize() {
  dpr = Math.min(window.devicePixelRatio || 1, 2); // cap: 3× phones don't need 9× fill
  vw = innerWidth; vh = innerHeight;
  for (const c of [cv, ckcv]) {
    c.width = Math.round(vw * dpr);
    c.height = Math.round(vh * dpr);
    c.style.width = vw + 'px';
    c.style.height = vh + 'px';
  }
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0); // sticky; save/restore in draw preserve it
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
let follow = true;
let camE = 0, camN = 0;
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
    if (follow && (!tick || !tick.pose) && s.boundaries.length && s.boundaries[0].length) {
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
    gridMinor: mk('rgba(255,255,255,0.055)', 1), gridMajor: mk('rgba(255,255,255,0.13)', 1),
    axisX: mk('rgba(204,51,51,0.5)', 1.5), axisY: mk('rgba(51,204,51,0.5)', 1.5),
    vehicle: fill('#39FF6A'),
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
function applyRenderer() {
  cv.style.display = useSkia ? 'none' : 'block';
  ckcv.style.display = useSkia ? 'block' : 'none';
}

if (typeof CanvasKitInit === 'function') {
  CanvasKitInit({ locateFile: f => '/vendor/' + f }).then(ck => {
    CK = ck;
    buildSkPaints();
    recreateSkSurface();
    ckStatus = skSurface ? 'ready (Skia WebGL)' : 'no WebGL surface';
    requestAnimationFrame(skFrame);
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
addEventListener('pointerdown', e => { dragging = true; follow = false; lastX = e.clientX; lastY = e.clientY; });
addEventListener('pointerup', () => dragging = false);
addEventListener('pointermove', e => {
  if (!dragging) return;
  camE -= (e.clientX - lastX) / pxPerM;
  camN += (e.clientY - lastY) / pxPerM;
  lastX = e.clientX; lastY = e.clientY;
});
addEventListener('keydown', e => {
  if (e.key === 'f' || e.key === 'F') follow = true;
  else if (e.key === 'k' || e.key === 'K') { useSkia = !useSkia; applyRenderer(); }
  // 3D tilt (Skia only): 3 toggles, [ / ] nudge the pitch.
  else if (e.key === '3') { if (!useSkia) { useSkia = true; applyRenderer(); } pitch = pitch > 0.001 ? 0 : DEFAULT_PITCH; }
  else if (e.key === '[') pitch = Math.max(0, pitch - PITCH_STEP);
  else if (e.key === ']') pitch = Math.min(MAX_PITCH, pitch + PITCH_STEP);
});

// ---- sim drive controls (client→host commands) ----
// Keys for desktop; the on-screen #ctl buttons for touch. The host maps these
// ids to the matching VM command on its UI thread (safe allowlist).
const KEY_CMD = {
  ArrowLeft: 'sim.steerLeft', ArrowRight: 'sim.steerRight',
  ArrowUp: 'sim.speedUp', ArrowDown: 'sim.speedDown', ' ': 'sim.stop',
};
addEventListener('keydown', e => {
  const cmd = KEY_CMD[e.key];
  if (cmd) { e.preventDefault(); transport.send(cmd); }
});
for (const b of document.querySelectorAll('#ctl button')) {
  b.addEventListener('pointerdown', e => {
    e.preventDefault(); e.stopPropagation(); // don't also start a camera pan
    transport.send(b.dataset.cmd);
  });
}

// ---- remote actuation control (Phase 2 safety layer) ----
// Take/Release single-holder control + a Tier-2 stub. Only the holder may
// actuate; the holder must heartbeat (presence) or the host revokes it (deadman).
const ctlTake = document.getElementById('ctl-take');
const ctlTest = document.getElementById('ctl-test');
const ctlStatus = document.getElementById('ctl-status');
function updateControlUi() {
  if (!ctlTake) return;
  iHoldControl = lastControl.held && lastControl.holderId === myClientId;
  if (iHoldControl) {
    ctlTake.textContent = 'Release Control'; ctlTake.classList.add('held'); ctlTake.disabled = false;
    ctlTest.disabled = false;
    ctlStatus.textContent = '● You have control'; ctlStatus.style.color = '#39FF6A';
  } else if (lastControl.held) {
    ctlTake.textContent = 'Take Control'; ctlTake.classList.remove('held'); ctlTake.disabled = true;
    ctlTest.disabled = true;
    ctlStatus.textContent = '● Under remote control — ' + (lastControl.holderName || 'another client');
    ctlStatus.style.color = '#ff7a3d';
  } else {
    ctlTake.textContent = 'Take Control'; ctlTake.classList.remove('held'); ctlTake.disabled = false;
    ctlTest.disabled = true;
    ctlStatus.textContent = 'No one in control'; ctlStatus.style.color = '#9fb3cc';
  }
}
if (ctlTake) {
  document.getElementById('control').addEventListener('pointerdown', e => e.stopPropagation());
  ctlTake.addEventListener('click', () => {
    transport.send(iHoldControl ? 'control.release' : 'control.acquire|Browser');
  });
  ctlTest.addEventListener('click', () => {
    if (!iHoldControl) return;
    // NOTE: no blocking confirm() here — it would freeze the presence heartbeat
    // and trip the host deadman. Real Tier-2 actions get a non-blocking in-page
    // confirm in Phase 3.
    transport.send('test.actuate');
    ctlStatus.textContent = 'Test actuation sent ✓'; // cab status line also confirms
    setTimeout(updateControlUi, 1500);
  });
  // Presence heartbeat — keeps our hold alive; a lapse triggers the host deadman.
  setInterval(() => { if (iHoldControl) transport.send('control.presence'); }, 500);
  updateControlUi();
}

// ---- render ----
// True 3D is only meaningful under Skia (CanvasKit M44). When inactive, perspM is
// null and everything uses the plain ortho projection below.
function active3D() { return useSkia && CK && pitch > 0.001; }
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
// Apply perspM (row-major, M·v) to a ground point (e,n,0,1); perspective divide.
function applyM(M, e, n) {
  const x = M[0] * e + M[1] * n + M[3];
  const y = M[4] * e + M[5] * n + M[7];
  const w = M[12] * e + M[13] * n + M[15];
  return [x / w, y / w];
}
function w2s(e, n) {
  if (perspM) return applyM(perspM, e, n);
  return [vw / 2 + (e - camE) * pxPerM, vh / 2 - (n - camN) * pxPerM];
}
// On-screen heading at a ground point: the heading direction projected through the
// current matrix (so the vehicle marker aligns with the tilted ground, not screen
// up). Returns the world heading unchanged when top-down.
function screenHeading(e, n, h) {
  if (!perspM) return h;
  const a = w2s(e, n), b = w2s(e + Math.sin(h) * 3, n + Math.cos(h) * 3);
  return Math.atan2(b[0] - a[0], -(b[1] - a[1])); // 0 = up, clockwise
}
function strokePts(pts, close) {
  if (!pts || !pts.length) return;
  ctx.beginPath();
  pts.forEach((p, i) => { const [x, y] = w2s(p.e, p.n); i ? ctx.lineTo(x, y) : ctx.moveTo(x, y); });
  if (close) ctx.closePath();
  ctx.stroke();
}
function vehicle(p) {
  const [x, y] = w2s(p.e, p.n);
  ctx.save();
  ctx.translate(x, y);
  ctx.rotate(screenHeading(p.e, p.n, p.heading)); // radians, 0 = north (up), clockwise
  ctx.fillStyle = '#39FF6A';
  ctx.beginPath();
  ctx.moveTo(0, -14); ctx.lineTo(9, 11); ctx.lineTo(-9, 11); ctx.closePath();
  ctx.fill();
  ctx.restore();
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
function toolFootprint() {
  const t = renderTool();
  // Draw whenever we have a section layout and a real tool position. (Not gated
  // on t.ready — IsToolPositionReady can read false in the sim even while
  // coverage paints, and coverage proves the pose is live.)
  if (!t || !scene || !scene.toolSections || !scene.toolSections.length) return;
  if (!t.e && !t.n) return;
  const perp = t.heading + Math.PI / 2;
  const ps = Math.sin(perp), pc = Math.cos(perp);
  const secs = tick.sections || [];
  ctx.save();
  ctx.lineWidth = 7;
  ctx.lineCap = 'butt';
  for (let i = 0; i < scene.toolSections.length; i++) {
    const span = scene.toolSections[i];
    const [lx, ly] = w2s(t.e + ps * span.left, t.n + pc * span.left);
    const [rx, ry] = w2s(t.e + ps * span.right, t.n + pc * span.right);
    ctx.strokeStyle = SECTION_COLORS[secs[i]] || SECTION_COLORS[5];
    ctx.beginPath(); ctx.moveTo(lx, ly); ctx.lineTo(rx, ry); ctx.stroke();
  }
  ctx.restore();
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

// Lightbar: a centred LED strip across the top, lit toward the side the vehicle
// has drifted (right of line → right segments). Centre LED green when on-line.
// Only shown while guidance is engaged. 5 cm per segment.
function lightbar() {
  if (!tick || !tick.guidanceActive) return;
  const xte = tick.crossTrackError || 0;        // + = right of line
  const SEG = 15, W = 18, H = 16, GAP = 4, PER = 0.05;
  const mid = (SEG - 1) / 2;
  const totalW = SEG * (W + GAP) - GAP;
  const x0 = (vw - totalW) / 2, top = 54; // below the top status bar
  const lit = Math.min(Math.round(Math.abs(xte) / PER), mid);
  // Native convention: the lights point the way to STEER. Right-of-line (+xte)
  // lights the LEFT in orange-red (steer left); left-of-line lights the RIGHT
  // in green (steer right). Centre LED green when on-line.
  const onLine = Math.abs(xte) < PER;
  const steerLeft = xte > 0;
  const litCol = steerLeft ? '#ff7a3d' : '#39FF6A';

  for (let i = 0; i < SEG; i++) {
    const idx = i - mid;                  // signed position from centre
    const x = x0 + i * (W + GAP);
    let on = false, col = '#1c2230';
    if (idx === 0) {
      on = onLine; col = on ? '#39FF6A' : '#2a3550';
    } else if (steerLeft ? (idx < 0 && -idx <= lit) : (idx > 0 && idx <= lit)) {
      on = true; col = litCol;
    }
    ctx.fillStyle = on ? col : '#1c2230';
    ctx.fillRect(x, top, W, H);
  }
  // The readout text (arrow + cm + label) is a DOM overlay (#lb) shared by both
  // renderers — see updateLightbarText(), driven from the always-running draw().
}
// Lightbar readout text → DOM overlay, so it shows under both the 2D and Skia map
// renderers. Updated every frame from the latest tick; hidden when guidance is off.
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
  modAgg: document.getElementById('sb-modagg'), modBtn: document.getElementById('sb-modbtn'),
  modPop: document.getElementById('sb-modpop'),
  mGps: document.getElementById('sb-gps'), mImu: document.getElementById('sb-imu'),
  mAs: document.getElementById('sb-as'), mMa: document.getElementById('sb-ma'),
  dGps: document.getElementById('sb-gps-d'), dImu: document.getElementById('sb-imu-d'),
  dAs: document.getElementById('sb-as-d'), dMa: document.getElementById('sb-ma-d'),
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
SB.modBtn.addEventListener('pointerdown', e => {
  e.stopPropagation();
  SB.modPop.style.display = SB.modPop.style.display === 'block' ? 'none' : 'block';
});
addEventListener('pointerdown', () => { if (SB.modPop) SB.modPop.style.display = 'none'; });

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
}

// Background imagery (BackPic.png) — the bottom layer, drawn at its world rect.
function drawImagery() {
  if (!imageryImg || !imageryRect) return;
  const r = imageryRect;
  const [tlx, tly] = w2s(r.minE, r.maxN); // top-left = (minE, maxN)
  const [brx, bry] = w2s(r.maxE, r.minN); // bottom-right = (maxE, minN)
  ctx.imageSmoothingEnabled = true;
  ctx.drawImage(imageryImg, tlx, tly, brx - tlx, bry - tly);
}

// Blit the coverage offscreen (cell grid) into world space, under the vectors.
function drawCoverage() {
  if (!cov) return;
  const cs = cov.cellSize;
  const [tlx, tly] = w2s(cov.originE, cov.originN + cov.height * cs); // (minE, maxN)
  const [brx, bry] = w2s(cov.originE + cov.width * cs, cov.originN);  // (maxE, minN)
  ctx.imageSmoothingEnabled = false;
  ctx.drawImage(cov.canvas, tlx, tly, brx - tlx, bry - tly);
}

// Reference grid + origin axes (procedural, client-side — no wire). Spacing is a
// tool-width multiple on a 1-2-5 series keyed to zoom, matching the native grid
// (#417). Axes through the field origin (local 0,0): X (N=0) red, Y (E=0) green.
function niceStep125(x) {
  if (!(x > 0)) return 1;
  const p = Math.pow(10, Math.floor(Math.log10(x)));
  const f = x / p;
  return (f < 1.5 ? 1 : f < 3.5 ? 2 : f < 7.5 ? 5 : 10) * p;
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
function drawGrid() {
  const G = 2000; // native clamps the grid to ±2000 m
  const halfW = (vw / 2) / pxPerM, halfH = (vh / 2) / pxPerM;
  const minE = Math.max(camE - halfW, -G), maxE = Math.min(camE + halfW, G);
  const minN = Math.max(camN - halfH, -G), maxN = Math.min(camN + halfH, G);
  if (minE >= maxE || minN >= maxN) return;

  const toolW = toolWidthM();
  const spacing = toolW * Math.max(1, niceStep125(Math.max(vw, vh) / pxPerM / (toolW * 30)));
  ctx.lineWidth = 1;

  for (let k = Math.ceil(minE / spacing); k * spacing <= maxE; k++) {
    const e = k * spacing;
    ctx.strokeStyle = (k % 10 === 0) ? 'rgba(255,255,255,0.13)' : 'rgba(255,255,255,0.055)';
    const [x, y1] = w2s(e, minN), [, y2] = w2s(e, maxN);
    ctx.beginPath(); ctx.moveTo(x, y1); ctx.lineTo(x, y2); ctx.stroke();
  }
  for (let k = Math.ceil(minN / spacing); k * spacing <= maxN; k++) {
    const n = k * spacing;
    ctx.strokeStyle = (k % 10 === 0) ? 'rgba(255,255,255,0.13)' : 'rgba(255,255,255,0.055)';
    const [x1, y] = w2s(minE, n), [x2] = w2s(maxE, n);
    ctx.beginPath(); ctx.moveTo(x1, y); ctx.lineTo(x2, y); ctx.stroke();
  }

  ctx.lineWidth = 1.5;
  if (0 >= minE && 0 <= maxE) { // Y axis (E=0) — green
    ctx.strokeStyle = 'rgba(51,204,51,0.5)';
    const [x, y1] = w2s(0, minN), [, y2] = w2s(0, maxN);
    ctx.beginPath(); ctx.moveTo(x, y1); ctx.lineTo(x, y2); ctx.stroke();
  }
  if (0 >= minN && 0 <= maxN) { // X axis (N=0) — red
    ctx.strokeStyle = 'rgba(204,51,51,0.5)';
    const [x1, y] = w2s(minE, 0), [x2] = w2s(maxE, 0);
    ctx.beginPath(); ctx.moveTo(x1, y); ctx.lineTo(x2, y); ctx.stroke();
  }
}
function draw() {
  ctx.clearRect(0, 0, vw, vh);

  const rp = renderPose();
  if (rp && follow) { camE = rp.e; camN = rp.n; }
  updatePerspective(); // null unless Skia + tilt; 2D canvas stays ortho

  drawImagery();   // bottom layer
  drawCoverage();
  drawGrid();      // grid + origin axes over coverage, under the vectors

  if (scene) {
    ctx.lineWidth = 5; ctx.strokeStyle = '#46a0ff';
    for (const ring of scene.boundaries) strokePts(ring, true);
    if (scene.headland) { ctx.lineWidth = 4; ctx.strokeStyle = '#5fd35f'; strokePts(scene.headland, true); }
    const activeName = tick ? tick.activeTrackName : null;
    for (const tr of scene.tracks) {
      const isActive = activeName && tr.name === activeName;
      if (isActive) {
        ctx.setLineDash([9, 7]);
        ctx.lineWidth = 5; ctx.strokeStyle = '#a86bff'; // dashed purple = reference line
      } else {
        ctx.setLineDash([]);
        ctx.lineWidth = 4; ctx.strokeStyle = '#ffd24a';
      }
      strokePts(tr.points, false);
    }
    ctx.setLineDash([]);
    if (scene.nextTrack) {
      ctx.lineWidth = 4; ctx.strokeStyle = '#00c8c8'; // cyan = next pass (until picked up)
      strokePts(scene.nextTrack, false);
    }
    if (scene.uTurnPath) {
      ctx.lineWidth = 5; ctx.strokeStyle = '#4df24d'; // green = U-turn arc
      strokePts(scene.uTurnPath, false);
    }
    if (scene.guidanceLine) {
      ctx.lineWidth = 5; ctx.strokeStyle = '#fc56ba'; // magenta = current/followed line
      strokePts(scene.guidanceLine, false);
    }
  }
  toolFootprint();
  if (rp) vehicle(rp);
  lightbar();
  updateLightbarText(); // DOM overlay — shared by both renderers (draw() always runs)
  renderStatusBar();    // top status bar (DOM); always runs

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
    `speed: ${spd}   sections on: ${secs}\n${guid}\nzoom: ${pxPerM.toFixed(1)} px/m   follow: ${follow ? 'on' : 'off'}  (DR)\n` +
    `coverage: ${cov ? `${cov.width}x${cov.height} @ ${cov.cellSize.toFixed(2)}m, ${covCells} cells` : '—'}\n` +
    `imagery: ${imageryImg ? 'loaded' : imageryRect ? 'loading…' : 'none'}\n` +
    `canvaskit: ${ckStatus}   renderer: ${useSkia ? 'skia' : '2d'}   tilt: ${(pitch * 180 / Math.PI).toFixed(0)}°`;

  requestAnimationFrame(draw);
}
draw();

// ---- Skia (CanvasKit) render — Phase A: vector layers at parity. Works in CSS
//      px (canvas.scale(dpr)), reusing w2s. Coverage/imagery/tool/lightbar TODO. ----
// CanvasKit 0.41: Path is immutable (no moveTo/reset) — build via MakeFromCmds
// (flat [VERB,x,y,...]) for polylines; drawLine for single segments.
function strokePtsSk(canvas, pts, close, paint) {
  if (!pts || pts.length < 2) return;
  const cmds = [];
  for (let i = 0; i < pts.length; i++) {
    const xy = w2s(pts[i].e, pts[i].n);
    cmds.push(i === 0 ? CK.MOVE_VERB : CK.LINE_VERB, xy[0], xy[1]);
  }
  if (close) cmds.push(CK.CLOSE_VERB);
  const path = CK.Path.MakeFromCmds(cmds);
  if (path) { canvas.drawPath(path, paint); path.delete(); }
}
function segSk(canvas, e1, n1, e2, n2, paint) {
  const a = w2s(e1, n1), b = w2s(e2, n2);
  canvas.drawLine(a[0], a[1], b[0], b[1], paint);
}
function drawGridSk(canvas) {
  const G = 2000;
  let halfW = (vw / 2) / pxPerM, halfH = (vh / 2) / pxPerM;
  if (perspM) { halfW = Math.max(halfW, 180); halfH = Math.max(halfH, 180); } // tilt sees farther
  const minE = Math.max(camE - halfW, -G), maxE = Math.min(camE + halfW, G);
  const minN = Math.max(camN - halfH, -G), maxN = Math.min(camN + halfH, G);
  if (minE >= maxE || minN >= maxN) return;
  const toolW = toolWidthM();
  const spacing = toolW * Math.max(1, niceStep125(Math.max(vw, vh) / pxPerM / (toolW * 30)));
  for (let k = Math.ceil(minE / spacing); k * spacing <= maxE; k++)
    segSk(canvas, k * spacing, minN, k * spacing, maxN, k % 10 === 0 ? SKP.gridMajor : SKP.gridMinor);
  for (let k = Math.ceil(minN / spacing); k * spacing <= maxN; k++)
    segSk(canvas, minE, k * spacing, maxE, k * spacing, k % 10 === 0 ? SKP.gridMajor : SKP.gridMinor);
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
function renderSkia(canvas) {
  canvas.clear(ckColor('#0f1115'));
  canvas.save();
  canvas.scale(dpr, dpr); // work in CSS px so w2s + stroke widths match the 2D path
  const rp = renderPose();
  if (rp && follow) { camE = rp.e; camN = rp.n; }
  updatePerspective();
  drawImagerySk(canvas); // bottom layer
  drawCoverageSk(canvas);
  drawGridSk(canvas);
  if (scene) {
    for (const ring of scene.boundaries) strokePtsSk(canvas, ring, true, SKP.boundary);
    if (scene.headland) strokePtsSk(canvas, scene.headland, true, SKP.headland);
    const activeName = tick ? tick.activeTrackName : null;
    for (const tr of scene.tracks)
      strokePtsSk(canvas, tr.points, false, (activeName && tr.name === activeName) ? SKP.reference : SKP.track);
    if (scene.nextTrack) strokePtsSk(canvas, scene.nextTrack, false, SKP.next);
    if (scene.uTurnPath) strokePtsSk(canvas, scene.uTurnPath, false, SKP.uturn);
    if (scene.guidanceLine) strokePtsSk(canvas, scene.guidanceLine, false, SKP.guidance);
  }
  toolFootprintSk(canvas);
  if (rp) vehicleSk(canvas, rp);
  lightbarSk(canvas); // screen-space overlay, still inside the dpr scale
  canvas.restore();
}
function skFrame() {
  // Window rAF (not surface.requestAnimationFrame) so the loop survives surface
  // recreation on resize; re-reads the global skSurface each frame.
  if (useSkia && skSurface) {
    try { renderSkia(skSurface.getCanvas()); skSurface.flush(); }
    catch (e) { ckStatus = 'render err: ' + (e && e.message || e); useSkia = false; applyRenderer(); }
  }
  requestAnimationFrame(skFrame);
}
