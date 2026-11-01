// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// Lower-right cluster (roll gauge, camera pad), charts, the tool panels (roll correction, offset fix, import tracks, recorded path, boundary recording), card dragging.

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
  // Bind on POINTERDOWN, not click: the NativeWebView launcher (touch) doesn't reliably fire
  // `click`, so these camera buttons — notably the mode/heading cycle — did nothing when tapped
  // on the launcher build (issue #56). pointerdown is the same event the rest of the touch UI
  // uses; the pad's stopPropagation above already keeps it from panning the map.
  document.getElementById('cp-tiltup').addEventListener('pointerdown', () => { pitch = Math.max(0, pitch - PITCH_STEP); });
  document.getElementById('cp-tiltdown').addEventListener('pointerdown', () => {
    pitch = Math.min(MAX_PITCH, pitch + PITCH_STEP);
  });
  document.getElementById('cp-zoomin').addEventListener('pointerdown', () => { pxPerM = Math.min(200, pxPerM * 1.2); });
  document.getElementById('cp-zoomout').addEventListener('pointerdown', () => { pxPerM = Math.max(0.2, pxPerM * 0.83); });
  // Center: cycle the four native modes H → N → M → C → H; recenter on follow modes.
  document.getElementById('cp-mode').addEventListener('pointerdown', () => {
    cameraMode = cameraMode === 1 ? 0 : cameraMode === 0 ? 3 : cameraMode === 3 ? 2 : 1;
    if (cameraMode !== 2) { const rp = renderPose(); if (rp) { camE = rp.e; camN = rp.n; } }
  });
})();
// ── Diagnostic charts (Tools panel) ─────────────────────────────────────────
// Thin-client port of the native ChartControl: the host streams the scalar
// series sources on each Tick (see TickDto chart fields); we keep a rolling
// display buffer per series and redraw the open chart cards each frame to a 2D
// canvas. Configs mirror the native *ChartPanel.ConfigureChart() calls 1:1.
const CHART_WINDOW = 20; // seconds (IChartDataService.TimeWindowSeconds default)
const CHARTS = {
  steer: {
    title: tr('Steer'), yLabel: 'deg', minY: -40, maxY: 40, step: 10, auto: false,
    series: [
      // AgOpenGPS FormGraphSteer: set vs actual angle. PWM (0–255) doesn't belong on a
      // ±40° scale (#111).
      { name: tr('Set Angle'), color: '#E05020', pts: [] },
      { name: tr('Actual Angle'), color: '#2080E0', pts: [] },
    ],
  },
  heading: {
    title: tr('Heading'), yLabel: 'deg', minY: 0, maxY: 360, step: 45, auto: true,
    series: [
      // AgOpenGPS FormGraphHeading: GPS fix-to-fix vs IMU-corrected heading (#111).
      { name: tr('GPS Heading'), color: '#0088AA', pts: [] },
      { name: tr('IMU Heading'), color: '#D07020', pts: [] },
    ],
  },
  xte: {
    title: tr('XTE'), yLabel: 'm', minY: -2, maxY: 2, step: 0.5, auto: true,
    series: [{ name: tr('XTE'), color: '#C020C0', pts: [] }],
  },
};
const chartOpen = { steer: false, heading: false, xte: false };

// Buffer the latest Tick into every series (always, even while a card is closed,
// so opening mid-session shows recent history — matches ChartDataService.Start()).
function pushChartData(t) {
  const now = performance.now() / 1000;
  const vals = {
    steer: [t.chartSetSteer, t.chartActualSteer],
    heading: [t.chartGpsHeading, t.chartImuHeading],
    xte: [toDisplayUnit(t.crossTrackError, 'm')],   // plot in the active length unit
  };
  const trim = now - CHART_WINDOW - 2;
  for (const key in CHARTS) {
    const arr = vals[key];
    const series = CHARTS[key].series;
    for (let i = 0; i < series.length; i++) {
      const pts = series[i].pts;
      if (Number.isFinite(arr[i])) pts.push({ t: now, v: arr[i] }); // no IMU → no IMU line
      let cut = 0; while (cut < pts.length && pts[cut].t < trim) cut++;
      if (cut) pts.splice(0, cut);
    }
  }
}

function setChartOpen(key, open) {
  chartOpen[key] = open;
  document.getElementById('chart-' + key).style.display = open ? 'block' : 'none';
  const btn = document.querySelector('.tl-chartbtn[data-chart="' + key + '"]');
  if (btn) btn.classList.toggle('active', open);
}
function toggleChart(key) { setChartOpen(key, !chartOpen[key]); }
// Reflect open/closed state on the Tools fly-out chart buttons when it opens.
function renderToolsPanel() {
  for (const key in chartOpen) {
    const btn = document.querySelector('.tl-chartbtn[data-chart="' + key + '"]');
    if (btn) btn.classList.toggle('active', chartOpen[key]);
  }
}

function chartNiceStep(range, targetLines) {
  const raw = range / targetLines;
  const mag = Math.pow(10, Math.floor(Math.log10(raw)));
  const norm = raw / mag;
  const nice = norm <= 1.5 ? 1 : norm <= 3.5 ? 2 : norm <= 7.5 ? 5 : 10;
  return nice * mag;
}

function drawChart(cv, c) {
  const ctx = cv.getContext('2d');
  const dpr = window.devicePixelRatio || 1;
  const W = 400, H = 150;
  if (cv.width !== Math.round(W * dpr)) { cv.width = Math.round(W * dpr); cv.height = Math.round(H * dpr); }
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, W, H);
  ctx.font = '10px system-ui,sans-serif';
  ctx.textBaseline = 'alphabetic';

  // Background + frame.
  ctx.fillStyle = '#10141d';
  ctx.fillRect(0, 0, W, H);

  const L = 50, R = 10, T = 8, B = 20;
  const cl = L, cr = W - R, ctop = T, cbot = H - B;
  const cw = cr - cl, ch = cbot - ctop;
  if (cw <= 0 || ch <= 0) return;

  // Y range (auto-scale mirrors native ComputeAutoScale: 10% pad + nice step).
  let minY = c.minY, maxY = c.maxY, step = c.step;
  if (c.auto) {
    let dmin = Infinity, dmax = -Infinity;
    for (const s of c.series) for (const p of s.pts) { if (p.v < dmin) dmin = p.v; if (p.v > dmax) dmax = p.v; }
    if (dmin !== Infinity) {
      let range = dmax - dmin; if (range < 1) range = 1;
      const pad = range * 0.1;
      minY = dmin - pad; maxY = dmax + pad;
      step = chartNiceStep(maxY - minY, 5);
    }
  }
  let yRange = maxY - minY; if (yRange <= 0) yRange = 1;

  const now = performance.now() / 1000;
  const timeStart = now - CHART_WINDOW;

  // Horizontal grid + Y labels.
  ctx.strokeStyle = 'rgba(140,155,180,0.25)'; ctx.lineWidth = 0.5;
  ctx.fillStyle = '#aeb8c8'; ctx.textAlign = 'right';
  const fmt = step >= 1 ? 0 : step >= 0.1 ? 1 : 2;
  const firstY = Math.ceil(minY / step) * step;
  for (let val = firstY; val <= maxY + 1e-9; val += step) {
    const y = cbot - ((val - minY) / yRange * ch);
    ctx.beginPath(); ctx.moveTo(cl, y); ctx.lineTo(cr, y); ctx.stroke();
    ctx.fillText(val.toFixed(fmt), cl - 4, y + 3);
  }

  // Zero line.
  if (minY < 0 && maxY > 0) {
    const zy = cbot - ((-minY) / yRange * ch);
    ctx.strokeStyle = 'rgba(206,214,230,0.5)'; ctx.lineWidth = 1;
    ctx.beginPath(); ctx.moveTo(cl, zy); ctx.lineTo(cr, zy); ctx.stroke();
  }

  // Series polylines (clipped to chart area).
  ctx.save();
  ctx.beginPath(); ctx.rect(cl, ctop, cw, ch); ctx.clip();
  ctx.lineWidth = 1.5;
  for (const s of c.series) {
    if (s.pts.length < 2) continue;
    ctx.strokeStyle = s.color;
    ctx.beginPath();
    let started = false;
    for (const p of s.pts) {
      if (p.t < timeStart) continue;
      const x = cl + ((p.t - timeStart) / CHART_WINDOW * cw);
      const y = cbot - ((p.v - minY) / yRange * ch);
      if (!started) { ctx.moveTo(x, y); started = true; } else ctx.lineTo(x, y);
    }
    if (started) ctx.stroke();
  }
  ctx.restore();

  // Border.
  ctx.strokeStyle = 'rgba(140,155,180,0.4)'; ctx.lineWidth = 1;
  ctx.strokeRect(cl, ctop, cw, ch);

  // Title (top-left of chart area).
  ctx.fillStyle = '#aeb8c8'; ctx.textAlign = 'left';
  ctx.font = '11px system-ui,sans-serif';
  ctx.fillText(I18n.text(c.title), cl + 4, ctop + 11);

  // Legend (top-right, right-to-left).
  ctx.font = '9px system-ui,sans-serif';
  let lx = cr - 8;
  for (let i = c.series.length - 1; i >= 0; i--) {
    const s = c.series[i];
    const name = I18n.text(s.name); // drawn on the canvas, so translated here
    const tw = ctx.measureText(name).width;
    lx -= tw;
    ctx.fillStyle = s.color;
    ctx.textAlign = 'left';
    ctx.fillText(name, lx, ctop + 9);
    ctx.strokeStyle = s.color; ctx.lineWidth = 2;
    ctx.beginPath(); ctx.moveTo(lx - 14, ctop + 6); ctx.lineTo(lx - 4, ctop + 6); ctx.stroke();
    lx -= 22;
  }

  // Time labels (relative seconds, −window … 0).
  ctx.fillStyle = '#aeb8c8'; ctx.textAlign = 'center'; ctx.font = '9px system-ui,sans-serif';
  const labelCount = 5;
  for (let i = 0; i <= labelCount; i++) {
    const frac = i / labelCount;
    const x = cl + frac * cw;
    const rel = Math.round(-CHART_WINDOW + frac * CHART_WINDOW);
    ctx.fillText(rel + 's', x, cbot + 13);
    ctx.strokeStyle = 'rgba(140,155,180,0.25)'; ctx.lineWidth = 0.5;
    ctx.beginPath(); ctx.moveTo(x, ctop); ctx.lineTo(x, cbot); ctx.stroke();
  }
}

function renderCharts() {
  for (const key in chartOpen) {
    if (!chartOpen[key]) continue;
    const cv = document.getElementById('chart-' + key).querySelector('.chart-cv');
    drawChart(cv, CHARTS[key]);
  }
}

// Roll Correction panel: live gauge + offset/invert readouts (only when open).
// Mirrors the wizard roll-cal step; the bar rotates by the live (post-calibration)
// roll the same way the map roll gauge does.
function renderRollCorr() {
  const panel = document.getElementById('rollcorr');
  if (!panel.classList.contains('open')) return;
  const rv = (tick && typeof tick.roll === 'number') ? tick.roll : 0;
  const bar = document.getElementById('rc-roll-bar');
  if (bar) bar.setAttribute('transform', 'rotate(' + rv.toFixed(2) + ' 100 40)');
  const deg = document.getElementById('rc-roll-deg');
  if (deg) deg.textContent = rv.toFixed(1);
  const roll = (config && config.roll) || {};
  const off = document.getElementById('rc-offset');
  if (off) off.textContent = (typeof roll.rollZero === 'number' ? roll.rollZero : 0).toFixed(2) + '°';
  const inv = document.getElementById('rc-invert');
  if (inv) { const on = !!roll.isRollInvert; inv.classList.toggle('on', on); inv.textContent = on ? tr('On') : tr('Off'); }
}

// Offset Fix: reflect the live drift offset into the manual inputs (unless the user is
// editing that field). Drift rides the Status frame.
function renderOffsetFix() {
  if (!document.getElementById('offsetfix').classList.contains('open')) return;
  if (!statusBar) return;
  const ns = document.getElementById('of-ns-in'), ew = document.getElementById('of-ew-in');
  if (document.activeElement !== ns) writeUnitInput(ns, statusBar.driftNorthing || 0);
  if (document.activeElement !== ew) writeUnitInput(ew, statusBar.driftEasting || 0);
}

// Import Tracks: list the other fields that have saved tracks; tap one to copy its
// tracks into the open field (host ImportTracksFromFieldCommand transforms origins).
function renderImportTracks() {
  const list = document.getElementById('it-list'); list.innerHTML = '';
  const fields = fieldTools ? fieldTools.importFields : [];
  if (!fields || !fields.length) {
    list.innerHTML = '<div class="fj-empty">No other fields with tracks found.</div>';
    return;
  }
  for (const name of fields) {
    const row = document.createElement('div');
    row.className = 'fj-jrow';
    row.innerHTML = '<div class="fj-jtop"><span class="fj-jname"></span></div>';
    row.querySelector('.fj-jname').textContent = name;
    row.addEventListener('pointerdown', ev => {
      ev.stopPropagation();
      transport.send('field.importTracks|' + name);
      lnCloseAll();
    });
    list.appendChild(row);
  }
}

// Recorded Path: GPS-driven record + playback. All state
// comes from the host-driven RecordedPath frame; the name field is local input.
function renderRecPath() {
  const r = recPath || { recFiles: [], isRecording: false, isPlaying: false, hasUnsaved: false, recordedPathInfo: '', resumeModeLabel: 'Start' };
  document.getElementById('rp-tab-rec').classList.toggle('active', recPathTab === 0);
  document.getElementById('rp-tab-play').classList.toggle('active', recPathTab === 1);
  document.getElementById('rp-record').classList.toggle('active', recPathTab === 0);
  document.getElementById('rp-play').classList.toggle('active', recPathTab === 1);
  // Record tab.
  document.getElementById('rp-start').style.display = r.isRecording ? 'none' : '';
  document.getElementById('rp-stop').style.display = r.isRecording ? '' : 'none';
  document.getElementById('rp-recind').style.display = r.isRecording ? 'block' : 'none';
  document.getElementById('rp-saverow').style.display = r.hasUnsaved ? 'flex' : 'none';
  // Auto-fill the Save-as name from the host's generated default (native parity), but
  // don't clobber the user mid-edit.
  const nameIn = document.getElementById('rp-name');
  if (document.activeElement !== nameIn && r.recordedPathName) nameIn.value = r.recordedPathName;
  // Playback tab.
  document.getElementById('rp-playimg').src = r.isPlaying ? '/icons/RecPathStop.png' : '/icons/RecPathPlay.png';
  document.getElementById('rp-playlbl').textContent = r.isPlaying ? tr('Stop') : tr('Play');
  document.getElementById('rp-playbtn').classList.toggle('disabled', !iHoldControl);
  document.getElementById('rp-resumelbl').textContent = r.resumeModeLabel || tr('Start');
  const list = document.getElementById('rp-list'); list.innerHTML = '';
  if (!r.recFiles || !r.recFiles.length) {
    list.innerHTML = '<div class="fj-empty">No saved paths.</div>';
  } else {
    for (const name of r.recFiles) {
      const row = document.createElement('div');
      row.className = 'fj-jrow rp-frow' + (name === recSelFile ? ' sel' : '');
      row.innerHTML = '<span class="fj-jname"></span><button class="rp-del" title="Delete">🗑</button>';
      row.querySelector('.fj-jname').textContent = name;
      row.querySelector('.fj-jname').addEventListener('pointerdown', ev => {
        ev.stopPropagation(); recSelFile = name; transport.send('recpath.selectFile|' + name); renderRecPath();
      });
      row.querySelector('.rp-del').addEventListener('pointerdown', ev => {
        ev.stopPropagation();
        showConfirm(tr('Delete Recorded Path'), tr('Delete "{name}"? This cannot be undone.', { name }), () => {
          if (recSelFile === name) recSelFile = null;
          transport.send('recpath.delete|' + name);
        });
      });
      list.appendChild(row);
    }
  }
  document.getElementById('rp-info').textContent = r.recordedPathInfo || '';
}

// Boundary recording menu: the field's boundaries (Outer / Inner N) with area + the
// Drive-Thru / Hard flags. Tap a row to select; tap a flag cell to select + toggle it.
function renderBoundaryMenu() {
  const b = boundary;
  const list = document.getElementById('bm-list'); list.innerHTML = '';
  const items = b ? b.items : [];
  if (!items.length) { list.innerHTML = '<div class="fj-empty">No boundaries yet — drive around the field to record one.</div>'; return; }
  for (const it of items) {
    const row = document.createElement('div');
    row.className = 'bm-row' + (b && it.index === b.selectedIndex ? ' sel' : '');
    row.innerHTML = '<span class="bm-name"></span><span class="bm-area"></span>'
      + '<span class="bm-flag ' + (it.driveThru ? 'on' : 'off') + '" data-flag="driveThru"></span>'
      + '<span class="bm-flag ' + (it.hard ? 'on' : 'off') + '" data-flag="hard"></span>';
    row.querySelector('.bm-name').textContent = it.boundaryType;
    row.querySelector('.bm-area').textContent = fmtUnit(it.areaHa, 'ha');
    row.querySelector('[data-flag="driveThru"]').textContent = it.driveThru ? tr('Yes') : '--';
    row.querySelector('[data-flag="hard"]').textContent = it.hard ? tr('Hard') : tr('Soft');
    row.addEventListener('pointerdown', ev => { ev.stopPropagation(); transport.send('boundary.select|' + it.index); });
    row.querySelector('[data-flag="driveThru"]').addEventListener('pointerdown', ev => {
      ev.stopPropagation(); transport.send('boundary.select|' + it.index); transport.send('boundary.driveThru');
    });
    row.querySelector('[data-flag="hard"]').addEventListener('pointerdown', ev => {
      ev.stopPropagation(); transport.send('boundary.select|' + it.index); transport.send('boundary.hard');
    });
    list.appendChild(row);
  }
}

// Boundary player: live drive-around recording controls + point count / area.
function renderBoundaryPlayer() {
  const b = boundary; if (!b) return;
  const off = document.getElementById('bp-offset');
  if (document.activeElement !== off) writeUnitInput(off, b.offsetCm || 0);
  document.getElementById('bp-section').classList.toggle('on', b.sectionControlOn);
  document.getElementById('bp-lrimg').src = b.drawRightSide ? '/icons/BoundaryRight.png' : '/icons/BoundaryLeft.png';
  document.getElementById('bp-atimg').src = b.drawAtPivot ? '/icons/BoundaryRecordPivot.png' : '/icons/BoundaryRecordTool.png';
  document.getElementById('bp-points').textContent = b.pointCount;
  document.getElementById('bp-area').textContent = fmtUnit(b.areaHa || 0, 'ha');
  document.getElementById('bp-recimg').src = b.isRecording ? '/icons/boundaryPause.png' : '/icons/BoundaryRecord.png';
}

// Drag chart cards by the header (web docks ln-panels, but charts are free overlays
// like the native FloatingPanel so the operator can move them out of the way). The VT cards,
// made later, use wireCardDrag too.
// The card touched last is on top of the other cards (29, the cards' layer; the others one
// below it, still above the map's overlays at 20 and under the panels from 30 up).
function raiseCard(card) {
  // Every card each time: one added since (a VT card) starts at the stylesheet's 29 too.
  for (const c of document.querySelectorAll('.chart-card')) c.style.zIndex = c === card ? '29' : '28';
}
function wireCardDrag(card) {
  const hdr = card.querySelector('.chart-hdr');
  let drag = false, sx = 0, sy = 0, ox = 0, oy = 0;
  card.addEventListener('pointerdown', e => { e.stopPropagation(); raiseCard(card); }); // don't pan the map
  hdr.addEventListener('pointerdown', e => {
    raiseCard(card); // the card's own handler doesn't hear it
    if (e.target.closest('button, select')) return;
    e.stopPropagation();
    if (card.classList.contains('in-pane')) return; // the pane places it
    const r = zoomRect(card);
    // zoomRect and clientX are screen pixels; left/top are in the card's own (zoomed)
    // pixels.
    drag = true; sx = e.clientX; sy = e.clientY; ox = r.left / uiZoom; oy = r.top / uiZoom;
    card.style.left = ox + 'px'; card.style.top = oy + 'px'; card.style.right = 'auto';
    try { hdr.setPointerCapture(e.pointerId); } catch (_) {}
  });
  hdr.addEventListener('pointermove', e => {
    if (!drag) return;
    card.style.left = (ox + (e.clientX - sx) / uiZoom) + 'px';
    card.style.top = (oy + (e.clientY - sy) / uiZoom) + 'px';
  });
  const end = e => { drag = false; try { hdr.releasePointerCapture(e.pointerId); } catch (_) {} };
  hdr.addEventListener('pointerup', end);
  hdr.addEventListener('pointercancel', end);
}
for (const id of ['chart-steer', 'chart-heading', 'chart-xte']) wireCardDrag(document.getElementById(id));
