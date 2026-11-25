// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// Skia map layers: lines and tracks, recording and drawing previews, flags, grid (and its step), hitch, vehicle, ground texture, imagery.

// ---- Skia (CanvasKit) render: the vector layers. Works in CSS px (canvas.scale(dpr)),
//      reusing w2s. ----
// CanvasKit 0.41: Path is immutable (no moveTo/reset) — build via MakeFromCmds
// (flat [VERB,x,y,...]) for polylines; drawLine for single segments.
// True when line `a` lies on polyline `b`: its middle point is within 5 cm of it.
function onPolyline(a, b) {
  if (!a || a.length < 2 || !b || b.length < 2) return false;
  const m = a.length === 2
    ? { e: (a[0].e + a[1].e) / 2, n: (a[0].n + a[1].n) / 2 } : a[a.length >> 1];
  for (let i = 0; i + 1 < b.length; i++) {
    const p = b[i], q = b[i + 1], de = q.e - p.e, dn = q.n - p.n, l2 = de * de + dn * dn;
    const u = l2 ? Math.max(0, Math.min(1, ((m.e - p.e) * de + (m.n - p.n) * dn) / l2)) : 0;
    if (Math.hypot(m.e - p.e - u * de, m.n - p.n - u * dn) < 0.05) return true;
  }
  return false;
}
// `paint` may be a list of paints, drawn in order on the one path (a shadow, then the line).
function strokePtsSk(canvas, pts, close, paint) {
  if (!pts || pts.length < 2) return;
  strokePtsSk3D(canvas, pts, close, paint);
}
// strokePtsSk for a line that is never changed in place: the Scene's (the host replaces its
// arrays, the page never edits them) or one made from it once. A long one is drawn from its copy
// without the points that add nothing to the picture (drawnPts). Not for a line being drawn or
// edited on the page (its points move or grow in place, and the copy would not follow).
function strokeFixedPtsSk(canvas, pts, close, paint) {
  if (!pts || pts.length < 2) return;
  strokePtsSk3D(canvas, drawnPts(pts), close, paint);
}
// A long line is drawn from a copy without the points that add nothing to the picture. A curve
// from the host carries a point about every metre: a boundary curve round a 2 km fence has 2,249,
// and its 20 extra guidelines up to 2,200 each, most of them on the straight stretches between
// the fence's corners. Projecting and stroking all of them on every frame took ~110 ms a frame on
// an older PC (Intel HD 4000): the map fell to 7 frames a second, the host's frames waited behind
// the drawing (the screen froze) and the page missed its presence heartbeat (AutoSteer off,
// "presence lost (deadman)"). The copy keeps every point more than 2 mm off the line between its
// neighbours (Douglas-Peucker; 0.4 px at the closest zoom, 200 px/m), so the line looks the same;
// it is made once per array (a WeakMap on the array itself).
const SIMPLIFY_MIN_PTS = 64, SIMPLIFY_TOL_M = 0.002;
const _drawnPts = new WeakMap();
function drawnPts(pts) {
  if (pts.length < SIMPLIFY_MIN_PTS) return pts;
  let s = _drawnPts.get(pts);
  if (!s) { s = simplifyPolyline(pts, SIMPLIFY_TOL_M); _drawnPts.set(pts, s); }
  return s;
}
// Douglas-Peucker: which of the points (xs[i], ys[i]) to keep so that none of the others lies more
// than `tol` from the chord of the stretch it is in; the first and last are always kept.
// Iterative, so a long line can't overflow the stack.
function dpKeep(xs, ys, tol) {
  const n = xs.length, keep = new Uint8Array(n);
  keep[0] = 1; keep[n - 1] = 1;
  const stack = [0, n - 1];
  while (stack.length) {
    const b = stack.pop(), a = stack.pop();
    if (b - a < 2) continue;
    const ax = xs[a], ay = ys[a], dx = xs[b] - ax, dy = ys[b] - ay, len = Math.hypot(dx, dy);
    let worst = -1, wi = -1;
    for (let i = a + 1; i < b; i++) {
      const d = len > 1e-9 ? Math.abs(dx * (ys[i] - ay) - dy * (xs[i] - ax)) / len
        : Math.hypot(xs[i] - ax, ys[i] - ay); // a closed stretch: its end is its start
      if (d > worst) { worst = d; wi = i; }
    }
    if (worst > tol) { keep[wi] = 1; stack.push(a, wi, wi, b); }
  }
  return keep;
}
// {e, n} points without those within `tol` metres of the line through their neighbours.
function simplifyPolyline(pts, tol) {
  const n = pts.length, xs = new Float64Array(n), ys = new Float64Array(n);
  for (let i = 0; i < n; i++) { xs[i] = pts[i].e; ys[i] = pts[i].n; }
  const keep = dpKeep(xs, ys, tol), out = [];
  for (let i = 0; i < n; i++) if (keep[i]) out.push(pts[i]);
  return out;
}
// The same on a screen polyline ([x, y] px), on every frame: a curve's points a metre apart are
// a pixel or less apart when the map is zoomed out, and Skia strokes every one of them (the
// most of a frame's time with a long curve's extra guidelines). A quarter of a pixel is not seen.
const SCREEN_SIMPLIFY_MIN_PTS = 32, SCREEN_SIMPLIFY_TOL_PX = 0.25;
function simplifyScreen(sub) {
  const n = sub.length;
  if (n < SCREEN_SIMPLIFY_MIN_PTS) return sub;
  const xs = new Float64Array(n), ys = new Float64Array(n);
  for (let i = 0; i < n; i++) { xs[i] = sub[i][0]; ys[i] = sub[i][1]; }
  const keep = dpKeep(xs, ys, SCREEN_SIMPLIFY_TOL_PX), out = [];
  for (let i = 0; i < n; i++) if (keep[i]) out.push(sub[i]);
  return out;
}
// Extend a 2-point AB line far past both ends so the reference line spans the field instead of
// being a short stub (issue #37). Curves (>2 points) pass through unchanged. The viewport clip
// in strokePtsSk3D bounds the draw cost of the now-long line. AB_EXTEND covers any field.
const AB_EXTEND = 3000; // metres added beyond each endpoint
function extendAbLine(pts) {
  if (!pts || pts.length !== 2) return pts;
  const a = pts[0], b = pts[1];
  let dx = b.e - a.e, dy = b.n - a.n;
  const len = Math.hypot(dx, dy);
  if (len < 1e-6) return pts;
  dx /= len; dy /= len;
  return [
    { e: a.e - dx * AB_EXTEND, n: a.n - dy * AB_EXTEND },
    { e: b.e + dx * AB_EXTEND, n: b.n + dy * AB_EXTEND },
  ];
}
// Perspective path for strokePtsSk: a vertex behind the tilted camera (w < EPS)
// projects through w2s with a negative w → a mirrored ghost segment (the same bug
// clipNear() fixes for single grid segments). So walk the polyline in WORLD space,
// split it at every near-plane crossing, and stroke each continuous front-facing run
// in screen space — only ever feeding w2s points that are in front of the camera.
// Clip a screen-space polyline to the viewport (+margin) into connected sub-polylines. Under
// tilt a line recedes to the horizon vanishing point, so its PROJECTED length — and a dashed
// stroke's dash count — is effectively unbounded, which collapsed FPS. Clipping bounds the work
// to on-screen length. Cohen–Sutherland per segment, re-joining contiguous pieces. [x,y] arrays.
function clipScreenPolyline(pts, margin) {
  const x0 = -margin, y0 = -margin, x1 = vw + margin, y1 = vh + margin;
  const code = (x, y) => (x < x0 ? 1 : x > x1 ? 2 : 0) | (y < y0 ? 4 : y > y1 ? 8 : 0);
  const out = [];
  let cur = null;
  for (let i = 0; i + 1 < pts.length; i++) {
    let ax = pts[i][0], ay = pts[i][1], bx = pts[i + 1][0], by = pts[i + 1][1];
    let ca = code(ax, ay), cb = code(bx, by), accept = false;
    for (let g = 0; g < 8; g++) {
      if (!(ca | cb)) { accept = true; break; }   // both inside
      if (ca & cb) break;                          // both outside the same edge → reject
      const c = ca || cb; let nx, ny;
      if (c & 8) { nx = ax + (bx - ax) * (y1 - ay) / (by - ay); ny = y1; }
      else if (c & 4) { nx = ax + (bx - ax) * (y0 - ay) / (by - ay); ny = y0; }
      else if (c & 2) { ny = ay + (by - ay) * (x1 - ax) / (bx - ax); nx = x1; }
      else { ny = ay + (by - ay) * (x0 - ax) / (bx - ax); nx = x0; }
      if (c === ca) { ax = nx; ay = ny; ca = code(ax, ay); }
      else { bx = nx; by = ny; cb = code(bx, by); }
    }
    if (!accept) { if (cur) { out.push(cur); cur = null; } continue; }
    if (cur && Math.abs(cur[cur.length - 1][0] - ax) < 0.5 && Math.abs(cur[cur.length - 1][1] - ay) < 0.5)
      cur.push([bx, by]);
    else { if (cur) out.push(cur); cur = [[ax, ay], [bx, by]]; }
  }
  if (cur) out.push(cur);
  return out;
}
function strokePtsSk3D(canvas, pts, close, paint) {
  const cmds = [];
  lineCmds(pts, close, cmds);
  strokeCmdsSk(canvas, cmds, paint);
}
// One path from path commands, stroked with each paint in turn.
function strokeCmdsSk(canvas, cmds, paint) {
  if (!cmds.length) return;
  const path = CK.Path.MakeFromCmds(cmds);
  if (!path) return;
  if (Array.isArray(paint)) for (const p of paint) canvas.drawPath(path, p);
  else canvas.drawPath(path, paint);
  path.delete();
}
// The path commands of a line's parts in front of the camera and on the screen, added to `cmds`
// (a contour each), so several lines can go in one path.
function lineCmds(pts, close, cmds) {
  const EPS = 1.0;
  const n = pts.length;
  const wOf = (p) => pw(p.e, p.n);
  // World point where segment a→b crosses the near plane (a, b straddle it).
  const cross = (a, b, wa, wb) => {
    const t = (EPS - wa) / (wb - wa);
    return [a.e + (b.e - a.e) * t, a.n + (b.n - a.n) * t];
  };
  let run = [];
  const flush = () => {
    // Clip the front-facing screen run to the viewport before stroking — bounds a dashed
    // line's dash flattening (and stroke fill) to on-screen length, the tilt FPS fix.
    if (run.length >= 2) {
      for (const clipped of clipScreenPolyline(run, 48)) {
        if (clipped.length < 2) continue;
        const sub = simplifyScreen(clipped);
        for (let i = 0; i < sub.length; i++)
          cmds.push(i === 0 ? CK.MOVE_VERB : CK.LINE_VERB, sub[i][0], sub[i][1]);
      }
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
// Field flags — filled dot (0.8 m radius, min 4 px) + dark outline,
// coloured by the flag's hex. Skips flags behind the tilted camera (near-plane).
// Live recorded-path markers (Recorded Path → Record). The host streams the points
// captured so far on the RecordedPath frame; draw each as a dot so the path is visible
// as it's driven.
function drawRecordingMarkersSk(canvas) {
  const r = recPath;
  if (!r || !r.recordingPoints || r.recordingPoints.length < 2) return;
  const pts = r.recordingPoints;
  const rad = Math.max(3, 0.4 * pxPerM);
  SKP.flagFill.setColor(ckColor('#FFD040')); // recorded-path yellow
  for (let i = 0; i + 1 < pts.length; i += 2) {
    const e = pts[i], n = pts[i + 1];
    if (pw(e, n) < 1.0) continue; // behind camera
    const xy = w2s(e, n);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagFill);
  }
}
// Contour mode: the strip being followed, as points — green, yellow when locked
// (AgOpenGPS CContour.DrawContourLine) (#110).
function drawContourRefSk(canvas) {
  const pts = scene && scene.contourRef;
  if (!pts || !pts.length) return;
  SKP.flagFill.setColor(ckColor(scene.contourLocked ? 'rgb(251,235,107)' : 'rgb(77,250,0)'));
  const rad = scene.contourLocked ? 2.5 : 2;
  for (const p of pts) {
    if (pw(p.e, p.n) < 1.0) continue; // behind camera
    const xy = w2s(p.e, p.n);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagFill);
  }
}
// Live drive-around boundary (Boundary → Drive Around): the points captured so far,
// streamed on the Boundary frame. Draw a connecting line + dots so it's visible as it's
// driven.
function drawBoundaryRecordingSk(canvas) {
  const b = boundary;
  if (!b || !b.recordingPoints || b.recordingPoints.length < 2) return;
  const flat = b.recordingPoints;
  const pts = [];
  for (let i = 0; i + 1 < flat.length; i += 2) pts.push({ e: flat[i], n: flat[i + 1] });
  if (pts.length >= 2) strokePtsSk(canvas, pts, false, SKP.boundary);
  const rad = Math.max(3, 0.35 * pxPerM);
  SKP.flagFill.setColor(ckColor('#E05220'));
  for (const p of pts) {
    if ((pw(p.e, p.n)) < 1.0) continue;
    const xy = w2s(p.e, p.n);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagFill);
  }
}
// Extra guidelines — the adjacent passes either side of the followed line,
// when Display.ExtraGuidelines is on, faint green over a shadow, with a zoom gate so the lines
// don't collapse into a band. The host builds them from the reference like the pass itself
// (#450): a curve keeps its shape at corners, and the spacing is width − overlap with the tool
// offset on alternate sides. An AB line's come as two points each and are drawn across the field.
function drawExtraGuidelinesSk(canvas) {
  const d = config && config.display;
  if (!d || !d.extraGuidelines) return;
  const line = scene && scene.guidanceLine; // the followed pass
  if (!line || line.length < 2) return;
  const toolW = toolWidthM();
  if (toolW < 0.1 || toolW * pxPerM < 3) return; // zoom gate: skip when passes < ~3 px apart
  // All the lines in one path, drawn once with the shadow and once with the green: Skia strokes a
  // path that bends through a mask the size of the path on the screen, and a boundary curve's
  // guidelines each go round the field, a screen-sized mask each, forty a frame with ten each side.
  const cmds = [];
  for (const gl of scene.guideLines || []) {
    const pts = extendAbLine(gl);
    if (pts && pts.length >= 2) lineCmds(drawnPts(pts), false, cmds);
  }
  strokeCmdsSk(canvas, cmds, [SKP.extraGuideShadow, SKP.extraGuide]);
}
// Live boundary draw-on-map preview — the polygon vertices tapped on the
// satellite imagery so far. Closed white outline + outlined dots; client-side only.
function drawSatBoundarySk(canvas) {
  if (!satBnd || !satPts.length) return;
  if (satPts.length >= 2) strokePtsSk(canvas, satPts, satPts.length >= 3, SKP.boundary);
  const rad = Math.max(4, 0.5 * pxPerM);
  SKP.flagFill.setColor(ckColor('#FFFFFF'));
  for (const p of satPts) {
    if ((pw(p.e, p.n)) < 1.0) continue;
    const xy = w2s(p.e, p.n);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagFill);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagOutline);
  }
}
// #40: preview the AB/curve points being drawn as dots — orange A (first),
// blue B (last), white interior — so the placed A point is visible before B is set. The
// host holds the real point list; drawPts is the client's tap echo (preview only).
function drawAbDrawPreviewSk(canvas) {
  if (!drawPts.length) return; // renders for map-tap AND Drive-AB markers (drawMode may be null)
  const rad = Math.max(5, 0.6 * pxPerM);
  for (let i = 0; i < drawPts.length; i++) {
    const p = drawPts[i];
    if (pw(p.e, p.n) < 1.0) continue; // behind the near plane
    const xy = w2s(p.e, p.n);
    SKP.flagFill.setColor(ckColor(abLabelColor(i))); // A orange, B blue, rest white — matches labels
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagFill);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagOutline);
  }
}
// AgOpenGPS's shadow on the current line (CABLine.DrawABLines): the tool's width along the pass
// shown, so you see where the tool will land. The host sends the pass's centre (the tool offset
// already in it); on a curve it follows the pass, corners rounded as the tractor drives them. One
// path of quads along it, skipping any behind the camera, so a tilted view draws no ghost. Each
// edge point is projected once, and quads off the screen are left out.
function drawToolBandSk(canvas) {
  const c = scene && scene.toolPass;
  const w = toolWidthM();
  if (!c || c.length < 2 || w < 0.1 || !(tick && tick.guidanceActive)) return;
  const { L, R } = toolBandEdges(c, w), n = L.length;
  const ls = new Array(n), rs = new Array(n);
  for (let i = 0; i < n; i++)                    // left empty: behind the camera
    if (pw(L[i].e, L[i].n) >= 1.0 && pw(R[i].e, R[i].n) >= 1.0) { ls[i] = w2s(L[i].e, L[i].n); rs[i] = w2s(R[i].e, R[i].n); }
  const m = 48, x0 = -m, y0 = -m, x1 = vw + m, y1 = vh + m;
  const cmds = [];
  for (let i = 0; i + 1 < n; i++) {
    if (!ls[i] || !ls[i + 1]) continue;
    const q = [ls[i], ls[i + 1], rs[i + 1], rs[i]];
    if (q.every(p => p[0] < x0) || q.every(p => p[0] > x1) || q.every(p => p[1] < y0) || q.every(p => p[1] > y1)) continue; // off the screen
    q.forEach((p, k) => cmds.push(k ? CK.LINE_VERB : CK.MOVE_VERB, p[0], p[1]));
    cmds.push(CK.CLOSE_VERB);
  }
  if (cmds.length) {
    const path = CK.Path.MakeFromCmds(cmds);
    if (path) { canvas.drawPath(path, SKP.toolBand); path.delete(); }
  }
  strokeFixedPtsSk(canvas, L, false, SKP.toolBandEdge);
  strokeFixedPtsSk(canvas, R, false, SKP.toolBandEdge);
}
// The band's two edges, made once per pass the host sends (and tool width), not on every frame;
// a curve's from its drawn copy (drawnPts), so a long pass doesn't put thousands of quads on
// every frame.
let _bandSrc = null, _bandW = 0, _bandEdges = null;
function toolBandEdges(c, w) {
  if (c === _bandSrc && w === _bandW) return _bandEdges;
  let pts = drawnPts(extendAbLine(c));
  if (pts.length === 2) {                  // a straight line: pieces, so the far ones can be skipped
    const [a, b] = pts, len = Math.hypot(b.e - a.e, b.n - a.n), k = Math.max(1, Math.ceil(len / 20));
    pts = Array.from({ length: k + 1 }, (_, i) => ({ e: a.e + (b.e - a.e) * i / k, n: a.n + (b.n - a.n) * i / k }));
  }
  const n = pts.length, half = w / 2, L = new Array(n), R = new Array(n);
  for (let i = 0; i < n; i++) {
    const a = pts[Math.max(0, i - 1)], b = pts[Math.min(n - 1, i + 1)];
    const dx = b.e - a.e, dy = b.n - a.n, len = Math.hypot(dx, dy) || 1;
    const re = dy / len, rn = -dx / len;   // right of the way the line runs
    L[i] = { e: pts[i].e - re * half, n: pts[i].n - rn * half };
    R[i] = { e: pts[i].e + re * half, n: pts[i].n + rn * half };
  }
  _bandSrc = c; _bandW = w;
  return _bandEdges = { L, R };
}
// The active line's A and B, as AgOpenGPS marks them: a red A and a cyan B at its ends (labels
// in renderAbDotLabels), so you see which way A→B runs. Not while a line is being made.
const ACTIVE_A_COLOR = 'rgb(242,0,0)', ACTIVE_B_COLOR = 'rgb(0,230,242)'; // CABLine pointARed / pointBCyan
function activeAbPoints() {
  if (drawPts.length || abFlow || !scene || !scene.tracks || !scene.tracks.length) return null;
  const p = scene.tracks[0].points;
  return p && p.length >= 2 ? [p[0], p[p.length - 1]] : null;
}
function drawActiveAbPointsSk(canvas) {
  const ab = activeAbPoints();
  if (!ab) return;
  const rad = Math.max(4, 0.4 * pxPerM);
  ab.forEach((p, i) => {
    if (pw(p.e, p.n) < 1.0) return;
    const xy = w2s(p.e, p.n);
    SKP.flagFill.setColor(ckColor(i ? ACTIVE_B_COLOR : ACTIVE_A_COLOR));
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagFill);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagOutline);
  });
}
// Tram lines (wheel tracks) generated from the tram systems. Drawn when the tram display is
// on (bottom-nav tram mode > 0) or while the Field Builder Tram tab is open (so you see the
// effect of edits). Orange polylines; the host owns the geometry.
function drawTramLinesSk(canvas) {
  if (!scene || !scene.tramLines || !scene.tramLines.length) return;
  const fbTramOpen = document.getElementById('fieldbuilder').classList.contains('open') && fbTab === 'tram';
  const tramOn = tick && tick.tools && tick.tools.tramMode > 0;
  if (!fbTramOpen && !tramOn) return;
  SKP.tram.setStrokeWidth(Math.max(2, 0.3 * pxPerM));
  for (const line of scene.tramLines) if (line.length >= 2) strokeFixedPtsSk(canvas, line, false, SKP.tram);
}
// Field Builder Headland editor — draw each segment's offset line + overshoot extensions
// (yellow = contributes to the closed headland, red = doesn't yet) ONLY while the Headland
// tab is open, so the operator can watch the lines cross and enclose the area. The built headland polygon draws separately.
function drawHeadlandSegEditLinesSk(canvas) {
  if (!scene || !scene.headlandSegs || !scene.headlandSegs.length) return;
  const fb = document.getElementById('fieldbuilder');
  if (!fb.classList.contains('open') || fbTab !== 'headland') return;
  const w = Math.max(2, 0.4 * pxPerM);
  for (const s of scene.headlandSegs) {
    if (!s.editLine || s.editLine.length < 2) continue;
    const paint = s.effective ? SKP.hlEdit : SKP.hlEditOff;
    paint.setStrokeWidth(w);
    strokePtsSk(canvas, s.editLine, false, paint);
  }
}
// Live headland draw preview (Field Builder stage 2) — the boundary points tapped so far,
// snapped to the nearest boundary vertex. Green (headland colour) dots + connecting line;
// the host builds the real inward-offset segment + headland on finish.
function drawHeadlandDrawSk(canvas) {
  if (!hlFlow || !hlPts.length) return;
  if (hlPts.length >= 2) strokePtsSk(canvas, hlPts, false, SKP.headland);
  const rad = Math.max(4, 0.6 * pxPerM);
  SKP.flagFill.setColor(ckColor('#6BFF6B'));
  for (const p of hlPts) {
    if ((pw(p.e, p.n)) < 1.0) continue;
    const xy = w2s(p.e, p.n);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagFill);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagOutline);
  }
}
// Live draw-track preview — the points tapped so far while drawing an AB/curve
// on the map. Client-side only (the host holds the authoritative list); cleared on
// finish/cancel. Cyan line through the points + an outlined dot at each.
function drawDrawingSk(canvas) {
  if (!drawMode || !drawPts.length) return;
  if (drawPts.length >= 2) strokePtsSk(canvas, abFlow === 'curve' ? drawnCurvePreview() : drawPts, false, SKP.track);
  const rad = Math.max(4, 0.5 * pxPerM);
  SKP.flagFill.setColor(ckColor('#40E0FF'));
  for (const p of drawPts) {
    if ((pw(p.e, p.n)) < 1.0) continue; // behind camera
    const xy = w2s(p.e, p.n);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagFill);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagOutline);
  }
}
function drawFlagsSk(canvas, flags) {
  if (!flags || !flags.length) return;
  const r = Math.max(4, 0.8 * pxPerM);
  for (const fl of flags) {
    if (pw(fl.e, fl.n) < 1.0) continue; // behind camera
    const xy = w2s(fl.e, fl.n);
    SKP.flagFill.setColor(ckColor(fl.color || '#FF0000'));
    canvas.drawCircle(xy[0], xy[1], r, SKP.flagFill);
    canvas.drawCircle(xy[0], xy[1], r, SKP.flagOutline);
  }
}
// Reference grid (procedural, client-side — no wire). Spacing is a tool-width multiple on a
// 1-2-5 series keyed to zoom (#417). One line style, 1 px wide at any
// zoom, no major lines and no coloured axes, as AgOpenGPS (#496).
// ≤1 → 1, then 2, 5, 10, 20 … (no 1.5 step).
function niceStep125(x) {
  if (x <= 1) return 1;
  const pow = Math.pow(10, Math.floor(Math.log10(x)));
  const f = x / pow;
  return (f <= 2 ? 2 : f <= 5 ? 5 : 10) * pow;
}
function drawGridSk(canvas) {
  const disp = config && config.display;
  if (!disp || !disp.gridVisible) return; // honour Display.GridVisible
  const G = 2000;
  let halfW = (vw / 2) / pxPerM, halfH = (vh / 2) / pxPerM;
  halfW = Math.max(halfW, 180); halfH = Math.max(halfH, 180); // see far enough under tilt
  if (mapRotation !== 0) { const d = Math.hypot(halfW, halfH); halfW = d; halfH = d; } // cover rotated corners
  const minE = Math.max(camE - halfW, -G), maxE = Math.min(camE + halfW, G);
  const minN = Math.max(camN - halfH, -G), maxN = Math.min(camN + halfH, G);
  if (minE >= maxE || minN >= maxN) return;
  const toolW = toolWidthM() || 1; // no tool width: plain metres
  // Spacing = tool-width × niceStep125(viewSpan / (toolW·30)).
  const spacing = toolW * niceStep125(Math.max(vw, vh) / pxPerM / (toolW * 30));

  // Draw in WORLD coords under the perspective matrix so Skia GPU-clips at the near
  // plane. (Per-vertex screen projection garbles any line crossing behind a tilted
  // camera.) Every line is 1 CSS px wide at any zoom, as AgOpenGPS's 1 px grid: the width is one
  // pixel in world metres (1 / pxPerM), with no metre floor, so zooming in doesn't thicken it (#496).
  SKP.gridMinor.setStrokeWidth(1 / pxPerM);
  canvas.save();
  canvas.concat(perspM);
  const line = (e1, n1, e2, n2) => {
    const c = clipNear(e1, n1, e2, n2); // returns WORLD coords
    if (c) canvas.drawLine(c[0] - camE, c[1] - camN, c[2] - camE, c[3] - camN, SKP.gridMinor); // camera-relative
  };
  for (let k = Math.ceil(minE / spacing); k * spacing <= maxE; k++)
    line(k * spacing, minN, k * spacing, maxN);
  for (let k = Math.ceil(minN / spacing); k * spacing <= maxN; k++)
    line(minE, k * spacing, maxE, k * spacing);
  canvas.restore();
}
// Implement hitch line: yellow line from the vehicle hitch pivot to the tool, drawn
// under the perspective path so it clips/tilts like the other vectors.
function drawHitchSk(canvas) {
  const tool = renderTool(), p = renderPose();
  if (!tool || !p || (!tool.e && !tool.n)) return;
  const veh = config && config.vehicle, tl = config && config.tool;
  if (!veh || !tl) return;
  if (!SKP.hitch) {
    SKP.hitch = new CK.Paint();
    SKP.hitch.setStyle(CK.PaintStyle.Stroke);
    SKP.hitch.setColor(ckColor('#FFFF00'));
    SKP.hitch.setStrokeWidth(3);
    SKP.hitch.setAntiAlias(true);
    SKP.hitch.setStrokeCap(CK.StrokeCap.Round);
  }
  const type = tl.type | 0; // 0 front, 1 rear, 2 TBT, 3 trailing (ToolConfigDto)
  if (type === 3 || type === 2) {
    // Trailing / TBT: a single tongue line from the vehicle hitch pivot to the tool.
    if (tick && tick.hitchE != null && (tick.hitchE || tick.hitchN))
      strokePtsSk(canvas, [{ e: tick.hitchE, n: tick.hitchN }, { e: tool.e, n: tool.n }], false, SKP.hitch);
    return;
  }
  // Mounted (front/rear 3-point): two arms from the tractor mount (rear axle, or
  // wheelbase ahead for front-mount) ± a lateral spread, converging on the tool.
  // spread = trackWidth*0.3; base = pivot (+wheelbase ahead if front).
  const spread = veh.trackWidth * 0.3;
  const front = type === 0;
  const baseE = p.e + (front ? Math.sin(p.heading) * veh.wheelbase : 0);
  const baseN = p.n + (front ? Math.cos(p.heading) * veh.wheelbase : 0);
  const ps = Math.cos(p.heading), pc = -Math.sin(p.heading); // perpendicular
  strokePtsSk(canvas, [{ e: baseE - ps * spread, n: baseN - pc * spread }, { e: tool.e, n: tool.n }], false, SKP.hitch);
  strokePtsSk(canvas, [{ e: baseE + ps * spread, n: baseN + pc * spread }, { e: tool.e, n: tool.n }], false, SKP.hitch);
}
// Pure Pursuit goal marker (#95): where the steering aims — the engaged target, or before
// engaging the point it would chase — as AgOpenGPS's yellow goal square. A lazy (long)
// look-ahead shows as the marker sitting further out. Fixed screen size, drawn over the
// vehicle so a short hold look-ahead under the sprite is still visible.
function drawGoalSk(canvas) {
  const g = renderGoal();
  if (!g || pw(g.e, g.n) < 1.0) return; // none / behind the near plane
  if (!SKP.goalFill) {
    SKP.goalFill = new CK.Paint();
    SKP.goalFill.setStyle(CK.PaintStyle.Fill);
    SKP.goalFill.setColor(ckColor('#FFD400'));
    SKP.goalFill.setAntiAlias(true);
    SKP.goalOutline = new CK.Paint();
    SKP.goalOutline.setStyle(CK.PaintStyle.Stroke);
    SKP.goalOutline.setColor(ckColor('#000000'));
    SKP.goalOutline.setStrokeWidth(2);
    SKP.goalOutline.setAntiAlias(true);
  }
  const xy = w2s(g.e, g.n), h = 6;
  const r = CK.LTRBRect(xy[0] - h, xy[1] - h, xy[0] + h, xy[1] + h);
  canvas.drawRect(r, SKP.goalFill);
  canvas.drawRect(r, SKP.goalOutline);
}
// Vehicle: the TractorAoG sprite drawn world-sized on the ground (scales with zoom,
// foreshortens under tilt), sized from track-width/wheelbase via fixed
// normalized sprite proportions. Falls back to the screen-space triangle until the
// image/config is ready. Sequence: translate→rotate(-heading)
// →flip Y→draw bitmap into the rear-axle-anchored rect.
const SPR_REAR = 0.245, SPR_FRONT = 0.75, SPR_HALFX = 0.245; // bitmap norm anchors
// Vector line weights: world-metre stroke widths (× 3), so they scale with zoom (no
// per-pixel factor — that's only the grid). We stroke in screen space, so set
// px = worldMetres × pxPerM each frame (min 1 px so lines don't vanish when zoomed far out).
function updateLineWidths() {
  // World-metre widths scaled by zoom, but CAPPED at MAXW px so a line can't balloon when
  // zoomed in and swallow the implement/vehicle (issue #38: a 3 m boundary line covered a
  // 4 m tool). Min 1 px so it stays visible zoomed out.
  // AutoSteer › Line width (AgOpenGPS ABLine.lineWidth, px, default 2) scales the guidance
  // lines, headland/U-turn and saved tracks (#110).
  const k = Math.max(1, (config && config.autosteer && config.autosteer.lineWidth) || 2) / 2;
  const z = pxPerM, MAXW = 3.5 * k, w = (m) => Math.min(Math.max(m * z * k, k), MAXW);
  applyLineSmoothing();
  // Boundaries keep a fixed width, as in AgOpenGPS CFence (not scaled by Line width).
  const wb = (m) => Math.min(Math.max(m * z, 1), 3.5);
  SKP.boundary.setStrokeWidth(wb(3.0));   // boundaryOuter 1 × 3
  SKP.boundaryInner.setStrokeWidth(wb(3.0)); // boundaryInner 1 × 3
  SKP.boundaryOut.setStrokeWidth(3 * 2 * k);  // out of bounds: AgOpenGPS ABLine.lineWidth × 3 (#494)
  SKP.headland.setStrokeWidth(w(3.0));   // headland 1 × 3
  SKP.guidance.setStrokeWidth(w(1.5));   // trackActive 0.5 × 3
  SKP.guidanceOutline.setStrokeWidth(w(1.5) * 3); // AgOpenGPS draws the current line over black, 3 × its width
  SKP.reference.setStrokeWidth(w(0.9));  // trackBaseDash 0.3 × 3
  SKP.next.setStrokeWidth(w(1.2));       // trackNext 0.4 × 3
  SKP.uturn.setStrokeWidth(w(3.0));      // youTurn 1 × 3
  SKP.track.setStrokeWidth(w(1.5));      // saved tracks ~ active weight
  SKP.extraGuide.setStrokeWidth(w(0.9)); // extra guide 0.3 × 3
  SKP.extraGuideShadow.setStrokeWidth(w(1.2));
  SKP.recPath.setStrokeWidth(2 * k); SKP.contourStrip.setStrokeWidth(2 * k);
}
// Screen & Alerts › Smoothing (AgOpenGPS isLineSmooth → GL LineSmooth): anti-aliased
// map lines, or crisp ones when off (#110).
let lineSmoothApplied = null;
function applyLineSmoothing() {
  const on = !(config && config.display && config.display.lineSmoothEnabled === false);
  if (on === lineSmoothApplied || !SKP) return;
  lineSmoothApplied = on;
  for (const key of Object.keys(SKP)) {
    const p = SKP[key];
    if (p && typeof p.setAntiAlias === 'function' && key !== 'lbFill' && key !== 'ground') p.setAntiAlias(on);
  }
}
// Draw a sprite in the vehicle frame (+Y forward) centred on (cx, cy) with half-extents
// (hx, hy), bitmap top row toward +Y — AgOpenGPS Texture2D.DrawCentered with its V flip.
function drawVehSprite(canvas, sk, cx, cy, hx, hy, rotDeg, paint) {
  canvas.save();
  canvas.translate(cx, cy);
  if (rotDeg) canvas.rotate(rotDeg, 0, 0);
  canvas.scale(1, -1);
  canvas.drawImageRectOptions(sk, CK.LTRBRect(0, 0, sk.width(), sk.height()),
    CK.LTRBRect(-hx, -hy, hx, hy), CK.FilterMode.Linear, CK.MipmapMode.None, paint || null);
  canvas.restore();
}
// Harvester / articulated bodies (issue #88), laid out as AgOpenGPS CVehicle.DrawVehicle:
//  harvester  — body 2·tw × 3·wb centred on the pivot; steerable wheels at the REAR axle
//               (−wb), turned the opposite way to a tractor, tinted HarvesterWheelColor.
//  articulated — front/rear halves 2·tw × 1.3·wb centred ±wb/2 about the hinge, each
//               turned by half the steer angle in opposite directions.
// Returns false until the sprites are loaded so the caller falls back to the triangle.
function vehicleBodySk(canvas, p, veh, type) {
  const tw = veh.trackWidth, wb = veh.wheelbase;
  const steer = tick ? tick.vehicleSteerAngle : 0; // degrees, +right
  if (type === 1) {
    const body = skSprite(harvesterSpr);
    if (!body) return false;
    beginVehicleFrame(canvas, p);
    const wheel = frontWheelReady && (skFrontWheel || (skFrontWheel = CK.MakeImageFromCanvasImageSource(frontWheelImg)));
    if (wheel) {
      if (!SKP.harvesterWheel) {
        SKP.harvesterWheel = new CK.Paint();
        SKP.harvesterWheel.setColorFilter(CK.ColorFilter.MakeBlend(CK.Color(20, 20, 20, 1), CK.BlendMode.Modulate));
      }
      for (const sx of [1, -1]) drawVehSprite(canvas, wheel, sx * tw / 2, -wb, 0.25 * tw, 0.5 * wb, steer, SKP.harvesterWheel);
    }
    drawVehSprite(canvas, body, 0, 0, tw, 1.5 * wb, 0);
  } else {
    const front = skSprite(artFrontSpr), rear = skSprite(artRearSpr);
    if (!front || !rear) return false;
    beginVehicleFrame(canvas, p);
    drawVehSprite(canvas, rear, 0, -wb / 2, tw, 0.65 * wb, steer / 2);
    drawVehSprite(canvas, front, 0, wb / 2, tw, 0.65 * wb, -steer / 2);
  }
  canvas.restore();
  return true;
}
function beginVehicleFrame(canvas, p) {
  canvas.save();
  canvas.concat(perspM);
  canvas.translate(p.e - camE, p.n - camN); // camera-relative (f64) — see buildScreenMatrix
  canvas.rotate(-p.heading * 180 / Math.PI, 0, 0); // vehicle frame: +Y forward, +X right
}
// Svenn arrow (AgOpenGPS CVehicle): a yellow chevron ahead of the front axle that keeps
// its size on screen, so the heading reads at any zoom (#110).
function svennArrowSk(canvas, p) {
  const veh = config && config.vehicle, disp = config && config.display;
  if (!veh || !disp || !disp.svennArrowVisible) return;
  const camDist = vh / pxPerM;                // ~ AgOpenGPS camSetDistance (m)
  const dist = camDist * 0.07, width = dist * 0.22, wb = veh.wheelbase || 3;
  if (!SKP.svenn) { SKP.svenn = new CK.Paint(); SKP.svenn.setStyle(CK.PaintStyle.Stroke); SKP.svenn.setColor(ckColor('rgb(242,242,26)')); SKP.svenn.setStrokeJoin(CK.StrokeJoin.Round); SKP.svenn.setStrokeCap(CK.StrokeCap.Round); lineSmoothApplied = null; }
  const lw = Math.max(1, (config.autosteer && config.autosteer.lineWidth) || 2);
  SKP.svenn.setStrokeWidth(lw / pxPerM);      // px → metres (drawn in the vehicle frame)
  const path = CK.Path.MakeFromCmds([
    CK.MOVE_VERB, width, wb + dist,
    CK.LINE_VERB, 0, wb + width + 0.5 + dist,
    CK.LINE_VERB, -width, wb + dist,
  ]);
  if (!path) return;
  beginVehicleFrame(canvas, p);
  canvas.drawPath(path, SKP.svenn);
  canvas.restore();
  path.delete();
}
function vehicleSk(canvas, p) {
  const veh = config && config.vehicle;
  const type = veh ? Math.max(0, Math.min(2, veh.type | 0)) : 0; // 0 Tractor / 1 Harvester / 2 Articulated
  if (type !== 0 && veh.trackWidth > 0.01 && veh.wheelbase > 0.01 && vehicleBodySk(canvas, p, veh, type)) return;
  if (type === 0 && tractorReady && veh && veh.trackWidth > 0.01 && veh.wheelbase > 0.01) {
    if (!skTractor) skTractor = CK.MakeImageFromCanvasImageSource(tractorImg);
    if (skTractor) {
      const bW = veh.trackWidth / (2 * SPR_HALFX);
      const bH = veh.wheelbase / (SPR_FRONT - SPR_REAR);
      const half = bW / 2, top = (1 - SPR_REAR) * bH, bot = -SPR_REAR * bH;
      beginVehicleFrame(canvas, p);
      // Body sprite (scale 1,-1 = bitmap rows top-down → world N up).
      canvas.save();
      canvas.scale(1, -1);
      canvas.drawImageRectOptions(skTractor,
        CK.LTRBRect(0, 0, skTractor.width(), skTractor.height()),
        CK.LTRBRect(-half, -top, half, -bot),
        CK.FilterMode.Linear, CK.MipmapMode.None, null);
      canvas.restore();
      // Steerable front wheels: one sprite drawn at both front-axle ends, rotated by the
      // live wheel angle (translate ±trackWidth/2, wheelbase −0.05; rotate −steer;
      // scale 1,-1; centred dst).
      if (frontWheelReady && (skFrontWheel || (skFrontWheel = CK.MakeImageFromCanvasImageSource(frontWheelImg)))) {
        const steerDeg = -(tick ? tick.vehicleSteerAngle : 0); // tick angle already in degrees, +right
        const woX = veh.trackWidth / 2, woY = veh.wheelbase - 0.05;
        const ww = 0.378 / 0.27, wh = 0.85 / 0.29; // FrontTireWidth/ContentW, Diameter/ContentH
        const wsrc = CK.LTRBRect(0, 0, skFrontWheel.width(), skFrontWheel.height());
        const wdst = CK.LTRBRect(-ww / 2, -wh / 2, ww / 2, wh / 2);
        for (const sx of [1, -1]) {
          canvas.save();
          canvas.translate(sx * woX, woY);
          canvas.rotate(steerDeg, 0, 0);
          canvas.scale(1, -1);
          canvas.drawImageRectOptions(skFrontWheel, wsrc, wdst, CK.FilterMode.Linear, CK.MipmapMode.None, null);
          canvas.restore();
        }
      }
      canvas.restore();
      return;
    }
  }
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
// Tiled ground texture: a repeating shader over a camera-centred world rect, drawn under
// the perspective matrix so it foreshortens with tilt. One tile = 50 m. CanvasKit's
// makeShaderOptions localMatrix maps texel→local(world), so W texels span 50 m
// when the scale is 50/W. Shader + paint are built once (world-anchored — no per-frame
// rebuild; the tiles stay fixed in the world and scroll under the vehicle).
function drawGroundTextureSk(canvas) {
  const disp = config && config.display;
  if (!disp || !disp.fieldTextureVisible) return;
  if (mapIsDay ? !groundReadyDay : !groundReadyNight) return;
  let skGround = mapIsDay ? skGroundDay : skGroundNight;
  if (!skGround) {
    skGround = CK.MakeImageFromCanvasImageSource(mapIsDay ? groundImgDay : groundImgNight);
    if (!skGround) return;
    if (mapIsDay) skGroundDay = skGround; else skGroundNight = skGround;
    if (!SKP.ground) { SKP.ground = new CK.Paint(); SKP.ground.setAntiAlias(false); }
  }
  const W = skGround.width(), H = skGround.height();
  // We render camera-relative, but the tiles must stay anchored to WORLD. The pattern
  // repeats every 50 m, so only camE,camN MOD 50 affects alignment — using the remainder
  // keeps the shader's local-matrix translation small (f32-safe → no shimmer). texel =
  // (W/50)(P + off) ⇒ texel→local matrix is scale(50/W) then translate(-off).
  // Texture Moves off: the tiles stay fixed to the camera instead of the world (#110).
  const moves = disp.fieldTextureMoveable !== false;
  const offE = moves ? camE - Math.floor(camE / 50) * 50 : 0;
  const offN = moves ? camN - Math.floor(camN / 50) * 50 : 0;
  const lm = [50 / W, 0, -offE, 0, 50 / H, -offN, 0, 0, 1];
  if (SKP.groundShader) SKP.groundShader.delete(); // free last frame's shader (already flushed)
  SKP.groundShader = skGround.makeShaderOptions(
    CK.TileMode.Repeat, CK.TileMode.Repeat, CK.FilterMode.Linear, CK.MipmapMode.Linear, lm);
  SKP.ground.setShader(SKP.groundShader);
  const half = Math.max(Math.max(vw, vh) / pxPerM, 300); // cover the view; see far under tilt
  canvas.save();
  canvas.concat(perspM);
  canvas.drawRect(CK.LTRBRect(-half, -half, half, half), SKP.ground); // camera-relative
  canvas.restore();
}
let skImagery = null, skImageryVer = null, skImageryMult = -1;
function drawImagerySk(canvas) {
  if (!imageryImg || !imageryRect) return;
  if (config && config.display && config.display.imageryVisible === false) return; // Screen & Alerts › Aerial Imagery (#500)
  // Quality button: scale the imagery LOD by DisplayResolutionMultiplier so the background
  // degrades with quality. Ultra = full res; lower quality = downsampled (also a
  // real GPU/memory win on the tablet). Downsample via a 2D canvas (reliable browser scaling).
  const mult = (config && config.display && config.display.resolutionMultiplier) || 1;
  if (skImageryVer !== imageryVer || skImageryMult !== mult || !skImagery) {
    if (skImagery) skImagery.delete();
    let src = imageryImg;
    if (mult > 1.01 && imageryImg.naturalWidth > 0) {
      const w = Math.max(1, Math.round(imageryImg.naturalWidth / mult));
      const h = Math.max(1, Math.round(imageryImg.naturalHeight / mult));
      const cv = document.createElement('canvas');
      cv.width = w; cv.height = h;
      const ctx = cv.getContext('2d');
      ctx.imageSmoothingEnabled = true;
      ctx.drawImage(imageryImg, 0, 0, w, h);
      src = cv;
    }
    skImagery = CK.MakeImageFromCanvasImageSource(src);
    skImageryVer = imageryVer;
    skImageryMult = mult;
  }
  if (!skImagery) return;
  const r = imageryRect;
  drawImageWorldSk(canvas, skImagery, r.minE, r.minN, r.maxE, r.maxN, CK.FilterMode.Linear);
}
// Draw a north-up image over a world rect under the perspective matrix. The image
// (top row = high northing) is placed via a px→world affine, then perspM warps it
// — Skia does perspective-correct sampling AND near-plane clipping on the GPU, so
// it stays correct even when part of the rect falls behind the tilted camera.
function drawImageWorldSk(canvas, img, minE, minN, maxE, maxN, filter, mipMode) {
  const w = img.width(), h = img.height();
  // Map image px → CAMERA-RELATIVE world (minE-camE …): the world−camera offset is done
  // here in f64 so only small coords reach the f32 matrix — no floating-origin jitter on
  // imagery/coverage. See buildScreenMatrix.
  const imgToWorld = [(maxE - minE) / w, 0, minE - camE, 0, -(maxN - minN) / h, maxN - camN, 0, 0, 1];
  // Photo layers (imagery / satellite tiles, Linear filter) recede to the horizon
  // under perspective → heavy minification → shimmer/crawl under motion without a
  // mip chain. Trilinear mipmaps fix it. Callers
  // may override the mip mode: coverage uses Linear sampling but mip-free, so its edges
  // smooth (bilinear) without paying the per-update mip-regen on a multi-MP texture.
  const mip = (mipMode !== undefined) ? mipMode
    : ((filter === CK.FilterMode.Linear) ? CK.MipmapMode.Linear : CK.MipmapMode.None);
  canvas.save();
  canvas.concat(perspM);
  canvas.concat(imgToWorld);
  canvas.drawImageOptions(img, 0, 0, filter, mip, null);
  canvas.restore();
}

// ---- The implement's picture on the map (Tool > Type > Picture on the map) ----
// A top view drawn to scale at the tool's real hitch points: the tool position the host sends and the
// vehicle's hitches, so the picture sits where AgOpenWeb puts the tool, never on the tractor's wheels.
// Each picture is drawn only with the tool type it is made for; otherwise the section bars and the
// hitch line, as before. Over a picture the section bars shrink to a strip where it works.
const implPics = {}; // loaded the first time a picture is drawn
function implSprite(src) { return skSprite(implPics[src] || (implPics[src] = loadSprite(src))); }
let implementPictureOn = false, implPicFailed = false;
let implementStrip = { half: 1.0, ahead: 0 }; // the strip's half depth and its move ahead of the tool point (m)
// The implement's colour (Tool > Type > Colour, tool.pictureColor 0xRRGGBB, -1 its own colours): while a
// picture drawn from shapes is drawn, its paint (the reds, greens and yellows of IC) is drawn in that colour and its
// darker parts in a darker shade of it (ip). Tyres, discs, tines, tanks and frames in grey keep theirs. The pictures
// from photos (the seed drill, the front sprayer) are not recoloured; a combine's header takes it (headerFrameSk).
let implRecolour = null;
function implColourChosen(tl) {
  const c = tl && typeof tl.pictureColor === 'number' ? tl.pictureColor : -1;
  return c >= 0 && c <= 0xFFFFFF ? c : null;
}
function implementPictureSk(canvas) {
  implementPictureOn = false;
  try { implWorkLeft(); } catch (e) { } // the way the work goes, followed with no picture shown too; never the map
  const tl = config && config.tool, veh = config && config.vehicle;
  if (!tl || !veh) return false;
  const pic = IMPLEMENT_PICTURES.find(q => q.id === (tl.picture | 0));
  if (!pic || pic.type !== (tl.type | 0) || !pic.vehicles.includes(veh.type | 0)) return false;
  // A picture that fails is left out (the section bars instead), never the whole map: a throw in the frame's
  // draw leaves the map blank.
  const saves = canvas.getSaveCount(), col = implColourChosen(tl);
  let drawn = null;
  implRecolour = col === null ? null : { main: col, dark: hdrShade(col, 0.55) };
  try { drawn = pic.draw(canvas, tl, veh); }
  catch (e) { if (!implPicFailed) { implPicFailed = true; console.warn('implement picture:', e); } }
  finally { canvas.restoreToCount(saves); implRecolour = null; }
  if (!drawn) return false;
  implementStrip = drawn;
  implementPictureOn = true;
  return true;
}
// A picture into the rectangle l..r across, t..b along (m, in the frame set up before; y backwards).
function drawImplPic(canvas, sk, l, t, r, b) {
  canvas.drawImageRectOptions(sk, CK.LTRBRect(0, 0, sk.width(), sk.height()), CK.LTRBRect(l, t, r, b),
    CK.FilterMode.Linear, CK.MipmapMode.Linear, null);
}
// A semi-mounted seed drill. The headstock rides on the tractor (its heading), from the lower links to
// the drawbar pin (Vehicle > Hitch); the drill swings about that pin with the tool's heading (the
// host's, from the hitch's movement), so the working line (between the coulter banks) is where the
// map paints. Pictures (DrillBody, DrillHeadstock): metres from the pin, +y backwards.
function seedDrillPictureSk(canvas, tl, veh) {
  const t = renderTool(), p = renderPose();
  if (!t || !p || (!t.e && !t.n)) return null;
  const body = implSprite('/icons/DrillBody.png'), head = implSprite('/icons/DrillHeadstock.png');
  if (!body || !head) return null;
  const back = tl.trailingHitchLength + tl.trailingToolToPivotLength; // the pin to the working line
  const pe = t.e + Math.sin(t.heading) * back, pn = t.n + Math.cos(t.heading) * back;
  canvas.save();
  canvas.concat(perspM);
  canvas.translate(pe - camE, pn - camN);
  canvas.rotate(-t.heading * 180 / Math.PI, 0, 0); // the tool's frame: +Y forward
  canvas.scale(1, -1);
  drawImplPic(canvas, body, -1.75, -0.25, 1.75, 5.0);
  canvas.restore();
  beginVehicleFrame(canvas, p);
  canvas.translate(0, -Math.abs(veh.hitchLength));
  canvas.scale(1, -1);
  drawImplPic(canvas, head, -0.70, -0.60, 0.70, 0.15);
  canvas.restore();
  return { half: 0.09, ahead: 0 };
}
// A front sprayer boom at the working point (Vehicle > Front hitch + Tool > Hitch to working point,
// ahead of the rear axle) and its tank on the rear lower links (Vehicle > Rear 3-point hitch). Both
// ride on the tractor. Pictures: SprayerBoom from the nozzle line, SprayerTank from the lower-link
// pins, metres, +y backwards. The strip is the ground just behind the nozzles.
function frontSprayerPictureSk(canvas, tl, veh) {
  const p = renderPose();
  if (!p) return null;
  const boom = implSprite('/icons/SprayerBoom.png'), tank = implSprite('/icons/SprayerTank.png');
  if (!boom || !tank) return null;
  beginVehicleFrame(canvas, p);
  canvas.save();
  canvas.translate(0, -Math.abs(veh.threePointHitchLength));
  canvas.scale(1, -1);
  drawImplPic(canvas, tank, -0.80, -0.12, 0.80, 1.10);
  canvas.restore();
  canvas.translate(0, Math.abs(veh.frontHitchLength) + tl.workingPointFromHitch);
  canvas.scale(1, -1);
  drawImplPic(canvas, boom, -4.10, -0.40, 4.10, 1.10);
  canvas.restore();
  return { half: 0.035, ahead: -0.095 };
}
// A combine's header at the working point (Vehicle > Front hitch + Tool > Hitch to working point), as wide
// as the tool, drawn here rather than from a picture: headers come in every width and the dividers and
// snouts keep their size. Its frame is grey, or the vehicle's colour on the map once one is set (#936),
// and a feeder house joins it to the combine's body (the harvester picture's front, 1.5 wheelbases ahead
// of the pivot). Metres, the cutter bar (grain) or the stalk rolls (maize) on y = 0, +y forward.
const hdrPaints = {};
function hdrPaint(key, rgb, stroke) {
  let h = hdrPaints[key];
  if (!h) {
    const p = new CK.Paint(); p.setAntiAlias(true);
    p.setStyle(stroke ? CK.PaintStyle.Stroke : CK.PaintStyle.Fill);
    if (stroke) { p.setStrokeWidth(stroke); p.setStrokeCap(CK.StrokeCap.Round); }
    h = hdrPaints[key] = { p, rgb: -1 };
  }
  if (h.rgb !== rgb) { h.p.setColor(CK.Color((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255, 1)); h.rgb = rgb; }
  return h.p;
}
function hdrShade(rgb, k) {
  const c = (v) => Math.max(0, Math.min(255, Math.round(v * k)));
  return (c((rgb >> 16) & 255) << 16) | (c((rgb >> 8) & 255) << 8) | c(rgb & 255);
}
function hdrRect(canvas, l, b, r, t, paint) { canvas.drawRect(CK.LTRBRect(l, b, r, t), paint); }
// Paths from verbs (CanvasKit 0.41 paths are immutable): segs is a list of [x0, y0, x1, y1, ...] runs.
function hdrStroke(canvas, segs, paint) {
  const cmds = [];
  for (const g of segs) { cmds.push(CK.MOVE_VERB, g[0], g[1]); for (let i = 2; i < g.length; i += 2) cmds.push(CK.LINE_VERB, g[i], g[i + 1]); }
  const path = cmds.length ? CK.Path.MakeFromCmds(cmds) : null;
  if (path) { canvas.drawPath(path, paint); path.delete(); }
}
function hdrPoly(canvas, pts, fill, line) {
  const cmds = [CK.MOVE_VERB, pts[0], pts[1]];
  for (let i = 2; i < pts.length; i += 2) cmds.push(CK.LINE_VERB, pts[i], pts[i + 1]);
  cmds.push(CK.CLOSE_VERB);
  const path = CK.Path.MakeFromCmds(cmds);
  if (!path) return;
  canvas.drawPath(path, fill);
  if (line) canvas.drawPath(path, line);
  path.delete();
}
// The header's frame, its auger and the feeder house: shared by both headers. Returns the paints.
function headerFrameSk(canvas, tl, veh, w, back) {
  const vc = veh && typeof veh.color === 'number' ? veh.color & 0xFFFFFF : 0xFFFFFF, own = implColourChosen(tl);
  const frame = own !== null ? own : vc === 0xFFFFFF ? 0x8C8C8C : vc;
  const P = {
    frame: hdrPaint('frame', frame), dark: hdrPaint('dark', hdrShade(frame, 0.55)),
    lightRgb: hdrShade(frame, 1.3), steel: hdrPaint('steel', 0x4A4A4A),
    edge: hdrPaint('edge', 0x1E1E1E, 0.03), flight: hdrPaint('flight', 0x9A9A9A, 0.05),
  };
  // Feeder house to the combine's body.
  const bodyFront = 1.5 * (veh.wheelbase || 3) - (Math.abs(veh.frontHitchLength) + tl.workingPointFromHitch);
  if (bodyFront < back - 0.2) { hdrRect(canvas, -0.7, bodyFront, 0.7, back - 0.2, P.dark); hdrRect(canvas, -0.7, bodyFront, 0.7, back - 0.2, P.edge); }
  // Back wall and auger trough.
  hdrRect(canvas, -w / 2, back - 0.25, w / 2, back, P.frame); hdrRect(canvas, -w / 2, back - 0.25, w / 2, back, P.edge);
  hdrRect(canvas, -w / 2, back, w / 2, back + 0.5, P.steel);
  const fl = [];
  for (let x = -w / 2 + 0.15; x < w / 2 - 0.1; x += 0.3) fl.push([x, back + 0.05, x + 0.18, back + 0.45]);
  hdrStroke(canvas, fl, P.flight);
  return P;
}
function grainHeaderPictureSk(canvas, tl, veh) {
  const p = renderPose();
  if (!p) return null;
  const w = Math.max(2, tl.totalWidth || 6), back = -1.75;
  beginVehicleFrame(canvas, p);
  canvas.translate(0, Math.abs(veh.frontHitchLength) + tl.workingPointFromHitch);
  const P = headerFrameSk(canvas, tl, veh, w, back);
  hdrRect(canvas, -w / 2, back + 0.5, w / 2, 0, P.dark);                       // the floor
  for (const s of [-1, 1]) {                                                    // end plates and dividers
    hdrRect(canvas, s * w / 2, back - 0.25, s * (w / 2 + 0.1), 0.05, P.frame);
    hdrPoly(canvas, [s * (w / 2 + 0.1), 0.05, s * (w / 2 + 0.06), 0.85, s * (w / 2 - 0.12), 0.05], P.frame, P.edge);
  }
  const guards = [];                                                            // the knife's guards
  for (let x = -w / 2; x < w / 2 - 0.01; x += 0.15) guards.push([x, 0, x + 0.075, 0.12, x + 0.15, 0]);
  hdrStroke(canvas, guards, P.edge);
  hdrRect(canvas, -w / 2, -0.04, w / 2, 0.02, P.steel);
  // The reel seen from above: its bats across, its arms along.
  const bats = hdrPaint('bat', P.lightRgb, 0.07);
  const reel = [], arms = [], n = Math.max(2, Math.round(w / 3));
  for (let y = 0.1; y > -1.0; y -= 0.22) reel.push([-w / 2 + 0.15, y, w / 2 - 0.15, y]);
  hdrStroke(canvas, reel, bats);
  for (let k = 0; k <= n; k++) { const x = -w / 2 + 0.15 + k * (w - 0.3) / n; arms.push([x, 0.15, x, -0.95]); }
  hdrStroke(canvas, arms, P.edge);
  canvas.restore();
  return { half: 0.05, ahead: -0.12 };
}
function maizeHeaderPictureSk(canvas, tl, veh) {
  const p = renderPose();
  if (!p) return null;
  const w = Math.max(1.5, tl.totalWidth || 6), back = -1.5;
  const rows = Math.max(2, Math.round(w / 0.75)), s = w / rows, hw = Math.max(0.08, Math.min(0.3, s / 2 - 0.08));
  beginVehicleFrame(canvas, p);
  canvas.translate(0, Math.abs(veh.frontHitchLength) + tl.workingPointFromHitch);
  const P = headerFrameSk(canvas, tl, veh, w, back);
  hdrRect(canvas, -w / 2, back + 0.5, w / 2, 0.3, P.steel);                    // the row units under the snouts
  for (let i = 0; i < rows; i++) { const c = -w / 2 + (i + 0.5) * s; hdrRect(canvas, c - 0.05, back + 0.5, c + 0.05, 0.3, P.edge); }
  for (let i = 0; i <= rows; i++) {                                             // a snout between each two rows
    const x = -w / 2 + i * s;
    hdrPoly(canvas, [x - hw, back + 0.5, x - hw, 0.2, x, 1.3, x + hw, 0.2, x + hw, back + 0.5], P.frame, P.edge);
  }
  canvas.restore();
  return { half: 0.05, ahead: -0.1 };
}

// ---- Implements drawn from shapes ----
// Drawn in metres like the headers, but with y BACKWARDS (x right): from the rear axle for what rides on the
// tractor (implTractorFrame), from the drawbar pin or the swivel for a trailing tool (implTrailFrame). Sizes
// come from the tool's own width and hitch lengths, so the picture fits the settings. The part that works (the
// bodies, the discs, the cutter bar, the beaters) is drawn on the line the map paints, whatever the settings say:
// with the working point set too near, the picture is too near as well, and shows it; frames, arms and wheels fit
// round it. Colours from a tester's own machines where there were photos, else generic; never a maker's name.
const IC = {
  red: 0xC8322B, redD: 0x7D1712, steel: 0xAEB4BA, steelD: 0x5D636A, dark: 0x3D4044, grey: 0x4A4D52,
  tyre: 0x1A1B1D, tread: 0x3A3C40, rim: 0x9AA0A6, white: 0xF4F6F7, edge: 0x1E1E1E, yellow: 0xD4A62A, yellowD: 0x8A6A14,
  green: 0x3B8A3A, greenD: 0x1D4D1C, soil: 0x6A4A2C, soilD: 0x3F2A16, disc: 0xC9CED3,
};
const implPaints = {};
// The paint of the pictures drawn from shapes, and its darker parts: what the implement's colour replaces.
const IMPL_PAINT = new Set([IC.red, IC.green, IC.yellow, 0xC0392B]), IMPL_PAINT_DARK = new Set([IC.redD, IC.greenD, IC.yellowD]);
// A fill (no stroke width) or a stroke paint, made once per colour, alpha and width (all constants here).
function ip(rgb, alpha = 1, stroke = 0) {
  if (implRecolour) rgb = IMPL_PAINT.has(rgb) ? implRecolour.main : IMPL_PAINT_DARK.has(rgb) ? implRecolour.dark : rgb;
  const key = rgb + '|' + alpha + '|' + stroke;
  let p = implPaints[key];
  if (!p) {
    p = implPaints[key] = new CK.Paint();
    p.setAntiAlias(true);
    p.setStyle(stroke ? CK.PaintStyle.Stroke : CK.PaintStyle.Fill);
    if (stroke) { p.setStrokeWidth(stroke); p.setStrokeCap(CK.StrokeCap.Round); p.setStrokeJoin(CK.StrokeJoin.Round); }
    p.setColor(CK.Color((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255, alpha));
  }
  return p;
}
const iEdge = () => ip(IC.edge, 1, 0.025);
function iRect(c, l, t, r, b, fill, line = iEdge()) {
  const R = CK.LTRBRect(l, t, r, b);
  c.drawRect(R, fill);
  if (line) c.drawRect(R, line);
}
function iRRect(c, l, t, r, b, rad, fill, line = iEdge()) {
  const R = CK.RRectXY(CK.LTRBRect(l, t, r, b), rad, rad);
  c.drawRRect(R, fill);
  if (line) c.drawRRect(R, line);
}
// A tube from one point to another, w thick, with a dark rim.
function iBar(c, x1, y1, x2, y2, rgb, w, rim = true) {
  if (rim) hdrStroke(c, [[x1, y1, x2, y2]], ip(0x1D1D1D, 1, w + 0.035));
  hdrStroke(c, [[x1, y1, x2, y2]], ip(rgb, 1, w));
}
function iCircle(c, x, y, r, fill, line = iEdge()) {
  c.drawCircle(x, y, r, fill);
  if (line) c.drawCircle(x, y, r, line);
}
// A tyre seen from above: w across, d along, its tread across it.
function iTyre(c, x, y, w, d, rim) {
  iRRect(c, x - w / 2, y - d / 2, x + w / 2, y + d / 2, Math.min(w, d) * 0.22, ip(IC.tyre), ip(0x000000, 1, 0.015));
  const n = Math.max(4, Math.floor(d / 0.09)), segs = [];
  for (let k = 1; k < n; k++) { const yy = y - d / 2 + k * d / n; segs.push([x - w / 2 + w * 0.12, yy, x + w / 2 - w * 0.12, yy]); }
  hdrStroke(c, segs, ip(IC.tread, 1, 0.025));
  if (rim) iRRect(c, x - w * 0.18, y - d * 0.22, x + w * 0.18, y + d * 0.22, 0.02, ip(rim), ip(0x555555, 1, 0.01));
}
// A disc seen from above: a thin oval along the travel, turned deg from it (a gang's angle, its dish).
function iDisc(c, x, y, r, deg, fill = ip(IC.disc), line = ip(0x3C4146, 1, 0.015)) {
  c.save();
  c.translate(x, y);
  c.rotate(deg, 0, 0);
  const R = CK.LTRBRect(-r * 0.2, -r, r * 0.2, r);
  c.drawOval(R, fill);
  c.drawOval(R, line);
  c.restore();
}
// The tractor's lower links from under the rear axle to the pins L behind it, and the top link.
function iLowerLinks(c, L) {
  for (const s of [-1, 1]) iBar(c, s * 0.42, 0.25, s * 0.43, L, IC.dark, 0.08);
  iBar(c, 0, 0.35, 0, L - 0.05, 0x55595E, 0.07);
}
// A 3-point headstock (an A-frame) from the lower-link pins at y = L back to the frame at L + depth.
function iHeadstock(c, L, depth, rgb, rgbD) {
  for (const s of [-1, 1]) {
    iBar(c, s * 0.45, L, s * 0.55, L + depth, rgb, 0.09);
    iCircle(c, s * 0.45, L, 0.05, ip(0xD0D4D8));
  }
  iBar(c, 0, L - 0.05, 0, L + depth, rgb, 0.09);
  iBar(c, -0.45, L + 0.02, 0.45, L + 0.02, rgbD, 0.05, false);
}
// The tractor's frame, y backwards from the rear axle: for what rides on the tractor.
function implTractorFrame(canvas, p) {
  beginVehicleFrame(canvas, p);
  canvas.scale(1, -1);
}
// The tool's position, or null before there is one (a trailing picture asks first, so it draws neither half).
function implToolPose() {
  const t = renderTool();
  return t && (t.e || t.n) ? t : null;
}
// A trailing tool's frame at its pin (the drawbar pin, the swivel or the pivot), y backwards along the tool's
// heading: its wheels at y = Trailing hitch, its working line at Trailing hitch + Pivot distance. False
// without a tool position.
function implTrailFrame(canvas, tl) {
  const t = implToolPose();
  if (!t) return false;
  const back = tl.trailingHitchLength + tl.trailingToolToPivotLength; // the pin to the working line
  const pe = t.e + Math.sin(t.heading) * back, pn = t.n + Math.cos(t.heading) * back;
  canvas.save();
  canvas.concat(perspM);
  canvas.translate(pe - camE, pn - camN);
  canvas.rotate(-t.heading * 180 / Math.PI, 0, 0);
  canvas.scale(1, -1);
  return true;
}
// The middle of the sections across the tool (m, + right): where the tool offset puts the implement.
function implSectionsMiddle(tl) {
  const s = scene && scene.toolSections;
  return s && s.length ? (s[0].left + s[s.length - 1].right) / 2 : (tl.offset || 0);
}

// Which side of the tractor the ground still to work is on, for the implements that change side every pass
// (the reversible plough turns over, the centre-pivot mower swings across). Nothing to set: the pass label
// says which way the tractor drives on the line (↑ A to B, ↓ B to A) and, as the passes change, which way the
// work goes across the field (19R after 18R: to the right of A to B). Until the pass has changed on this track,
// the U-turn arrow's side (the next turn goes towards the ground still to work). During a U-turn the label
// names the pass it lands on, driven the other way, so the implement changes side as the turn starts. A pass
// counts once the tractor has gone 10 m on it without leaving its line (or a U-turn lands on it): crossing passes
// in a turn by hand does not. Followed on every Tick, a picture shown or not (implementPictureSk), so one chosen in the
// middle of a job knows the way the work goes; a page loaded then has the U-turn arrow until the pass changes.
// Started again on another track (another index or name) or another field.
const implSide = { tick: null, track: null, pass: null, cand: null, candM: 0, step: 0, same: true, e: 0, n: 0, f: null, t: 0 };
function implWorkLeft() {
  const k = tick;
  if (k && k !== implSide.tick) {
    implSide.tick = k;
    const act = ((scene && scene.trackList) || []).find(t => t.active);
    const track = ((scene && scene.fieldName) || '') + '\n' + (act ? act.index : '') + '\n' + (k.activeTrackName || '');
    if (track !== implSide.track) { implSide.track = track; implSide.pass = implSide.cand = null; implSide.step = 0; }
    const m = /^([↑↓])?(\d+)([RL])/.exec(k.lineLabel || '');
    const moved = Math.min(5, Math.hypot(k.pose.e - implSide.e, k.pose.n - implSide.n));
    implSide.e = k.pose.e; implSide.n = k.pose.n;
    if (m && k.guidanceActive) {
      implSide.same = m[1] !== '↓';
      const pass = m[3] === 'R' ? +m[2] - 1 : -m[2];
      const near = Math.max(0.5, ((config && config.tool && config.tool.totalWidth) || 2) / 4);
      if (pass !== implSide.cand || Math.abs(k.crossTrackError || 0) > near) { implSide.cand = pass; implSide.candM = 0; }
      else implSide.candM += moved;
      if ((k.op.executing || implSide.candM >= 10) && pass !== implSide.pass) {
        if (implSide.pass !== null) implSide.step = Math.sign(pass - implSide.pass);
        implSide.pass = pass;
      }
    }
  }
  const same = implSide.same;
  // positive: the work goes towards the passes right of A to B. Before a pass change, from the U-turn arrow
  // (its side is that, seen from the tractor: turnLeft = positive XOR same).
  const positive = implSide.step ? implSide.step > 0 : (!!(k && k.op.turnLeft)) !== same;
  return positive !== same;
}
// The side drawn, -1 (right) to +1 (left), moving towards the side of the ground still to work at `rate`
// a second: the plough turns over, the mower swings across. 0 is half way (the plough on edge).
function implSideNow(rate) {
  const target = implWorkLeft() ? 1 : -1, now = performance.now();
  const dt = (now - implSide.t) / 1000;
  implSide.t = now;
  if (implSide.f === null || dt > 2) implSide.f = target; // the first picture, or after a long gap: no swing
  else {
    const d = target - implSide.f, s = rate * dt;
    implSide.f = Math.abs(d) <= s ? target : implSide.f + Math.sign(d) * s;
  }
  return implSide.f;
}

// A one-axle manure spreader on the drawbar pin: an A-frame drawbar, the body with its load, two vertical
// beaters at the back on the working line (Trailing hitch + Pivot distance behind the pin), and the spread
// as wide as the tool, faint, behind them.
function manureSpreaderPictureSk(canvas, tl, veh) {
  if (!implTrailFrame(canvas, tl)) return null;
  const ax = tl.trailingHitchLength, beat = ax + tl.trailingToolToPivotLength;
  const back = beat + 0.2, front = Math.min(Math.max(0.9, ax - 2.85), back - 1), half = 1.0, W = Math.max(2, tl.totalWidth || 10);
  hdrPoly(canvas, [-0.9, back, -W / 2, back + 2.6, 0, back + 3.4, W / 2, back + 2.6, 0.9, back], ip(0x7A5532, 0.16), null);
  for (const s of [-1, 1]) iBar(canvas, 0, 0, s * 0.55, front + 0.05, IC.red, 0.12);
  iRRect(canvas, -0.08, -0.12, 0.08, 0.12, 0.03, ip(0x2A2C2F));
  for (const s of [-1, 1]) iTyre(canvas, s * 1.17, ax, 0.42, 1.15, IC.rim);
  iRRect(canvas, -half, front, half, back, 0.06, ip(IC.red), ip(IC.redD, 1, 0.03));
  iRRect(canvas, -half + 0.1, front + 0.1, half - 0.1, back - 0.35, 0.03, ip(IC.soil), ip(IC.soilD, 1, 0.015));
  const bars = [];
  for (let k = -3; k <= 3; k++) bars.push([k * 0.26, front + 0.12, k * 0.26, back - 0.4]);
  hdrStroke(canvas, bars, ip(0x4A3320, 1, 0.025));
  const lumps = [];
  for (let y = front + 0.35; y < back - 0.5; y += 0.45) lumps.push([-0.7, y, -0.35, y - 0.1, 0, y, 0.35, y + 0.1, 0.7, y]);
  hdrStroke(canvas, lumps, ip(0x83623F, 1, 0.05));
  iRRect(canvas, -half - 0.06, beat - 0.25, half + 0.06, beat + 0.25, 0.05, ip(IC.steel));
  for (const s of [-1, 1]) {
    iCircle(canvas, s * 0.5, beat, 0.36, ip(0x5F6469));
    hdrStroke(canvas, [[s * 0.5, beat - 0.3, s * 0.5 + 0.21, beat - 0.21, s * 0.5 + 0.3, beat, s * 0.5 + 0.16, beat + 0.16, s * 0.5, beat + 0.2, s * 0.5 - 0.12, beat + 0.08, s * 0.5 - 0.12, beat]], ip(0xE8EAEC, 1, 0.05));
  }
  canvas.restore();
  return { half: 0.05, ahead: 0 };
}

// A centre-pivot mower conditioner: on the tractor the bar from the lower links to the swivel (Vehicle hitch);
// from the swivel the tongue to the machine, swung 25° to the side still standing (implWorkLeft), the other side
// every pass, across as the U-turn starts. Its wheels Trailing hitch behind the swivel, the cutter bar Pivot
// distance from the wheels (ahead: negative), on the line the map paints. Only across does it follow the side: the
// map paints behind the tractor, where the tool settings put it.
function mowerConditionerPictureSk(canvas, tl, veh) {
  const p = renderPose();
  if (!p || !implToolPose()) return null;
  const L = Math.abs(veh.threePointHitchLength), H = Math.max(L, Math.abs(veh.hitchLength));
  implTractorFrame(canvas, p);
  iLowerLinks(canvas, L);
  iRect(canvas, -0.5, L - 0.08, 0.5, L + 0.08, ip(IC.redD));
  iBar(canvas, 0, L, 0, H, IC.red, 0.14);
  iCircle(canvas, 0, H, 0.1, ip(0xE0E3E6));
  canvas.restore();
  if (!implTrailFrame(canvas, tl)) return null;
  const side = implSideNow(0.6);           // +1: the grass standing on the left
  const a = -side * 25 * Math.PI / 180;
  const W = Math.max(1.5, tl.totalWidth || 3.2), half = W / 2;
  const wy = tl.trailingHitchLength, wx = Math.tan(a) * wy, cut = wy + tl.trailingToolToPivotLength;
  iRect(canvas, wx - half - 0.15, cut - 0.35, wx + half + 0.15, cut + 0.3, ip(IC.dark));            // the cutter bar
  for (let x = wx - half + 0.2; x < wx + half - 0.05; x += 0.4) iCircle(canvas, x, cut - 0.1, 0.17, ip(IC.disc), ip(0x333333, 1, 0.015));
  iRect(canvas, wx - half - 0.1, cut - 0.5, wx + half + 0.1, cut - 0.3, ip(IC.white), ip(IC.red, 1, 0.04)); // the front cover
  iRRect(canvas, wx - half, cut + 0.25, wx + half, cut + 1.4, 0.12, ip(IC.red), ip(IC.redD, 1, 0.03)); // the conditioner's hood
  for (const s of [-1, 1]) iRect(canvas, wx + s * half - 0.35, cut + 0.2, wx + s * half + 0.05 * s, cut + 1.45, ip(IC.grey));
  const ribs = [];
  for (let k = 0; k < 6; k++) ribs.push([wx - half + 0.4, cut + 0.42 + k * 0.17, wx + half - 0.4, cut + 0.42 + k * 0.17]);
  hdrStroke(canvas, ribs, ip(IC.redD, 1, 0.025));
  iRect(canvas, wx - half + 0.1, cut + 1.4, wx + half - 0.1, cut + 1.65, ip(IC.grey));              // the swath boards
  iBar(canvas, wx - half + 0.1, wy, wx + half - 0.1, wy, IC.dark, 0.1);
  for (const s of [-1, 1]) iTyre(canvas, wx + s * (half + 0.02), wy, 0.32, 0.72, IC.rim);
  const px = wx, py = cut + 0.55;                                                                    // the centre pivot
  iBar(canvas, 0.1, 0.3, px * 0.95 + 0.1, py - 0.3, 0x2B2D30, 0.06);                                 // the drive shaft
  iBar(canvas, 0, 0, px, py, IC.red, 0.16);                                                          // the tongue
  iCircle(canvas, px, py, 0.13, ip(IC.redD));
  canvas.restore();
  return { half: 0.05, ahead: 0 };
}

// A quick disc harrow on the rear 3-point hitch: two rows of notched discs either side of the working point,
// a row of spring tines and a packer roller behind them, as wide as the tool.
function quickDiscHarrowPictureSk(canvas, tl, veh) {
  const p = renderPose();
  if (!p) return null;
  const L = Math.abs(veh.threePointHitchLength), W = Math.max(1, tl.totalWidth || 2.7);
  const wc = L + (tl.workingPointFromHitch || 0), f1 = wc - 0.275, f2 = wc + 0.275, ry = wc + 1.1;
  implTractorFrame(canvas, p);
  iLowerLinks(canvas, L);
  iHeadstock(canvas, L, Math.max(0.1, wc - 0.6 - L), IC.red, IC.redD);
  const fr = ip(IC.red), frD = ip(IC.redD, 1, 0.025);
  iRect(canvas, -W / 2 - 0.1, wc - 0.62, W / 2 + 0.1, wc - 0.45, fr, frD);                       // main beam
  for (const s of [-1, 1]) iRect(canvas, s * (W / 2 + 0.02) - 0.05 + 0.05 * s, wc - 0.62, s * (W / 2 + 0.02) + 0.05 + 0.05 * s, ry + 0.2, fr, frD);
  const n = Math.max(3, Math.round(W / 0.25)), legs = [];
  for (let k = 0; k < n; k++) {
    const x = -W / 2 + (k + 0.5) * W / n;
    legs.push([x, wc - 0.45, x - 0.05, f1]);
    iDisc(canvas, x - 0.06, f1, 0.25, x < 0 ? -18 : 18);
    const x2 = x + W / n / 2;
    if (Math.abs(x2) < W / 2) { legs.push([x2, wc - 0.45, x2 + 0.05, f2]); iDisc(canvas, x2 + 0.06, f2, 0.25, x2 < 0 ? 18 : -18); }
  }
  hdrStroke(canvas, legs, ip(0x5D636A, 1, 0.035));
  iRect(canvas, -W / 2 - 0.1, wc + 0.45, W / 2 + 0.1, wc + 0.58, fr, frD);                        // the tines' beam
  const tines = [];
  for (let x = -W / 2 + 0.1; x < W / 2; x += 0.2) tines.push([x, wc + 0.5, x + 0.03, wc + 0.72, x - 0.02, wc + 0.8]);
  hdrStroke(canvas, tines, ip(IC.redD, 1, 0.03));
  iRRect(canvas, -W / 2, ry - 0.28, W / 2, ry + 0.28, 0.1, ip(0x6D747B));                       // the cage roller
  const bars = [];
  for (let y = ry - 0.2; y < ry + 0.25; y += 0.13) bars.push([-W / 2 + 0.05, y, W / 2 - 0.05, y]);
  hdrStroke(canvas, bars, ip(0xC7CCD1, 1, 0.04));
  for (const s of [-1, 1]) iRect(canvas, s * W / 2 - 0.04, ry - 0.3, s * W / 2 + 0.04, ry + 0.3, fr, frD);
  canvas.restore();
  return { half: 0.05, ahead: 0 };
}

// A semi-mounted roller: on the tractor a bar on the lower links to its pivot (Vehicle hitch); from the pivot
// an A-frame to a row of spring tines and the roller on the working line, as wide as the tool.
function rollerPictureSk(canvas, tl, veh) {
  const p = renderPose();
  if (!p || !implToolPose()) return null;
  const L = Math.abs(veh.threePointHitchLength), H = Math.max(L, Math.abs(veh.hitchLength));
  implTractorFrame(canvas, p);
  iLowerLinks(canvas, L);
  iRect(canvas, -0.55, L - 0.07, 0.55, L + 0.07, ip(IC.dark));
  iBar(canvas, 0, L, 0, H, IC.dark, 0.12);
  canvas.restore();
  if (!implTrailFrame(canvas, tl)) return null;
  const W = Math.max(1, tl.totalWidth || 3.2), ry = tl.trailingHitchLength + tl.trailingToolToPivotLength;
  const fr = ip(IC.yellow), frD = ip(IC.yellowD, 1, 0.025), tb = ry - 0.75;
  for (const s of [-1, 1]) iBar(canvas, 0, 0, s * (W / 2 - 0.05), ry - 0.35, IC.yellow, 0.12);  // the A-frame
  iBar(canvas, -W / 4, ry / 2, W / 4, ry / 2, IC.yellow, 0.1);
  iRect(canvas, -W / 2 - 0.05, tb - 0.07, W / 2 + 0.05, tb + 0.07, ip(0x5B4A2A), frD);          // the tines' bar
  const tines = [];
  for (let x = -W / 2 + 0.15; x < W / 2 - 0.05; x += 0.3) { iCircle(canvas, x, tb, 0.06, ip(0x2B2D30), null); tines.push([x, tb, x + 0.02, tb + 0.3]); }
  hdrStroke(canvas, tines, ip(0x2B2D30, 1, 0.025));
  for (const s of [-1, 1]) iRect(canvas, s * (W / 2 + 0.06) - 0.07, ry - 0.45, s * (W / 2 + 0.06) + 0.07, ry + 0.35, fr, frD);
  iRRect(canvas, -W / 2, ry - 0.28, W / 2, ry + 0.28, 0.12, ip(0x8B9198));                      // the roller's rings
  const rings = [], spikes = [];
  for (let k = 1, n = Math.round(W / 0.13); k < n; k++) {
    const x = -W / 2 + k * W / n;
    rings.push([x, ry - 0.27, x, ry + 0.27]);
    spikes.push([x - 0.05, ry - 0.2 + (k % 2) * 0.2, x + 0.05, ry - 0.1 + (k % 2) * 0.2]);
  }
  hdrStroke(canvas, rings, ip(0x5A6067, 1, 0.04));
  hdrStroke(canvas, spikes, ip(0xD3D7DB, 1, 0.03));
  iCircle(canvas, 0, 0, 0.1, ip(0xE0E3E6));
  canvas.restore();
  return { half: 0.05, ahead: 0 };
}

// Where the bodies of a reversible plough go across (m, + right of the tractor's middle): the front body behind
// the rear tyre in the open furrow, its land side at that tyre's inner edge, the others stepping back towards
// the ground still to plough, w apart. th: the side the soil is thrown to (the land ploughed before, where that
// tyre is). Each body's y along the beam, the bodies' middle at yc, sp apart.
function iPloughBodyPlaces(veh, n, w, th, yc, sp) {
  const edge = (veh.trackWidth || 1.8) / 2 - 0.3, y0 = yc - (n - 1) * sp / 2, xs = [];
  for (let b = 0; b < n; b++) xs.push([th * (edge - w * (b + 0.5)), y0 + b * sp]);
  return xs;
}
// A reversible plough's bodies at xs: each one at work (white, throwing to th) and the one above it, up (grey);
// k their size (1 for 40 cm), X the squeeze across as the plough turns over.
function iPloughBodies(canvas, xs, th, k, X) {
  const body = (x, y, t) => [X(x - t * 0.06 * k), y - 0.5 * k, X(x + t * 0.4 * k), y - 0.08 * k, X(x + t * 0.6 * k), y + 0.4 * k,
    X(x + t * 0.3 * k), y + 0.34 * k, X(x - t * 0.03 * k), y + 0.22 * k];
  for (const [x, y] of xs) {
    hdrPoly(canvas, body(x, y, -th), ip(0xCFD4D8, 0.9), ip(0x6B7177, 1, 0.015));                 // the body up, not working
    hdrPoly(canvas, body(x, y, th), ip(IC.white), ip(0x24282C, 1, 0.025));                        // the body at work
    hdrStroke(canvas, [[X(x + th * 0.02 * k), y - 0.4 * k, X(x + th * 0.3 * k), y - 0.05 * k, X(x + th * 0.52 * k), y + 0.36 * k]], ip(0x9AA1A7, 1, 0.035));
    iRect(canvas, X(x) - 0.07, y - 0.62 * k, X(x) + 0.07, y - 0.28 * k, ip(IC.red), ip(IC.redD, 1, 0.015)); // its leg
    hdrPoly(canvas, [X(x + th * 0.02), y - 0.98 * k, X(x + th * 0.26), y - 0.84 * k, X(x + th * 0.05), y - 0.74 * k], ip(0xF1F3F4), iEdge()); // the skimmer
  }
}
// A reversible plough on the rear 3-point hitch. The bodies at work throw the soil away from the ground still
// to plough, towards the land ploughed before: the front body runs behind the rear tyre that is in the open
// furrow, the others step back towards the ground still to plough. It turns over as each U-turn starts (drawn
// on edge half way: x squeezed by |f|). 2 to 7 bodies (mounted ploughs are made so), about 40 cm each, as many as
// the tool's width takes; the bodies' middle at the working point. On a tractor or an articulated one.
function reversiblePloughPictureSk(canvas, tl, veh) {
  const p = renderPose();
  if (!p) return null;
  const L = Math.abs(veh.threePointHitchLength), W = Math.max(0.6, tl.totalWidth || 1.6);
  const n = Math.max(2, Math.min(7, Math.round(W / 0.4))), w = W / n, k = w / 0.4, sp = 0.95 * k;
  const f = implSideNow(0.6), th = f >= 0 ? 1 : -1, sq = Math.max(0.04, Math.abs(f));
  const X = (x) => x * sq;
  const xs = iPloughBodyPlaces(veh, n, w, th, L + (tl.workingPointFromHitch || 0), sp);
  implTractorFrame(canvas, p);
  iLowerLinks(canvas, L);
  iRRect(canvas, -0.5, L - 0.08, 0.5, L + 0.12, 0.04, ip(IC.redD));                              // the headstock
  iCircle(canvas, 0, L + 0.38, 0.2, ip(IC.grey));                                                  // the turnover head
  iBar(canvas, 0, L + 0.1, 0, L + 0.55, IC.redD, 0.12);
  const fx = xs[0][0] + th * 0.1, fy = Math.max(L + 0.7, xs[0][1] - 0.8), rx = xs[n - 1][0] - th * 0.1, ry = xs[n - 1][1] + 0.25;
  iBar(canvas, 0, L + 0.55, X(fx), fy, IC.red, 0.18);                                              // the arm and the beam
  iBar(canvas, X(fx), fy, X(rx), ry, IC.red, 0.22);
  iCircle(canvas, X(fx), fy, 0.08, ip(IC.redD));
  iPloughBodies(canvas, xs, th, k, X);
  const [lx, ly] = xs[n - 1];
  iBar(canvas, X(rx), ry - 0.05, X(lx - th * 0.55), ly + 0.15, IC.red, 0.07);
  iTyre(canvas, X(lx - th * 0.55), ly + 0.15, 0.28, 0.62, IC.rim);                                 // the depth wheel
  canvas.restore();
  return { half: 0.05, ahead: 0 };
}
// A semi-mounted reversible plough: a headstock on the lower links with the plough's swivel behind it (Vehicle
// hitch), the turnover head and a long beam to 5 to 12 bodies (about 40 cm each, as many as the tool's width
// takes) turning about that swivel, the bodies' middle on the working line (Trailing hitch plus Pivot distance),
// the combined depth and transport wheel behind the last body (or at Trailing hitch, if further back). It turns
// over as each U-turn starts, as the mounted one. On a tractor or an articulated one.
function semiMountedPloughPictureSk(canvas, tl, veh) {
  const p = renderPose();
  if (!p || !implToolPose()) return null;
  const L = Math.abs(veh.threePointHitchLength), H = Math.max(L + 0.25, Math.abs(veh.hitchLength));
  implTractorFrame(canvas, p);
  iLowerLinks(canvas, L);
  iHeadstock(canvas, L, H - L, IC.red, IC.redD);
  iCircle(canvas, 0, H, 0.12, ip(0xE0E3E6));                                                       // the swivel
  canvas.restore();
  if (!implTrailFrame(canvas, tl)) return null;
  const W = Math.max(2, tl.totalWidth || 4);
  const n = Math.max(5, Math.min(12, Math.round(W / 0.4))), w = W / n, k = w / 0.4, sp = 0.95 * k;
  const f = implSideNow(0.6), th = f >= 0 ? 1 : -1, sq = Math.max(0.04, Math.abs(f));
  const X = (x) => x * sq;
  const xs = iPloughBodyPlaces(veh, n, w, th, tl.trailingHitchLength + tl.trailingToolToPivotLength, sp);
  iBar(canvas, 0, 0, 0, 0.6, IC.redD, 0.16);                                                       // the frame from the swivel
  iCircle(canvas, 0, 0.55, 0.22, ip(IC.grey));                                                     // the turnover head
  const fx = xs[0][0] + th * 0.1, fy = Math.max(1.0, xs[0][1] - 0.8);
  const [lx, ly] = xs[n - 1], wy = Math.max(ly + 0.55, tl.trailingHitchLength), wx = lx - th * 0.7;
  iBar(canvas, 0, 0.6, X(fx), fy, IC.red, 0.22);                                                   // the beam
  iBar(canvas, X(fx), fy, X(lx - th * 0.1), ly + 0.25, IC.red, 0.26);
  iBar(canvas, X(lx - th * 0.1), ly + 0.25, X(wx), wy, IC.red, 0.14);
  iCircle(canvas, X(fx), fy, 0.09, ip(IC.redD));
  hdrStroke(canvas, [[0.12 * th, 0.75, X(fx * 0.6), fy * 0.6 + 0.3]], ip(0x2B2D30, 1, 0.06));          // the turnover ram
  iPloughBodies(canvas, xs, th, k, X);
  iTyre(canvas, X(wx), wy, 0.42, 1.0, IC.rim);                                                     // the depth and transport wheel
  canvas.restore();
  return { half: 0.05, ahead: 0 };
}
// A disc seen from above, its plane along (ux, uy): an oval r long and r·m across, with its hub.
function iDiscAlong(c, x, y, ux, uy, r, m, fill = ip(IC.disc), line = ip(0x3C4146, 1, 0.015)) {
  const pts = [];
  for (let a = 0; a < 24; a++) {
    const t = a * Math.PI / 12, cs = Math.cos(t) * r, sn = Math.sin(t) * r * m;
    pts.push(x + ux * cs - uy * sn, y + uy * cs + ux * sn);
  }
  hdrPoly(c, pts, fill, line);
  iCircle(c, x, y, r * 0.16, ip(0x5D636A), null);
}
// A reversible disc plough on the rear 3-point hitch: a headstock with the turntable that swings the beam across,
// the beam back towards the ground still to plough, 2 to 6 discs of 70 cm (one for every 30 cm or so of the
// tool's width) on their standards, each at 45° to the travel and throwing the soil towards the land ploughed
// before, the discs' middle at the working point, and the furrow wheel behind the last one. It swings across as
// each U-turn starts (x squeezed by |f|). On a tractor or an articulated one.
function discPloughPictureSk(canvas, tl, veh) {
  const p = renderPose();
  if (!p) return null;
  const L = Math.abs(veh.threePointHitchLength), W = Math.max(0.5, tl.totalWidth || 1.2);
  const n = Math.max(2, Math.min(6, Math.round(W / 0.3))), w = W / n, sp = 0.8, r = 0.35;
  const f = implSideNow(0.6), th = f >= 0 ? 1 : -1, sq = Math.max(0.04, Math.abs(f));
  const X = (x) => x * sq;
  const xs = iPloughBodyPlaces(veh, n, w, th, L + (tl.workingPointFromHitch || 0), sp);
  implTractorFrame(canvas, p);
  iLowerLinks(canvas, L);
  iHeadstock(canvas, L, 0.35, IC.red, IC.redD);
  iCircle(canvas, 0, L + 0.45, 0.2, ip(IC.grey));                                                  // the turntable
  const bx = -th * 0.3, [lx, ly] = xs[n - 1];                                                     // the beam beside the discs, over the land side
  const fx = xs[0][0] + bx, fy = Math.max(L + 0.6, xs[0][1] - 0.45);
  const u = 1 / Math.SQRT2, ux = th * u * sq, uy = u, m = Math.hypot(ux, uy) || 1;
  for (const [x, y] of xs) iDiscAlong(canvas, X(x), y, ux / m, uy / m, r, 0.32);                   // the discs at work
  const wx = lx - th * 0.2, wy = ly + 0.75;                                                        // in the last furrow, against its wall
  iDiscAlong(canvas, X(wx), wy, th * 0.26 * sq, 0.97, 0.3, 0.55, ip(IC.tyre), ip(0x000000, 1, 0.015)); // the furrow wheel
  iBar(canvas, 0, L + 0.45, X(fx), fy, IC.red, 0.2);                                               // the beam, over them
  iBar(canvas, X(fx), fy, X(lx + bx), ly + 0.1, IC.red, 0.22);
  for (const [x, y] of xs) iBar(canvas, X(x + bx), y - 0.25, X(x), y - 0.02, IC.redD, 0.1);       // each disc's standard
  iBar(canvas, X(lx + bx), ly + 0.1, X(wx), wy - 0.2, IC.red, 0.1);
  canvas.restore();
  return { half: 0.05, ahead: 0 };
}

// Four gangs of discs in an X about the working line y = yc (the front gangs' inner ends further back, the rear
// gangs' further on, 16° from square), their outer ends g either side of it, under a frame d either side of it, W
// across: a tandem disc harrow seen from above. Each disc square to its gang's axle.
function iDiscGangsX(c, W, yc, d, g = d * 0.62) {
  const fr = ip(IC.red), frD = ip(IC.redD, 1, 0.025), dark = ip(0x8A9096), rim = ip(0x2B2D30, 1, 0.02);
  iRect(c, -W / 2 - 0.1, yc - d, W / 2 + 0.1, yc - d + 0.14, fr, frD);                              // front and rear beams
  iRect(c, -W / 2 - 0.1, yc + d - 0.12, W / 2 + 0.1, yc + d, fr, frD);
  for (const s of [-1, 1]) iRect(c, s * (W / 2 + 0.05) - 0.06, yc - d, s * (W / 2 + 0.05) + 0.06, yc + d, fr, frD);
  const gang = (x0, x1, y, ang) => {
    const tn = Math.tan(ang * Math.PI / 180), m = Math.max(1, Math.floor(Math.abs(x1 - x0) / 0.23));
    hdrStroke(c, [[x0, y, x1, y + (x1 - x0) * tn]], ip(0x202225, 1, 0.07));
    for (let j = 0; j <= m; j++) { const x = x0 + (x1 - x0) * j / m; iDisc(c, x, y + (x - x0) * tn, 0.28, ang, dark, rim); }
  };
  gang(-W / 2 + 0.1, -0.05, yc - g, 16); gang(W / 2 - 0.1, 0.05, yc - g, -16);
  gang(-W / 2 + 0.25, 0.1, yc + g, -16); gang(W / 2 - 0.25, -0.1, yc + g, 16);
  for (const s of [-1, 1]) iRect(c, s * W / 4 - 0.06, yc - d, s * W / 4 + 0.06, yc + d, fr, frD);  // the beams the gangs hang from
}
// A trailed disc harrow on the drawbar pin: an A-frame tongue to a frame over four gangs in an X, its wheels in
// pairs in the gap between the front and the rear gangs (Trailing hitch behind the pin, kept in the gap), the gangs
// about the working line, as wide as the tool; a pair of wheels more every 4 m past 5 m. The gangs' inner ends keep
// clear of the wheels, so the frame is longer the wider the harrow (3.7 m for 4 m).
function discHarrowTrailedPictureSk(canvas, tl, veh) {
  if (!implTrailFrame(canvas, tl)) return null;
  const W = Math.max(2, tl.totalWidth || 4), slant = (W / 2 - 0.15) * Math.tan(16 * Math.PI / 180);
  const clear = 0.28 + 0.36 + 0.2, g = slant + clear, d = g + 0.45;  // a disc's radius, a wheel's, room between
  const yc = tl.trailingHitchLength + tl.trailingToolToPivotLength, room = clear - 0.28 - 0.36;
  const wy = Math.max(yc - room, Math.min(yc + room, tl.trailingHitchLength));
  for (const s of [-1, 1]) iBar(canvas, 0.05 * s, 0.35, s * W / 4, yc - d + 0.07, IC.red, 0.13);  // the A-frame tongue
  iBar(canvas, 0, 0, 0, 0.6, IC.red, 0.15);
  iBar(canvas, -W / 8, (yc - d) * 0.7, W / 8, (yc - d) * 0.7, IC.red, 0.08);
  iRRect(canvas, -0.08, -0.12, 0.08, 0.12, 0.03, ip(0x2A2C2F));                                     // the hitch eye
  iDiscGangsX(canvas, W, yc, d, g);
  const pairs = [0.55];
  for (let x = 2.0; x < W / 2 - 0.4; x += 2.0) pairs.push(x);
  iBar(canvas, -pairs[pairs.length - 1] - 0.2, wy, pairs[pairs.length - 1] + 0.2, wy, IC.dark, 0.08);
  for (const x of pairs) for (const s of [-1, 1]) { iTyre(canvas, s * (x - 0.2), wy, 0.26, 0.72, IC.rim); iTyre(canvas, s * (x + 0.2), wy, 0.26, 0.72, IC.rim); }
  canvas.restore();
  return { half: 0.05, ahead: 0 };
}
// A disc harrow on the rear 3-point hitch: a headstock tower braced to the frame over four gangs in an X about the
// working point, as wide as the tool, no wheels.
function discHarrowMountedPictureSk(canvas, tl, veh) {
  const p = renderPose();
  if (!p) return null;
  const L = Math.abs(veh.threePointHitchLength), W = Math.max(1, tl.totalWidth || 2.4), d = 0.9;
  const yc = L + (tl.workingPointFromHitch || 0);
  implTractorFrame(canvas, p);
  iLowerLinks(canvas, L);
  iHeadstock(canvas, L, Math.max(0.1, yc - d - L), IC.red, IC.redD);
  for (const s of [-1, 1]) iBar(canvas, 0, L + 0.05, s * W / 4, yc + d - 0.06, IC.red, 0.08);    // the braces
  iDiscGangsX(canvas, W, yc, d);
  canvas.restore();
  return { half: 0.05, ahead: 0 };
}

// A twin-disc fertiliser spreader on the rear 3-point hitch: the hopper (red, its grey extension and the dark
// cover seen from above) just behind the lower links, its two discs behind it and the spread, faint, as wide as the
// tool. It rides on the tractor wherever the working point (where the product lands) is set; the spread lands there
// when that is behind the discs.
function fertiliserSpreaderPictureSk(canvas, tl, veh) {
  const p = renderPose();
  if (!p) return null;
  const L = Math.abs(veh.threePointHitchLength), W = Math.max(3, tl.totalWidth || 18), hw = 1.2;
  const y0 = L + 0.1, y1 = L + 1.4, dy = y1 + 0.08, land = L + (tl.workingPointFromHitch || 0);
  const r = land > dy + 1 ? land - dy : W * 0.2, b = W * 0.12;                                     // to where it lands
  implTractorFrame(canvas, p);
  hdrPoly(canvas, [-0.7, dy, -W / 2, dy + r, -W / 4, dy + r + b * 0.8, 0, dy + r + b, W / 4, dy + r + b * 0.8, W / 2, dy + r, 0.7, dy],
    ip(0xD9D2C3, 0.12), null);                                                                      // the spread
  iLowerLinks(canvas, L);
  iRect(canvas, -0.55, L - 0.06, 0.55, L + 0.2, ip(0x2B2D30));                                     // its frame
  for (const s of [-1, 1]) {                                                                        // the two discs and their vanes
    const x = s * 0.42;
    iCircle(canvas, x, dy, 0.32, ip(0x8A9096), ip(0x2B2D30, 1, 0.02));
    hdrStroke(canvas, [[x - 0.28, dy, x + 0.28, dy], [x, dy - 0.28, x, dy + 0.28]], ip(0x3A3D41, 1, 0.035));
  }
  iRRect(canvas, -hw, y0, hw, y1, 0.08, ip(IC.red), ip(IC.redD, 1, 0.025));                        // the hopper
  iRRect(canvas, -hw + 0.05, y0 + 0.05, hw - 0.05, y1 - 0.05, 0.06, ip(0xB9BEC3), null);          // its extension
  iRRect(canvas, -hw + 0.14, y0 + 0.13, hw - 0.14, y1 - 0.13, 0.05, ip(0x3A3D41), null);          // the cover
  hdrStroke(canvas, [[-hw + 0.2, (y0 + y1) / 2, hw - 0.2, (y0 + y1) / 2]], ip(0x5D636A, 1, 0.03));
  iBar(canvas, -0.95, dy + 0.42, 0.95, dy + 0.42, 0x2B2D30, 0.05);                                 // the guard behind the discs
  canvas.restore();
  return { half: 0.05, ahead: 0 };
}

// A disc mower on the rear 3-point hitch, working beside the tractor: its cutter bar where the tool offset puts
// it (the sections' middle), at the working point, as wide as the tool; an arm with its breakaway spring from the
// headstock to the bar's inner end. With no offset it is drawn behind the tractor, where AgOpenWeb paints.
function rearSideMowerPictureSk(canvas, tl, veh) {
  const p = renderPose();
  if (!p) return null;
  const L = Math.abs(veh.threePointHitchLength), W = Math.max(1, tl.totalWidth || 2.4);
  const cx = implSectionsMiddle(tl), wc = L + (tl.workingPointFromHitch || 0);
  const s = cx >= 0 ? 1 : -1, inner = Math.abs(cx) > 0.5 ? cx - s * W / 2 : 0;
  implTractorFrame(canvas, p);
  iLowerLinks(canvas, L);
  iHeadstock(canvas, L, 0.35, IC.red, IC.redD);
  const ay = Math.max(L + 0.4, wc - 0.1), sy = Math.max(L + 0.2, wc - 0.35);
  iBar(canvas, s * 0.3, L + 0.35, inner, ay, IC.red, 0.16);                                         // the arm
  iBar(canvas, s * 0.1, L + 0.1, inner * 0.7, sy, 0xC0392B, 0.08);                                 // its spring
  hdrStroke(canvas, [[s * 0.1, L + 0.1, inner * 0.7, sy]], ip(0x7D1712, 1, 0.03));
  iRect(canvas, cx - W / 2 - 0.05, wc - 0.25, cx + W / 2 + 0.05, wc + 0.8, ip(IC.green), ip(IC.greenD, 1, 0.03)); // the cover
  iRect(canvas, cx - W / 2 + 0.1, wc + 0.08, cx + W / 2 - 0.1, wc + 0.38, ip(IC.red), ip(IC.redD, 1, 0.02));     // its top
  const folds = [];
  for (let k = 1; k < 4; k++) folds.push([cx - W / 2 + 0.05, wc - 0.25 + k * 0.26, cx + W / 2 - 0.05, wc - 0.25 + k * 0.26]);
  hdrStroke(canvas, folds, ip(IC.greenD, 0.5, 0.02));
  iRRect(canvas, inner - 0.22, wc - 0.3, inner + 0.22, wc + 0.3, 0.05, ip(IC.red));                // the drive
  canvas.restore();
  return { half: 0.05, ahead: 0 };
}

// A rotary rake's rotor from above at (x, y), r to the tips of its tines: four small carrier wheels under the
// gearbox, a tine arm every 30° or so with its four tines at the end, bent back against the turn (spin +1:
// clockwise from above, the swath to the left; -1 the other way), and the gearbox.
function iRakeRotor(c, x, y, r, spin) {
  const n = Math.max(8, Math.min(16, Math.round(r * 4.5))), arms = [], tines = [];
  for (const sx of [-1, 1]) for (const sy of [-1, 1]) iTyre(c, x + sx * 0.3, y + sy * 0.3, 0.12, 0.3, null);
  for (let k = 0; k < n; k++) {
    const a = (k + 0.5) * 2 * Math.PI / n, ca = Math.cos(a), sa = Math.sin(a), e = Math.max(0.5, r - 0.38);
    const ex = x + ca * e, ey = y + sa * e, tx = -sa * spin, ty = ca * spin;    // the arm's end, the way it turns
    arms.push([x + ca * 0.25, y + sa * 0.25, ex, ey]);
    for (let j = 0; j < 4; j++) {
      const bx = ex - tx * j * 0.09, by = ey - ty * j * 0.09;
      tines.push([bx, by, bx + ca * 0.36 - tx * 0.06, by + sa * 0.36 - ty * 0.06]);
    }
  }
  hdrStroke(c, arms, ip(IC.dark, 1, 0.05));
  hdrStroke(c, tines, ip(0x2F5E2F, 1, 0.025));
  iCircle(c, x, y, 0.3, ip(IC.red));
  iCircle(c, x, y, 0.12, ip(IC.grey), null);
}
// A guard of tube round a rotor, r + 0.15 out, from angle a0 to a1 (radians from the right, + towards the back).
function iRakeGuard(c, x, y, r, a0, a1) {
  const segs = [], n = 12, R = r + 0.15;
  for (let k = 0; k < n; k++) {
    const t0 = a0 + (a1 - a0) * k / n, t1 = a0 + (a1 - a0) * (k + 1) / n;
    segs.push([x + Math.cos(t0) * R, y + Math.sin(t0) * R, x + Math.cos(t1) * R, y + Math.sin(t1) * R]);
  }
  hdrStroke(c, segs, ip(0x1D1D1D, 1, 0.09));
  hdrStroke(c, segs, ip(IC.red, 1, 0.055));
}
// A swath curtain: a white cloth on a frame, from (x, y0) to (x, y1).
function iSwathCurtain(c, x, y0, y1) {
  iRect(c, x - 0.05, y0, x + 0.05, y1, ip(0xEDEFF0), ip(IC.red, 1, 0.04));
}
// The tractor's half of a trailed rake: the lower links and a headstock to the rake's pin (Vehicle hitch, at
// least 25 cm behind the lower-link pins).
function iRakeHitch(canvas, p, veh) {
  const L = Math.abs(veh.threePointHitchLength), H = Math.max(L + 0.25, Math.abs(veh.hitchLength));
  implTractorFrame(canvas, p);
  iLowerLinks(canvas, L);
  iHeadstock(canvas, L, H - L, IC.red, IC.redD);
  iCircle(canvas, 0, H, 0.1, ip(0xE0E3E6));
  canvas.restore();
}
// A rotary rake with one rotor on the rear 3-point hitch: the headstock, a frame to the gearbox at the working
// point (where the sections are across), the rotor as wide as the tool turning clockwise, a guard round its front
// and right, and the swath curtain on its left, where it lays the swath.
function rakeOneRotorPictureSk(canvas, tl, veh) {
  const p = renderPose();
  if (!p) return null;
  const L = Math.abs(veh.threePointHitchLength), r = Math.max(1, (tl.totalWidth || 3.5) / 2);
  const cx = implSectionsMiddle(tl), yc = L + (tl.workingPointFromHitch || 0);
  implTractorFrame(canvas, p);
  iLowerLinks(canvas, L);
  iRakeRotor(canvas, cx, yc, r, 1);
  iRakeGuard(canvas, cx, yc, r, -0.85 * Math.PI, 0.3 * Math.PI);
  iHeadstock(canvas, L, 0.3, IC.red, IC.redD);
  iBar(canvas, 0, L + 0.3, cx, yc, IC.red, 0.16);                                                  // the frame to the gearbox
  iBar(canvas, cx - 0.2, yc - 0.3, cx - r - 0.3, yc - 0.5, IC.red, 0.06);
  iSwathCurtain(canvas, cx - r - 0.3, yc - 0.55, yc + 0.95);
  canvas.restore();
  return { half: 0.05, ahead: 0 };
}
// A trailed rotary rake with two rotors side by side, centre delivery: a beam from the pin (Vehicle hitch) to the
// chassis wheels (Trailing hitch), the rotors on the working line (Trailing hitch plus Pivot distance) turning in
// towards the middle, where the swath is laid, each half the tool's width, with a guard round its front and side.
function rakeTwinCentrePictureSk(canvas, tl, veh) {
  const p = renderPose();
  if (!p || !implToolPose()) return null;
  iRakeHitch(canvas, p, veh);
  if (!implTrailFrame(canvas, tl)) return null;
  const W = Math.max(3, tl.totalWidth || 6.5), r = W / 4 + 0.05, cx = implSectionsMiddle(tl);
  const yc = tl.trailingHitchLength + tl.trailingToolToPivotLength, wy = Math.max(yc + 0.6, tl.trailingHitchLength);
  for (const s of [-1, 1]) iTyre(canvas, cx + s * 0.55, wy, 0.3, 0.75, IC.rim);                   // the chassis wheels
  iBar(canvas, cx - 0.55, wy, cx + 0.55, wy, IC.dark, 0.08);
  for (const s of [-1, 1]) {
    const x = cx + s * (W / 2 - r);
    iRakeRotor(canvas, x, yc, r, -s);
    iRakeGuard(canvas, x, yc, r, s < 0 ? 0.7 * Math.PI : 0.3 * Math.PI, s < 0 ? 1.6 * Math.PI : -0.6 * Math.PI);
  }
  iBar(canvas, 0, 0, cx, yc - 0.9, IC.red, 0.18);                                                  // the drawbar beam
  iBar(canvas, cx, yc - 0.9, cx, wy, IC.red, 0.2);
  for (const s of [-1, 1]) iBar(canvas, cx, yc - 0.6, cx + s * (W / 2 - r), yc, IC.red, 0.12);     // the arms to the rotors
  canvas.restore();
  return { half: 0.05, ahead: 0 };
}
// A trailed rotary rake with two rotors one behind the other, side delivery: from the pin (Vehicle hitch) a beam
// back and out to the right, the front rotor on the left of the tool's width and the rear one on the right, each
// half of it, both turning anticlockwise: the front one hands its hay to the rear one, which lays the swath on its
// right behind the curtain. The two rotors' middle on the working line, the wheels on the beam at Trailing hitch,
// held between the rotors.
function rakeTwinSidePictureSk(canvas, tl, veh) {
  const p = renderPose();
  if (!p || !implToolPose()) return null;
  iRakeHitch(canvas, p, veh);
  if (!implTrailFrame(canvas, tl)) return null;
  const W = Math.max(3, tl.totalWidth || 6.5), r = W / 4 + 0.15, cx = implSectionsMiddle(tl), d = r * 0.85;
  const yc = tl.trailingHitchLength + tl.trailingToolToPivotLength;
  const f = [cx - W / 2 + r, yc - d], b = [cx + W / 2 - r, yc + d];
  const my = Math.max(f[1] + 0.4, Math.min(b[1] - 0.4, tl.trailingHitchLength)), m = [f[0] + (b[0] - f[0]) * (my - f[1]) / (b[1] - f[1]), my];
  for (const s of [-1, 1]) iTyre(canvas, m[0] + s * 0.45, m[1], 0.26, 0.66, IC.rim);              // the wheels
  iRakeRotor(canvas, f[0], f[1], r, -1);
  iRakeRotor(canvas, b[0], b[1], r, -1);
  iRakeGuard(canvas, f[0], f[1], r, 0.6 * Math.PI, 1.45 * Math.PI);
  iRakeGuard(canvas, b[0], b[1], r, -0.8 * Math.PI, -0.15 * Math.PI);
  iBar(canvas, 0, 0, f[0], f[1], IC.red, 0.18);                                                    // the beam
  iBar(canvas, f[0], f[1], b[0], b[1], IC.red, 0.2);
  iBar(canvas, m[0] - 0.45, m[1], m[0] + 0.45, m[1], IC.dark, 0.08);
  iBar(canvas, b[0] + 0.2, b[1] - 0.3, b[0] + r + 0.3, b[1] - 0.5, IC.red, 0.06);
  iSwathCurtain(canvas, b[0] + r + 0.3, b[1] - 0.55, b[1] + 0.95);
  canvas.restore();
  return { half: 0.05, ahead: 0 };
}
// A trailed rotary rake with four rotors, centre delivery: a beam from the pin (Vehicle hitch) to the chassis
// wheels (Trailing hitch), two rotors in front on arms out to the tool's edges and two behind, closer in, all
// turning in towards the middle, where the swath is laid, each with a guard on its outer side; the rotors' middle
// on the working line.
function rakeFourPictureSk(canvas, tl, veh) {
  const p = renderPose();
  if (!p || !implToolPose()) return null;
  iRakeHitch(canvas, p, veh);
  if (!implTrailFrame(canvas, tl)) return null;
  const W = Math.max(6, tl.totalWidth || 12.5), r = Math.max(1.2, Math.min(2.0, W * 0.14)), cx = implSectionsMiddle(tl);
  const yc = tl.trailingHitchLength + tl.trailingToolToPivotLength, d = r + 0.1;
  const fx = W / 2 - r, rx = Math.min(fx - r * 0.6, 0.7 + r), wy = Math.max(yc - d + 0.8, Math.min(yc + d - 0.6, tl.trailingHitchLength));
  for (const s of [-1, 1]) iTyre(canvas, cx + s * 0.75, wy, 0.42, 1.0, IC.rim);                    // the chassis wheels
  for (const s of [-1, 1]) {
    iRakeRotor(canvas, cx + s * fx, yc - d, r, -s);
    iRakeRotor(canvas, cx + s * rx, yc + d, r, -s);
    iRakeGuard(canvas, cx + s * fx, yc - d, r, s < 0 ? 0.75 * Math.PI : 0.25 * Math.PI, s < 0 ? 1.55 * Math.PI : -0.55 * Math.PI);
    iRakeGuard(canvas, cx + s * rx, yc + d, r, s < 0 ? 0.7 * Math.PI : 0.3 * Math.PI, s < 0 ? 1.3 * Math.PI : -0.3 * Math.PI);
  }
  iBar(canvas, 0, 0, cx, yc - d - 1.2, IC.red, 0.2);                                               // the main beam
  iBar(canvas, cx, yc - d - 1.2, cx, wy + 0.6, IC.red, 0.24);
  iBar(canvas, cx - 0.75, wy, cx + 0.75, wy, IC.dark, 0.1);
  for (const s of [-1, 1]) {
    iBar(canvas, cx + s * 0.1, yc - d - 1.0, cx + s * fx, yc - d, IC.red, 0.14);                   // the front arms
    iBar(canvas, cx + s * 0.1, yc + 0.2, cx + s * rx, yc + d, IC.red, 0.12);                       // the rear arms
  }
  canvas.restore();
  return { half: 0.05, ahead: 0 };
}
// A finger wheel from above at (x, y), its plane along (ux, uy), r to the fingers' tips, a little tilted (m): the
// rim and its spokes, the fingers round it.
function iFingerWheel(c, x, y, ux, uy, r, m) {
  const rim = [], spokes = [], fingers = [], R = r - 0.15;
  for (let k = 0; k < 24; k++) {
    const t = k * Math.PI / 12, cs = Math.cos(t), sn = Math.sin(t);
    const px = x + (ux * cs - uy * sn * m) * R, py = y + (uy * cs + ux * sn * m) * R;
    const qx = x + (ux * cs - uy * sn * m) * r, qy = y + (uy * cs + ux * sn * m) * r;
    rim.push(px, py);
    fingers.push([px, py, qx, qy]);
    if (k % 4 === 0) spokes.push([x, y, px, py]);
  }
  hdrPoly(c, rim, ip(0xD8C27A, 0.35), ip(0x6E5B22, 1, 0.05));
  hdrStroke(c, spokes, ip(0x6E5B22, 1, 0.025));
  hdrStroke(c, fingers, ip(0xB59A45, 1, 0.03));
  iCircle(c, x, y, 0.07, ip(IC.dark), null);
}
// A trailed finger-wheel rake in a V: from the pin (Vehicle hitch) a tongue to the point of the V at the back,
// two arms out to the tool's edges at the front, each with its finger wheels (1.4 m, at most 10) overlapping along it, set
// in towards the middle, where the swath is laid; the carrier wheels at the arms' front ends, the V's middle on
// the working line.
function wheelRakePictureSk(canvas, tl, veh) {
  const p = renderPose();
  if (!p || !implToolPose()) return null;
  iRakeHitch(canvas, p, veh);
  if (!implTrailFrame(canvas, tl)) return null;
  const W = Math.max(3, tl.totalWidth || 7), cx = implSectionsMiddle(tl), yc = tl.trailingHitchLength + tl.trailingToolToPivotLength;
  const gap = 0.55, h = (W / 2 - gap) / Math.tan(38 * Math.PI / 180), yf = yc - h / 2, yb = yc + h / 2;
  const m = Math.max(2, Math.min(10, Math.round((W / 2 - gap) / 0.6)));               // wheels a side
  const armX = (t) => W / 2 + 0.35 - (W / 2 - gap) * t;                                          // the arm, outside the wheels
  for (const s of [-1, 1]) iTyre(canvas, cx + s * armX(0), yf - 0.6, 0.22, 0.55, IC.rim);          // the carrier wheels
  iBar(canvas, 0, 0, cx, yb + 0.4, IC.red, 0.16);                                                  // the tongue
  iBar(canvas, cx - armX(0.3), yf + h * 0.3, cx + armX(0.3), yf + h * 0.3, IC.red, 0.08);         // the width bar
  for (const s of [-1, 1]) {
    iBar(canvas, cx + s * armX(0), yf - 0.35, cx + s * armX(1), yb - 0.05, IC.red, 0.13);
    iBar(canvas, cx + s * armX(1), yb - 0.05, cx, yb + 0.4, IC.red, 0.13);
    for (let k = 0; k < m; k++) {
      const t = (k + 0.5) / m, x = cx + s * (W / 2 - (W / 2 - gap) * t), y = yf + h * t;
      const a = 22 * Math.PI / 180;                                                                  // set in towards the middle
      hdrStroke(canvas, [[x, y, x + s * 0.35, y - 0.12]], ip(IC.dark, 1, 0.05));                    // its spring arm to the frame
      iFingerWheel(canvas, x, y, -s * Math.sin(a), Math.cos(a), 0.72, 0.3);
    }
  }
  canvas.restore();
  return { half: 0.05, ahead: 0 };
}

// Seeders. A frame at a world point (e, n), y backwards along heading (radians), for what is not on the tractor or
// on the tool: an air seeder's cart.
function implFrameAt(canvas, e, n, heading) {
  canvas.save();
  canvas.concat(perspM);
  canvas.translate(e - camE, n - camN);
  canvas.rotate(-heading * 180 / Math.PI, 0, 0);
  canvas.scale(1, -1);
}
// Many rectangles [l, t, r, b] in one path: the row units' parts, a few dozen of each.
function iRects(c, rects, fill, line = iEdge()) {
  if (!rects.length) return;
  const cmds = [];
  for (const q of rects) cmds.push(CK.MOVE_VERB, q[0], q[1], CK.LINE_VERB, q[2], q[1], CK.LINE_VERB, q[2], q[3], CK.LINE_VERB, q[0], q[3], CK.CLOSE_VERB);
  const path = CK.Path.MakeFromCmds(cmds);
  if (!path) return;
  c.drawPath(path, fill);
  if (line) c.drawPath(path, line);
  path.delete();
}
// The tractor's hitch on the ground (Vehicle hitch behind the rear axle) at the pose drawn.
function implVehicleHitch(p, veh) {
  const h = Math.abs(veh.hitchLength);
  return { e: p.e - Math.sin(p.heading) * h, n: p.n - Math.cos(p.heading) * h };
}
// An air seeder's tine bar from its pin (y = 0), the openers' middle on the working line yc: an A-frame to the
// front beam, the castor wheels ahead of it, four ranks of tines a row apart (25 cm) staggered across the tool's
// width, a packer wheel behind each tine, the transport wheels at Trailing hitch held within the ranks and the
// distribution towers with the hose from the cart. Returns how far back its rear hitch is.
function iAirDrill(c, tl, W, cx, yc) {
  const n = Math.max(8, Math.min(96, Math.round(W / 0.25))), s = W / n, rank = 0.65, yF = yc - 1.6;
  const ry = (r) => yc + (r - 1.5) * rank;
  for (const sx of [-1, 1]) iBar(c, 0, 0, cx + sx * 1.1, yF, IC.red, 0.14);                       // the A-frame
  iBar(c, 0, 0, cx, yF, IC.red, 0.16);
  const casters = Math.max(2, Math.round(W / 4) + 1);
  for (let k = 0; k < casters; k++) {
    const x = cx - W / 2 + 0.4 + (W - 0.8) * k / (casters - 1);
    iBar(c, x, yF, x, yF - 0.35, IC.redD, 0.06);
    iTyre(c, x, yF - 0.5, 0.26, 0.58, IC.rim);                                                       // the castor wheels
  }
  const fore = [cx - W / 2, cx + W / 2, cx - 1.1, cx + 1.1];
  if (W > 8) fore.push(cx - W / 4, cx + W / 4);
  for (const x of fore) iBar(c, x, yF, x, ry(3) - 0.3, IC.red, 0.12);                                // the frame along
  iBar(c, cx - W / 2, yF, cx + W / 2, yF, IC.red, 0.16);                                             // the front beam
  for (let r = 0; r < 4; r++) iBar(c, cx - W / 2, ry(r) - 0.3, cx + W / 2, ry(r) - 0.3, IC.red, 0.1); // a tube each rank
  const tines = [], tips = [], packers = [], arms = [];
  for (let k = 0; k < n; k++) {
    const x = cx - W / 2 + (k + 0.5) * s, y = ry(k % 4);
    tines.push([x, y - 0.3, x, y - 0.02]);
    tips.push([x, y - 0.04, x, y + 0.04]);
    arms.push([x, y - 0.25, x + 0.07, y + 0.3]);
    packers.push([x + 0.07, y + 0.18, x + 0.07, y + 0.56]);
  }
  hdrStroke(c, arms, ip(IC.dark, 1, 0.035));
  hdrStroke(c, tines, ip(0x2A2C2F, 1, 0.05));
  hdrStroke(c, tips, ip(IC.disc, 1, 0.06));
  hdrStroke(c, packers, ip(IC.tyre, 1, 0.13));
  const wy = Math.max(ry(0), Math.min(ry(3), tl.trailingHitchLength));
  for (const sx of [-1, 1]) iTyre(c, cx + sx * 0.75, wy, 0.4, 1.0, IC.rim);                         // the transport wheels
  const towers = W > 8 ? [cx - W / 4, cx, cx + W / 4] : [cx - W / 6, cx + W / 6];
  for (const x of towers) hdrStroke(c, [[0, 0.3, 0, yF - 0.2, x, yc - 0.2]], ip(0x2E3033, 1, 0.08)); // the hoses
  for (const x of towers) { iCircle(c, x, yc - 0.2, 0.2, ip(0x5A5E63)); iCircle(c, x, yc - 0.2, 0.09, ip(IC.dark), null); }
  return ry(3) + 0.75;
}
// An air seeder's cart from its tongue's tip (y0, at the hitch it is pulled by) to its axles (y1, behind): the
// tongue, two axles of big tyres, the tank in two or three compartments with their lids, the fan and the meter.
function iAirCart(c, y0, y1) {
  const t0 = Math.min(y1 - 1.5, Math.max(y0 + 1.5, y1 - 4.6)), t1 = y1 + 0.9;
  for (const sx of [-1, 1]) iBar(c, 0, y0, sx * 0.8, t0 + 0.1, 0x2E3033, 0.12);                    // the tongue
  iBar(c, 0, y0, 0, t0 + 0.1, 0x2E3033, 0.14);
  for (const sx of [-1, 1]) for (const dy of [-0.65, 0.65]) iTyre(c, sx * 1.27, y1 + dy, 0.55, 1.2, IC.rim);
  iRRect(c, -1.1, t0 + 0.15, 1.1, t1 - 0.1, 0.08, ip(0x3A3D41));                                    // the chassis
  const m = t1 - t0 > 3.6 ? 3 : 2, len = (t1 - t0) / m;
  for (let k = 0; k < m; k++) {
    const a = t0 + k * len;
    iRRect(c, -1.2, a + 0.04, 1.2, a + len - 0.04, 0.35, ip(0xDCDFE2), ip(0x7E8489, 1, 0.03));      // a compartment
    iCircle(c, 0, a + len / 2, 0.28, ip(0x2E3033));                                                  // its lid
  }
  iRect(c, 1.22, t0 + 0.1, 1.42, t0 + 1.3, ip(0x8C9196), ip(0x2E3033, 1, 0.02));                     // the steps
  iCircle(c, -0.85, t0 - 0.1, 0.3, ip(0x5A5E63)); iCircle(c, -0.85, t0 - 0.1, 0.14, ip(IC.dark), null); // the fan
  iRect(c, -0.9, t1 - 0.15, 0.9, t1 + 0.12, ip(0x2E3033));                                           // the meter
}
// An air seeder with its cart between the tractor and the tine bar (a TBT tool): the cart from the tractor's hitch
// to the tank's axle (Tank hitch), the bar turning about that axle (Trailing hitch to its wheels), the openers on
// the working line. The bar is drawn first: its pin is under the cart.
function airSeederCartBetweenPictureSk(canvas, tl, veh) {
  const p = renderPose(), t = implToolPose();
  if (!p || !t) return null;
  const back = tl.trailingHitchLength + tl.trailingToolToPivotLength;
  const pe = t.e + Math.sin(t.heading) * back, pn = t.n + Math.cos(t.heading) * back;          // the tank's axle
  const h = implVehicleHitch(p, veh), L = Math.hypot(h.e - pe, h.n - pn);
  const W = Math.max(4, tl.totalWidth || 12), cx = implSectionsMiddle(tl);
  if (!implTrailFrame(canvas, tl)) return null;
  iAirDrill(canvas, tl, W, cx, back);
  canvas.restore();
  implFrameAt(canvas, pe, pn, L > 0.5 ? Math.atan2(h.e - pe, h.n - pn) : t.heading);
  iAirCart(canvas, -Math.max(L, 2), 0);
  canvas.restore();
  return { half: 0.05, ahead: 0 };
}
// The cart pulled behind an air seeder's bar, its axles 4.6 m behind the bar's rear hitch, following it as a
// trailer does (the page's own: the host knows the bar only). Put straight behind the bar again when it was not
// drawn in the last half second (another picture, the page hidden) or the bar jumped.
const implCart = { e: 0, n: 0, t: -1e9 };
function airSeederCartBehindPictureSk(canvas, tl, veh) {
  const p = renderPose(), t = implToolPose();
  if (!p || !t) return null;
  const W = Math.max(4, tl.totalWidth || 12), cx = implSectionsMiddle(tl);
  const back = tl.trailingHitchLength + tl.trailingToolToPivotLength;
  if (!implTrailFrame(canvas, tl)) return null;
  const rear = iAirDrill(canvas, tl, W, cx, back);
  iBar(canvas, 0, back + 1.2, 0, rear, IC.red, 0.12);                                               // the rear hitch
  canvas.restore();
  const Lc = 4.6, behind = rear - back;                                                              // the rear hitch, behind the working point
  const re = t.e - Math.sin(t.heading) * behind, rn = t.n - Math.cos(t.heading) * behind;
  let dx = implCart.e - re, dy = implCart.n - rn, d = Math.hypot(dx, dy);
  const now = performance.now();
  if (now - implCart.t > 500 || Math.abs(d - Lc) > 3) { dx = -Math.sin(t.heading); dy = -Math.cos(t.heading); d = 1; }
  implCart.e = re + dx / d * Lc; implCart.n = rn + dy / d * Lc; implCart.t = now;
  implFrameAt(canvas, re, rn, Math.atan2(re - implCart.e, rn - implCart.n));
  iAirCart(canvas, 0, Lc);
  canvas.restore();
  return { half: 0.05, ahead: 0 };
}
// Where a planter's row units are across (m, + right): one on each section when the sections are a row each
// (30 cm to 1 m), else a row every `pitch` across the tool's width.
function iPlanterRows(tl, W, cx, pitch) {
  const s = scene && scene.toolSections;
  if (s && s.length >= 2 && s.length <= 64 && s.every(q => q.right - q.left >= 0.3 && q.right - q.left <= 1.0))
    return s.map(q => (q.left + q.right) / 2);
  const n = Math.max(1, Math.min(48, Math.round(W / pitch)));
  return Array.from({ length: n }, (_, k) => cx - W / 2 + (k + 0.5) * W / n);
}
// A planter's row units on a toolbar at ytb, each seed tube (between its gauge wheels) on the working line yc: the
// parallel arms, the meter with its small hopper, the gauge wheels and the opener discs between them, the closing
// wheels in a V behind.
function iRowUnits(c, xs, ytb, yc, rgb) {
  const arms = [], meters = [], lids = [], gauges = [], discs = [], closers = [];
  const a1 = Math.max(ytb + 0.1, yc - 0.6);
  for (const x of xs) {
    arms.push([x - 0.07, ytb, x - 0.07, a1], [x + 0.07, ytb, x + 0.07, a1]);
    meters.push([x - 0.14, a1 - 0.05, x + 0.14, a1 + 0.33]);
    lids.push([x - 0.1, a1, x + 0.1, a1 + 0.16]);
    gauges.push([x - 0.16, yc - 0.24, x - 0.16, yc + 0.24], [x + 0.16, yc - 0.24, x + 0.16, yc + 0.24]);
    discs.push([x - 0.03, yc - 0.28, x, yc + 0.2], [x + 0.03, yc - 0.28, x, yc + 0.2]);
    closers.push([x - 0.13, yc + 0.42, x - 0.05, yc + 0.72], [x + 0.13, yc + 0.42, x + 0.05, yc + 0.72]);
  }
  hdrStroke(c, arms, ip(IC.dark, 1, 0.05));
  hdrStroke(c, discs, ip(IC.disc, 1, 0.03));
  hdrStroke(c, gauges, ip(IC.tyre, 1, 0.09));
  hdrStroke(c, closers, ip(IC.tyre, 1, 0.07));
  iRects(c, meters, ip(rgb));
  iRects(c, lids, ip(0x2A2C2F), null);
}
// A precision planter on the rear 3-point hitch, its hopper on the tractor's front linkage (Vehicle front hitch):
// the hopper with its fan, the hose along the tractor's right to the distributor on the toolbar, the headstock,
// the toolbar and a row unit on each section (or every 75 cm), the seed on the working line.
function planterFrontHopperPictureSk(canvas, tl, veh) {
  const p = renderPose();
  if (!p) return null;
  const L = Math.abs(veh.threePointHitchLength), W = Math.max(1.5, tl.totalWidth || 4.5), cx = implSectionsMiddle(tl);
  const yc = L + (tl.workingPointFromHitch || 0), ytb = Math.max(L + 0.25, yc - 0.9);
  const F = Math.max(Math.abs(veh.frontHitchLength || 0), (veh.wheelbase || 2.5) + 0.9), side = (veh.trackWidth || 1.8) / 2 + 0.4;
  implTractorFrame(canvas, p);
  hdrStroke(canvas, [[0.7, -F - 0.2, side, -F + 0.5, side, ytb - 0.5, cx + 0.3, ytb]], ip(0x2E3033, 1, 0.1)); // the hose
  iLowerLinks(canvas, L);
  iHeadstock(canvas, L, ytb - L, IC.green, IC.greenD);
  iRect(canvas, cx - W / 2, ytb - 0.08, cx + W / 2, ytb + 0.08, ip(IC.green), ip(IC.greenD, 1, 0.025)); // the toolbar
  iRowUnits(canvas, iPlanterRows(tl, W, cx, 0.75), ytb, yc, IC.green);
  iCircle(canvas, cx + 0.3, ytb - 0.02, 0.16, ip(0x5A5E63));                                         // the distributor
  iBar(canvas, -0.45, -F, 0.45, -F, IC.dark, 0.08);                                                  // the front linkage
  iRRect(canvas, -0.95, -F - 1.3, 0.95, -F - 0.08, 0.12, ip(0xDCDFE2), ip(0x7E8489, 1, 0.03));       // the hopper
  iRect(canvas, -0.95, -F - 0.3, 0.95, -F - 0.08, ip(IC.green), ip(IC.greenD, 1, 0.02));
  iRRect(canvas, -0.6, -F - 1.15, 0.6, -F - 0.5, 0.1, ip(0x2E3033));                                 // its lid
  iCircle(canvas, 0.62, -F - 0.25, 0.2, ip(0x5A5E63)); iCircle(canvas, 0.62, -F - 0.25, 0.09, ip(IC.dark), null); // the fan
  canvas.restore();
  return { half: 0.05, ahead: 0 };
}
// A trailed precision planter (a wide one): a tongue from the drawbar pin (Vehicle hitch) to the centre frame, draft
// bars from the tongue to the wings, two seed tanks on the centre frame, the toolbar with a row unit on each section
// (or every 75 cm), the transport wheels at Trailing hitch held by the toolbar and a gauge wheel at each wing's end.
function planterTrailedPictureSk(canvas, tl, veh) {
  if (!implTrailFrame(canvas, tl)) return null;
  const W = Math.max(3, tl.totalWidth || 9), cx = implSectionsMiddle(tl);
  const yc = tl.trailingHitchLength + tl.trailingToolToPivotLength, ytb = yc - 0.9;
  const f = Math.max(0.6, ytb - 2.4);
  for (const sx of [-1, 1]) iBar(canvas, 0, 0, cx + sx * 0.9, f, IC.green, 0.14);                   // the tongue
  iBar(canvas, 0, 0, cx, ytb, IC.green, 0.18);
  if (W > 5) for (const sx of [-1, 1]) iBar(canvas, cx, Math.max(0.3, f * 0.45), cx + sx * (W / 2 - 0.3), ytb, IC.green, 0.1); // the draft bars
  const wy = Math.max(ytb - 1.8, Math.min(ytb - 0.3, tl.trailingHitchLength));
  for (const sx of [-1, 1]) iTyre(canvas, cx + sx * 1.45, wy, 0.4, 0.95, IC.rim);                   // the transport wheels
  for (const sx of [-1, 1]) iBar(canvas, cx + sx * 1.45, wy, cx + sx * 1.45, ytb, IC.green, 0.1);
  for (const sx of [-1, 1]) { const x = cx + sx * (W / 2 + 0.15); iBar(canvas, x, ytb, x, ytb - 0.3, IC.greenD, 0.06); iTyre(canvas, x, ytb - 0.45, 0.2, 0.45, IC.rim); }
  iRect(canvas, cx - W / 2, ytb - 0.09, cx + W / 2, ytb + 0.09, ip(IC.green), ip(IC.greenD, 1, 0.025)); // the toolbar
  iRowUnits(canvas, iPlanterRows(tl, W, cx, 0.75), ytb, yc, IC.green);
  for (const sx of [-1, 1]) {
    const a = cx + sx * 0.05, b = cx + sx * 1.2;
    iRRect(canvas, Math.min(a, b), f, Math.max(a, b), ytb - 0.12, 0.3, ip(0xDCDFE2), ip(0x7E8489, 1, 0.03)); // the tanks
    iCircle(canvas, cx + sx * 0.62, (f + ytb) / 2, 0.24, ip(0x2E3033));
  }
  canvas.restore();
  return { half: 0.05, ahead: 0 };
}

// The implement pictures, one row each (ToolPicture in ToolConfig.cs): the tool type it is made for (0 front,
// 1 rear, 2 TBT, 3 trailing), the vehicle types it goes on (0 tractor, 1 harvester, 2 articulated), how it is
// drawn, and its name and note in Tool > Type > Picture on the map, whose list shows the pictures of the tool
// and vehicle type chosen (photo: drawn from photos, it keeps its colours). A combine carries only its header; an
// articulated tractor pulls big implements: the trailed disc harrow, the ploughs, the four-rotor rake, the air seeders
// and the trailed planter only so far. A new picture: a ToolPicture value, its icons or draw function (returning the
// section strip) and a row here.
const IMPLEMENT_PICTURES = [
  { id: 1, type: 3, vehicles: [0], draw: seedDrillPictureSk, photo: true,
    name: () => tr('Seed drill, semi-mounted (3 m, 19 rows)'),
    hint: () => tr('The headstock on the lower links, the drill turning about the drawbar pin (Vehicle hitch), drawn to scale.') },
  { id: 2, type: 0, vehicles: [0], draw: frontSprayerPictureSk, photo: true,
    name: () => tr('Front sprayer boom (8 m) + rear tank (600 L)'),
    hint: () => tr('The boom at the working point, its tank on the rear lower links (Vehicle rear 3-point hitch), drawn to scale.') },
  { id: 3, type: 0, vehicles: [1], draw: grainHeaderPictureSk,
    name: () => tr('Grain header (reel)'),
    hint: () => tr('The cutter bar at the working point, as wide as the tool.') },
  { id: 4, type: 0, vehicles: [1], draw: maizeHeaderPictureSk,
    name: () => tr('Maize header (a row unit every 75 cm)'),
    hint: () => tr('The stalk rolls at the working point, as wide as the tool.') },
  { id: 5, type: 3, vehicles: [0], draw: manureSpreaderPictureSk,
    name: () => tr('Manure spreader, one axle'),
    hint: () => tr('On the drawbar pin (Vehicle hitch), its axle Trailing hitch behind it, the beaters on the working line; spreads as wide as the tool.') },
  { id: 6, type: 3, vehicles: [0], draw: mowerConditionerPictureSk,
    name: () => tr('Mower conditioner, centre pivot'),
    hint: () => tr('The swivel on the lower links (Vehicle hitch), the mower swung to the side still standing, the other side every pass. The painted strip stays where the tool settings put it.') },
  { id: 7, type: 1, vehicles: [0], draw: quickDiscHarrowPictureSk,
    name: () => tr('Quick disc harrow with roller'),
    hint: () => tr('On the rear 3-point hitch: two rows of discs at the working point, spring tines and a packer roller, as wide as the tool.') },
  { id: 8, type: 3, vehicles: [0], draw: rollerPictureSk,
    name: () => tr('Roller, semi-mounted'),
    hint: () => tr('Its pivot on a bar on the lower links (Vehicle hitch), a row of spring tines and the roller on the working line, as wide as the tool.') },
  { id: 9, type: 1, vehicles: [0, 2], draw: reversiblePloughPictureSk,
    name: () => tr('Reversible plough, mounted (2 to 7 bodies)'),
    hint: () => tr('On the rear 3-point hitch, a body every 40 cm or so of the tool width. It turns over as each U-turn starts: the bodies throw the soil towards the land ploughed before.') },
  { id: 10, type: 3, vehicles: [0, 2], draw: discHarrowTrailedPictureSk,
    name: () => tr('Disc harrow, trailed'),
    hint: () => tr('On the drawbar pin (Vehicle hitch): four gangs of discs in an X, its wheels between them (Trailing hitch), as wide as the tool.') },
  { id: 11, type: 1, vehicles: [0], draw: rearSideMowerPictureSk,
    name: () => tr('Disc mower, rear side-mounted'),
    hint: () => tr('On the rear 3-point hitch, the cutter bar where the tool offset puts it: set the offset to the side it mows.') },
  { id: 12, type: 1, vehicles: [0], draw: discHarrowMountedPictureSk,
    name: () => tr('Disc harrow, 3-point'),
    hint: () => tr('On the rear 3-point hitch: four gangs of discs in an X about the working point, as wide as the tool.') },
  { id: 13, type: 1, vehicles: [0], draw: fertiliserSpreaderPictureSk,
    name: () => tr('Fertiliser spreader, two discs'),
    hint: () => tr('On the rear 3-point hitch: the hopper and its two discs, the spread drawn as wide as the tool.') },
  { id: 14, type: 3, vehicles: [0, 2], draw: semiMountedPloughPictureSk,
    name: () => tr('Reversible plough, semi-mounted (5 to 12 bodies)'),
    hint: () => tr('Its swivel behind the lower links (Vehicle hitch), a body every 40 cm or so of the tool width, the bodies\' middle on the working line (Trailing hitch plus Pivot distance). It turns over as each U-turn starts.') },
  { id: 15, type: 1, vehicles: [0, 2], draw: discPloughPictureSk,
    name: () => tr('Reversible disc plough (2 to 6 discs)'),
    hint: () => tr('On the rear 3-point hitch, a disc every 30 cm or so of the tool width. It swings across as each U-turn starts: the discs throw the soil towards the land ploughed before.') },
  { id: 16, type: 1, vehicles: [0], draw: rakeOneRotorPictureSk,
    name: () => tr('Rotary rake, one rotor'),
    hint: () => tr('On the rear 3-point hitch: the rotor at the working point, as wide as the tool, the swath laid on its left behind the curtain.') },
  { id: 17, type: 3, vehicles: [0], draw: rakeTwinCentrePictureSk,
    name: () => tr('Rotary rake, two rotors, centre delivery'),
    hint: () => tr('Its pin behind the lower links (Vehicle hitch): two rotors side by side on the working line, the swath between them, the chassis wheels Trailing hitch behind the pin.') },
  { id: 18, type: 3, vehicles: [0], draw: rakeTwinSidePictureSk,
    name: () => tr('Rotary rake, two rotors, side delivery'),
    hint: () => tr('Its pin behind the lower links (Vehicle hitch): two rotors one behind the other, stepping out to the right, the swath laid on the right of the rear one; their middle on the working line.') },
  { id: 19, type: 3, vehicles: [0, 2], draw: rakeFourPictureSk,
    name: () => tr('Rotary rake, four rotors'),
    hint: () => tr('Its pin behind the lower links (Vehicle hitch): two rotors in front out to the edges, two behind closer in, the swath in the middle; the chassis wheels Trailing hitch behind the pin.') },
  { id: 20, type: 3, vehicles: [0], draw: wheelRakePictureSk,
    name: () => tr('Finger-wheel rake (V)'),
    hint: () => tr('On the drawbar pin (Vehicle hitch): finger wheels in a V open at the front, as wide as the tool, the swath at its point; the V\'s middle on the working line.') },
  { id: 21, type: 2, vehicles: [0, 2], draw: airSeederCartBetweenPictureSk,
    name: () => tr('Air seeder, cart between'),
    hint: () => tr('The cart from the tractor\'s hitch to its axle (Tank hitch), the tine bar turning about the axle: a tine every 25 cm with its packer, the openers on the working line.') },
  { id: 22, type: 3, vehicles: [0, 2], draw: airSeederCartBehindPictureSk,
    name: () => tr('Air seeder, cart behind'),
    hint: () => tr('The tine bar on the drawbar pin (Vehicle hitch), its transport wheels at Trailing hitch, the openers on the working line; the cart follows behind the bar.') },
  { id: 23, type: 1, vehicles: [0], draw: planterFrontHopperPictureSk,
    name: () => tr('Precision planter + front hopper'),
    hint: () => tr('The toolbar on the lower links, a row unit on each section (or every 75 cm), the seed on the working line; the hopper on the front linkage (Vehicle front hitch).') },
  { id: 24, type: 3, vehicles: [0, 2], draw: planterTrailedPictureSk,
    name: () => tr('Precision planter, trailed'),
    hint: () => tr('On the drawbar pin (Vehicle hitch): the tanks on the centre frame, a row unit on each section (or every 75 cm), the seed on the working line.') },
];
