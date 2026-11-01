// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// Flags, Ref Nudge, AB line creation, drawing on the map (boundary, headland, point edit), the tracks manager.

// ---- flag list (mirrors native FlagListDialogPanel) ----
// Colour swatch (tap → 10-colour picker), name (tap → inline rename), distance+bearing
// from the vehicle (computed here off the Tick), locate (pan the camera to the flag),
// delete. Footer: Place Here / Place on Map / Delete All / Close. Reads scene.flags
// (index = the projected order, which matches the host's Flags list).
const FLAG_HEX = ['#FF0000', '#00CC00', '#FFCC00', '#2080E0', '#FF8800', '#9933CC', '#00BBCC', '#FF66AA', '#FFFFFF', '#333333'];
const FLAG_NAMES = [tr('Red'), tr('Green'), tr('Yellow'), tr('Blue'), tr('Orange'), tr('Purple'), tr('Cyan'), tr('Pink'), tr('White'), tr('Black')];
const COMPASS = ['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW'];
document.getElementById('bn-flag-list').addEventListener('pointerdown', e => {
  e.stopPropagation();
  bnFlags.classList.remove('open');
  document.getElementById('bn-flags').classList.remove('menuopen');
  openFlagList();
});
function openFlagList() { renderFlagList(); openDialog('dlg-flags'); }
function renderFlagList() {
  const list = document.getElementById('flag-list');
  const fl = (scene && scene.flags) || [];
  if (!fl.length) { list.innerHTML = '<div class="trk-empty">No flags placed</div>'; return; }
  list.innerHTML = '';
  fl.forEach((f, i) => {
    let dist = '';
    if (tick && tick.pose) {
      const de = f.e - tick.pose.e, dn = f.n - tick.pose.n;
      const brg = (Math.atan2(de, dn) * 180 / Math.PI + 360) % 360;
      dist = Math.hypot(de, dn).toFixed(0) + ' m ' + COMPASS[Math.round(brg / 45) % 8];
    }
    const row = document.createElement('div');
    row.className = 'flg-row';
    row.innerHTML =
      '<button class="flg-sw"></button>' +
      '<span class="flg-name"></span>' +
      '<span class="flg-dist"></span>' +
      '<button class="flg-loc" title="Locate (pan to flag)">⊕</button>' +
      '<button class="flg-del" title="Delete flag">✕</button>';
    row.querySelector('.flg-sw').style.background = f.color;
    const nameEl = row.querySelector('.flg-name');
    nameEl.textContent = f.name || tr('Flag {n}', { n: i + 1 });
    row.querySelector('.flg-dist').textContent = dist;
    // Colour swatch → toggle an inline 10-colour picker under the row.
    row.querySelector('.flg-sw').addEventListener('pointerdown', ev => { ev.stopPropagation(); toggleFlagColorPick(row, i); });
    // Name → inline rename.
    nameEl.addEventListener('pointerdown', ev => { ev.stopPropagation(); editFlagName(nameEl, i); });
    // Locate → pan the camera to the flag, close the dialog to reveal the map.
    row.querySelector('.flg-loc').addEventListener('pointerdown', ev => {
      ev.stopPropagation(); camE = f.e; camN = f.n; cameraMode = 2; closeDialog();
    });
    row.querySelector('.flg-del').addEventListener('pointerdown', ev => { ev.stopPropagation(); transport.send('flag.delete|' + i); });
    list.appendChild(row);
  });
}
function toggleFlagColorPick(row, idx) {
  const existing = row.nextSibling;
  if (existing && existing.classList && existing.classList.contains('flg-colorpick')) { existing.remove(); return; }
  for (const c of document.querySelectorAll('.flg-colorpick')) c.remove();
  const pick = document.createElement('div');
  pick.className = 'flg-colorpick';
  FLAG_HEX.forEach((hex, ci) => {
    const b = document.createElement('button');
    b.style.background = hex; b.title = FLAG_NAMES[ci];
    b.addEventListener('pointerdown', ev => { ev.stopPropagation(); transport.send('flag.setColor|' + idx + ',' + FLAG_NAMES[ci]); pick.remove(); });
    pick.appendChild(b);
  });
  row.after(pick);
}
function editFlagName(nameEl, idx) {
  const cur = nameEl.textContent;
  const input = document.createElement('input');
  input.className = 'flg-nameedit'; input.value = cur;
  nameEl.replaceWith(input); input.focus(); input.select();
  const commit = () => { const v = input.value.trim(); if (v && v !== cur) transport.send('flag.rename|' + idx + ',' + v); renderFlagList(); };
  input.addEventListener('keydown', ev => { ev.stopPropagation(); if (ev.key === 'Enter') commit(); else if (ev.key === 'Escape') renderFlagList(); });
  input.addEventListener('blur', commit);
}
document.getElementById('flag-placehere').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); transport.send('flag.placeHere'); });
document.getElementById('flag-placemap').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); closeDialog(); armPlaceFlagOnMap(); });
document.getElementById('flag-deleteall').addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation();
  showConfirm(tr('Delete All Flags'), tr('Delete all flags? This cannot be undone.'), () => transport.send('flag.deleteAll'));
});
document.getElementById('flag-close').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); closeDialog(); });
document.getElementById('bn-abmenu').addEventListener('pointerdown', e => {
  e.stopPropagation(); bnToggleFly(bnAb, e.currentTarget);
  populateCfgControls(bnAb, true); // the snap distance
});
wireCfgControls(bnAb);

// ---- Ref Nudge panel (#489, AgOpenGPS FormRefNudge) ----
// Moves the saved line itself (the AB flyout's nudge only offsets the guidance line). The panel
// stays open: the right nav's track buttons pick the next line, each move is saved by the host
// at once, Exit closes. The total and the step come from the host (tick.op.refNudgeMoved, the
// vehicle profile's autosteer.refNudgeDistance); nothing is computed here.
const refPanel = document.getElementById('refnudge');
const refMoved = document.getElementById('rf-moved');
const refName = document.getElementById('rf-name');
let refNudgeOpen = false;
function openRefNudge() {
  refNudgeOpen = true;
  transport.send('track.refNudgeBegin'); // the total starts at zero
  refPanel.hidden = false;
  populateCfgControls(refPanel, true);
}
function closeRefNudge() { refNudgeOpen = false; refPanel.hidden = true; }
document.getElementById('bn-refnudge').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); abFlyoutClose(); openRefNudge(); });
const refUndo = document.getElementById('rf-undo');
const refSave = document.getElementById('rf-save');
let refPending = false; // an unsaved move on the selected line (tick.op.refNudgePending)
document.getElementById('rf-exit').addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation();
  if (!refPending) { closeRefNudge(); return; }
  // Leaving would undo the move (the next line takes over): ask, Cancel keeps the panel for Undo.
  showConfirm(tr('Reference Line Nudge'), tr('Save the moved line before closing?'), () => { transport.send('track.refNudgeSave'); closeRefNudge(); });
});
for (const b of refPanel.querySelectorAll('button[data-cmd]'))
  b.addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); transport.send(b.dataset.cmd); });
wireCfgControls(refPanel);
function renderRefNudge(hasTrack) {
  const show = refNudgeOpen && hasTrack;
  if (refPanel.hidden === show) refPanel.hidden = !show;
  if (!show) return;
  const m = (tick && tick.op && tick.op.refNudgeMoved) || 0;
  const amt = Math.round(Math.abs(m) * (isMetric() ? 100 : 39.3701));
  const text = amt === 0 ? '0' : amt + ' ' + unitLabel('cm') + (m < 0 ? ' L' : ' R');
  if (refMoved.textContent !== text) refMoved.textContent = text;
  const name = tick.activeTrackName || '';
  if (refName.textContent !== name) refName.textContent = name;
  refPending = !!(tick.op && tick.op.refNudgePending);
  refUndo.disabled = !refPending;
  refSave.disabled = !refPending;
  populateCfgControls(refPanel); // the step; not while it is being typed
}

// ---- AB-line creation (mirrors native QuickABSelector + DrawAB dialogs) ----
// All four modes run the REAL native commands on the host (no geometry in JS):
//   straight/curve   — DrawAB/DrawCurve: tap points on the map (s2w → track.drawPoint)
//   driveAB          — set A then B at the vehicle's live GPS (track.setABGps)
//   recordCurve      — record a curve by driving, Finish to create
// The client only captures input, mirrors a live preview for the map-tap modes, and
// ships points/commands. `drawMode` drives the map-tap preview; `abFlow` is the active
// creation flow (any of the four) that the bottom toolbar + hint follow.
let drawMode = null;     // 'straight' | 'curve' (map-tap modes only) — drives the preview
let drawPts = [];        // [{e,n}] captured so far — preview only (host holds the real list)
let abFlow = null;       // 'straight' | 'curve' | 'driveAB' | 'recordCurve' | null
let driveStep = 0;       // Drive-AB / record-curve step: 0 → next press sets A/Start, 1 → sets B/End
let curveMarks = [];     // client-side path dots dropped while driving a record-curve (Set Start→End)
let _lastCurveMark = null;
const drawBar = document.getElementById('draw-toolbar');
const hintEl = document.getElementById('maptap-hint');
function abHint() {
  switch (abFlow) {
    case 'straight': return drawPts.length === 0 ? tr('Tap Point A') : tr('Tap Point B');
    case 'curve': return tr('Tap points ({n}) — Finish when done', { n: drawPts.length });
    case 'driveAB': return driveStep === 0 ? tr('Drive to A, then Set Point') : tr('Drive to B, then Set Point');
    case 'recordCurve': return recPivot
      ? (driveStep === 0 ? tr('Drive onto the pivot track, then Start') : tr('Drive part of the way round the pivot, then Finish'))
      : (driveStep === 0 ? tr('Drive to A, then Set Point A') : tr('Drive the curve to B, then Set Point B'));
  }
  return '';
}
function setAbHint() { hintEl.textContent = abHint(); }
// #40: while drawing a straight AB line, ride an "A"/"B" letter on the crosshair so it's
// clear which point the next tap places, and render the placed points as native-style dots
// (orange A / blue B, matching FormABDraw's DrawABTouchPoints colours).
const AB_A_COLOR = '#FFBF59', AB_B_COLOR = '#8080FF';
const abLabelEl = document.getElementById('maptap-ab');
const abDotLabelsEl = document.getElementById('ab-dot-labels');
// Sequential point label: A, B, C … then 27, 28 … for very long curves.
function abLetter(i) { return i < 26 ? String.fromCharCode(65 + i) : String(i + 1); }
function abLabelColor(i) { return i === 0 ? AB_A_COLOR : i === 1 ? AB_B_COLOR : '#FFFFFF'; }
// A/B (straight) or A,B,C… (curve) letter riding the crosshair — the letter the NEXT tap places.
function updateAbCursorLabel(x, y) {
  if (!abLabelEl) return;
  if (mapTap && (abFlow === 'straight' || abFlow === 'curve')) {
    const i = drawPts.length;
    abLabelEl.textContent = abLetter(i);
    abLabelEl.style.color = abLabelColor(i);
    if (x != null) { abLabelEl.style.left = x + 'px'; abLabelEl.style.top = y + 'px'; }
    abLabelEl.classList.add('show');
  } else {
    abLabelEl.classList.remove('show');
  }
}
// Persistent letter beside each dropped point (straight A/B, curve A,B,C…). Positioned per
// frame from drawPts (world→screen); cleared when the draw ends. DOM (CanvasKit has no text).
function renderAbDotLabels() {
  if (!abDotLabelsEl) return;
  // Letters for map-tap AND Drive-AB markers (drawMode may be null), else the active line's A/B.
  const active = activeAbPoints(), pts = drawPts.length ? drawPts : (active || []);
  const n = pts.length;
  while (abDotLabelsEl.children.length < n) abDotLabelsEl.appendChild(document.createElement('span'));
  for (let i = 0; i < abDotLabelsEl.children.length; i++) {
    const span = abDotLabelsEl.children[i];
    if (i >= n || pw(pts[i].e, pts[i].n) < 1.0) { span.style.display = 'none'; continue; }
    const xy = w2s(pts[i].e, pts[i].n);
    span.textContent = abLetter(i);
    span.style.color = drawPts.length ? abLabelColor(i) : (i ? ACTIVE_B_COLOR : ACTIVE_A_COLOR);
    span.style.left = (xy[0] + mapL) + 'px';
    span.style.top = xy[1] + 'px';
    span.style.display = 'block';
  }
}
// Configure the bottom toolbar buttons for the active flow.
function showAbBar(setPoint, undo, finish) {
  drawBar.querySelector('#draw-setpoint').style.display = setPoint ? '' : 'none';
  drawBar.querySelector('#draw-undo').style.display = undo ? '' : 'none';
  drawBar.querySelector('#draw-finish').style.display = finish ? '' : 'none';
  drawBar.classList.add('show');
}
function startDrawTrack(mode) {           // map-tap straight/curve
  abFlow = mode; drawMode = mode; drawPts = [];
  transport.send(mode === 'straight' ? 'track.drawStraight' : 'track.drawCurve');
  showAbBar(false, mode === 'curve', mode === 'curve');
  startMapTap({ hint: abHint(), onTap: drawTap });
}
function startDriveAB() {                  // GPS: set A then B at the vehicle
  abFlow = 'driveAB'; driveStep = 0; drawPts = []; driveRaw = []; // drawPts holds the dropped A/B markers
  transport.send('track.driveAB');
  showAbBar(true, false, false);
  document.getElementById('draw-setpoint').textContent = tr('Set Point A');
  hintEl.classList.add('show'); setAbHint();
}
// A water pivot is recorded the same way: Start, drive part of the way round, Finish; the host
// fits the circle (AgOpenGPS adds three points round it by hand).
let recPivot = false;
function startRecordCurve(pivot = false) { // GPS: record by driving — Set Start → Set End
  abFlow = 'recordCurve'; driveStep = 0; curveMarks = []; _lastCurveMark = null; recPivot = pivot;
  // Recording doesn't begin until "Set Start" (deferred track.recordCurve), so the operator
  // can position at the curve's start first. The set-point button drives the two-step.
  showAbBar(true, false, false);
  document.getElementById('draw-setpoint').textContent = pivot ? tr('Start') : tr('Set Point A');
  hintEl.classList.add('show'); setAbHint();
}
// "Bnd. Curve" — tap point A then point B on the boundary, then pick which way round the
// curve goes (#450): A → B runs on from A in the boundary's own order, B → A the other way;
// the shorter one is picked first and drawn on the map. Each tap lands on the fence where
// it's nearest (a corner when one is close); the host projects A and B again and takes the
// way round that passes the chosen arc's midpoint.
let bcArcs = null, bcSel = 0;  // the two ways round on the outer boundary, [A→B, B→A]
function startBoundaryCurve() {
  abFlow = 'boundaryCurve'; drawPts = []; bcArcs = null;
  showAbBar(false, false, false);          // draw toolbar shows just Cancel
  startMapTap({ hint: tr('Tap point A on the boundary'), onTap: boundaryCurveTap });
}
function boundaryCurveTap(e, n) {
  drawPts.push(snapToFence(e, n, Infinity)); // A/B on the fence (dot shown)
  if (drawPts.length < 2) { hintEl.textContent = tr('Tap point B on the boundary'); return; }
  const ring = scene && scene.boundaries && scene.boundaries[0];
  bcArcs = ring && ring.length >= 3 ? boundaryArcs(ring, drawPts[0], drawPts[1]) : null;
  if (!bcArcs) { abFinish(); return; }     // no ring to choose on: the host takes the shorter
  bcSel = polyLen(bcArcs[0]) <= polyLen(bcArcs[1]) ? 0 : 1;
  mapTap = null; document.getElementById('maptap-hint').classList.remove('show'); // no more taps
  showAbBar(false, false, true);
  document.getElementById('draw-finish').textContent = tr('Create');
  for (const b of bcDirBtns) b.style.display = '';
  setBcDir(bcSel);
}
const bcDirBtns = [document.getElementById('draw-dir-ab'), document.getElementById('draw-dir-ba')];
bcDirBtns.forEach((b, i) => b.addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); setBcDir(i); }));
function setBcDir(i) {
  bcSel = i;
  bcDirBtns.forEach((b, k) => b.classList.toggle('active', k === i));
  hintEl.textContent = tr('Pick which way round, then Create'); hintEl.classList.add('show');
}
function polyLen(pts) { let l = 0; for (let i = 1; i < pts.length; i++) l += Math.hypot(pts[i].e - pts[i - 1].e, pts[i].n - pts[i - 1].n); return l; }
// The two ways round a ring between points a and b on it: [a on in ring order to b, b on to a].
function boundaryArcs(ring, a, b) {
  const m = ring.length;
  const at = (p) => {                      // the side (i → i+1) a point lies on, and how far along
    let best = { i: 0, t: 0 }, bd = Infinity;
    for (let i = 0; i < m; i++) {
      const p0 = ring[i], p1 = ring[(i + 1) % m];
      const dx = p1.e - p0.e, dn = p1.n - p0.n, l2 = dx * dx + dn * dn;
      const t = l2 > 0 ? Math.max(0, Math.min(1, ((p.e - p0.e) * dx + (p.n - p0.n) * dn) / l2)) : 0;
      const d = Math.hypot(p0.e + t * dx - p.e, p0.n + t * dn - p.n);
      if (d < bd) { bd = d; best = { i, t }; }
    }
    return best;
  };
  const walk = (p, pa, q, qa) => {         // from p on in ring order to q
    const out = [p];
    if (!(pa.i === qa.i && pa.t <= qa.t))
      for (let k = (pa.i + 1) % m; ; k = (k + 1) % m) { out.push(ring[k]); if (k === qa.i) break; }
    out.push(q);
    return out;
  };
  const sa = at(a), sb = at(b);
  return [walk(a, sa, b, sb), walk(b, sb, a, sa)];
}
// A drawn curve as the host will make it: between two taps in a row on the fence it follows
// the fence the shorter way round (AgOpenGPS FormABDraw); elsewhere it runs from tap to tap.
function drawnCurvePreview() {
  const ring = scene && scene.boundaries && scene.boundaries[0];
  if (!ring || ring.length < 3) return drawPts;
  const onFence = p => ring.some((a, i) => {
    const b = ring[(i + 1) % ring.length], dx = b.e - a.e, dn = b.n - a.n, l2 = dx * dx + dn * dn;
    const t = l2 > 0 ? Math.max(0, Math.min(1, ((p.e - a.e) * dx + (p.n - a.n) * dn) / l2)) : 0;
    return Math.hypot(a.e + t * dx - p.e, a.n + t * dn - p.n) < 0.01;
  });
  const out = [drawPts[0]];
  for (let i = 1; i < drawPts.length; i++) {
    const a = drawPts[i - 1], b = drawPts[i];
    if (onFence(a) && onFence(b)) {
      const arcs = boundaryArcs(ring, a, b);
      const run = polyLen(arcs[0]) <= polyLen(arcs[1]) ? arcs[0] : arcs[1].slice().reverse();
      out.push(...run.slice(1));
    } else out.push(b);
  }
  return out;
}
// The chosen way round, drawn while picking (#450).
function drawBoundaryCurvePreviewSk(canvas) {
  if (abFlow !== 'boundaryCurve' || !bcArcs) return;
  strokePtsSk(canvas, bcArcs[bcSel], false, SKP.track);
}
// A drawn line is the edge between passes (PassGeometry), as in AgOpenGPS's FormABDraw,
// whose taps all land on fence points. A tap near the fence snaps onto it (a corner when
// one is close, else the nearest point on the fence), so a line drawn along the boundary
// lies on it and its first pass is half a width inside. Measured on screen, so the reach
// is the same at any zoom or tilt.
const FENCE_SNAP_PX = 24;
function snapToFence(e, n, reach = FENCE_SNAP_PX) {
  const bs = scene && scene.boundaries;
  if (!bs) return { e, n };
  const t = w2s(e, n);
  const px = (q) => { const s = w2s(q.e, q.n); return Math.hypot(s[0] - t[0], s[1] - t[1]); };
  let corner = null, cd = FENCE_SNAP_PX, edge = null, ed = reach;
  for (const ring of bs) for (let i = 0; i < ring.length; i++) {
    const a = ring[i], b = ring[(i + 1) % ring.length];
    const d = px(a);
    if (d < cd) { cd = d; corner = a; }
    const dx = b.e - a.e, dn = b.n - a.n, len2 = dx * dx + dn * dn;
    if (len2 === 0) continue;
    const u = Math.max(0, Math.min(1, ((e - a.e) * dx + (n - a.n) * dn) / len2));
    const q = { e: a.e + u * dx, n: a.n + u * dn }, dq = px(q);
    if (dq < ed) { ed = dq; edge = q; }
  }
  const p = corner || edge;
  return p ? { e: p.e, n: p.n } : { e, n };
}
function drawTap(e, n) {                    // map-tap point captured (straight/curve)
  ({ e, n } = snapToFence(e, n));
  drawPts.push({ e, n });
  transport.send('track.drawPoint|' + e.toFixed(3) + ',' + n.toFixed(3));
  // Straight = exactly 2 points; the host auto-creates the line on the 2nd, so we're done.
  if (abFlow === 'straight' && drawPts.length >= 2) { endAbFlow(); return; }
  setAbHint();
}
// A driven line's reference goes to the implement's edge on the First pass side (the host's
// RefSideOffset), so the A/B markers and path dots go there too, not under the tractor.
let driveRaw = [];       // Drive-AB A/B at the vehicle, to re-place both on the A→B heading
function refEdgePt(e, n, h) {
  const t = config && config.tool;
  const half = Math.max(0, toolWidthM() - ((t && t.overlap) || 0)) * 0.5;
  const d = (qabSide === 'right' ? half : -half) + ((t && t.offset) || 0);
  return { e: e + Math.sin(h + Math.PI / 2) * d, n: n + Math.cos(h + Math.PI / 2) * d };
}
function poseEdgePt() { const p = tick.pose; return refEdgePt(p.e, p.n, p.heading || 0); }
function abSetPoint() {                     // Set-point button — drives Drive-AB and record-curve
  const btn = document.getElementById('draw-setpoint');
  if (abFlow === 'driveAB') {
    if (driveStep > 1) return;             // guard a double-B while B lingers
    transport.send('track.setABGps');      // SetABPoint uses live GPS in DriveAB mode
    // Drop an A/B marker where the line will lie. B fixes the heading the host shifts by
    // (A→B), so both markers are placed again on it.
    if (tick && tick.pose) {
      driveRaw.push({ e: tick.pose.e, n: tick.pose.n });
      if (driveRaw.length >= 2) {
        const a = driveRaw[0], b = driveRaw[driveRaw.length - 1];
        const h = Math.atan2(b.e - a.e, b.n - a.n);
        drawPts = driveRaw.slice(0, 2).map(p => refEdgePt(p.e, p.n, h));
      } else drawPts.push(poseEdgePt());
    }
    if (driveStep === 0) { driveStep = 1; btn.textContent = tr('Set Point B'); setAbHint(); }
    else {                                  // B set → host created the line; show B a beat, then clear
      driveStep = 2;
      setTimeout(() => { if (abFlow === 'driveAB') endAbFlow(); }, 700);
    }
  } else if (abFlow === 'recordCurve') {
    if (driveStep > 1) return;              // guard a double-B while B lingers
    if (driveStep === 0) {                  // Set Point A → drop the A endpoint + begin recording
      transport.send(recPivot ? 'track.recordPivot' : 'track.recordCurve');
      driveStep = 1; btn.textContent = recPivot ? tr('Finish') : tr('Set Point B');
      if (tick && tick.pose) {
        drawPts.push(poseEdgePt());                          // labeled "A" endpoint dot (orange)
        _lastCurveMark = { e: tick.pose.e, n: tick.pose.n }; // seed green-dot spacing; no dot at A itself
      }
      setAbHint();
    } else {                                // Set Point B → drop the B endpoint + finish
      driveStep = 2;
      if (tick && tick.pose) drawPts.push(poseEdgePt());    // labeled "B" endpoint dot (blue)
      transport.send('track.finishCurve');  // host builds the curve from its recorded points
      setTimeout(() => { if (abFlow === 'recordCurve') endAbFlow(); }, 700);   // show B a beat, then clear
    }
  }
}
function abUndo() {
  if (abFlow !== 'curve' || !drawPts.length) return;
  drawPts.pop();
  transport.send('track.drawUndo');
  setAbHint();
}
function abFinish() {
  if (abFlow === 'boundaryCurve') {
    if (drawPts.length < 2) return;
    const a = drawPts[0], b = drawPts[1], f = (v) => v.toFixed(3);
    let msg = 'track.boundaryCurveSeg|' + f(a.e) + ',' + f(a.n) + ',' + f(b.e) + ',' + f(b.n);
    if (bcArcs) {                          // + a point halfway along the chosen way round
      const arc = bcArcs[bcSel], half = polyLen(arc) / 2;
      let run = 0, via = arc[0];
      for (let i = 1; i < arc.length; i++) {
        const l = Math.hypot(arc[i].e - arc[i - 1].e, arc[i].n - arc[i - 1].n);
        if (run + l >= half) { const u = l > 0 ? (half - run) / l : 0; via = { e: arc[i - 1].e + u * (arc[i].e - arc[i - 1].e), n: arc[i - 1].n + u * (arc[i].n - arc[i - 1].n) }; break; }
        run += l;
      }
      msg += ',' + f(via.e) + ',' + f(via.n);
    }
    transport.send(msg);
  }
  else if (abFlow === 'curve') transport.send('track.drawFinish');
  else if (abFlow === 'recordCurve') transport.send('track.finishCurve');
  else return;
  endAbFlow();
}
function abCancel() {
  if (!abFlow) return;
  transport.send('track.drawCancel'); // CancelABCreationCommand — universal (all modes)
  endAbFlow();
}
function endAbFlow() {
  abFlow = null; drawMode = null; drawPts = []; driveRaw = []; driveStep = 0;
  bcArcs = null; for (const b of bcDirBtns) b.style.display = 'none';
  document.getElementById('draw-finish').textContent = tr('Finish');
  curveMarks = []; _lastCurveMark = null; // clear record-curve path dots
  drawBar.classList.remove('show');
  endMapTap();
  hintEl.classList.remove('show');
}
// Drop a path dot every ~1.5 m while recording a drive-curve (between Set Point A and B), so
// the operator sees the shape being captured. Visual only — the host records the real curve.
function sampleCurveMark() {
  if (abFlow !== 'recordCurve' || driveStep !== 1 || !tick || !tick.pose) return;
  const p = tick.pose;
  if (_lastCurveMark && Math.hypot(p.e - _lastCurveMark.e, p.n - _lastCurveMark.n) < 1.5) return;
  _lastCurveMark = { e: p.e, n: p.n };
  curveMarks.push(poseEdgePt());
}
function drawCurveMarksSk(canvas) {
  if (!curveMarks.length) return;
  const rad = Math.max(3, 0.4 * pxPerM);
  SKP.flagFill.setColor(ckColor('#39FF6A')); // recording green
  for (const p of curveMarks) {
    if (pw(p.e, p.n) < 1.0) continue; // behind the near plane
    const xy = w2s(p.e, p.n);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagFill);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagOutline);
  }
}
// ---- boundary draw-on-map (Phase MT) — native BoundaryMapDialog equivalent ----
// Shows a Bing aerial underlay (satEnabled) and captures the boundary polygon by tapping
// the imagery (s2w → field E/N). On finish the host builds the boundary AND assembles the
// covered Bing tiles into the field-background PNG (boundary.fromMapPoints). Client-side
// point buffer for the live preview; no geometry in JS.
let satBnd = false;      // drawing a boundary on the satellite underlay
let satPts = [];         // [{e,n}] polygon vertices (preview only)
function startSatBoundary() {
  satEnabled = true; satBnd = true; satPts = [];
  lnCloseAll();                           // close the boundary menu
  showAbBar(false, true, true);           // Undo + Finish + Cancel
  startMapTap({ hint: tr('Tap the field corners on the imagery — Finish when done'), onTap: satTap });
}
function satTap(e, n) { satPts.push({ e, n }); setSatHint(); }
function setSatHint() { hintEl.textContent = satPts.length === 1 ? tr('Boundary: 1 point — Finish when done') : tr('Boundary: {n} points — Finish when done', { n: satPts.length }); }
function satUndo() { if (satPts.length) { satPts.pop(); setSatHint(); } }
function satFinish() {
  if (satPts.length >= 3)
    transport.send('boundary.fromMapPoints|' + satPts.map(p => p.e.toFixed(3) + ',' + p.n.toFixed(3)).join(';'));
  endSatBoundary();
}
function satCancel() { endSatBoundary(); }
function endSatBoundary() {
  satBnd = false; satEnabled = false; satPts = [];
  drawBar.classList.remove('show'); endMapTap(); hintEl.classList.remove('show');
}

// ---- headland draw-on-map (Field Builder stage 2) — tap two boundary points -----------
// Preview snaps each tap to the nearest boundary vertex (visual only; the host re-snaps and
// builds the inward offset + headland). Ships headland.fromMapPoints on the 2nd tap.
let hlFlow = null;       // 'line' | 'curve' — drawing a headland segment, else null
let hlPts = [];          // [{e,n}] tapped+snapped boundary points (preview)
let hlOffset = 12;       // inward offset (m) chosen in the Add sub-view
function hlSnap(e, n) {
  const ring = scene && scene.boundaries && scene.boundaries[0];
  if (!ring || !ring.length) return { e, n };
  let best = ring[0], bd = Infinity;
  for (const p of ring) { const d = (p.e - e) ** 2 + (p.n - n) ** 2; if (d < bd) { bd = d; best = p; } }
  return { e: best.e, n: best.n };
}
function setHlHint() { hintEl.textContent = hlPts.length === 0 ? tr('Tap the start of an edge (add a line per edge to enclose the headland)') : tr('Tap the end of the edge'); }
function startHeadlandDraw(mode, offset) {
  const ring = scene && scene.boundaries && scene.boundaries[0];
  if (!ring || ring.length < 3) {
    const h = document.getElementById('maptap-hint');
    h.textContent = tr('No boundary to offset'); h.classList.add('show');
    setTimeout(() => h.classList.remove('show'), 1800);
    return;
  }
  hlFlow = mode; hlOffset = offset; hlPts = [];
  showAbBar(false, true, true);   // Undo + Finish + Cancel — draw MANY lines, Finish to stop
  startMapTap({ hint: 'Tap the start of an edge (add a line per edge to enclose the headland)', onTap: hlTap });
}
function hlTap(e, n) {
  hlPts.push(hlSnap(e, n));
  if (hlPts.length >= 2) {
    // Ship this edge line and immediately re-arm for the NEXT edge — headland drawing is
    // continuous (one line per edge) until the operator taps Finish, mirroring native.
    transport.send('headland.fromMapPoints|' + hlFlow + ',' + hlOffset + ',' +
      hlPts[0].e.toFixed(3) + ',' + hlPts[0].n.toFixed(3) + ',' +
      hlPts[1].e.toFixed(3) + ',' + hlPts[1].n.toFixed(3));
    hlPts = [];
    hintEl.textContent = tr('Edge added — draw the next edge, or tap Finish');
    return;
  }
  setHlHint();
}
function hlUndo() { if (hlPts.length) { hlPts.pop(); setHlHint(); } }
function endHeadlandDraw() {
  hlFlow = null; hlPts = [];
  drawBar.classList.remove('show'); endMapTap(); hintEl.classList.remove('show');
}

// ---- Field Builder stage 4 — on-map point editing -----------------------------------
// Drag a track's / headland segment's points on the main map, then Save. The client only
// captures the dragged positions; the host rebuilds (track heading recompute, or headland
// snap-to-boundary + re-offset + rebuild). One handle drag at a time; the map still pans
// when the press doesn't start on a handle.
let editSession = null;  // { kind:'track'|'headland', index, points:[{e,n}] }
let editDragIdx = -1;
function flashHint(t) { const h = document.getElementById('maptap-hint'); h.textContent = t; h.classList.add('show'); setTimeout(() => h.classList.remove('show'), 1800); }
function startTrackEdit() {
  const active = (scene && scene.trackList || []).find(t => t.active);
  const at = scene && scene.tracks && scene.tracks[0];
  if (!active || !at || !at.points || at.points.length < 2) { flashHint(tr('Select/activate a track to edit')); return; }
  editSession = { kind: 'track', index: active.index, points: at.points.map(p => ({ e: p.e, n: p.n })) };
  beginEdit(tr('Drag the track points, then Save'));
}
function startHeadlandEdit() {
  const hs = scene && scene.headlandSegs; if (fbHlSel < 0 || !hs || !hs[fbHlSel]) return;
  const s = hs[fbHlSel];
  if (s.type === 'Boundary') { flashHint(tr('Whole-boundary headland is not point-edited')); return; }
  editSession = { kind: 'headland', index: s.index, points: [{ e: s.endA.e, n: s.endA.n }, { e: s.endB.e, n: s.endB.n }] };
  beginEdit(tr('Drag the two endpoints (snap to boundary), then Save'));
}
function beginEdit(hint) {
  showAbBar(false, false, true);  // Save (Finish) + Cancel
  document.getElementById('draw-finish').textContent = tr('Save');
  hintEl.textContent = hint; hintEl.classList.add('show'); document.body.classList.add('maptap');
}
function editHitTest(px, py) {
  if (!editSession) return -1;
  let best = -1, bd = 22 * 22;
  for (let i = 0; i < editSession.points.length; i++) {
    const p = editSession.points[i];
    if ((pw(p.e, p.n)) < 1.0) continue;
    const xy = w2s(p.e, p.n); const dx = xy[0] - px, dy = xy[1] - py; const d = dx * dx + dy * dy;
    if (d < bd) { bd = d; best = i; }
  }
  return best;
}
function saveEdit() {
  if (!editSession) return;
  const pts = editSession.points;
  if (editSession.kind === 'track')
    transport.send('track.editSave|' + editSession.index + ';' + pts.map(p => p.e.toFixed(3) + ',' + p.n.toFixed(3)).join(';'));
  else if (pts.length >= 2)
    transport.send('headland.editSave|' + editSession.index + ',' + pts[0].e.toFixed(3) + ',' + pts[0].n.toFixed(3) + ',' + pts[1].e.toFixed(3) + ',' + pts[1].n.toFixed(3));
  endEdit();
}
function endEdit() {
  editSession = null; editDragIdx = -1;
  drawBar.classList.remove('show'); document.getElementById('draw-finish').textContent = tr('Finish');
  hintEl.classList.remove('show'); document.body.classList.remove('maptap');
}
function drawEditHandlesSk(canvas) {
  if (!editSession) return;
  if (editSession.points.length >= 2) strokePtsSk(canvas, editSession.points, false, SKP.track);
  const rad = Math.max(5, 0.7 * pxPerM);
  for (let i = 0; i < editSession.points.length; i++) {
    const p = editSession.points[i];
    if ((pw(p.e, p.n)) < 1.0) continue;
    const xy = w2s(p.e, p.n);
    SKP.flagFill.setColor(ckColor(i === editDragIdx ? '#FFD24A' : '#40E0FF'));
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagFill);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagOutline);
  }
}
document.getElementById('fb-hl-edit').addEventListener('pointerdown', e => { e.stopPropagation(); startHeadlandEdit(); });

// Draw toolbar serves whichever map-draw flow is active (AB line / boundary-on-map / headland).
drawBar.addEventListener('pointerdown', e => e.stopPropagation()); // don't pan / not a map tap
document.getElementById('draw-setpoint').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); abSetPoint(); });
document.getElementById('draw-undo').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); hlFlow ? hlUndo() : satBnd ? satUndo() : abUndo(); });
document.getElementById('draw-finish').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); editSession ? saveEdit() : hlFlow ? endHeadlandDraw() : satBnd ? satFinish() : abFinish(); });
document.getElementById('draw-cancel').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); editSession ? endEdit() : hlFlow ? endHeadlandDraw() : satBnd ? satCancel() : abCancel(); });
// Touch cancel for bare map-tap flows (flag). stopPropagation so the pill isn't taken as a map tap.
document.getElementById('maptap-cancel').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); if (mapTap && mapTap.onCancel) mapTap.onCancel(); else endMapTap(); });

// ---- AB flyout launchers → the three creation/management dialogs ----
function abFlyoutClose() { bnAb.classList.remove('open'); document.getElementById('bn-abmenu').classList.remove('menuopen'); }
document.getElementById('bn-tracks').addEventListener('pointerdown', e => { e.stopPropagation(); abFlyoutClose(); openTracksManager(); });
document.getElementById('bn-quickab').addEventListener('pointerdown', e => { e.stopPropagation(); abFlyoutClose(); qabHeadingReset(); qabSetSide('right'); openDialog('dlg-quickab'); });
// A+ heading box (#335): empty each time the dialog opens, the vehicle's heading as its
// placeholder (refreshed on focus); a typed value goes with track.aPlus.
const qabHeading = document.getElementById('qab-aplus-heading');
function vehicleHeadingDeg() {
  const h = tick && tick.pose && tick.pose.heading;
  return typeof h === 'number' ? ((h * 180 / Math.PI) % 360 + 360) % 360 : null;
}
function qabHeadingReset() {
  qabHeading.value = '';
  const d = vehicleHeadingDeg();
  qabHeading.placeholder = d === null ? '' : d.toFixed(1);
}
qabHeading.addEventListener('focus', () => { const d = vehicleHeadingDeg(); if (d !== null) qabHeading.placeholder = d.toFixed(1); });
// First-pass side for the driven modes (#345): Right each time the dialog opens, like
// AgOpenGPS; sent just before A+ / Drive AB / Curve so the host moves the line to that side.
const qabRefSide = document.getElementById('qab-refside');
let qabSide = 'right';
function qabSetSide(s) {
  qabSide = s;
  for (const b of qabRefSide.children) b.classList.toggle('active', b.dataset.side === s);
}
for (const b of qabRefSide.children)
  b.addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); qabSetSide(b.dataset.side); });
// Quick-AB selector buttons.
// Quick AB — the single creation hub: GPS-driven, from-boundary, and draw-on-map modes.
document.getElementById('dlg-quickab').querySelectorAll('[data-qab]').forEach(b => b.addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation(); closeDialog();
  const m = b.dataset.qab;
  if (m === 'aPlus' || m === 'driveAB' || m === 'recordCurve') transport.send('track.refSide|' + qabSide);
  if (m === 'aPlus') {
    // A+ builds an AB line from the vehicle's current position + heading. It needs a GPS
    // fix; the host bails silently without one, so give feedback here (StatusMessage isn't
    // on the wire). flashHint after a tick so it isn't cleared by the dialog close.
    const p = tick && tick.pose;
    if (!p || (!p.e && !p.n)) setTimeout(() => flashHint(tr('A+ needs a GPS position — start the simulator or GPS')), 0);
    else {
      const v = qabHeading.value.trim();
      transport.send('track.aPlus' + (v !== '' && isFinite(+v) ? '|' + (+v) : ''));
    }
  }
  else if (m === 'latLonHeading' || m === 'latLonAB' || m === 'latLonPivot') openLatLonLine(m);
  else if (m === 'recordPivot') startRecordCurve(true);
  else if (m === 'driveAB') startDriveAB();
  else if (m === 'recordCurve') startRecordCurve();
  else if (m === 'boundaryEdge') transport.send('track.createFromBoundary');
  else if (m === 'boundaryCurve') startBoundaryCurve();
  else if (m === 'allEdges') transport.send('track.allEdges');
  else if (m === 'drawStraight') startDrawTrack('straight');
  else if (m === 'drawCurve') startDrawTrack('curve');
}));
document.getElementById('dlg-qab-cancel').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); closeDialog(); });

// ---- Tracks manager (mirrors native TracksDialogPanel) ----
// View-only without control (just reads scene.trackList); the actions are Tier-2.
// Tapping a row highlights it (trkSel) and makes it the active track at once (#148), where
// AgOpenGPS waits for Use; Activate on the active track turns guidance off. Delete / Swap act
// on the highlighted row (#109).
let trkSel = -1; // index into scene.trackList, or -1
function openTracksManager() { trkSel = -1; renderTracksList(); openDialog('dlg-tracks'); }
function renderTracksList() {
  // Dim the guidance-affecting actions when we're not the operator. Delete/swap/activate
  // change the active line (every action needs control, #397; these are dimmed).
  for (const id of ['trk-delete', 'trk-swap', 'trk-activate'])
    document.getElementById(id).classList.toggle('disabled', !iHoldControl);
  const list = document.getElementById('trk-list');
  const tl = (scene && scene.trackList) || [];
  if (trkSel >= tl.length) trkSel = -1;
  if (!tl.length) { list.innerHTML = '<div class="trk-empty">No tracks in this field</div>'; return; }
  list.innerHTML = '';
  tl.forEach((t, i) => {
    const row = document.createElement('div');
    row.className = 'trk-row' + (t.active ? ' active' : '') + (i === trkSel ? ' sel' : '');
    row.innerHTML =
      '<input type="checkbox" class="trk-vis"' + (t.visible ? ' checked' : '') + '>' +
      '<span class="trk-name"></span>' +
      '<span class="trk-type"></span>' +
      '<span class="trk-dot"></span>';
    row.querySelector('.trk-name').textContent = t.name;
    row.querySelector('.trk-type').textContent = t.type || '—';
    // Tap row (not the checkbox) → that track becomes the active one, and stays
    // highlighted for Delete/Swap. A hidden track can't be activated (AgOpenGPS) (#148).
    // Checkbox → toggle visibility.
    row.addEventListener('pointerdown', e => {
      if (e.target.classList.contains('trk-vis')) return; // let the checkbox handle it
      e.stopPropagation();
      trkSel = t.visible ? i : -1;
      if (t.visible && iHoldControl) transport.send('track.select|' + t.index);
      renderTracksList();
    });
    const cb = row.querySelector('.trk-vis');
    cb.addEventListener('change', e => {
      e.stopPropagation();
      transport.send('track.setVisible|' + t.index + ',' + (cb.checked ? 1 : 0));
    });
    list.appendChild(row);
  });
}
document.getElementById('trk-delete').addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation();
  if (!iHoldControl) return;
  const t = (scene && scene.trackList || [])[trkSel];
  if (!t) { showToast(tr('Select a track first')); return; }
  showConfirm(tr('Delete Track'), tr("Delete '{name}'? This cannot be undone.", { name: t.name }),
    () => { transport.send('track.delete|' + t.index); trkSel = -1; },
    () => { renderTracksList(); openDialog('dlg-tracks'); }); // stay in the manager to delete more
});
document.getElementById('trk-swap').addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation();
  if (!iHoldControl) return;
  const t = (scene && scene.trackList || [])[trkSel];
  if (!t) { showToast(tr('Select a track first')); return; }
  transport.send('track.swapAB|' + t.index);
});
// Activate = AgOpenGPS "Use": the highlighted track (or the first visible one) becomes
// active and the dialog closes. On the already-active track it turns guidance off.
document.getElementById('trk-activate').addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation();
  if (!iHoldControl) return;
  const t = (scene && scene.trackList || [])[trkSel];
  transport.send('track.activate|' + (t ? t.index : -1));
  closeDialog();
});
document.getElementById('trk-recpaths').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); transport.send('track.toggleRecPaths'); });
document.getElementById('trk-import').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); closeDialog(); lnOpen('importtracks'); });
document.getElementById('dlg-tracks-close').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); closeDialog(); });
