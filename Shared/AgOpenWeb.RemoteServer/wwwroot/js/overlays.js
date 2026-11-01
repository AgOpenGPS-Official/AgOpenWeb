// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// Per-frame DOM overlays: tool and pose, light bar text, tram lights, pass and headland HUDs, status bar, GPS and field cards, simulator / section / bottom / right nav state, U-turn indicator.

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
  const s = sample();
  if (!s || !s.b.tool) return null;
  const pt = s.a.tool || s.b.tool, qt = s.b.tool, f = s.f;
  return {
    e: pt.e + (qt.e - pt.e) * f,
    n: pt.n + (qt.n - pt.n) * f,
    heading: lerpAngle(pt.heading, qt.heading, f),
  };
}
// Pure Pursuit goal point (#95), interpolated on the same playback timeline as the pose so
// it slides with the vehicle instead of stepping at the Tick rate. Hidden as soon as the
// newest Tick has no goal (track deselected / contour).
function renderGoal() {
  if (!tick || !tick.goal) return null;
  const s = sample();
  if (!s || !s.b.goal) return tick.goal;
  const a = s.a.goal || s.b.goal, b = s.b.goal, f = s.f;
  return { e: a.e + (b.e - a.e) * f, n: a.n + (b.n - a.n) * f };
}
// Shortest-path angular lerp (radians).
function lerpAngle(a, b, f) {
  let d = b - a; d -= 2 * Math.PI * Math.round(d / (2 * Math.PI));
  return a + d * f;
}
// Pick the two buffered poses bracketing the playback head (RENDER_DELAY in the PAST)
// and the fraction between them. The playhead runs on the HOST timeline (client clock
// − clockOffset), so its position relative to the poses' host build-times is jitter-free —
// WiFi arrival jitter only changes WHICH poses are buffered, never the interp rate (that
// rate-warp was the "wiggle"). A 2-pose buffer judders once RENDER_DELAY > the pose
// interval (playhead falls off the old end → clamps); the multi-pose buffer always has a
// bracketing pair. Returns { a, b, f } or null; holds oldest/newest outside the buffer.
function sample() {
  const m = poseBuf.length;
  if (m === 0) return null;
  if (m === 1) return { a: poseBuf[0], b: poseBuf[0], f: 1 };
  const useHost = clockOffset !== null && typeof poseBuf[m - 1].hostT === 'number';
  const key = useHost ? 'hostT' : 't';
  const playhead = useHost
    ? performance.now() - clockOffset - RENDER_DELAY
    : performance.now() - RENDER_DELAY; // legacy receipt timeline (host sent no timestamp)
  if (playhead <= poseBuf[0][key]) return { a: poseBuf[0], b: poseBuf[0], f: 0 };
  const nw = poseBuf[m - 1];
  if (playhead >= nw[key]) {
    // Buffer underrun (WiFi spike / late pose): EXTRAPOLATE the last segment's velocity
    // forward (f>1 → renderPose/renderTool extend prev→nw linearly) instead of HOLDING,
    // which would freeze a frame → stutter. Capped at EXTRAP_CAP_MS so a real dropout
    // settles to a hold rather than flying off.
    const prev = poseBuf[m - 2];
    const span = nw[key] - prev[key];
    if (span <= 0) return { a: nw, b: nw, f: 1 };
    const over = Math.min(playhead - nw[key], EXTRAP_CAP_MS);
    return { a: prev, b: nw, f: 1 + over / span };
  }
  for (let i = 0; i < m - 1; i++) {
    const a = poseBuf[i], b = poseBuf[i + 1];
    if (playhead >= a[key] && playhead < b[key]) {
      const span = b[key] - a[key];
      return { a, b, f: span > 0 ? (playhead - a[key]) / span : 1 };
    }
  }
  return { a: nw, b: nw, f: 1 };
}
function renderPose() {
  const s = sample();
  if (!s) return null;
  return {
    e: s.a.e + (s.b.e - s.a.e) * s.f,
    n: s.a.n + (s.b.n - s.a.n) * s.f,
    heading: lerpAngle(s.a.heading, s.b.heading, s.f),
    speed: s.b.speed,
  };
}
// Cross-track error as "12 cm R" / "5 in R" (R = right of line, +xte). Centimetres
// (inches in imperial) so the magnitude reads at a glance; the lightbar carries the
// live feel.
function xteText(xte) {
  if (xte == null) return '—';
  const side = xte > 0.005 ? 'R' : xte < -0.005 ? 'L' : '·';
  return `${fmtUnit(Math.abs(xte) * 100, 'cm', 0)} ${side}`;
}

// Lightbar readout text → DOM overlay (the LED strip itself is drawn by lightbarSk).
// Updated every frame from the latest tick; hidden when guidance is off.
const lbEl = document.getElementById('lb');
// Arrow + text as two spans, so the arrow can take the colour of the lit cells.
const lbCore = lbEl.appendChild(document.createElement('span'));
lbCore.className = 'lb-core';
const lbArrow = lbCore.appendChild(document.createElement('span'));
const lbText = lbCore.appendChild(document.createElement('span'));
// Light bar style: AgOpenGPS's small yellow figure, a slow average of the error that shows
// while it stays under 15 cm. Beside the distance, not under it: the pass HUD is there.
const lbAvg = lbCore.appendChild(document.createElement('span'));
lbAvg.className = 'lb-avg';
// The widest readout (three digits and the average), so the cells keep their place (#479).
const lbProbe = lbEl.appendChild(document.createElement('div'));
lbProbe.className = 'lb-probe';
const lbProbeCore = lbProbe.appendChild(document.createElement('span'));
const lbProbeAvg = lbProbe.appendChild(document.createElement('span'));
lbProbeAvg.className = 'lb-avg';
let lbProbeKey = '', lbProbeHalfW = 0;
let lbAvgTick = null, lbAvgM = 0.15;
let lbHalfW = 0; // from the centre to the readout's right end (average included), for the cells
let lbCy = 70, steerCy = 84; // screen y of the readout row's and the steer bar slot's centres
let lbSteerH = -1;
const lbSteer = document.getElementById('lb-steer');
const LB_TRAM_W = 38; // a tram dot and its gap from the readout, at HUD scale 1
const LB_LEFT = '#ff7a3d', LB_RIGHT = '#39FF6A'; // as lightbarSk: steer left / steer right, on line
function updateLightbarText() {
  const cfg = config && config.autosteer;
  // AgOpenGPS: On/Off shows or hides the bar, whatever its style; light bar or steer bar
  // is the style, and both show the distance from the line here (#479).
  const shown = !!(tick && tick.guidanceActive && cfg && cfg.guidanceBarOn && cfg.lightbarEnabled);
  document.body.classList.toggle('steerbar', shown && !!cfg.steerBarEnabled);
  document.body.classList.toggle('lightbar', shown && !cfg.steerBarEnabled);
  // The steer bar is drawn into #lb-steer's slot under the readout; open it only for that style.
  const steerH = shown && cfg.steerBarEnabled ? Math.round(24 * hudScale) : 0;
  if (steerH !== lbSteerH) { lbSteerH = steerH; lbSteer.style.height = steerH + 'px'; }
  if (!shown) { lbEl.style.display = 'none'; lbAvgTick = null; lbAvgM = 0.15; return; }
  const xte = tick.crossTrackError || 0;
  const onLine = Math.abs(xte) < 0.05;
  lbArrow.textContent = (onLine ? '●' : xte > 0 ? '◀' : '▶') + ' '; // arrow = steer direction
  lbArrow.style.color = !onLine && xte > 0 ? LB_LEFT : LB_RIGHT;
  lbText.textContent = fmtUnit(Math.abs(xte) * 100, 'cm', 0);
  if (cfg.steerBarEnabled) lbAvg.style.display = 'none';
  else {
    // Once per tick (AgOpenGPS: once per GPS fix), not per frame.
    if (tick !== lbAvgTick) { lbAvgTick = tick; lbAvgM = Math.min(lbAvgM * 0.98 + Math.abs(xte) * 0.02, 0.15); }
    lbAvg.textContent = fmtUnit(lbAvgM * 100, 'cm', 1);
    lbAvg.style.display = lbAvgM < 0.15 ? 'inline' : 'none';
  }
  lbEl.style.display = 'block';
  // Screen pixels (the canvas isn't zoomed): the readout's reach and its row's centre, for
  // the light bar's cells; the steer bar's slot centre. The reach is the widest readout's, not
  // this one's, so the cells don't jump as the digits or the average come and go; only a
  // distance past three digits pushes them out.
  const key = hudK + unitLabel('cm');
  if (key !== lbProbeKey) {
    lbProbeKey = key;
    lbProbeCore.textContent = '▶ ' + fmtUnit(888, 'cm', 0);
    lbProbeAvg.textContent = fmtUnit(14.9, 'cm', 1);
    lbProbeHalfW = zoomRect(lbProbeCore).width / 2 + 10 * hudK + zoomRect(lbProbeAvg).width;
  }
  const liveHalfW = zoomRect(lbCore).width / 2 + (lbAvg.style.display === 'none' ? 0 : 10 * hudK + zoomRect(lbAvg).width);
  lbHalfW = Math.max(lbProbeHalfW, liveHalfW);
  const row = zoomRect(lbEl);
  lbCy = row.top + row.height / 2;
  if (steerH) { const st = zoomRect(lbSteer); steerCy = st.top + st.height / 2; }
}

// Tram lights (#451): AgOpenGPS's two dots, left and right of the light bar. The host sends
// whether to show them (Tram tab Display, a tram mode on, a tool wider than the wheel track),
// which wheel is on a tram track, and which side is forced on; a tap toggles the force.
const tramLightsEl = document.getElementById('tram-lights');
const tramL = document.getElementById('tram-l'), tramR = document.getElementById('tram-r');
tramL.addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); transport.send('tram.manualLeft'); });
tramR.addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); transport.send('tram.manualRight'); });
function updateTramLights() {
  const b = tick && tick.tools ? tick.tools.tramLights | 0 : 0;
  tramLightsEl.classList.toggle('show', (b & 16) !== 0);
  tramR.classList.toggle('on', (b & 1) !== 0);
  tramL.classList.toggle('on', (b & 2) !== 0);
  tramR.classList.toggle('manual', (b & 4) !== 0);
  tramL.classList.toggle('manual', (b & 8) !== 0);
  // Light bar style: the dots sit between the readout and the cells (lightbarSk leaves room).
  const w = (b & 16) && document.body.classList.contains('lightbar')
    ? Math.round(2 * (lbHalfW + LB_TRAM_W * hudK) / uiZoom) + 'px' : '';
  if (tramLightsEl.style.width !== w) tramLightsEl.style.width = w;
}

// Pass HUD → DOM overlay under the lightbar readout, as AgOpenGPS's DrawTrackInfo: shown
// with any track, whatever the Light bar / Steer bar settings. The host's label is an arrow
// for the way you drive (↑ A→B, ↓ B→A) and the pass: the reference line is the edge between
// two passes (PassGeometry), so each side counts from 1: pass 0 is "1R" (first pass right of
// A→B), pass −1 is "1L". The side is fixed in the field, whichever way you drive. A nudge
// follows, with its side as the nudge window shows it: "<12 cm" left, "12 cm>" right.
const passHud = document.getElementById('pass-hud');
function updatePassHud() {
  // No passes on a contour (#110).
  if (!tick || !tick.guidanceActive || !tick.op || tick.op.contour) { passHud.style.display = 'none'; return; }
  const pass = tick.op.passNumber | 0;
  const nudgeM = tick.op.nudgeOffset || 0;
  const nudge = Math.round(Math.abs(nudgeM) * (isMetric() ? 100 : 39.3701));
  const nudgeText = nudge === 0 ? '' : nudgeM < 0 ? ` <${nudge} ${unitLabel('cm')}` : ` ${nudge} ${unitLabel('cm')}>`;
  const label = (tick.lineLabel || (pass >= 0 ? `${pass + 1}R` : `${-pass}L`)) + nudgeText;
  if (passHud.textContent !== label) passHud.textContent = label;
  passHud.style.display = 'block';
}

// Headland-distance HUD → DOM overlay (mirrors the native SkiaMapControl HUD).
// Shown only when Display.HeadlandDistanceVisible is on and a live distance ≥ 0
// (−1 = no headland / not driving). Metric/imperial follows the status bar.
const hlHud = document.getElementById('headland-hud');
function updateHeadlandHud() {
  const on = config && config.display && config.display.headlandDistanceVisible;
  const d = tick ? tick.headlandDist : -1;
  // Hide the HUD while a map-tap creation flow is active (Quick AB / boundary curve / draw / …):
  // the boundary distance isn't needed while placing points, and it would clutter the top-centre
  // instruction pill + toolbar. body.maptap is set by startMapTap for the duration of the flow.
  const creating = document.body.classList.contains('maptap');
  if (!on || creating || d == null || d < 0) { hlHud.style.display = 'none'; return; }
  hlHud.textContent = isMetric() ? fmtUnit(d, 'm', 1) : fmtUnit(d, 'm', 0);
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
  gcSentence: document.getElementById('gc-sentence'),
  // Dev diagnostics line (marker-gated by .show_dev_overlay).
  diagRow: document.getElementById('sb-diag'),
  sbdFps: document.getElementById('sbd-fps'), sbdLink: document.getElementById('sbd-link'),
  sbdCtrl: document.getElementById('sbd-ctrl'),
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
function fmtArea(m2) { return fmtUnit(m2 * 0.0001, 'ha'); }
function fmtRate(haPerHr) { return haPerHr == null ? '—' : fmtUnit(haPerHr, 'ha', 1) + '/hr'; }
// Work rate from speed (m/s) × tool width; null without a tool width (#398).
function workRateHa(speedMs) { const tw = toolWidthM(); return tw > 0 ? speedMs * 3600 * tw / 10000 : null; }
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
    if (!scene || !scene.hasField) return tr('No field');
    return tr('Field: {name}', { name: scene.fieldName || '—' }) + (s.jobName ? '  ' + tr('Job: {name}', { name: s.jobName }) : '');
  }
  if (sbPage === 1) { // Stats
    if (!scene || !scene.hasField) return '—';
    const workable = workableAreaSqM(), worked = s.workedAreaSqM || 0;
    const leftPct = workable > 0 ? ((workable - worked) * 100 / workable) : 100;
    const haPerHr = workRateHa(lastTick ? lastTick.speed : 0);
    return tr('Done {area}  Left {pct}%  Rate {rate}', { area: fmtArea(worked), pct: leftPct.toFixed(0), rate: fmtRate(haPerHr) });
  }
  const name = tick && tick.activeTrackName; // AB-line page
  if (!name) return tr('No AB Line');
  const h = activeTrackHeadingDeg();
  if (h == null) return tr('AB Line: {name}', { name });
  const primary = ((h % 360) + 360) % 360, recip = (primary + 180) % 360;
  return tr('AB Line: {name}', { name }) + '  ' + primary.toFixed(1) + '°, ' + recip.toFixed(1) + '°';
}
setInterval(() => { if (!sbPaused) sbPage = (sbPage + 1) % 3; }, 5000); // native 5 s cycle
SB.pause.addEventListener('click', () => { sbPaused = !sbPaused; SB.pause.textContent = sbPaused ? '▶' : '❚❚'; });
// Bar interaction must not pan the map (the camera listens on window pointerdown).
SB.bar.addEventListener('pointerdown', e => e.stopPropagation());
// Fullscreen toggle — hides the browser tabs/URL bar on tablets. Works on a user
// gesture over plain HTTP (no PWA install needed). Prefixed fallback for older Android.
// Start Fullscreen (#110): browsers only allow fullscreen from a user gesture, so in a
// plain browser go fullscreen on the first tap after the page loads. The desktop
// launcher window opens fullscreen itself.
// In the desktop launcher (Photino) the web view ignores the Fullscreen API: the button asks
// the launcher over its message channel, which answers with the window's state (#498).
const inLauncher = !!(window.external && typeof window.external.sendMessage === 'function');
document.addEventListener('click', function startFs() {
  document.removeEventListener('click', startFs, true);
  if (inLauncher) return;
  if (!(config && config.display && config.display.startFullscreen)) return;
  if (document.fullscreenElement || document.webkitFullscreenElement) return;
  const el = document.documentElement, req = el.requestFullscreen || el.webkitRequestFullscreen;
  if (req) { try { const p = req.call(el); if (p && p.catch) p.catch(() => {}); } catch (_) {} }
}, true);
const fsBtn = document.getElementById('sb-fs');
if (fsBtn) {
  const fsEl = () => document.fullscreenElement || document.webkitFullscreenElement;
  // Use 'click' (not pointerdown): requestFullscreen needs a durable user activation
  // that pointerdown doesn't reliably grant on Android Chrome (entering took 2–3 taps).
  // exitFullscreen needs no activation, so that always worked first tap.
  fsBtn.addEventListener('click', () => {
    if (inLauncher) { window.external.sendMessage('fullscreen|toggle'); return; }
    if (fsEl()) { (document.exitFullscreen || document.webkitExitFullscreen).call(document); return; }
    const el = document.documentElement, req = el.requestFullscreen || el.webkitRequestFullscreen;
    if (req) { try { const p = req.call(el); if (p && p.catch) p.catch(() => {}); } catch (_) {} }
  });
  fsBtn.addEventListener('pointerdown', e => e.stopPropagation()); // don't also pan the map
  const sync = () => {
    const fs = !!fsEl();
    fsBtn.textContent = fs ? '🗗' : '⛶';
    // In fullscreen the OS draws an exit "✕" pill at the top-left (e.g. iPad Safari's
    // Fullscreen-API control) that sits over the RTK fix + rotating info fields. We can't
    // hide OS chrome, so nudge that left stack clear of it — only while fullscreen.
    document.body.classList.toggle('fs-on', fs);
  };
  document.addEventListener('fullscreenchange', sync);
  document.addEventListener('webkitfullscreenchange', sync);
  if (inLauncher && typeof window.external.receiveMessage === 'function') {
    // No OS exit pill over a launcher window, so only the icon follows its state.
    window.external.receiveMessage(m => {
      if (m === 'fullscreen|1' || m === 'fullscreen|0') fsBtn.textContent = m === 'fullscreen|1' ? '🗗' : '⛶';
    });
    window.external.sendMessage('fullscreen|state'); // Start Fullscreen may have opened it so
  }
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
  if (show) document.getElementById('sb-fieldcard').style.display = 'none'; // one card at a time
  if (show) SB.modPop.style.display = 'none';
});
addEventListener('pointerdown', () => {
  if (SB.modPop) SB.modPop.style.display = 'none';
  if (SB.gpsCard) SB.gpsCard.style.display = 'none';
  document.getElementById('sb-fieldcard').style.display = 'none';
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

// Field data card (#321), after AgOpenGPS FormFieldData: tap the rotating field line under
// the fix readout. Total / Remaining / Time left / Actual remaining need a boundary.
// Worked counts every pass (overlap included); Actual is the ground covered at least once.
const FC = {
  card: document.getElementById('sb-fieldcard'), reset: document.getElementById('fc-reset'),
  name: document.getElementById('fc-name'), total: document.getElementById('fc-total'),
  worked: document.getElementById('fc-worked'), remain: document.getElementById('fc-remain'),
  time: document.getElementById('fc-time'), overlap: document.getElementById('fc-overlap'),
  actual: document.getElementById('fc-actual'), actRemain: document.getElementById('fc-actremain'),
  rate: document.getElementById('fc-rate'), tripArea: document.getElementById('fc-triparea'),
  tripDist: document.getElementById('fc-tripdist'),
};
SB.rot.addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (!scene || !scene.hasField) return;
  const show = FC.card.style.display !== 'block';
  FC.card.style.display = show ? 'block' : 'none';
  if (show) { SB.gpsCard.style.display = 'none'; renderFieldCard(); }
});
FC.card.addEventListener('pointerdown', e => e.stopPropagation());
FC.reset.addEventListener('pointerdown', e => { e.preventDefault(); transport.send('fielddata.resetTrip'); });
function renderFieldCard() {
  if (FC.card.style.display !== 'block') return;
  const s = statusBar, fd = s && s.fieldData;
  if (!fd || !scene || !scene.hasField) { FC.card.style.display = 'none'; return; }
  const total = fd.boundaryAreaSqM, worked = s.workedAreaSqM || 0;
  // A cell on the tool's edge can count as covered a little before the strip area
  // catches up, so keep Actual within Worked.
  const actual = Math.min(fd.actualAreaSqM, worked);
  const speedKmh = (lastTick ? lastTick.speed : 0) * 3.6;
  const haPerHr = workRateHa(lastTick ? lastTick.speed : 0);
  for (const r of FC.card.querySelectorAll('.fc-bnd')) r.classList.toggle('fc-hide', !(total > 0));
  FC.name.textContent = scene.fieldName + (s.jobName ? '  ·  ' + s.jobName : '');
  FC.worked.textContent = fmtArea(worked);
  FC.overlap.textContent = (worked > 0 ? (1 - actual / worked) * 100 : 0).toFixed(1) + '%';
  FC.actual.textContent = fmtArea(actual);
  FC.rate.textContent = fmtRate(haPerHr);
  FC.tripArea.textContent = fmtArea(fd.tripAreaSqM);
  FC.tripDist.textContent = fmtUnit(fd.tripDistanceM, 'm', 0);
  if (total > 0) {
    const left = Math.max(0, total - worked);
    FC.total.textContent = fmtArea(total);
    FC.remain.textContent = fmtArea(left) + '  (' + (left * 100 / total).toFixed(1) + '%)';
    FC.actRemain.textContent = fmtArea(Math.max(0, total - actual));
    // AgOpenGPS: no estimate below 2 km/h.
    if (speedKmh > 2 && haPerHr > 0) {
      const mins = Math.round(left / 10000 / haPerHr * 60);
      FC.time.textContent = Math.floor(mins / 60) + ':' + String(mins % 60).padStart(2, '0');
    } else FC.time.textContent = '∞';
  }
}

function renderStatusBar() {
  const s = statusBar;
  if (!s) { SB.bar.style.display = 'none'; if (SB.diagRow) SB.diagRow.style.display = 'none'; return; }
  SB.bar.style.display = 'flex';
  // GPS data lost: the host already reports No Fix; grey the dot so it reads as "nothing",
  // not as a live red "no fix" from a receiver that is still talking.
  SB.fixDot.style.background = s.gpsLost ? '#6b7280' : fixColor(s.fixQuality);
  SB.fix.textContent = s.fixText || '—';
  SB.age.textContent = tr('Age {age}', { age: Number.isFinite(s.age) ? s.age.toFixed(1) : '—' }); // NaN: the sentence has no age ($KSXT)
  SB.rot.textContent = rotatingLineText();
  renderFieldCard();
  // Speed from the live tick (m/s), formatted per the host's unit preference.
  const mps = lastTick ? lastTick.speed : 0;
  SB.speed.textContent = toDisplayUnit(mps * 3.6, 'kmh').toFixed(1);
  SB.unit.textContent = unitLabel('kmh');
  // Modules: aggregate dot + per-module popup rows.
  // All configured modules present = steady green; anything short of that flashes
  // (amber: some missing, red: none), so trouble catches the eye from across the cab.
  const aggColor = moduleAggColor(s);
  SB.modAgg.style.background = aggColor;
  SB.modAgg.classList.toggle('flash', aggColor !== '#22c55e');
  const dot = (el, ok) => { el.style.background = ok ? '#22c55e' : '#6b7280'; };
  dot(SB.mGps, s.gpsOk); dot(SB.mImu, s.imuOk); dot(SB.mAs, s.autoSteerOk); dot(SB.mMa, s.machineOk);
  SB.dGps.textContent = (s.fixText || '—') + (s.sats ? ' ' + tr('({n} sats)', { n: s.sats }) : '');
  SB.dImu.textContent = s.imuIp || tr('Not detected');
  SB.dAs.textContent = s.autoSteerIp || tr('Not detected');
  SB.dMa.textContent = s.machineIp || tr('Not detected');
  // Heading readout (right of speed) — Tick heading is radians (0 = N, CW).
  const hdgDeg = lastTick ? (lastTick.heading * 180 / Math.PI) : null;
  SB.hdg.textContent = hdgDeg != null ? fmtHdg(hdgDeg) : '—';
  // GPS-detail card — populated only while open (the fix dot toggles it).
  if (SB.gpsCard.style.display === 'block') {
    SB.gcLat.textContent = s.lat != null ? s.lat.toFixed(7) : '—';
    SB.gcLon.textContent = s.lon != null ? s.lon.toFixed(7) : '—';
    SB.gcElev.textContent = s.altitude != null ? fmtUnit(s.altitude, 'm', 1) : '—'; // m / ft (#112)
    SB.gcSats.textContent = s.sats != null ? s.sats : '—';
    SB.gcHdop.textContent = Number.isFinite(s.hdop) ? s.hdop.toFixed(2) : '—';
    SB.gcFix.textContent = s.fixText || '—';
    SB.gcAge.textContent = Number.isFinite(s.age) ? s.age.toFixed(1) : '—';
    // Incoming sentence (#157); flagged when Dual GPS is on but there's no dual heading.
    SB.gcSentence.textContent = (s.gpsSentence ? '$' + s.gpsSentence : '—')
      + (s.dualHeadingMissing ? ' ' + tr('(no dual heading)', {}) : '');
    SB.gcSentence.style.color = s.dualHeadingMissing ? 'salmon' : '';
    SB.gcHdg.textContent = hdgDeg != null ? (((hdgDeg % 360) + 360) % 360).toFixed(1) + '°' : '—';
    SB.gcRoll.textContent = (tick && typeof tick.roll === 'number') ? tick.roll.toFixed(1) + '°' : '—';
    SB.gcFps.textContent = fps.toFixed(0);
  }
  // Dev diagnostics line (marker-gated). DOM text only — no canvas overlay — so reading FPS
  // doesn't perturb the frame loop (an overlay/dialog did, hence the status-line approach).
  // Throttled to ~4 Hz; that tick also fires the link probe. FPS = client render rate; LINK =
  // server↔client round-trip (ms, from diag.ping/PONG); CTRL = backend→AiO control-loop
  // latency (GPS receive → PGN send, ms).
  if (s.devOverlay) {
    if (SB.diagRow.style.display !== 'flex') SB.diagRow.style.display = 'flex';
    const nowMs = performance.now();
    if (nowMs - _diagT0 >= 250) {
      _diagT0 = nowMs;
      transport.send('diag.ping|' + nowMs); // link probe; the PONG reply updates linkRttMs
      SB.sbdFps.textContent = fps.toFixed(0);
      SB.sbdLink.textContent = linkRttMs != null ? linkRttMs.toFixed(1) + ' ms' : '—';
      SB.sbdCtrl.textContent = (s.gpsToPgnLatencyMs || 0).toFixed(1) + ' ms';
    }
  } else if (SB.diagRow.style.display !== 'none') {
    SB.diagRow.style.display = 'none';
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
  if (!s || s.simPanelVisible === false) return;
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
  SIM.speedVal.textContent = fmtUnit(eff, 'kmh', 1);
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
// Zone mode (Tool config → Zones): one button per zone instead of per section, like
// AgOpenGPS's zone buttons (#111). Zone z = sections zoneRanges[z-1]..zoneRanges[z]-1.
function zoneLayout() {
  const t = config && config.tool;
  if (!t || t.isSectionsNotZones !== false || !t.zoneRanges) return null;
  const n = Math.max(1, Math.min(8, t.zones | 0));
  const zones = [];
  for (let z = 1; z <= n; z++) zones.push({ z, start: z === 1 ? 0 : t.zoneRanges[z - 1], end: t.zoneRanges[z] });
  return zones;
}
function buildZoneRow(zones) {
  sectionBar.innerHTML = '';
  const row = document.createElement('div');
  row.className = 'sec-row';
  for (const zn of zones) {
    const b = document.createElement('button');
    b.className = 'sec-btn';
    b.dataset.zone = zn.z; b.dataset.last = Math.max(0, zn.end - 1);
    b.textContent = 'Z' + zn.z;
    row.appendChild(b);
  }
  sectionBar.appendChild(row);
}
function renderSectionBar() {
  // Native gate: field open AND a master (auto or manual) engaged.
  const secs = tick && tick.sections;
  const visible = !!(scene && scene.hasField && tick && tick.op
    && (tick.op.sectionManual || tick.op.sectionAuto) && secs && secs.length);
  sectionBar.classList.toggle('open', visible);
  if (!visible) { _secCount = -1; return; } // force a rebuild when it reappears
  const zones = zoneLayout();
  const key = zones ? 'z' + JSON.stringify(zones) : secs.length;
  if (key !== _secCount) { if (zones) buildZoneRow(zones); else buildSectionRows(secs.length); _secCount = key; }
  for (const b of sectionBar.querySelectorAll('button[data-idx]'))
    b.style.background = SECTION_COLORS[secs[+b.dataset.idx]] || SECTION_COLORS[0];
  for (const b of sectionBar.querySelectorAll('button[data-zone]')) // zone colour = its last section
    b.style.background = SECTION_COLORS[secs[+b.dataset.last]] || SECTION_COLORS[0];
  sectionBar.classList.toggle('locked', !iHoldControl); // dim when we can't actuate
  renderIsobusSections();
}
// Under the section buttons: what the ISOBUS implement reports for its own sections
// (PGN 240), so a section that didn't follow its command shows (#312). One cell per
// implement section, which need not be the app's section count.
function renderIsobusSections() {
  const iso = statusBar && statusBar.isobus;
  let row = sectionBar.querySelector('.sec-iso');
  if (!iso || !iso.sections) { if (row) row.remove(); return; }
  if (!row || +row.dataset.n !== iso.reported.length) {
    if (row) row.remove();
    row = document.createElement('div');
    row.className = 'sec-iso'; row.dataset.n = iso.reported.length;
    const label = document.createElement('span');
    label.className = 'sec-iso-label'; label.textContent = 'ISOBUS';
    const cells = document.createElement('div');
    cells.className = 'sec-iso-cells';
    for (let i = 0; i < iso.reported.length; i++) {
      const c = document.createElement('div'); c.className = 'sec-iso-cell'; cells.appendChild(c);
    }
    row.appendChild(label); row.appendChild(cells);
    sectionBar.appendChild(row);
  }
  const cells = row.querySelectorAll('.sec-iso-cell');
  for (let i = 0; i < cells.length; i++) cells[i].classList.toggle('on', !!iso.reported[i]);
}
// Bottom nav (Phase 8). Swap icons only on change; reflect Tick toggle state.
function bnIcon(img, name) { const p = '/icons/' + name; if (img && !img.src.endsWith(p)) img.src = p; }
const TRAM_ICONS = ['TramOff.png', 'TramAll.png', 'TramLines.png', 'TramOuter.png'];
function renderBottomNav() {
  applyOnScreenButtons(); // U-Turn / Lateral glyphs follow field-open + display toggles
  const visible = !!(scene && scene.hasField); // native gate: IsFieldOpen
  bottomNav.classList.toggle('open', visible);
  if (!visible) { bnFlags.classList.remove('open'); bnAb.classList.remove('open'); renderRefNudge(false); return; }
  // AB-line-dependent buttons appear only with an active track.
  const hasTrack = !!(tick && tick.activeTrackName);
  for (const el of bottomNav.querySelectorAll('.bn-abdep')) el.classList.toggle('hide', !hasTrack);
  renderRefNudge(hasTrack);
  const t = (tick && tick.tools) || {};
  document.getElementById('bn-skipnum').textContent = t.skipRows || 0;
  // Nudge readout (#93): driver-relative offset, cm (metric) / in (imperial), L/R side.
  const nudgeM = (tick && tick.op && tick.op.nudgeOffset) || 0;
  const nudgeAmt = Math.round(Math.abs(nudgeM) * (isMetric() ? 100 : 39.3701));
  document.getElementById('bn-nudgeval').textContent =
    nudgeAmt === 0 ? '0' : nudgeAmt + (nudgeM < 0 ? ' L' : ' R');
  // AgOpenGPS skip-button icons: normal / alternative / ignore worked tracks (#111).
  bnIcon(document.getElementById('bn-skip-ic'), ['YouSkipOff.png', 'YouSkipOn.png', 'YouSkipWorkedTracks.png'][t.skipMode | 0] || 'YouSkipOff.png');
  // Icon shows the PAINTING state, not the raw flag: sectionInHeadland (=Tool.
  // IsHeadlandSectionControl) true means sections AUTO-OFF in the headland (NOT
  // painted) → the "Off" icon; false means sections paint the headland → "On".
  bnIcon(document.getElementById('bn-secheadland-ic'), t.sectionInHeadland ? 'HeadlandSectionOff.png' : 'HeadlandSectionOn.png');
  bnIcon(document.getElementById('bn-headland-ic'), t.headlandOn ? 'HeadlandOn.png' : 'HeadlandOff.png');
  bnIcon(document.getElementById('bn-tram-ic'), TRAM_ICONS[t.tramMode || 0]);
  bottomNav.classList.toggle('locked', !iHoldControl);
}

// Right-nav operational toolbar (Phase 3a: read-only live indicators; 3b wires the
// Tier-2 commands). Colour-coded from the Tick's operational state.
const RN = {
  root: document.getElementById('rightnav'),
  contourI: document.getElementById('rn-contour-i'), manualI: document.getElementById('rn-manual-i'),
  contourLock: document.getElementById('rn-contourlock'), contourLockI: document.getElementById('rn-contourlock-i'),
  autoI: document.getElementById('rn-auto-i'), youturnI: document.getElementById('rn-youturn-i'),
  isobus: document.getElementById('rn-isobus'), isobusI: document.getElementById('rn-isobus-i'),
  cycle: document.getElementById('rn-cycle'), cycleBack: document.getElementById('rn-cycleback'),
  autoTrack: document.getElementById('rn-autotrack'), autoTrackI: document.getElementById('rn-autotrack-i'),
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
  if (RN.readonly) RN.readonly.textContent = iHoldControl ? '' : tr('observing');
  // State carried by the icon image (native uses the same On/Off/Gray PNGs).
  rnIcon(RN.contourI, op.contour ? 'ContourOn.png' : 'ContourOff.png');
  // Contour lock shows only in contour mode, and U-turn hides then (AgOpenGPS) (#110).
  RN.contourLock.style.display = op.contour ? '' : 'none';
  rnIcon(RN.contourLockI, scene.contourLocked ? 'ColorLocked.png' : 'ColorUnlocked.png');
  RN.youturnI.parentElement.style.display = op.contour ? 'none' : '';
  // Track picking (#336), AgOpenGPS's rule: two or more visible tracks, one active, no contour.
  const tl = scene.trackList || [];
  const visible = tl.filter(t => t.visible);
  const activeName = tick.activeTrackName || '';
  const showPick = visible.length > 1 && !!activeName && !op.contour;
  for (const b of [RN.cycle, RN.cycleBack, RN.autoTrack]) b.style.display = showPick ? '' : 'none';
  rnIcon(RN.autoTrackI, tick.tools && tick.tools.autoTrack ? 'AutoTrack.png' : 'AutoTrackOff.png');
  // Name and "n/total" after a cycle tap, like AgOpenGPS's guidance-line label.
  if (activeName !== rnLastTrack) {
    if (rnLastTrack !== null && Date.now() - rnCycleAt < 2000) {
      const n = visible.findIndex(t => t.name === activeName) + 1;
      flashHint(n > 0 ? activeName + '  ' + n + '/' + visible.length : activeName);
    }
    rnLastTrack = activeName;
  }
  rnIcon(RN.manualI, op.sectionManual ? 'ManualOn.png' : 'ManualOff.png');
  rnIcon(RN.autoI, op.sectionAuto ? 'SectionMasterOn.png' : 'SectionMasterOff.png');
  rnIcon(RN.youturnI, op.youturn ? 'YouTurnYes.png' : 'YouTurnNo.png');
  // ISOBUS task controller (#312): the button is there while its heartbeat is heard, like
  // AgOpenGPS. Idle = no implement with sections; otherwise section control on / off.
  const iso = statusBar && statusBar.isobus;
  RN.isobus.style.display = iso ? '' : 'none';
  if (iso) {
    rnIcon(RN.isobusI, !iso.sections ? 'IsobusSectionControlIdle.png'
      : iso.sectionControl ? 'IsobusSectionControlOn.png' : 'IsobusSectionControlOff.png');
    const tip = !iso.sections ? tr('ISOBUS: no implement')
      : iso.sectionControl ? tr('ISOBUS section control on ({n} sections)', { n: iso.sections })
      : tr('ISOBUS section control off ({n} sections)', { n: iso.sections });
    if (RN.isobus.title !== tip) RN.isobus.title = tip;
  }
  // U-turn direction + distance-to-trigger now render on-screen (see renderUTurnIndicator).
  // AutoSteer 3-state icon: grey (no track) / off-ready / on-engaged.
  rnIcon(RN.steerI, !op.autoSteerAvail ? 'AutoSteerGray.png' : op.autoSteer ? 'AutoSteerOn.png' : 'AutoSteerOff.png');
  // Engaged but the module isn't steering (kickout / switch / button): red, like
  // AgOpenGPS's steer circle (#126).
  RN.steerI.parentElement.classList.toggle('not-steering', !!(op.autoSteer && op.moduleNotSteering));
}

// Auto-U-turn approach indicator (upper-right): green direction arrow + distance to
// trigger + turn-type glyph. Mirrors native's on-map readout; shown while auto U-turn is
// armed with a pending turn. Turn type comes from config (Omega / Sagitta); distance honours
// the metric/imperial unit like the headland HUD.
const UTI = {
  root: document.getElementById('uturn-indicator'),
  arrow: document.getElementById('uti-arrow'),
  dist: document.getElementById('uti-distval'),
  typePath: document.getElementById('uti-typepath'),
};
// A tap on the arrow swaps the planned turn's direction, as in AgOpenGPS (SwapDirection, #411).
// The host replans the turn and the arrow follows the new path's side.
if (UTI.arrow) UTI.arrow.parentElement.addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation(); rnSend('youturn.direction');
});
const UTURN_GLYPH = {
  0: 'M7 34 Q7 20 20 20 Q33 20 33 34',         // Sagitta (default) — flatter rounded arch
  1: 'M9 34 L9 22 A11 11 0 0 1 31 22 L31 34',  // K-turn — squared/tighter inverted-U
  2: 'M15 34 A9 9 0 1 1 25 34',                 // Keyhole — loop centred over a narrow neck
};
function renderUTurnIndicator() {
  if (!UTI.root) return;
  const op = tick && tick.op;
  const show = !!(scene && scene.hasField && op && op.youturn && op.distToTrigger > 0);
  UTI.root.hidden = !show;
  if (!show) return;
  UTI.dist.textContent = fmtUnit(op.distToTrigger, 'm', 0);
  UTI.arrow.classList.toggle('left', !!op.turnLeft);
  const style = (config && config.uturn && config.uturn.style) || 0;
  UTI.typePath.setAttribute('d', UTURN_GLYPH[style] || UTURN_GLYPH[0]);
}
