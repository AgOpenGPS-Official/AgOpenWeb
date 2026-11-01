// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// Field Operations (fields and jobs), AgShare.

// ---- Field Operations — field/job lifecycle. Fly-out launches the
// Fields-and-Jobs chain panel (fields table + jobs + new-job form) and New Field.
// Reads ride the FieldOps frame; writes are field.* commands (host-driven via the real
// StartWorkSessionDialogViewModel); deletes confirm client-side.
const JOB_STATUS = [tr('In progress'), tr('Done'), tr('Abandoned')];
let fjSelField = null, fjSelJob = null;

// Field Operations fly-out: status pill + Close gating from the live scene/status.
function renderFieldOps() {
  const has = !!(scene && scene.hasField);
  const pill = document.getElementById('fo-current');
  if (has) {
    const fld = (scene && scene.fieldName) || '—';
    const job = statusBar && statusBar.jobName;
    pill.textContent = tr('Current: {name}', { name: fld + (job ? ' / ' + job : '') });
    pill.style.display = 'block';
  } else pill.style.display = 'none';
  document.getElementById('fo-close').classList.toggle('disabled', !has);
}
document.getElementById('fo-fields').addEventListener('pointerdown', e => { e.stopPropagation(); openFieldsAndJobs(); });
document.getElementById('fo-resumelast').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('field.resumeLast'); lnCloseAll(); });
document.getElementById('fo-resumejob').addEventListener('pointerdown', e => { e.stopPropagation(); openResumeJob(); });
// Drive In (AgOpenGPS FormJob "In Field"): one field within 0.5 km opens directly;
// 2+ come back as a Drive Pick frame (#109). The host broadcasts it, so only the
// browser that just pressed Drive In shows the list.
let driveInPressedAt = -1e9;
document.getElementById('fo-drivein').addEventListener('pointerdown', e => {
  e.stopPropagation(); driveInPressedAt = performance.now(); transport.send('field.driveIn'); lnCloseAll();
});
function showDrivePick(fields) {
  if (performance.now() - driveInPressedAt > 5000) return; // someone else's Drive In
  driveInPressedAt = -1e9;
  const list = document.getElementById('dp-list'); list.innerHTML = '';
  for (const f of fields) {
    const row = document.createElement('div');
    row.className = 'dp-row';
    row.innerHTML = '<span class="dp-name"></span><span class="dp-num"></span><span class="dp-num"></span>';
    row.querySelector('.dp-name').textContent = f.name;
    const nums = row.querySelectorAll('.dp-num');
    nums[0].textContent = fmtUnit(f.distanceKm * 1000, 'm', 0);
    nums[1].textContent = fmtUnit(f.areaHa, 'ha', 1);
    row.addEventListener('pointerdown', ev => {
      ev.preventDefault(); ev.stopPropagation();
      transport.send('field.driveInOpen|' + f.name); closeDialog();
    });
    list.appendChild(row);
  }
  openDialog('dlg-drivepick');
}
document.getElementById('dp-cancel').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); closeDialog(); });
document.getElementById('fo-close').addEventListener('pointerdown', e => { e.stopPropagation(); if (scene && scene.hasField) { transport.send('field.close'); lnCloseAll(); } });

// Fields-and-Jobs chain panel.
function openFieldsAndJobs() {
  if (fieldOps && !fjSelField)
    fjSelField = fieldOps.activeField || (fieldOps.fields[0] && fieldOps.fields[0].name) || null;
  lnOpen('fieldsandjobs', 'ln-fieldops', renderFieldsAndJobs);
}
document.getElementById('fj-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('fieldops', 'ln-fieldops', renderFieldOps); });
function fjJobsArr() { return fieldOps ? fieldOps.jobs.filter(j => j.fieldName === fjSelField) : []; }
function renderFieldsAndJobs() {
  const fl = document.getElementById('fj-fieldlist'); fl.innerHTML = '';
  for (const f of (fieldOps ? fieldOps.fields : [])) {
    const row = document.createElement('div');
    row.className = 'fj-frow' + (f.name === fjSelField ? ' sel' : '');
    row.innerHTML = '<span class="fj-fname"></span><span class="fj-fnum"></span><span class="fj-fnum"></span>';
    row.querySelector('.fj-fname').textContent = f.name;
    const nums = row.querySelectorAll('.fj-fnum');
    nums[0].textContent = f.hasDistance ? toDisplayUnit(f.distanceKm, 'km').toFixed(1) : '—';
    nums[1].textContent = toDisplayUnit(f.areaHa, 'ha').toFixed(1);
    row.addEventListener('pointerdown', ev => { ev.stopPropagation(); fjSelField = f.name; fjSelJob = null; renderFieldsAndJobs(); });
    fl.appendChild(row);
  }
  const sel = !!fjSelField;
  document.getElementById('fj-jobside').style.display = sel ? 'block' : 'none';
  document.getElementById('fj-empty').style.display = sel ? 'none' : 'block';
  if (sel) {
    const jl = document.getElementById('fj-joblist'); jl.innerHTML = '';
    for (const j of fjJobsArr()) {
      const row = document.createElement('div');
      row.className = 'fj-jrow' + (j.taskName === fjSelJob ? ' sel' : '');
      row.innerHTML = '<div class="fj-jtop"><span class="fj-jname"></span><span class="fj-jwt"></span><span class="fj-jst"></span></div><div class="fj-jsub"></div>';
      row.querySelector('.fj-jname').textContent = j.taskName;
      row.querySelector('.fj-jwt').textContent = j.workType;
      row.querySelector('.fj-jst').textContent = JOB_STATUS[j.status] || '';
      row.querySelector('.fj-jsub').textContent = tr('Last opened: {date}', { date: j.lastOpened });
      row.addEventListener('pointerdown', ev => { ev.stopPropagation(); fjSelJob = j.taskName; renderFieldsAndJobs(); });
      jl.appendChild(row);
    }
    const dl = document.getElementById('fj-wtlist'); dl.innerHTML = '';
    for (const w of (fieldOps ? fieldOps.workTypes : [])) { const o = document.createElement('option'); o.value = w; dl.appendChild(o); }
    const tn = document.getElementById('fj-taskname');
    if (!tn.value && document.activeElement !== tn) tn.value = isoDate();
  }
}
function isoDate() { const d = new Date(), p = n => String(n).padStart(2, '0'); return d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate()); }
function isoTime() { const d = new Date(), p = n => String(n).padStart(2, '0'); return p(d.getHours()) + '-' + p(d.getMinutes()); }
// New-job form helpers
document.getElementById('fj-uselast').addEventListener('pointerdown', e => { e.stopPropagation(); const j = fjJobsArr()[0]; if (j) document.getElementById('fj-notes').value = j.notes; });
document.getElementById('fj-date').addEventListener('pointerdown', e => { e.stopPropagation(); const tn = document.getElementById('fj-taskname'); tn.value = (tn.value ? tn.value + '_' : '') + isoDate(); });
document.getElementById('fj-time').addEventListener('pointerdown', e => { e.stopPropagation(); const tn = document.getElementById('fj-taskname'); tn.value = (tn.value ? tn.value + '_' : '') + isoTime(); });
// Footer actions
document.getElementById('fj-openonly').addEventListener('pointerdown', e => { e.stopPropagation(); if (fjSelField) { transport.send('field.openOnly|' + fjSelField); lnCloseAll(); } });
document.getElementById('fj-deletefield').addEventListener('pointerdown', e => {
  e.stopPropagation(); if (!fjSelField) return;
  showConfirm(tr('Delete Field'), tr("Delete field '{name}' and all its jobs?", { name: fjSelField }), () => { transport.send('field.deleteField|' + fjSelField); fjSelField = null; fjSelJob = null; });
});
document.getElementById('fj-resumejob').addEventListener('pointerdown', e => { e.stopPropagation(); if (fjSelField && fjSelJob) { transport.send('field.resumeJob|' + fjSelField + '\t' + fjSelJob); lnCloseAll(); } });
document.getElementById('fj-startjob').addEventListener('pointerdown', e => {
  e.stopPropagation(); if (!fjSelField) return;
  const wt = document.getElementById('fj-worktype').value.trim();
  const notes = document.getElementById('fj-notes').value;
  const task = document.getElementById('fj-taskname').value.trim();
  transport.send('field.startJob|' + [fjSelField, wt, notes, task].join('\t'));
  lnCloseAll();
});
document.getElementById('fj-deletejob').addEventListener('pointerdown', e => {
  e.stopPropagation(); if (!fjSelField || !fjSelJob) return;
  const job = fjSelJob;
  showConfirm(tr('Delete Job'), tr("Delete job '{job}' from field '{field}'?", { job, field: fjSelField }), () => { transport.send('field.deleteJob|' + fjSelField + '\t' + job); fjSelJob = null; });
});
// New Field chain panel (Creation column)
document.getElementById('fj-newfield').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('newfield', 'ln-fieldops', () => { document.getElementById('nf-name').value = ''; renderNewField(); }); });
document.getElementById('fj-fromexisting').addEventListener('pointerdown', e => { e.stopPropagation(); openFromExisting(); });
document.getElementById('fj-iso').addEventListener('pointerdown', e => { e.stopPropagation(); openImport('isoimport', 'iso-file', 'isoFiles'); });
document.getElementById('fj-kml').addEventListener('pointerdown', e => { e.stopPropagation(); openImport('kmlimport', 'kml-file', 'kmlFiles'); });
// Show the origin the field will be created at — the live GPS, or the host's
// no-fix fallback (40.7128, -74.0060) so the readout matches what gets written.
function renderNewField() {
  const hasFix = statusBar && (statusBar.lat || statusBar.lon);
  const lat = hasFix ? statusBar.lat : 40.7128, lon = hasFix ? statusBar.lon : -74.0060;
  document.getElementById('nf-pos').textContent = lat.toFixed(8) + ', ' + lon.toFixed(8);
  document.getElementById('nf-poshint').textContent = hasFix
    ? tr('The field is created at this position.')
    : tr('No GPS fix — a default origin will be used.');
}
document.getElementById('nf-back').addEventListener('pointerdown', e => { e.stopPropagation(); openFieldsAndJobs(); });
document.getElementById('nf-create').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const name = document.getElementById('nf-name').value.trim();
  if (!name) return;
  transport.send('field.new|' + name);
  fjSelField = name;
  openFieldsAndJobs(); // back to the list; the new field appears once the frame updates
});

// Helper: fill a <select> with options from a string array (preserving the choice).
function fillSelect(sel, items) {
  const prev = sel.value;
  sel.innerHTML = '';
  for (const it of items) { const o = document.createElement('option'); o.value = it; o.textContent = it; sel.appendChild(o); }
  if (items.includes(prev)) sel.value = prev;
}

// From Existing — clone a field (source + new name + copy toggles).
document.getElementById('fe-back').addEventListener('pointerdown', e => { e.stopPropagation(); openFieldsAndJobs(); });
for (const b of document.querySelectorAll('#fromexisting .cfg-tgl'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); b.classList.toggle('active'); });
function openFromExisting() {
  lnOpen('fromexisting', 'ln-fieldops', () => {
    fillSelect(document.getElementById('fe-source'), (fieldOps ? fieldOps.fields : []).map(f => f.name));
    document.getElementById('fe-name').value = '';
    for (const b of document.querySelectorAll('#fromexisting .cfg-tgl')) b.classList.add('active'); // all copy on by default
  });
}
document.getElementById('fe-create').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const src = document.getElementById('fe-source').value;
  const name = document.getElementById('fe-name').value.trim();
  if (!src || !name) return;
  const on = id => document.getElementById(id).classList.contains('active') ? '1' : '0';
  transport.send('field.fromExisting|' + [src, name, on('fe-flags'), on('fe-mapping'), on('fe-headland'), on('fe-lines')].join('\t'));
  fjSelField = name; openFieldsAndJobs();
});

// From ISO-XML / From KML — import a field from a file in the Import folder.
function openImport(panelId, selId, fileArr) {
  lnOpen(panelId, 'ln-fieldops', () => {
    fillSelect(document.getElementById(selId), fieldOps ? fieldOps[fileArr] : []);
    document.getElementById(panelId === 'isoimport' ? 'iso-name' : 'kml-name').value = '';
  });
}
document.getElementById('iso-back').addEventListener('pointerdown', e => { e.stopPropagation(); openFieldsAndJobs(); });
document.getElementById('kml-back').addEventListener('pointerdown', e => { e.stopPropagation(); openFieldsAndJobs(); });
document.getElementById('iso-create').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const file = document.getElementById('iso-file').value, name = document.getElementById('iso-name').value.trim();
  if (!file || !name) return;
  transport.send('field.fromIsoXml|' + file + '\t' + name); fjSelField = name; openFieldsAndJobs();
});
document.getElementById('kml-create').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const file = document.getElementById('kml-file').value, name = document.getElementById('kml-name').value.trim();
  if (!file || !name) return;
  transport.send('field.fromKml|' + file + '\t' + name); fjSelField = name; openFieldsAndJobs();
});

// Cross-field Resume Job picker (all jobs, recency order from the host).
function openResumeJob() { lnOpen('resumejob', 'ln-fieldops', renderResumeJob); }
document.getElementById('rj-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('fieldops', 'ln-fieldops', renderFieldOps); });
function renderResumeJob() {
  const list = document.getElementById('rj-list'); list.innerHTML = '';
  const jobs = fieldOps ? fieldOps.jobs : [];
  if (!jobs.length) { list.innerHTML = '<div class="fj-empty">No jobs yet.</div>'; return; }
  for (const j of jobs) {
    const row = document.createElement('div');
    row.className = 'fj-jrow';
    row.innerHTML = '<div class="fj-jtop"><span class="fj-jname"></span><span class="fj-jwt"></span><span class="fj-jst"></span></div><div class="fj-jsub"></div>';
    row.querySelector('.fj-jname').textContent = j.taskName;
    row.querySelector('.fj-jwt').textContent = j.fieldName;
    row.querySelector('.fj-jst').textContent = JOB_STATUS[j.status] || '';
    row.querySelector('.fj-jsub').textContent = tr('Last opened: {date}', { date: j.lastOpened });
    row.addEventListener('pointerdown', ev => { ev.stopPropagation(); transport.send('field.resumeJob|' + j.fieldName + '\t' + j.taskName); lnCloseAll(); });
    list.appendChild(row);
  }
}

// ---- AgShare cloud sync (Settings / Upload / Download) — chain sub-panels of Field
// Operations. Settings via config.set conn.agShare*; actions via agshare.* with results
// (status / cloud list) riding the AgShare frame. Launched from the fly-out's AgShare row.
document.getElementById('fo-agupload').addEventListener('pointerdown', e => { e.stopPropagation(); openAgUpload(); });
document.getElementById('fo-agdownload').addEventListener('pointerdown', e => { e.stopPropagation(); openAgDownload(); });
document.getElementById('fo-agapi').addEventListener('pointerdown', e => { e.stopPropagation(); openAgSettings(); });

// Settings
function openAgSettings() { lnOpen('agsettings', 'ln-fieldops', renderAgSettings); }
document.getElementById('ag-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('fieldops', 'ln-fieldops', renderFieldOps); });
function renderAgSettings() {
  if (!agShare) return;
  const sv = document.getElementById('ag-server'), kv = document.getElementById('ag-key');
  if (document.activeElement !== sv) sv.value = agShare.serverUrl || '';
  if (document.activeElement !== kv) kv.value = agShare.apiKey || '';
  document.getElementById('ag-enabled').classList.toggle('active', !!agShare.enabled);
  document.getElementById('ag-teststatus').textContent = agShare.status || '';
}
document.getElementById('ag-server').addEventListener('change', () => cfgSend('conn.agShareServer', document.getElementById('ag-server').value.trim()));
// The field holds the host's mask while a key is stored; sending the mask back keeps it (#397).
document.getElementById('ag-key').addEventListener('change', () => cfgSend('conn.agShareApiKey', document.getElementById('ag-key').value.trim()));
document.getElementById('ag-enabled').addEventListener('pointerdown', e => { e.stopPropagation(); cfgSend('conn.agShareEnabled', e.currentTarget.classList.contains('active') ? '0' : '1'); });
document.getElementById('ag-test').addEventListener('pointerdown', e => {
  e.stopPropagation();
  cfgSend('conn.agShareServer', document.getElementById('ag-server').value.trim());
  cfgSend('conn.agShareApiKey', document.getElementById('ag-key').value.trim());
  transport.send('agshare.test');
});

// Upload
let aguSel = new Set();
function openAgUpload() { aguSel = new Set(); lnOpen('agupload', 'ln-fieldops', renderAgUpload); }
document.getElementById('agu-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('fieldops', 'ln-fieldops', renderFieldOps); });
function renderAgUpload() {
  const list = document.getElementById('agu-list'); list.innerHTML = '';
  for (const f of (agShare ? agShare.localFields : [])) {
    const row = document.createElement('label'); row.className = 'agu-row';
    const cb = document.createElement('input'); cb.type = 'checkbox'; cb.checked = aguSel.has(f.name);
    cb.addEventListener('change', () => { if (cb.checked) aguSel.add(f.name); else aguSel.delete(f.name); });
    const nm = document.createElement('span'); nm.className = 'agu-name'; nm.textContent = f.name;
    const bd = document.createElement('span'); bd.className = 'agu-bd ' + (f.hasBoundary ? 'ok' : 'no'); bd.textContent = f.hasBoundary ? tr('Has boundary') : tr('No boundary');
    row.appendChild(cb); row.appendChild(nm); row.appendChild(bd); list.appendChild(row);
  }
  document.getElementById('agu-status').textContent = agShare ? (agShare.status || '') : '';
}
document.getElementById('agu-selall').addEventListener('pointerdown', e => { e.stopPropagation(); for (const f of (agShare ? agShare.localFields : [])) aguSel.add(f.name); renderAgUpload(); });
document.getElementById('agu-selnone').addEventListener('pointerdown', e => { e.stopPropagation(); aguSel.clear(); renderAgUpload(); });
document.getElementById('agu-public').addEventListener('pointerdown', e => { e.stopPropagation(); e.currentTarget.classList.toggle('active'); });
document.getElementById('agu-upload').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (!aguSel.size) return;
  const pub = document.getElementById('agu-public').classList.contains('active') ? '1' : '0';
  transport.send('agshare.upload|' + [pub, ...aguSel].join('\t'));
});

// Download
let agdSel = null;
function openAgDownload() { agdSel = null; lnOpen('agdownload', 'ln-fieldops', () => { transport.send('agshare.fetch'); renderAgDownload(); }); }
document.getElementById('agd-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('fieldops', 'ln-fieldops', renderFieldOps); });
function renderAgDownload() {
  const list = document.getElementById('agd-list'); list.innerHTML = '';
  const cf = agShare ? agShare.cloudFields : [];
  if (!cf.length) list.innerHTML = '<div class="fj-empty">No cloud fields (Refresh to load).</div>';
  for (const f of cf) {
    const row = document.createElement('div'); row.className = 'fj-jrow' + (f.id === agdSel ? ' sel' : '');
    row.innerHTML = '<div class="fj-jtop"><span class="fj-jname"></span><span class="fj-jwt"></span></div>';
    row.querySelector('.fj-jname').textContent = f.name;
    row.querySelector('.fj-jwt').textContent = fmtUnit(f.areaHa, 'ha');
    row.addEventListener('pointerdown', ev => { ev.stopPropagation(); agdSel = f.id; renderAgDownload(); });
    list.appendChild(row);
  }
  document.getElementById('agd-status').textContent = agShare ? (agShare.status || '') : '';
}
document.getElementById('agd-refresh').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('agshare.fetch'); });
document.getElementById('agd-download').addEventListener('pointerdown', e => { e.stopPropagation(); if (agdSel) transport.send('agshare.download|' + agdSel); });
document.getElementById('agd-force').addEventListener('pointerdown', e => { e.stopPropagation(); e.currentTarget.classList.toggle('active'); });
document.getElementById('agd-downloadall').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('agshare.downloadAll|' + (document.getElementById('agd-force').classList.contains('active') ? '1' : '0')); });
