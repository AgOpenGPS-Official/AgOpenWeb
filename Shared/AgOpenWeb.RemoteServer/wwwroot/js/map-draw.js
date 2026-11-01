// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// Skia map layers: lines and tracks, recording and drawing previews, flags, grid, hitch, vehicle, ground texture, imagery.

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
function strokePtsSk(canvas, pts, close, paint) {
  if (!pts || pts.length < 2) return;
  strokePtsSk3D(canvas, pts, close, paint);
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
      for (const sub of clipScreenPolyline(run, 48)) {
        if (sub.length < 2) continue;
        const cmds = [];
        for (let i = 0; i < sub.length; i++)
          cmds.push(i === 0 ? CK.MOVE_VERB : CK.LINE_VERB, sub[i][0], sub[i][1]);
        const path = CK.Path.MakeFromCmds(cmds);
        if (path) { canvas.drawPath(path, paint); path.delete(); }
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
  for (const gl of scene.guideLines || []) {
    const pts = extendAbLine(gl);
    strokePtsSk(canvas, pts, false, SKP.extraGuideShadow);
    strokePtsSk(canvas, pts, false, SKP.extraGuide);
  }
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
// path of quads along it, skipping any behind the camera, so a tilted view draws no ghost.
function drawToolBandSk(canvas) {
  const c = scene && scene.toolPass;
  const w = toolWidthM();
  if (!c || c.length < 2 || w < 0.1 || !(tick && tick.guidanceActive)) return;
  let pts = extendAbLine(c);
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
  const cmds = [];
  for (let i = 0; i + 1 < n; i++) {
    const q = [L[i], L[i + 1], R[i + 1], R[i]];
    if (q.some(p => pw(p.e, p.n) < 1.0)) continue; // behind the camera
    q.forEach((p, k) => { const xy = w2s(p.e, p.n); cmds.push(k ? CK.LINE_VERB : CK.MOVE_VERB, xy[0], xy[1]); });
    cmds.push(CK.CLOSE_VERB);
  }
  if (cmds.length) {
    const path = CK.Path.MakeFromCmds(cmds);
    if (path) { canvas.drawPath(path, SKP.toolBand); path.delete(); }
  }
  strokePtsSk(canvas, L, false, SKP.toolBandEdge);
  strokePtsSk(canvas, R, false, SKP.toolBandEdge);
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
  for (const line of scene.tramLines) if (line.length >= 2) strokePtsSk(canvas, line, false, SKP.tram);
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
