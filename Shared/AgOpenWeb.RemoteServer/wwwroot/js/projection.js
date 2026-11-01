// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// The screen matrix and screen <-> world projection.

// ---- render ----
// ONE projection: the CanvasKit M44 perspective matrix, always — top-down is just
// pitch 0 on the same matrix (distance is chosen so pitch 0 matches the ortho scale
// exactly, see buildScreenMatrix). There is no separate 2D renderer; perspM is non-null
// for the entire Skia render path (it's only null before CanvasKit finishes loading, and
// nothing is drawn then). The render helpers (w2s, drawGridSk, drawImagerySk,
// drawCoverageSk, …) assume perspM and no longer branch on it — that old per-pitch
// branch dropped top-down into an axis-aligned raster path that couldn't rotate, so
// imagery/coverage skewed away under HeadingUp/Map as heading changed.
function active3D() { return !!CK; }
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
  // Camera-relative: the camera translation is deliberately NOT baked into the matrix.
  // Baking absolute -camE,-camN into the f32 M44 forces it to subtract two field-scale
  // numbers each frame → the result steps in f32 ULPs as the camera moves → the whole
  // world jitters a sub-mm fraction ("floating-origin" jitter, visible on high-frequency
  // imagery). Instead w2s / pw / the raster sites subtract camE,camN in f64 (JS) and feed
  // only small relative coords here. Mirrors native (SkiaMapControl: double relX =
  // _vehicleX - _cameraX before its f32 SKMatrix44).
  const view = CK.M44.multiply(
    CK.M44.translated([0, 0, -distance]),
    CK.M44.rotated([1, 0, 0], -pitch),
    CK.M44.rotated([0, 0, 1], -mapRotation)); // map rotation (HeadingUp etc.)
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
function updatePerspective() {
  perspM = active3D() ? buildScreenMatrix() : null;
  // Cache the inverse once per frame (Phase MT s2w foundation). perspM is row-major
  // (CanvasKit M44); the inverse is the screen-px→world-ray map used by s2w.
  perspMInv = perspM ? invertM44(perspM) : null;
}
// Near-plane clip a ground segment for the perspective path. perspM's w (bottom row,
// z=0) is positive in front of the camera; CanvasKit's drawLine doesn't clip the
// behind part (it perspective-divides by a negative w → a mirrored ghost line), so
// we clip in world space first. Returns [e1,n1,e2,n2] fully in front, or null.
function clipNear(e1, n1, e2, n2) {
  const EPS = 1.0; // matches the projection near plane
  const w1 = pw(e1, n1);
  const w2 = pw(e2, n2);
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
  const rp = renderPose(); // interpolated pose (position + heading both smooth now)
  let target = mapRotation; // Free (mode 2): hold rotation
  if (rp) {
    if (cameraMode === 0) { camE = rp.e; camN = rp.n; target = 0; }                // NorthUp
    else if (cameraMode === 1) { camE = rp.e; camN = rp.n; target = -rp.heading; } // HeadingUp
    else if (cameraMode === 3) { target = 0; applyAutoPan(rp); }                   // Map
  } else if (cameraMode !== 2) target = 0;
  // Ease toward the target along the shortest angular path — turns the 10 Hz heading
  // steps (HeadingUp) into continuous rotation. Adaptive alpha: heavy damping for small
  // errors (line-holding dither) → no map wobble; responsive for large errors → crisp turns.
  let d = target - mapRotation;
  d -= 2 * Math.PI * Math.round(d / (2 * Math.PI));
  // QUADRATIC ramp: stays near MIN for tiny errors (line-holding dither ≈0.005 rad → the
  // map barely follows it) but rises sharply toward MAX as the error approaches a real turn.
  // A linear ramp lifted alpha too much at the dither scale, leaving residual wobble.
  const rotFrac = Math.min(1, Math.abs(d) / ROT_SMOOTH_FULL);
  const rotAlpha = ROT_SMOOTH_MIN + (ROT_SMOOTH_MAX - ROT_SMOOTH_MIN) * rotFrac * rotFrac;
  mapRotation += d * rotAlpha;
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
// World (e,n) → CSS px through the one perspective matrix (top-down is pitch 0). Only
// ever called from the Skia render path, where perspM is guaranteed set (CanvasKit up).
function w2s(e, n) {
  return applyM(perspM, e - camE, n - camN);
}
// Homogeneous w of a WORLD point through the camera-relative perspM (w < 1 ⇒ behind the
// near plane; used for cull/clip). The camE,camN subtraction happens here in f64 so the
// f32 matrix only ever sees small relative coords (see buildScreenMatrix's note).
function pw(e, n) {
  return perspM[12] * (e - camE) + perspM[13] * (n - camN) + perspM[15];
}
// ---- screen→world unprojection (Phase MT foundation) ----
// w2s only ever does world→screen; map-tap features need the inverse: turn a CSS-px tap
// into a field coordinate (E,N) on the ground plane z=0. perspM bakes the viewport in, so
// a tap (px,py) is already in perspM's output space — no NDC pre-step. We unproject the
// tap at two clip depths (the near/far representatives where the homogeneous w=1), giving
// two world points on the viewing ray, then intersect that ray with z=0.
//
// Full 4×4 row-major Gauss-Jordan inverse (perspM is row-major: a[r][c]=m[r*4+c]).
// Returns null if singular. Robust to any pitch — no special-casing top-down.
function invertM44(m) {
  const a = [];
  for (let r = 0; r < 4; r++) {
    a.push([m[r*4], m[r*4+1], m[r*4+2], m[r*4+3],
            r===0?1:0, r===1?1:0, r===2?1:0, r===3?1:0]);
  }
  for (let col = 0; col < 4; col++) {
    let piv = col, best = Math.abs(a[col][col]);
    for (let r = col + 1; r < 4; r++) {
      const v = Math.abs(a[r][col]); if (v > best) { best = v; piv = r; }
    }
    if (best < 1e-12) return null;
    if (piv !== col) { const t = a[piv]; a[piv] = a[col]; a[col] = t; }
    const pv = a[col][col];
    for (let j = 0; j < 8; j++) a[col][j] /= pv;
    for (let r = 0; r < 4; r++) {
      if (r === col) continue;
      const f = a[r][col];
      if (f !== 0) for (let j = 0; j < 8; j++) a[r][j] -= f * a[col][j];
    }
  }
  const out = new Array(16);
  for (let r = 0; r < 4; r++) for (let c = 0; c < 4; c++) out[r*4+c] = a[r][c+4];
  return out;
}
// Row-major 4×4 · column vector (x,y,z,w) → [4].
function m44vec(M, x, y, z, w) {
  return [
    M[0]*x + M[1]*y + M[2]*z + M[3]*w,
    M[4]*x + M[5]*y + M[6]*z + M[7]*w,
    M[8]*x + M[9]*y + M[10]*z + M[11]*w,
    M[12]*x + M[13]*y + M[14]*z + M[15]*w,
  ];
}
// Screen (CSS px) → world (E,N) on the ground plane z=0. Returns {e,n} or null (no matrix
// yet, or the viewing ray is parallel to the ground — e.g. a tap on/above the horizon under
// extreme tilt). Reuse everywhere a tap must become a field coordinate.
function s2w(px, py) {
  if (!perspMInv) return null;
  const A = m44vec(perspMInv, px, py, 0, 1); // near-plane representative
  const B = m44vec(perspMInv, px, py, 1, 1); // far-plane representative
  if (A[3] === 0 || B[3] === 0) return null;
  const ax = A[0]/A[3], ay = A[1]/A[3], az = A[2]/A[3];
  const bx = B[0]/B[3], by = B[1]/B[3], bz = B[2]/B[3];
  const dz = bz - az;
  if (Math.abs(dz) < 1e-9) return null;       // ray parallel to ground (horizon)
  const t = -az / dz;
  // perspMInv maps screen → camera-relative world; add the camera origin back for world E/N.
  return { e: ax + (bx - ax) * t + camE, n: ay + (by - ay) * t + camN };
}
// Dev round-trip check: project a grid of world points with w2s, unproject with s2w,
// report max error. Call from the console at pitch 0 AND tilted (press 3) — expect
// sub-millimetre error. Skips points behind the camera (w<1), which w2s can't show anyway.
window._s2wTest = function () {
  if (!perspM) { console.log('s2w test: no perspM (CanvasKit not ready)'); return; }
  let maxErr = 0, n = 0;
  for (let de = -200; de <= 200; de += 50) for (let dn = -200; dn <= 200; dn += 50) {
    const e = camE + de, nn = camN + dn;
    if (pw(e, nn) < 1.0) continue; // behind camera
    const s = w2s(e, nn);
    const b = s2w(s[0], s[1]);
    if (!b) continue;
    maxErr = Math.max(maxErr, Math.hypot(b.e - e, b.n - nn));
    n++;
  }
  console.log(`s2w round-trip: pitch=${(pitch*180/Math.PI).toFixed(0)}° samples=${n} maxErr=${maxErr.toExponential(3)} m`);
  return maxErr;
};
// On-screen heading at a ground point: the heading direction projected through the
// current matrix (so the vehicle marker aligns with the tilted ground, not screen
// up). Returns the world heading unchanged when top-down.
function screenHeading(e, n, h) {
  // Project a short step along the heading and read the on-screen angle. Works for
  // 2D (now rotation-aware via w2s) and 3D; returns h when north-up + top-down.
  const a = w2s(e, n), b = w2s(e + Math.sin(h) * 3, n + Math.cos(h) * 3);
  return Math.atan2(b[0] - a[0], -(b[1] - a[1])); // 0 = up, clockwise
}
