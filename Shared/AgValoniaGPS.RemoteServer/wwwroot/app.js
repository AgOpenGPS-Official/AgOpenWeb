// Renderer — consumes plain Scene + Tick objects via the transport interface.
// It has NO knowledge of SignalR or the wire format (see transport.js); swapping
// the transport at Phase 2 leaves this file untouched. Canvas2D for now; the
// CanvasKit swap (and client-side dead-reckoning) slot in here without touching
// the transport seam.

const cv = document.getElementById('c');
const ctx = cv.getContext('2d');
const hud = document.getElementById('hud');

// Logical (CSS-pixel) canvas size. The backing store is scaled by the device
// pixel ratio so vectors render at native resolution on hi-DPI screens (tablets,
// retina) — otherwise thin strokes look faint and shimmer when panning. All draw
// code works in these logical coordinates; the dpr scale is baked into ctx.
let vw = innerWidth, vh = innerHeight;
function resize() {
  const dpr = Math.min(window.devicePixelRatio || 1, 2); // cap: 3× phones don't need 9× fill
  vw = innerWidth; vh = innerHeight;
  cv.width = Math.round(vw * dpr);
  cv.height = Math.round(vh * dpr);
  cv.style.width = vw + 'px';
  cv.style.height = vh + 'px';
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0); // sticky; save/restore in draw preserve it
}
addEventListener('resize', resize); resize();

// ---- model (fed by the transport) ----
let scene = null;      // SceneDto
let tick = null;       // TickDto (latest — for sections/HUD)
let lastTick = null;   // { e, n, heading, speed, t } authoritative pose + receipt time, for DR
let connState = 'connecting…';

// ---- coverage offscreen (Phase 2): cells painted into a cell-grid canvas,
//      blitted to world space each frame. Snapshot on connect, deltas after. ----
let cov = null;        // { cellSize, originE, originN, width, height, canvas, cctx }
let covCells = 0;

// ---- client-owned camera (never crosses the wire) ----
let pxPerM = 4.0;
let follow = true;
let camE = 0, camN = 0;

// ---- transport wiring (the only coupling point) ----
const transport = RemoteTransport.create({
  onScene(s) {
    scene = s;
    if (follow && (!tick || !tick.pose) && s.boundaries.length && s.boundaries[0].length) {
      const r = s.boundaries[0];
      camE = r.reduce((a, p) => a + p.e, 0) / r.length;
      camN = r.reduce((a, p) => a + p.n, 0) / r.length;
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
  },
  onStatus(s) { connState = s; },
});
transport.start();

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
addEventListener('keydown', e => { if (e.key === 'f' || e.key === 'F') follow = true; });

// ---- render ----
function w2s(e, n) {
  return [vw / 2 + (e - camE) * pxPerM, vh / 2 - (n - camN) * pxPerM];
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
  ctx.rotate(p.heading); // radians, 0 = north (up), clockwise
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
  const x0 = (vw - totalW) / 2, top = 22;
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

  const cm = Math.abs(xte) * 100;
  const arrow = onLine ? '●' : steerLeft ? '◀' : '▶'; // arrow = steer direction
  ctx.save();
  ctx.fillStyle = '#cfe3ff';
  ctx.font = '600 15px system-ui, sans-serif';
  ctx.textAlign = 'center';
  ctx.fillText(`${arrow} ${cm.toFixed(0)} cm   ${tick.lineLabel || ''}`, vw / 2, top + H + 17);
  ctx.restore();
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
function draw() {
  ctx.clearRect(0, 0, vw, vh);

  const rp = renderPose();
  if (rp && follow) { camE = rp.e; camN = rp.n; }

  drawCoverage();

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
    `coverage: ${cov ? `${cov.width}x${cov.height} @ ${cov.cellSize.toFixed(2)}m, ${covCells} cells` : '—'}`;

  requestAnimationFrame(draw);
}
draw();
