// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// The frame: renderSkia, the frame loop and its DOM updates, start.

function renderSkia(canvas, rp) {
  canvas.clear(ckColor(mapIsDay ? MAP_CLEAR.day : MAP_CLEAR.night));
  canvas.save();
  canvas.scale(dpr, dpr); // work in CSS px so w2s + stroke widths match
  updateLineWidths(); // world-metre line weights × current zoom (matches native)
  drawGroundTextureSk(canvas); // ground backdrop (under everything)
  drawSatelliteSk(canvas); // Bing aerial underlay while drawing a boundary on map
  drawImagerySk(canvas); // imagery overlays the ground where present
  drawCoverageSk(canvas);
  drawCoverageEdgeSk(canvas); // crisp vector perimeter over the raster (server-fed)
  drawGridSk(canvas);
  if (scene) {
    for (let bi = 0; bi < scene.boundaries.length; bi++)
      strokePtsSk(canvas, scene.boundaries[bi], true,
        (scene.boundaryInner && scene.boundaryInner[bi]) ? SKP.boundaryInner : SKP.boundary);
    // Headland line shows only when the headland is ON — mirrors the native
    // SetHeadlandVisible gate (IsHeadlandOn). The bottom-nav headland button drives it.
    if (scene.headland && tick && tick.tools && tick.tools.headlandOn)
      strokePtsSk(canvas, scene.headland, true, SKP.headland);
    drawExtraGuidelinesSk(canvas); // faint adjacent passes (under the bold lines)
    for (const l of scene.recordedPaths || []) strokePtsSk(canvas, l, false, SKP.recPath);   // #110
    for (const l of scene.contourStrips || []) strokePtsSk(canvas, l, false, SKP.contourStrip);
    drawContourRefSk(canvas);
    if (scene.nextTrack) strokePtsSk(canvas, scene.nextTrack, false, SKP.next);
    if (scene.uTurnPath) strokePtsSk(canvas, scene.uTurnPath, false, SKP.uturn);
    // Purple reference (extended across the field). The reference is the edge between passes
    // (PassGeometry), so pass 0 lies half a width from it; only a track driven on the line
    // itself (boundary-follow) puts the magenta guidance on top of it, and then just the one
    // line shows. Matches the host's baseTrack = (display track is the reference) ? null : track.
    drawToolBandSk(canvas); // where the tool will land, under the lines
    const _onRef = scene.guidanceLine && scene.tracks.some(t => onPolyline(scene.guidanceLine, t.points));
    if (!_onRef) for (const tr of scene.tracks)
      strokePtsSk(canvas, extendAbLine(tr.points), false, SKP.reference);
    // Magenta current pass, extended across the field. The host's DisplayLine is only ~track
    // length (a 2-point offset AB); extendAbLine spans it full-field, so an offset pass isn't a
    // short stub. Curves are >2 points and already host-extended, so they pass through unchanged.
    if (scene.guidanceLine) {
      const gl = extendAbLine(scene.guidanceLine);
      strokePtsSk(canvas, gl, false, SKP.guidanceOutline);
      strokePtsSk(canvas, gl, false, SKP.guidance);
    }
    drawActiveAbPointsSk(canvas);
    drawFlagsSk(canvas, scene.flags);
  }
  drawRecordingMarkersSk(canvas); // live recorded-path dots (independent of the Scene)
  drawBoundaryRecordingSk(canvas); // live drive-around boundary line + dots
  drawSatBoundarySk(canvas); // live boundary-on-satellite polygon being drawn
  drawAbDrawPreviewSk(canvas); // #40 — A/B (orange/blue) dots for the AB/curve being drawn
  drawCurveMarksSk(canvas); // record-curve path dots (Set Point A → B)
  drawBoundaryCurvePreviewSk(canvas); // Bnd. Curve: the chosen way round (#450)
  drawDrawingSk(canvas); // live draw-on-map AB/curve preview (Phase MT)
  drawHeadlandDrawSk(canvas); // live headland draw preview (Field Builder stage 2)
  drawHeadlandSegEditLinesSk(canvas); // headland-editor offset lines (yellow/red) while editing
  drawTramLinesSk(canvas); // generated tram lines (orange) when tram display on / editing
  drawEditHandlesSk(canvas); // stage-4 on-map edit handles (drag points to reshape)
  drawHitchSk(canvas); // implement hitch line (under the tool footprint)
  toolFootprintSk(canvas);
  if (rp) { vehicleSk(canvas, rp); svennArrowSk(canvas, rp); }
  drawGoalSk(canvas); // #95 — Pure Pursuit target (over the vehicle)
  lightbarSk(canvas); // screen-space overlay, still inside the dpr scale
  canvas.restore();
}
// The single render loop. Window rAF (not surface.requestAnimationFrame) so it
// survives surface recreation on resize and runs even before CanvasKit finishes
// loading (DOM overlays update regardless; the GL map draws once skSurface exists).
// Frame cap (#408): the picture changes with the 10 Hz pose and its extrapolation at field
// speeds, so the map is drawn at 30 fps; drawing at the display's rate (60-120 Hz) only heats
// the tablet. While the operator handles it (a press, a drag or pinch, the wheel, a key) and
// shortly after, every frame is drawn, so panning and zooming stay smooth.
const FRAME_CAP_FPS = 30, FULL_RATE_AFTER_INPUT_MS = 300;
let _frameLast = 0, _fullRateUntil = 0;
function noteInput(e) {
  if (e.type === 'pointermove' && !e.buttons) return; // a hover is not handling the map
  _fullRateUntil = performance.now() + FULL_RATE_AFTER_INPUT_MS;
}
for (const t of ['pointerdown', 'pointermove', 'pointerup', 'wheel', 'keydown'])
  addEventListener(t, noteInput, { capture: true, passive: true });
function skFrame(ts) {
  // rAF runs at the display's rate; a frame that comes too soon is skipped whole (map and DOM
  // overlays). _frameLast advances by the interval, so the average holds on any refresh rate
  // (30 on a 144 Hz screen); the 2 ms tolerance absorbs rAF jitter.
  if (typeof ts === 'number') {
    if (ts < _fullRateUntil) _frameLast = ts;
    else {
      const interval = 1000 / FRAME_CAP_FPS, since = ts - _frameLast;
      if (since < interval - 2) { requestAnimationFrame(skFrame); return; }
      _frameLast = since > interval * 2 ? ts : _frameLast + interval;
    }
  }
  // Client render rate over a ~500 ms window (shown on the GPS-detail card).
  const _now = performance.now();
  if (_fpsT0 === 0) _fpsT0 = _now;
  else { _fpsFrames++; const dt = _now - _fpsT0; if (dt >= 500) { fps = _fpsFrames * 1000 / dt; _fpsFrames = 0; _fpsT0 = _now; } }
  const rp = updateCamera(); // follow mode + rotation + perspM, once per frame
  maybeSaveView();           // persist tilt/zoom changes (debounced) — issue #35
  sampleCurveMark();         // drop record-curve path dots as the vehicle drives
  if (skSurface) {
    try { renderSkia(skSurface.getCanvas(), rp); skSurface.flush(); }
    catch (e) { ckStatus = 'render err: ' + (e && e.message || e); }
  }
  // DOM overlays (independent of the GL surface).
  updateLightbarText();
  updateTramLights();
  updatePassHud();
  updateHeadlandHud();
  renderStatusBar();
  renderSimBar();
  renderSectionBar();
  renderBottomNav();
  renderSettings();
  renderRightNav();
  renderUTurnIndicator();
  renderAbDotLabels(); // #40 — letters beside the dropped AB/curve points
  renderRoll();
  renderCampad();
  renderCharts();
  renderRollCorr();
  renderOffsetFix();
  requestAnimationFrame(skFrame);
}
// Start the connection and CanvasKit only now, with every script loaded: their callbacks reach into all of them.
transport.start();
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
skFrame();
