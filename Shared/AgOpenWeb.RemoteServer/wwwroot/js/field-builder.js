// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// Field Builder: tracks, headland and tram tabs; KML boundary; offset fix.

// ---- Field Builder (Phase MT) — Tracks / Headland / Tram editor (NO_SCRIM: the map
// stays interactive so you draw/edit on it). Stage 1 = Tracks tab; reuses the existing
// AB-draw / create-from-boundary flows on the main map. ----
let fbTab = 'tracks';   // active FB tab
let fbSel = -1;         // selected track index in the Tracks tab
document.getElementById('ft-fieldbuilder').addEventListener('pointerdown', e => {
  e.stopPropagation(); if (!requireField()) return; fbTab = 'tracks'; fbSel = -1; lnOpen('fieldbuilder', 'ln-fieldtools', renderFieldBuilder);
});
document.getElementById('fieldbuilder').addEventListener('pointerdown', e => e.stopPropagation()); // panel taps don't pan the map (NO_SCRIM)
document.querySelector('#fieldbuilder .fb-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('fieldtools', 'ln-fieldtools'); });
function showFbTab(tab) {
  fbTab = tab;
  document.getElementById('fb-addtrack').hidden = true;
  document.getElementById('fb-addhl').hidden = true;
  document.getElementById('fb-tramedit').hidden = true;
  for (const t of document.querySelectorAll('#fieldbuilder .fb-tab')) t.classList.toggle('active', t.dataset.fbtab === tab);
  for (const p of document.querySelectorAll('#fieldbuilder .fb-pane[data-fbpane]')) p.hidden = (p.dataset.fbpane !== tab);
  if (tab === 'tracks') renderFbTracks();
  else if (tab === 'headland') renderFbHeadland();
  else if (tab === 'tram') renderFbTram();
}
function renderFieldBuilder() { showFbTab(fbTab); }
for (const t of document.querySelectorAll('#fieldbuilder .fb-tab'))
  t.addEventListener('pointerdown', e => { e.stopPropagation(); showFbTab(t.dataset.fbtab); });
function renderFbTracks() {
  const list = document.getElementById('fb-tracklist');
  const tl = (scene && scene.trackList) || [];
  if (fbSel >= tl.length) fbSel = -1;
  if (!tl.length) { list.innerHTML = '<div class="trk-empty">No tracks</div>'; return; }
  list.innerHTML = '';
  tl.forEach((t, i) => {
    const row = document.createElement('div');
    row.className = 'fb-trkrow' + (t.active ? ' active' : '') + (i === fbSel ? ' sel' : '');
    row.innerHTML = '<span class="fb-dot"></span><span class="fb-tname"></span>';
    row.querySelector('.fb-tname').textContent = t.name;
    row.addEventListener('pointerdown', ev => {
      ev.stopPropagation(); fbSel = i;
      if (iHoldControl) transport.send('track.select|' + t.index); // activate (highlights on map)
      renderFbTracks();
    });
    list.appendChild(row);
  });
}
// Add-track sub-view.
document.getElementById('fb-trk-add').addEventListener('pointerdown', e => {
  e.stopPropagation();
  for (const p of document.querySelectorAll('#fieldbuilder .fb-pane[data-fbpane]')) p.hidden = true;
  document.getElementById('fb-addtrack').hidden = false;
});
document.getElementById('fb-add-back').addEventListener('pointerdown', e => { e.stopPropagation(); showFbTab('tracks'); });
for (const b of document.querySelectorAll('#fb-addtrack .fb-addbtn'))
  b.addEventListener('pointerdown', e => {
    e.stopPropagation();
    const m = b.dataset.fbadd;
    showFbTab('tracks');
    if (m === 'ab') startDrawTrack('straight');        // map-tap draw (FB stays open, NO_SCRIM)
    else if (m === 'curve') startDrawTrack('curve');
    else if (m === 'aplus') { transport.send('track.refSide|right'); transport.send('track.aPlus'); }
    else if (m === 'bedge') transport.send('track.createFromBoundary');
    else if (m === 'bcurve') transport.send('track.boundaryCurve');
    else if (m === 'ball') transport.send('track.allEdges');
  });
// Track actions (Edit = stage 4).
document.getElementById('fb-trk-edit').addEventListener('pointerdown', e => { e.stopPropagation(); startTrackEdit(); });
document.getElementById('fb-trk-delete').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const tl = scene && scene.trackList; if (fbSel < 0 || !tl || !tl[fbSel] || !iHoldControl) return;
  const t = tl[fbSel];
  // Asks first, as the Tracks manager does: a track can't be brought back.
  showConfirm(tr('Delete Track'), tr("Delete '{name}'? This cannot be undone.", { name: t.name }),
    () => { transport.send('track.delete|' + t.index); fbSel = -1; });
});
document.getElementById('fb-trk-deleteall').addEventListener('pointerdown', e => {
  e.stopPropagation();
  showConfirm(tr('Delete All Tracks'), tr('Delete all tracks? This cannot be undone.'),
    () => { if (iHoldControl) transport.send('track.deleteAll'); fbSel = -1; });
});
document.getElementById('fb-trk-rename').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const tl = scene && scene.trackList; if (fbSel < 0 || !tl || !tl[fbSel]) return;
  const t = tl[fbSel];
  renderFbTracks();
  const row = document.querySelectorAll('#fb-tracklist .fb-trkrow')[fbSel]; if (!row) return;
  const nameEl = row.querySelector('.fb-tname');
  const input = document.createElement('input'); input.className = 'flg-nameedit'; input.value = t.name;
  nameEl.replaceWith(input); input.focus(); input.select();
  const commit = () => { const v = input.value.trim(); if (v && v !== t.name) transport.send('track.rename|' + t.index + ',' + v); renderFbTracks(); };
  input.addEventListener('keydown', ev => { ev.stopPropagation(); if (ev.key === 'Enter') commit(); else if (ev.key === 'Escape') renderFbTracks(); });
  input.addEventListener('blur', commit);
});
// ---- Field Builder Headland tab (stage 2) — segment list + build ----------------------
// Line/Curve = tap two boundary points
// on the main map (host snaps + builds the inward offset + the headland polygon); Whole
// Boundary = the entire boundary offset inward. The host runs ALL geometry — the client
// only captures the two taps + the offset and ships them (no headland math in JS).
let fbHlSel = -1;        // selected segment index in the Headland tab
function fbDefaultOffset() {  // tool width × 2; 0 = no tool width to go by
  const tw = toolWidthM();
  return tw > 0 ? +(tw * 2).toFixed(1) : 0;
}
function renderFbHeadland() {
  const list = document.getElementById('fb-hllist');
  const hs = (scene && scene.headlandSegs) || [];
  if (fbHlSel >= hs.length) fbHlSel = -1;
  const offrow = document.getElementById('fb-hl-offrow');
  if (!hs.length) { list.innerHTML = '<div class="trk-empty">No headland yet. Add a line along each edge (or Whole Boundary). Where the lines cross they enclose the headland.</div>'; offrow.hidden = true; return; }
  list.innerHTML = '';
  hs.forEach((s, i) => {
    const row = document.createElement('div');
    row.className = 'fb-hlrow' + (s.effective ? '' : ' noeffect') + (i === fbHlSel ? ' sel' : '');
    row.innerHTML = '<span class="fb-dot"></span><span class="fb-hlname"></span><span class="fb-hlmeta"></span>';
    row.querySelector('.fb-hlname').textContent = s.name;
    row.querySelector('.fb-hlmeta').textContent = s.type + ' · ' + fmtUnit(+s.offset, 'm', 1);
    row.addEventListener('pointerdown', ev => { ev.stopPropagation(); fbHlSel = i; renderFbHeadland(); });
    list.appendChild(row);
  });
  // Offset editor for the selected segment (re-runs the build on change).
  if (fbHlSel >= 0 && hs[fbHlSel]) {
    offrow.hidden = false;
    const off = document.getElementById('fb-hl-off');
    if (document.activeElement !== off) writeUnitInput(off, +hs[fbHlSel].offset);
  } else offrow.hidden = true;
}
document.getElementById('fb-hl-off').addEventListener('change', e => {
  e.stopPropagation();
  const hs = scene && scene.headlandSegs; if (fbHlSel < 0 || !hs || !hs[fbHlSel]) return;
  const v = readUnitInput(e.target);   // display → metres before it reaches the host
  if (Number.isFinite(v) && v > 0) transport.send('headland.setOffset|' + hs[fbHlSel].index + ',' + v);
});
// Inset = N tool widths (dropdown, mirrors AgOpen's cboxToolWidths) → fills the metre box;
// editing the metre box directly flips the dropdown to Custom.
const fbTwSel = document.getElementById('fb-hl-tw');
const fbNewOff = document.getElementById('fb-hl-newoff');
function fbApplyTw() {
  const n = parseInt(fbTwSel.value), tw = toolWidthM();
  if (!(n > 0)) return;
  if (tw > 0) writeUnitInput(fbNewOff, n * tw);
  else fbNewOff.value = '';   // no tool width: the operator enters the inset
}
fbTwSel.addEventListener('change', e => { e.stopPropagation(); fbApplyTw(); });
fbNewOff.addEventListener('input', e => { e.stopPropagation(); fbTwSel.value = '0'; });
document.getElementById('fb-hl-add').addEventListener('pointerdown', e => {
  e.stopPropagation();
  fbTwSel.value = '2'; fbApplyTw();   // default 2 tool widths
  for (const p of document.querySelectorAll('#fieldbuilder .fb-pane[data-fbpane]')) p.hidden = true;
  document.getElementById('fb-addhl').hidden = false;
});
document.getElementById('fb-hl-addback').addEventListener('pointerdown', e => { e.stopPropagation(); showFbTab('headland'); });
for (const b of document.querySelectorAll('#fb-addhl .fb-addbtn'))
  b.addEventListener('pointerdown', e => {
    e.stopPropagation();
    const off = readUnitInput(document.getElementById('fb-hl-newoff'));
    const offset = (Number.isFinite(off) && off > 0) ? off : fbDefaultOffset();
    if (!(offset > 0)) { showToast(tr('Tool width not set: enter the headland width')); return; }
    const m = b.dataset.fbhl;
    showFbTab('headland');
    if (m === 'whole') transport.send('headland.wholeBoundary|' + offset);
    else startHeadlandDraw(m, offset);   // 'line' | 'curve' — map-tap two boundary points
  });
document.getElementById('fb-hl-delete').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const hs = scene && scene.headlandSegs; if (fbHlSel < 0 || !hs || !hs[fbHlSel]) return;
  transport.send('headland.delete|' + hs[fbHlSel].index); fbHlSel = -1;
});
document.getElementById('fb-hl-deleteall').addEventListener('pointerdown', e => {
  e.stopPropagation();
  showConfirm(tr('Delete All Headland'), tr('Delete all headland segments? The headland reverts to the boundary.'),
    () => { transport.send('headland.deleteAll'); fbHlSel = -1; });
});
document.getElementById('fb-hl-rename').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const hs = scene && scene.headlandSegs; if (fbHlSel < 0 || !hs || !hs[fbHlSel]) return;
  const s = hs[fbHlSel];
  renderFbHeadland();
  const row = document.querySelectorAll('#fb-hllist .fb-hlrow')[fbHlSel]; if (!row) return;
  const nameEl = row.querySelector('.fb-hlname');
  const input = document.createElement('input'); input.className = 'flg-nameedit'; input.value = s.name;
  nameEl.replaceWith(input); input.focus(); input.select();
  const commit = () => { const v = input.value.trim(); if (v && v !== s.name) transport.send('headland.rename|' + s.index + ',' + v); renderFbHeadland(); };
  input.addEventListener('keydown', ev => { ev.stopPropagation(); if (ev.key === 'Enter') commit(); else if (ev.key === 'Escape') renderFbHeadland(); });
  input.addEventListener('blur', commit);
});
// ---- Field Builder Tram tab (stage 3) — system list + per-system editor --------------
// Systems live in ConfigStore.Tram.Systems (host SoT); the browser sends field edits and
// the host regenerates the lines + persists.
let fbTramSel = -1;
let fbTramPendingEdit = false; // open the editor on the system we just added (native parity)
function openTramEditor() {
  for (const p of document.querySelectorAll('#fieldbuilder .fb-pane[data-fbpane]')) p.hidden = true;
  document.getElementById('fb-tramedit').hidden = false;
  populateTramEdit();
}
function renderFbTram() {
  const list = document.getElementById('fb-tramlist');
  const ts = (scene && scene.tramSystems) || [];
  if (fbTramPendingEdit && ts.length) { fbTramPendingEdit = false; fbTramSel = ts.length - 1; openTramEditor(); return; }
  if (fbTramSel >= ts.length) fbTramSel = -1;
  if (!ts.length) { list.innerHTML = '<div class="trk-empty">No tram systems. Add one referencing a track or the boundary.</div>'; return; }
  list.innerHTML = '';
  ts.forEach((s, i) => {
    const row = document.createElement('div');
    row.className = 'fb-hlrow' + (s.enabled ? '' : ' noeffect') + (i === fbTramSel ? ' sel' : '');
    row.innerHTML = '<span class="fb-dot"></span><span class="fb-hlname"></span><span class="fb-hlmeta"></span>';
    row.querySelector('.fb-hlname').textContent = s.name;
    row.querySelector('.fb-hlmeta').textContent = fmtUnit(s.width, 'm', 1) + ' · ' + (s.passCount ? tr('{n} pass', { n: s.passCount }) : tr('all', {}));
    row.addEventListener('pointerdown', ev => { ev.stopPropagation(); fbTramSel = i; renderFbTram(); });
    list.appendChild(row);
  });
}
function curTram() { const ts = scene && scene.tramSystems; return (fbTramSel >= 0 && ts && ts[fbTramSel]) ? ts[fbTramSel] : null; }
function tramSet(field, value) { const s = curTram(); if (s) transport.send('tram.set|' + s.index + ',' + field + ',' + value); }
document.getElementById('fb-tram-add').addEventListener('pointerdown', e => { e.stopPropagation(); fbTramPendingEdit = true; transport.send('tram.add'); });
document.getElementById('fb-tram-delete').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const s = curTram(); if (!s) return;
  showConfirm(tr('Delete Tram System'), tr('Delete "{name}"?', { name: s.name }), () => { transport.send('tram.delete|' + s.index); fbTramSel = -1; });
});
document.getElementById('fb-tram-edit').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (curTram()) openTramEditor();
});
document.getElementById('fb-tram-back').addEventListener('pointerdown', e => { e.stopPropagation(); showFbTab('tram'); });
document.getElementById('fb-tram-done').addEventListener('pointerdown', e => { e.stopPropagation(); showFbTab('tram'); });
function populateTramEdit() {
  const s = curTram(); if (!s) { showFbTab('tram'); return; }
  document.getElementById('fb-tram-title').textContent = tr('Edit: {name}', { name: s.name });
  const en = document.getElementById('fb-tram-en'); en.classList.toggle('active', s.enabled); en.textContent = s.enabled ? tr('On') : tr('Off');
  const ref = document.getElementById('fb-tram-ref');
  const opts = [tr('(Boundary)')];
  for (const t of (scene.trackList || [])) if (t.type !== 'Path' && t.type !== 'Contour') opts.push(t.name);
  ref.innerHTML = opts.map(o => { const e = document.createElement('option'); e.textContent = o; return e.outerHTML; }).join('');
  ref.value = s.refLabel;
  const wEl = document.getElementById('fb-tram-w'); if (document.activeElement !== wEl) writeUnitInput(wEl, s.width);
  const offEl = document.getElementById('fb-tram-off'); if (document.activeElement !== offEl) writeUnitInput(offEl, s.offset);
  // A boundary system is rings around the boundary: there's no "all", 0 means 1 (#112).
  const pEl = document.getElementById('fb-tram-passes');
  pEl.min = s.isBoundary ? 1 : 0;
  document.getElementById('fb-tram-passes-lbl').textContent = s.isBoundary ? tr('Passes') : tr('Passes (0 = all)');
  if (document.activeElement !== pEl) pEl.value = s.isBoundary ? Math.max(1, s.passCount) : s.passCount;
  for (const b of document.querySelectorAll('#fb-tramedit [data-tmode]')) b.classList.toggle('sel', +b.dataset.tmode === s.mode);
  for (const b of document.querySelectorAll('#fb-tramedit [data-tdir]')) b.classList.toggle('sel', +b.dataset.tdir === s.direction);
  document.getElementById('fb-tram-offrow').hidden = s.isBoundary;   // offset/direction don't apply
  document.getElementById('fb-tram-dirlbl').hidden = s.isBoundary;   // to boundary-referenced systems
  document.getElementById('fb-tram-dir').hidden = s.isBoundary;
}
document.getElementById('fb-tram-en').addEventListener('pointerdown', e => { e.stopPropagation(); const s = curTram(); if (s) tramSet('enabled', s.enabled ? '0' : '1'); });
document.getElementById('fb-tram-ref').addEventListener('change', e => { e.stopPropagation(); tramSet('ref', e.target.value); });
document.getElementById('fb-tram-w').addEventListener('change', e => { e.stopPropagation(); const v = readUnitInput(e.target); if (v > 0) tramSet('width', v); });
document.getElementById('fb-tram-off').addEventListener('change', e => { e.stopPropagation(); const v = readUnitInput(e.target); if (Number.isFinite(v)) tramSet('offset', v); });
document.getElementById('fb-tram-passes').addEventListener('change', e => { e.stopPropagation(); const v = parseInt(e.target.value); const s = curTram(); if (Number.isFinite(v)) tramSet('passes', Math.max(s && s.isBoundary ? 1 : 0, v)); });
for (const b of document.querySelectorAll('#fb-tramedit [data-tmode]')) b.addEventListener('pointerdown', e => { e.stopPropagation(); tramSet('mode', b.dataset.tmode); });
for (const b of document.querySelectorAll('#fb-tramedit [data-tdir]')) b.addEventListener('pointerdown', e => { e.stopPropagation(); tramSet('dir', b.dataset.tdir); });
document.getElementById('bm-delete').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (!boundary || boundary.selectedIndex < 0) return;
  const it = boundary.items.find(i => i.index === boundary.selectedIndex);
  showConfirm(tr('Delete Boundary'), tr('Delete the {type} boundary? This cannot be undone.', { type: it ? it.boundaryType : tr('selected', {}) }),
    () => transport.send('boundary.delete'));
});
document.getElementById('bm-buildtracks').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('boundary.buildFromTracks'); });
// The aerial covering the field's boundary, however it was made (driven, KML, AgShare), into
// the field background; replaces any it has. Needs the internet (#500).
document.getElementById('bm-imagery').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (!imageryImg) { transport.send('boundary.captureImagery'); return; }
  showConfirm(tr('Get Aerial Imagery'), tr('Replace the field\'s aerial imagery with a new one for its boundary?'),
    () => transport.send('boundary.captureImagery'));
});
document.getElementById('bm-drawmap').addEventListener('pointerdown', e => { e.stopPropagation(); startSatBoundary(); });
// Import-KML boundary picker: lists KML/KMZ from the Import folder (FieldOps frame),
// tapping one imports it as the field boundary (host parses + imports, replacing the outer).
document.getElementById('bm-importkml').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('kmlboundary', 'ln-fieldtools', renderKmlBoundary); });
document.getElementById('kb-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('boundarymenu', 'ln-fieldtools', renderBoundaryMenu); });
function renderKmlBoundary() {
  const list = document.getElementById('kb-list'); list.innerHTML = '';
  const files = (fieldOps && fieldOps.kmlFiles) || [];
  if (!files.length) { list.innerHTML = '<div class="trk-empty">No KML/KMZ files in the Import folder.</div>'; return; }
  for (const f of files) {
    const row = document.createElement('div'); row.className = 'fb-trkrow';
    row.innerHTML = '<span class="fb-dot"></span><span class="fb-tname"></span>';
    row.querySelector('.fb-tname').textContent = f;
    row.addEventListener('pointerdown', ev => {
      ev.stopPropagation();
      showConfirm(tr('Import KML Boundary'), tr('Import "{file}" as the field boundary? This replaces the current outer boundary.', { file: f }),
        () => { transport.send('boundary.importKmlFile|' + f); lnCloseAll(); });
    });
    list.appendChild(row);
  }
}
document.getElementById('bm-drivearound').addEventListener('pointerdown', e => {
  e.stopPropagation(); transport.send('boundary.driveAround'); lnOpen('boundaryplayer', 'ln-fieldtools', renderBoundaryPlayer);
});
document.getElementById('bm-driveinner').addEventListener('pointerdown', e => {
  e.stopPropagation(); transport.send('boundary.driveAroundInner'); lnOpen('boundaryplayer', 'ln-fieldtools', renderBoundaryPlayer);
});
document.getElementById('bm-accept').addEventListener('pointerdown', e => { e.stopPropagation(); lnCloseAll(); }); // edits are already saved
// Boundary player.
document.getElementById('bp-back').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('boundary.refresh'); lnOpen('boundarymenu', 'ln-fieldtools', renderBoundaryMenu); });
document.getElementById('bp-offset').addEventListener('change', e => { e.stopPropagation(); const v = readUnitInput(e.target); if (Number.isFinite(v)) transport.send('boundary.setOffset|' + v); });
document.getElementById('bp-clear').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('boundary.clear'); });
document.getElementById('bp-section').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('boundary.toggleSectionControl'); });
document.getElementById('bp-undo').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('boundary.undo'); });
document.getElementById('bp-add').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('boundary.addPoint'); });
document.getElementById('bp-leftright').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('boundary.toggleLeftRight'); });
document.getElementById('bp-antennatool').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('boundary.toggleAntennaTool'); });
document.getElementById('bp-stop').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('boundary.stop'); transport.send('boundary.refresh'); lnOpen('boundarymenu', 'ln-fieldtools', renderBoundaryMenu); });
document.getElementById('bp-record').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('boundary.toggleRecording'); });
// Offset Fix D-pad (argless commands).
for (const b of document.querySelectorAll('#offsetfix .of-btn'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); transport.send(b.dataset.cmd); });
// Manual Easting/Northing entry → absolute set (offset.set|E,N).
function sendOffsetSet() {
  const ns = readUnitInput(document.getElementById('of-ns-in'));
  const ew = readUnitInput(document.getElementById('of-ew-in'));
  if (Number.isFinite(ns) && Number.isFinite(ew)) transport.send('offset.set|' + ew + ',' + ns);
}
for (const id of ['of-ns-in', 'of-ew-in'])
  document.getElementById(id).addEventListener('change', e => { e.stopPropagation(); sendOffsetSet(); });
for (const b of document.querySelectorAll('.ln-back'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('vehtoolhub', 'ln-vehicle', refreshHub); });
// Standard header close (X) → close the chain to the map. Tagged .ln-closex so the
// NTRIP chain Back/X buttons (which need parent-aware nav) keep their own handlers.
for (const b of document.querySelectorAll('.ln-closex'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); lnCloseAll(); });
