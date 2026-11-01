// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// Satellite tiles, coverage, tool footprint, light bar and steer bar.

// ---- satellite tile underlay (Phase MT — Draw boundary on map) ----
// A slippy-map of keyless Bing aerial tiles drawn world-positioned under the field, so
// the existing pan/zoom frames it and the s2w tap primitive draws the boundary on it.
// Tiles are proxied through the host (/sattile/<quadkey>) to avoid CORS taint. Flat-earth
// E/N↔lat/lon around the field origin is sub-metre accurate at field scale (the saved
// imagery uses the host's precise LocalPlane); good enough to draw against.
const MPD_LAT = 111320; // metres per degree latitude (flat-earth)
function mpdLon() { return MPD_LAT * Math.cos(originLat * Math.PI / 180); }
function enToLon(e) { return originLon + e / mpdLon(); }
function enToLat(n) { return originLat + n / MPD_LAT; }
function lonToE(lon) { return (lon - originLon) * mpdLon(); }
function latToN(lat) { return (lat - originLat) * MPD_LAT; }
function lonToTileX(lon, z) { return Math.floor((lon + 180) / 360 * (1 << z)); }
function latToTileY(lat, z) {
  const r = lat * Math.PI / 180;
  return Math.floor((1 - Math.log(Math.tan(r) + 1 / Math.cos(r)) / Math.PI) / 2 * (1 << z));
}
function tileXToLon(x, z) { return x / (1 << z) * 360 - 180; }
function tileYToLat(y, z) {
  const n = Math.PI - 2 * Math.PI * y / (1 << z);
  return 180 / Math.PI * Math.atan(0.5 * (Math.exp(n) - Math.exp(-n)));
}
function tileQuadkey(x, y, z) {
  let qk = '';
  for (let i = z; i > 0; i--) {
    let d = 0; const m = 1 << (i - 1);
    if (x & m) d += 1;
    if (y & m) d += 2;
    qk += d;
  }
  return qk;
}
// Pick the Bing zoom whose ground resolution ≈ the screen resolution (1 tile px ≈ 1 css px).
function satZoom() {
  const z = Math.log2(156543.03392 * Math.cos(originLat * Math.PI / 180) * pxPerM);
  return Math.max(2, Math.min(20, Math.round(z)));
}
// Bing has no tiles past its coverage (rural areas often stop at z19 or below); the host
// answers 404 for those and the tile is drawn from its nearest ancestor instead. A failed
// fetch (502, no network) is asked again after SAT_RETRY_MS.
const SAT_FILL_LEVELS = 6, SAT_RETRY_MS = 15000;
const satTiles = new Map(); // "z/x/y" → { img, url, skImg, none, retryAt }
function loadSatTile(t, x, y, z) {
  const retry = () => { t.retryAt = performance.now() + SAT_RETRY_MS; };
  t.retryAt = 0;
  fetch('/sattile/' + tileQuadkey(x, y, z))
    .then(r => {
      if (r.status === 404) { t.none = true; return null; }
      if (!r.ok) throw new Error('HTTP ' + r.status);
      return r.blob();
    })
    .then(b => {
      if (!b) return;
      const im = new Image();
      im.onload = () => { t.img = im; };
      im.onerror = () => { URL.revokeObjectURL(im.src); retry(); };
      im.src = t.url = URL.createObjectURL(b);
    })
    .catch(retry);
}
function getSatTile(x, y, z) {
  const key = z + '/' + x + '/' + y;
  let t = satTiles.get(key);
  if (!t) {
    t = { img: null, url: null, skImg: null, none: false, retryAt: 0 };
    satTiles.set(key, t);
    loadSatTile(t, x, y, z);
    if (satTiles.size > 500) {
      const k = satTiles.keys().next().value, ev = satTiles.get(k);
      if (ev.skImg) ev.skImg.delete();
      if (ev.url) URL.revokeObjectURL(ev.url);
      satTiles.delete(k);
    }
  } else if (t.retryAt && performance.now() >= t.retryAt) loadSatTile(t, x, y, z);
  if (t.img && !t.skImg) {
    t.skImg = CK.MakeImageFromCanvasImageSource(t.img);
    URL.revokeObjectURL(t.url); t.url = null; t.img = null;
  }
  return t;
}
function drawSatTileSk(canvas, img, tx, ty, z) {
  const eW = lonToE(tileXToLon(tx, z)), eE = lonToE(tileXToLon(tx + 1, z));
  const nN = latToN(tileYToLat(ty, z)), nS = latToN(tileYToLat(ty + 1, z));
  drawImageWorldSk(canvas, img, eW, nS, eE, nN, CK.FilterMode.Linear);
}
function drawSatelliteSk(canvas) {
  if (!satEnabled || (!originLat && !originLon)) return;
  const z = satZoom();
  let halfW = (vw / 2) / pxPerM, halfH = (vh / 2) / pxPerM;
  halfW = Math.max(halfW, 50); halfH = Math.max(halfH, 50);
  if (mapRotation !== 0) { const d = Math.hypot(halfW, halfH); halfW = d; halfH = d; }
  // Visible E/N → lat/lon corners → tile range.
  const lonW = enToLon(camE - halfW), lonE = enToLon(camE + halfW);
  const latN = enToLat(camN + halfH), latS = enToLat(camN - halfH);
  const xMin = lonToTileX(lonW, z), xMax = lonToTileX(lonE, z);
  const yMin = latToTileY(latN, z), yMax = latToTileY(latS, z); // y grows southward
  if ((xMax - xMin) > 40 || (yMax - yMin) > 40 || xMax < xMin || yMax < yMin) return; // sanity
  // A tile not drawn yet shows its nearest loaded ancestor, each drawn once and under
  // the loaded tiles. Ancestors are only fetched for a tile Bing has none of; while a
  // tile loads, whatever is already cached stands in.
  const fill = new Map(), ready = [];
  for (let tx = xMin; tx <= xMax; tx++) for (let ty = yMin; ty <= yMax; ty++) {
    const t = getSatTile(tx, ty, z);
    if (t.skImg) { ready.push([t.skImg, tx, ty]); continue; }
    for (let up = 1; up <= SAT_FILL_LEVELS && z - up >= 1; up++) {
      const px = tx >> up, py = ty >> up, pz = z - up, key = pz + '/' + px + '/' + py;
      const p = t.none ? getSatTile(px, py, pz) : satTiles.get(key);
      if (p && p.skImg) { fill.set(key, [p.skImg, px, py, pz]); break; }
      if (t.none && !p.none) break; // still loading
    }
  }
  [...fill.values()].sort((a, b) => a[3] - b[3])
    .forEach(([img, px, py, pz]) => drawSatTileSk(canvas, img, px, py, pz));
  for (const [img, tx, ty] of ready) drawSatTileSk(canvas, img, tx, ty, z);
}
// Coverage offscreen → SkImage, re-snapshotted only when the cell grid changed
// (cov.dirty). The world blit uses Linear (bilinear) sampling so the cell-grid edge
// reads smooth instead of stair-stepped; see the blit call below for the mip-free
// rationale. The snapshot lives on the cov object so a new coverage-init naturally
// starts with a fresh (null) image.
// Fallback-path throttle (2D canvas → full re-upload): cap the expensive whole-texture
// rebuild at ~4 Hz. The GPU render-target path below has no such cost.
const COV_REBUILD_MS = 250;
// Build a fresh coverage grid for a CoverageInit. Primary: a persistent GPU render target (new
// cells drawn straight onto it, snapshotted via texture copy-on-write — no whole-texture
// re-upload). Fallback: an offscreen 2D canvas + throttled re-upload if the target can't be made.
function makeCovGrid(init) {
  const w = init.width, h = init.height;
  let surface = null;
  if (CK && grCtx) { try { surface = CK.MakeRenderTarget(grCtx, w, h); } catch (e) { surface = null; } }
  let canvas = null, cctx = null, covPaint = null;
  if (surface) {
    surface.getCanvas().clear(CK.TRANSPARENT);
    covPaint = makeCovPaint();
  } else {
    canvas = document.createElement('canvas'); canvas.width = w; canvas.height = h; cctx = canvas.getContext('2d');
  }
  return {
    cellSize: init.cellSize, originE: init.originE, originN: init.originN,
    width: w, height: h, surface, covPaint, pending: [],
    canvas, cctx, dirty: true, skImg: null, lastBuild: 0,
  };
}
function makeCovPaint() {
  const p = new CK.Paint(); p.setStyle(CK.PaintStyle.Fill); p.setAntiAlias(false);
  // Src (replace), not SrcOver: cells carry a coverage-fraction alpha (edge cells < 1), and a
  // cell is re-emitted with rising alpha as it fills — replace sets the exact value each time;
  // SrcOver would blend re-draws and creep edges toward opaque.
  p.setBlendMode(CK.BlendMode.Src);
  return p;
}
// A page that loads with a job open gets its coverage before CanvasKit is ready, so the grid
// is built on the 2D fallback and would stay there. Once the GPU context exists, copy it onto
// a render target (once) and carry on with the GPU path. Both paths flip y the same way
// (row 0 = highest northing), so the copy lines up as is.
function upgradeCovToGpu() {
  if (!cov || cov.surface || !cov.canvas || !CK || !grCtx) return;
  let surface = null;
  try { surface = CK.MakeRenderTarget(grCtx, cov.width, cov.height); } catch (e) { surface = null; }
  if (!surface) return; // bigger than a texture: stays on the 2D path
  const sk = surface.getCanvas();
  sk.clear(CK.TRANSPARENT);
  const img = CK.MakeImageFromCanvasImageSource(cov.canvas);
  if (img) { sk.drawImageOptions(img, 0, 0, CK.FilterMode.Nearest, CK.MipmapMode.None, null); img.delete(); }
  surface.flush();
  if (cov.skImg) cov.skImg.delete();
  cov.surface = surface; cov.covPaint = makeCovPaint();
  cov.canvas = null; cov.cctx = null; cov.skImg = null; cov.dirty = true;
}

// Re-anchor existing coverage into a grown grid (bounds expansion, SAME cell size). The old
// painted surface is copied into the new, larger surface at the integer cell offset between the
// grids — exact, because the host shifts the grid by a whole number of cells — accounting for the
// render y-flip (surface row 0 = highest northing). Not-yet-rasterized delta batches are shifted
// into the new coords too. Nothing is re-sent by the host; the newly-added ground is empty.
function reanchorCoverage(init) {
  const cs = init.cellSize;
  const oldCov = cov;
  const offX = Math.round((oldCov.originE - init.originE) / cs);
  const offY = Math.round((oldCov.originN - init.originN) / cs);
  const nc = makeCovGrid(init);
  const destX = offX;
  const destY = (nc.height - oldCov.height) - offY; // y-flip: old image sits (maxN growth) below the top

  if (oldCov.surface && nc.surface) {
    oldCov.surface.flush();
    const img = oldCov.surface.makeImageSnapshot();
    // Nearest + integer offset = exact pixel copy (no resample).
    nc.surface.getCanvas().drawImageOptions(img, destX, destY, CK.FilterMode.Nearest, CK.MipmapMode.None, null);
    img.delete();
    nc.surface.flush();
  } else if (oldCov.canvas && nc.canvas) {
    nc.cctx.drawImage(oldCov.canvas, destX, destY);
  }
  // else: render-target mode changed across the grow (rare) — old coverage isn't carried; deltas
  // repaint going forward and the next full reload/reconnect reseeds. No crash, no misplacement.
  nc.dirty = true;

  // Carry queued (not-yet-drained) delta batches, shifted into the new grid coords.
  for (const b of oldCov.pending) {
    const c = b.cells;
    for (let i = 0; i + 2 < c.length; i += 3) { c[i] += offX; c[i + 1] += offY; }
    nc.pending.push(b);
  }

  if (oldCov.skImg) oldCov.skImg.delete();
  if (oldCov.surface) oldCov.surface.delete();
  if (oldCov.covPaint) oldCov.covPaint.delete();
  cov = nc;
}

function drawCoverageSk(canvas) {
  if (!cov) return;
  // Only rasterize batches that have aged past RENDER_DELAY, so coverage lands on the same
  // playback timeline as the render-delayed tool (see onCoverageCells). pending is FIFO in
  // arrival time, so the first not-yet-aged batch ends the drain.
  const cutoff = performance.now() - RENDER_DELAY;
  if (cov.surface) {
    // GPU render target: draw only the aged pending NEW cells onto it, then snapshot (a GPU
    // texture COW — cheap). No whole-texture upload, so no regular per-rebuild stutter.
    if (cov.pending.length && cov.pending[0].t <= cutoff) {
      const sk = cov.surface.getCanvas(), paint = cov.covPaint, H = cov.height;
      let lastRgb = -1, drained = 0, budget = COV_DRAIN_BUDGET;
      for (const batch of cov.pending) {
        if (batch.t > cutoff) break;
        const c = batch.cells;
        let i = batch.off || 0; // resume a batch we ran out of budget on last frame
        for (; i + 2 < c.length && budget > 0; i += 3) {
          const x = c[i], y = c[i + 1], v = c[i + 2] >>> 0; // (a<<24)|(r<<16)|(g<<8)|b
          if (v !== lastRgb) { paint.setColor(CK.Color((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF, ((v >>> 24) & 0xFF) / 255)); lastRgb = v; covEdgeRgb = v & 0xFFFFFF; }
          sk.drawRect(CK.XYWHRect(x, H - 1 - y, 1, 1), paint); // flip: high northing at top
          covCells++; budget--;
        }
        if (i + 2 < c.length) { batch.off = i; break; } // budget hit mid-batch; finish next frame
        drained++;
      }
      cov.pending.splice(0, drained);
      cov.surface.flush();
      cov.dirty = true;
    }
    if (cov.dirty || !cov.skImg) {
      if (cov.skImg) cov.skImg.delete();
      cov.skImg = cov.surface.makeImageSnapshot();
      cov.dirty = false;
    }
  } else {
    const nowMs = performance.now();
    // 2D fallback: drain aged batches into the offscreen canvas (same RENDER_DELAY gate).
    if (cov.pending.length && cov.pending[0].t <= cutoff) {
      const H = cov.height, cctx = cov.cctx;
      let lastRgb = -1, drained = 0, budget = COV_DRAIN_BUDGET;
      for (const batch of cov.pending) {
        if (batch.t > cutoff) break;
        const c = batch.cells;
        let i = batch.off || 0; // resume a batch we ran out of budget on last frame
        for (; i + 2 < c.length && budget > 0; i += 3) {
          const x = c[i], y = c[i + 1], v = c[i + 2] >>> 0; // (a<<24)|(r<<16)|(g<<8)|b
          if (v !== lastRgb) {
            cctx.fillStyle = 'rgba(' + ((v >> 16) & 0xFF) + ',' + ((v >> 8) & 0xFF) + ',' + (v & 0xFF) + ',' + (((v >>> 24) & 0xFF) / 255) + ')';
            lastRgb = v;
          }
          // clear+fill = replace (the 2D analogue of BlendMode.Src) so re-emitted edge
          // cells set their exact alpha instead of blending toward opaque.
          cctx.clearRect(x, H - 1 - y, 1, 1);
          cctx.fillRect(x, H - 1 - y, 1, 1); // flip: high northing at offscreen top
          covCells++; budget--;
        }
        if (i + 2 < c.length) { batch.off = i; break; } // budget hit mid-batch; finish next frame
        drained++;
      }
      cov.pending.splice(0, drained);
      cov.dirty = true;
    }
    if (!cov.skImg || (cov.dirty && nowMs - (cov.lastBuild || 0) >= COV_REBUILD_MS)) {
      if (cov.skImg) cov.skImg.delete();
      cov.skImg = CK.MakeImageFromCanvasImageSource(cov.canvas);
      cov.dirty = false;
      cov.lastBuild = nowMs;
    }
  }
  if (!cov.skImg) return;
  const cs = cov.cellSize;
  const minE = cov.originE, minN = cov.originN;
  const maxE = cov.originE + cov.width * cs, maxN = cov.originN + cov.height * cs;
  // Bilinear (Linear), mip-free: smooths the cell-grid stair-step at the worked-area edge
  // without the per-update mip-regen cost. The surface is cleared to premultiplied
  // TRANSPARENT, so the boundary fades cleanly to transparent (no dark fringe).
  drawImageWorldSk(canvas, cov.skImg, minE, minN, maxE, maxN, CK.FilterMode.Linear, CK.MipmapMode.None);
}
// Crisp worked-area edge — the server-fed vector perimeter (only segments that don't adjoin
// another pass), stroked in the coverage colour over the raster so the soft alpha-AA feather
// is replaced by a sharp boundary. Bounded by perimeter length, so cost is ~flat regardless
// of field size. Stroke width 0.6 m (covers the feather): the host sends the line 0.3 m inside
// the swept edge (EDGE_STROKE_WIDTH in CoverageMapService), so the stroke ends at the edge of the
// tool, and the caps are butt so it doesn't run past the ends of a pass either (#325). Polylines
// are broken at the near plane per vertex (see below). Reloaded fields have no perimeter
// (graceful fall back to feather).
function drawCoverageEdgeSk(canvas) {
  if (EDGE_OFF || !coverageEdges || !coverageEdges.length) return;
  const p = SKP.covEdge || (SKP.covEdge = (() => {
    const q = new CK.Paint(); q.setStyle(CK.PaintStyle.Stroke); q.setAntiAlias(true);
    q.setStrokeCap(CK.StrokeCap.Butt); q.setStrokeJoin(CK.StrokeJoin.Round); return q;
  })());
  const c = covEdgeRgb;
  p.setColor(CK.Color((c >> 16) & 0xFF, (c >> 8) & 0xFF, c & 0xFF, 1));
  p.setStrokeWidth(Math.max(1, 0.6 * pxPerM));
  // Per-vertex near-plane gate: a vertex at/behind the near plane (homogeneous w ≤ NEAR)
  // projects through w2s to a huge/flipped coord, so a polyline crossing behind the camera
  // would shoot a stroke to infinity (seen on zoom/tilt). Break the polyline at those
  // vertices and draw each in-front run separately — no clipping math, no infinity lines.
  const NEAR = 0.02;
  const flush = (cmds) => {
    if (cmds && cmds.length >= 6) { const path = CK.Path.MakeFromCmds(cmds); if (path) { canvas.drawPath(path, p); path.delete(); } }
  };
  for (const pl of coverageEdges) {
    if (pl.length < 2) continue;
    let cmds = null;
    for (let i = 0; i < pl.length; i++) {
      if (pw(pl[i].e, pl[i].n) < NEAR) { flush(cmds); cmds = null; continue; } // behind near plane → break
      const xy = w2s(pl[i].e, pl[i].n);
      if (!cmds) cmds = [CK.MOVE_VERB, xy[0], xy[1]];
      else cmds.push(CK.LINE_VERB, xy[0], xy[1]);
    }
    flush(cmds);
  }
}
// Tool/section footprint — section bars perpendicular to the (dead-reckoned) tool
// heading, coloured by ColorCode. Same geometry as the 2D toolFootprint().
// Tool footprint: each section a filled 2 m-deep rect (coloured by ColorCode) with a
// thin black outline + a 0.05 m inter-section gap, matching native DrawToolSk. The four
// world corners come from the perpendicular (left/right) and forward (depth) directions;
// all four are projected via w2s (the tool is always near the camera, so no near-plane
// clipping is needed) and filled/stroked as a quad.
function toolFootprintSk(canvas) {
  const t = renderTool();
  if (!t || !scene || !scene.toolSections || !scene.toolSections.length) return;
  if (!t.e && !t.n) return;
  const perp = t.heading + Math.PI / 2;
  const ps = Math.sin(perp), pc = Math.cos(perp);        // perpendicular (right) dir
  const hs = Math.sin(t.heading), hc = Math.cos(t.heading); // forward (depth) dir
  const depth = 1.0, halfGap = 0.025;                    // toolDepth 2 m; 0.05 m gap
  const secs = (tick && tick.sections) || [];
  const quad = (L, R) => {
    const cmds = [];
    const corners = [[L, -depth], [R, -depth], [R, depth], [L, depth]];
    for (let k = 0; k < 4; k++) {
      const off = corners[k][0], d = corners[k][1];
      const xy = w2s(t.e + ps * off + hs * d, t.n + pc * off + hc * d);
      cmds.push(k === 0 ? CK.MOVE_VERB : CK.LINE_VERB, xy[0], xy[1]);
    }
    cmds.push(CK.CLOSE_VERB);
    return CK.Path.MakeFromCmds(cmds);
  };
  for (let i = 0; i < scene.toolSections.length; i++) {
    const span = scene.toolSections[i];
    const L = span.left + halfGap, R = span.right - halfGap;
    if (R - L < 0.01) continue;
    const path = quad(L, R);
    if (!path) continue;
    canvas.drawPath(path, SKP.sectionFill[secs[i]] || SKP.sectionFill[5]);
    canvas.drawPath(path, SKP.sectionOutline);
    path.delete();
  }
}
// Lightbar — screen-space LED strip, identical logic/geometry to the 2D
// lightbar(). The arrow + cm/label text lives in the HUD div (no CanvasKit font
// bundled), so here we draw only the LEDs. Drawn in CSS px
// (under renderSkia's scale(dpr)), so it must run before canvas.restore().
function lightbarSk(canvas) {
  // The Light bar / Steer bar style picks what's drawn (AgOpenGPS isLightBarNotSteerBar);
  // On/Off hides either (isLightbarOn, #479). GuidanceBarOn has no control and stays on.
  const cfg = config && config.autosteer;
  if (!tick || !tick.guidanceActive || !cfg || !cfg.guidanceBarOn) return;
  if (!cfg.lightbarEnabled) return;
  if (cfg.steerBarEnabled) { steerBarSk(canvas, steerCy); return; } // in #lb-steer, under the readout
  // As AgOpenGPS: the cells either side of the readout, on its row (body.lightbar #lb), lit
  // outwards from it on the side to steer. No centre cell: the readout's ● is on the line.
  const SIDE = 7, W = 18, H = 16, GAP = 4;
  // AutoSteer › cm per pixel (AgOpenGPS lightbarCmPerPixel): cm of XTE per lit cell (#110).
  const cpp = Math.max(1, (cfg.cmPerPixel | 0) || 5);
  const val = tick.crossTrackError || 0, PER = cpp / 100; // + = right of line
  const lit = Math.min(Math.round(Math.abs(val) / PER), SIDE);
  const steerLeft = val > 0; // lights point the way to STEER (native convention)
  const litCol = steerLeft ? '#ff7a3d' : '#39FF6A';
  // Scaled with the HUD (hudK), as the readout beside it, but never wider than the screen allows.
  // Clear of the readout and, when shown, the tram dots beside it.
  const tram = tramLightsEl.classList.contains('show') ? LB_TRAM_W * hudK : 0;
  const inner0 = (k) => Math.max(40 * k, lbHalfW + tram + 10 * k);
  let k = hudK;
  while (k > 0.5 && inner0(k) + SIDE * (W + GAP) * k > 0.47 * vw) k -= 0.05;
  const inner = inner0(k), w = W * k, h = H * k, g = GAP * k, top = lbCy - h / 2;
  const p = SKP.lbFill;
  for (let i = 1; i <= SIDE; i++) {
    const off = inner + (i - 1) * (w + g);
    for (const left of [true, false]) {
      const on = left === steerLeft && i <= lit;
      p.setColor(ckColor(on ? litCol : '#1c2230'));
      canvas.drawRect(CK.XYWHRect(left ? vw / 2 - off - w : vw / 2 + off, top, w, h), p);
    }
  }
}
// Steer bar (AgOpenGPS DrawSteerBarText): the steer angle error (actual − commanded) as a
// yellow bar from the centre, its length the error (up to 12°), lying on the side away from
// the turn; an olive arrow at the centre and a green one at the bar's end point the way to
// turn the wheel. Nothing inside the dead zone (0.2°, 0.5° under AutoSteer); smaller and at
// half strength under AutoSteer.
function steerBarSk(canvas, cy) {
  const auto = !!(tick.op && tick.op.autoSteer);
  const err = tick.steerAngleError || 0;
  if (Math.abs(err) < (auto ? 0.5 : 0.2)) return;
  const k = hudK;                                // HUD scale × UI zoom, as the light bar
  const spacing = Math.max(28, vw / 50);
  const cx = vw / 2;
  const turn = err > 0 ? -1 : 1;                 // + error: turn left (as the light bar)
  const endX = cx - turn * (Math.min(Math.abs(err), 12) * spacing + 35 * k);
  // AgOpenGPS's shapes, sized to the light bar's strip so the readout under it stays clear.
  const half = (auto ? 7 : 10) * k;                    // arrow half-height
  const point = half + 14 * k;                       // arrow length (AgOpenGPS offset + pointy)
  const a = auto ? 0.5 : 1;
  const f = SKP.sbFill, l = SKP.sbLine;
  // The bar: black under yellow.
  l.setStrokeCap(CK.StrokeCap.Butt);
  for (const [w, col] of [[(auto ? 5 : 9) * k, `rgba(0,0,0,${a})`], [(auto ? 3 : 6) * k, `rgba(242,251,102,${a})`]]) {
    l.setStrokeWidth(w); l.setColor(ckColor(col));
    canvas.drawLine(cx, cy, endX, cy, l);
  }
  l.setStrokeWidth(2); l.setColor(ckColor(`rgba(0,0,0,${a})`));
  for (const [x, col] of [[endX, `rgba(0,252,0,${a})`], [cx, `rgba(201,201,0,${a})`]]) {
    const path = CK.Path.MakeFromCmds([
      CK.MOVE_VERB, x, cy - half,
      CK.LINE_VERB, x + turn * point, cy,
      CK.LINE_VERB, x, cy + half,
      CK.CLOSE_VERB,
    ]);
    if (!path) continue;
    f.setColor(ckColor(col));
    canvas.drawPath(path, f);
    canvas.drawPath(path, l);
    path.delete();
  }
}
