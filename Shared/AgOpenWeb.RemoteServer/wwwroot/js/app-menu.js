// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// The application menu: settings and tools, language, About, bug report.

// ---- File / Application Menu — settings/tools launcher. Reads ride the AppInfo
// frame + config/status; writes are config.set (App Settings) + app.* (language/reset/
// hotkeys/bug-report). Sub-panels are chain panels (replace the menu; Back reopens it).
document.getElementById('ln-filemenu').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const anyOpen = ['filemenu', 'appsettings', 'language', 'viewsettings', 'logviewer', 'hotkeys', 'help', 'about', 'bugreport'].some(id => document.getElementById(id).classList.contains('open'));
  if (anyOpen) lnCloseAll(); else lnOpen('filemenu', 'ln-filemenu');
});
for (const b of document.querySelectorAll('.fm-back'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('filemenu', 'ln-filemenu'); });
document.getElementById('fm-language').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('language', 'ln-filemenu', renderLanguage); });
document.getElementById('fm-reset').addEventListener('pointerdown', e => { e.stopPropagation(); showConfirm(tr('Reset All Settings'), tr('Reset all settings to their defaults? This cannot be undone.'), () => transport.send('app.resetSettings')); });
document.getElementById('fm-appsettings').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('appsettings', 'ln-filemenu', renderAppSettings); });
document.getElementById('fm-viewsettings').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('viewsettings', 'ln-filemenu', renderViewSettings); });
document.getElementById('fm-logviewer').addEventListener('pointerdown', e => { e.stopPropagation(); logViewerParent = 'filemenu'; lnOpen('logviewer', 'ln-filemenu', renderLogViewer); });
document.getElementById('fm-hotkeys').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('hotkeys', 'ln-filemenu', renderHotkeys); });
document.getElementById('fm-simulator').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('sim.togglePanel'); lnCloseAll(); });
document.getElementById('fm-help').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('help', 'ln-filemenu'); });
document.getElementById('fm-about').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('about', 'ln-filemenu', renderAbout); });
document.getElementById('fm-bugreport').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('bugreport', 'ln-filemenu', renderBugReport); });

// App Settings: units (status) + device toggles (config.display) + app directories (appInfo).
function renderAppSettings() {
  const metric = isMetric();
  document.getElementById('as-metric').classList.toggle('active', metric);
  document.getElementById('as-imperial').classList.toggle('active', !metric);
  const d = config && config.display;
  if (d) for (const b of document.querySelectorAll('#appsettings .as-tgl')) b.classList.toggle('active', !!d[b.dataset.key.split('.')[1]]);
  const dl = document.getElementById('as-dirs'); dl.innerHTML = '';
  for (const dir of (appInfo ? appInfo.directories : [])) {
    const row = document.createElement('div'); row.className = 'as-dir';
    row.innerHTML = '<div class="as-dirname"></div><div class="as-dirpath"></div>';
    row.querySelector('.as-dirname').textContent = dir.exists ? dir.name : tr('{name} (missing)', { name: dir.name });
    row.querySelector('.as-dirpath').textContent = dir.path;
    dl.appendChild(row);
  }
}
for (const b of document.querySelectorAll('#appsettings .ln-segbtn[data-units]'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('config.set|units:' + b.dataset.units); });
for (const b of document.querySelectorAll('#appsettings .as-tgl'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); cfgSend(b.dataset.key, b.classList.contains('active') ? '0' : '1'); });

// Language picker
function renderLanguage() {
  const list = document.getElementById('lang-list'); list.innerHTML = '';
  const cur = appInfo ? appInfo.currentLanguage : 'en';
  for (const l of (appInfo ? appInfo.languages : [])) {
    const b = document.createElement('button'); b.className = 'lang-btn' + (l.code === cur ? ' active' : ''); b.textContent = l.name;
    b.addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('app.setLanguage|' + l.code); });
    list.appendChild(b);
  }
}

// View All Settings — read-only tree rendered from the config frame the client already has.
function renderViewSettings() {
  const box = document.getElementById('vs-tree'); box.innerHTML = '';
  if (!config) { box.textContent = tr('Loading…'); return; }
  const groups = { Vehicle: config.vehicle, GPS: config.gps, Roll: config.roll, Tool: config.tool, 'U-Turn': config.uturn, Tram: config.tram, Machine: config.machine, Display: config.display, AutoSteer: config.autosteer };
  for (const name in groups) {
    const obj = groups[name]; if (!obj) continue;
    const sec = document.createElement('div'); sec.className = 'vs-section';
    const h = document.createElement('div'); h.className = 'vs-group'; h.textContent = name; sec.appendChild(h);
    // Nested objects and arrays (sections, pins, …) are flattened into path keys
    // ("sections[0].width"); a list of plain values stays on one row (#112).
    const addRow = (k, v) => {
      const r = document.createElement('div'); r.className = 'vs-row';
      r.innerHTML = '<span class="vs-k"></span><span class="vs-v"></span>';
      r.querySelector('.vs-k').textContent = k; r.querySelector('.vs-v').textContent = String(v);
      sec.appendChild(r);
    };
    const walk = (prefix, v) => {
      if (v === null || typeof v !== 'object') { addRow(prefix, v); return; }
      if (Array.isArray(v)) {
        if (v.every(x => x === null || typeof x !== 'object')) { addRow(prefix, v.join(', ')); return; }
        v.forEach((x, i) => walk(prefix + '[' + i + ']', x));
        return;
      }
      for (const k in v) walk(prefix ? prefix + '.' + k : k, v[k]);
    };
    walk('', obj);
    box.appendChild(sec);
  }
}

// Log Viewer — filter + list (from AppInfo).
let logFilter = 0;
const LOG_LVL = ['Trace', 'Debug', 'Info', 'Warn', 'Error', 'Crit'];
function renderLogViewer() {
  const list = document.getElementById('log-list'); list.innerHTML = '';
  for (const e of (appInfo ? appInfo.logs : [])) {
    if (e.level < logFilter) continue;
    const r = document.createElement('div'); r.className = 'log-row lvl' + e.level;
    r.innerHTML = '<span class="log-t"></span><span class="log-l"></span><span class="log-m"></span>';
    r.querySelector('.log-t').textContent = e.time;
    r.querySelector('.log-l').textContent = LOG_LVL[e.level] || '';
    r.querySelector('.log-m').textContent = e.message;
    list.appendChild(r);
  }
  list.scrollTop = list.scrollHeight;
}
for (const b of document.querySelectorAll('#logviewer .log-filt'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); logFilter = parseInt(b.dataset.lvl); for (const x of document.querySelectorAll('#logviewer .log-filt')) x.classList.toggle('active', x === b); renderLogViewer(); });

// Hotkeys — list + click-to-capture.
let hkCapture = null;
function renderHotkeys() {
  const list = document.getElementById('hk-list'); list.innerHTML = '';
  for (const hk of (appInfo ? appInfo.hotkeys : [])) {
    const r = document.createElement('div'); r.className = 'hk-row';
    r.innerHTML = '<span class="hk-label"></span><button class="hk-key"></button>';
    r.querySelector('.hk-label').textContent = hk.label;
    const kb = r.querySelector('.hk-key');
    kb.textContent = hkCapture === hk.action ? tr('Press a key…') : (hk.key || '—');
    if (hkCapture === hk.action) kb.classList.add('capturing');
    kb.addEventListener('pointerdown', ev => { ev.stopPropagation(); hkCapture = hk.action; renderHotkeys(); });
    list.appendChild(r);
  }
}
window.addEventListener('keydown', e => {
  if (!hkCapture || !document.getElementById('hotkeys').classList.contains('open')) return;
  e.preventDefault();
  if (e.key === 'Escape') { hkCapture = null; renderHotkeys(); return; } // cancel, don't bind Escape (#111)
  const key = e.key.length === 1 ? e.key.toUpperCase() : e.key;
  transport.send('app.setHotkey|' + hkCapture + ':' + key);
  hkCapture = null;
});
document.getElementById('hk-reset').addEventListener('pointerdown', e => { e.stopPropagation(); showConfirm(tr('Reset Hotkeys'), tr('Reset all hotkeys to their defaults?'), () => transport.send('app.resetHotkeys')); });

// Help — external links open in a new tab.
// External links. Inside the desktop launcher (Photino) window.open can't leave the web view,
// so the page asks its host to open the system browser over Photino's message channel.
function openExternal(url) {
  if (window.external && typeof window.external.sendMessage === 'function') window.external.sendMessage('open|' + url);
  else window.open(url, '_blank');
}
for (const b of document.querySelectorAll('#help .help-link'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); openExternal(b.dataset.url); });

// About
function renderAbout() {
  document.getElementById('ab-version').textContent = appInfo ? tr('Version {version}', { version: appInfo.version }) : '';
  document.getElementById('ab-git').textContent = appInfo ? appInfo.gitHash : '';
}

// Bug Report
// The report is a zip on the host. Download fetches it through the system browser, which saves
// it to this device's Downloads: inside an app shell the web view itself can't save a file.
let brFile = null;
function renderBugReport() {
  const s = appInfo ? (appInfo.bugReportStatus || '') : '';
  document.getElementById('br-status').textContent = s;
  const m = /(bugreport_[^\/\\]+\.zip)$/.exec(s);
  brFile = m ? m[1] : null;
  for (const id of ['br-sendbox', 'br-download'])
    document.getElementById(id).style.display = brFile ? '' : 'none';
  // Send Report state, as BugReportRemote codes it: sending, queued, sent|n|url, accepted, rejected|msg.
  const ss = appInfo ? (appInfo.bugReportSendStatus || '') : '';
  const bar = ss.indexOf('|'), kind = bar < 0 ? ss : ss.slice(0, bar), rest = bar < 0 ? '' : ss.slice(bar + 1);
  const issueUrl = kind === 'sent' ? rest.slice(rest.indexOf('|') + 1) : '';
  document.getElementById('br-sendstatus').textContent =
    kind === 'sending' ? tr('Sending…')
    : kind === 'queued' ? tr('No connection: the report sends by itself when the device is back online.')
    : kind === 'sent' ? tr('Sent as issue #{n}. Thank you!', { n: rest.split('|')[0] })
    : kind === 'accepted' ? tr('Sent. Thank you! The GitHub issue opens shortly.')
    : kind === 'rejected' ? tr('Not sent: {reason}', { reason: rest })
    : '';
  brIssueUrl = issueUrl || null;
  document.getElementById('br-openissue').style.display = brIssueUrl ? '' : 'none';
  brSendBusy = ['sending', 'queued', 'sent', 'accepted'].includes(kind);
  if (brFile !== brFilesFor) { brFiles = []; brFilesFor = brFile; renderBrFiles(); }
  document.getElementById('br-attach').disabled = brSendBusy || brAttaching;
  brUpdateSend();
}
// Screenshots and files: each one is posted to the host as it is picked and goes into the
// dump under attachments/ (BugReportAttachments). Only the screen holding control may add.
let brFiles = [], brFilesFor = null, brAttaching = false;
const brSize = n => n >= 1048576 ? (n / 1048576).toFixed(1) + ' MB' : Math.max(1, Math.round(n / 1024)) + ' KB';
function renderBrFiles() {
  const box = document.getElementById('br-files'); box.innerHTML = '';
  for (const f of brFiles) {
    const row = document.createElement('div'); row.className = 'br-filerow';
    const nm = document.createElement('span'); nm.className = 'br-fname'; nm.textContent = f.name; nm.translate = false;
    const sz = document.createElement('span'); sz.className = 'br-fsize'; sz.textContent = brSize(f.size);
    const x = document.createElement('button'); x.className = 'ln-x'; x.textContent = '✕'; x.title = tr('Remove');
    x.disabled = brSendBusy;
    x.addEventListener('pointerdown', e => { e.stopPropagation(); brChangeFiles('detach', f.name, new Uint8Array(0)); });
    row.append(nm, sz, x); box.appendChild(row);
  }
}
async function brChangeFiles(op, name, body) {
  const st = document.getElementById('br-attachstatus');
  const q = new URLSearchParams({ zip: brFile, name, client: myClientId || '' });
  let res, reply = null;
  try {
    res = await fetch('/bugreports/' + op + '?' + q, { method: 'POST', body });
    if (res.ok) reply = await res.json();
  } catch (_) { st.textContent = tr('Could not add {name}', { name }); return false; }
  if (res.status === 403) { st.textContent = tr('View only: another screen has control'); return false; }
  if (res.status === 413) { st.textContent = tr('{name} is too large (20 MB at most)', { name }); return false; }
  if (!reply) { st.textContent = tr('Could not add {name}', { name }); return false; }
  const why = {
    TooLarge: tr('{name} is too large (20 MB at most)', { name }),
    TotalTooLarge: tr('Attachments are limited to 25 MB in all'),
    TooMany: tr('At most 10 attachments'),
    Empty: tr('{name} is empty', { name }),
    Busy: tr('The report is being sent; try again in a moment'),
  }[reply.refusal];
  if (reply.refusal !== 'Busy') { brFiles = reply.files; renderBrFiles(); }
  st.textContent = why || tr('Like the rest of the report, attachments go only to the maintainers.');
  return !why;
}
document.getElementById('br-attach').addEventListener('click', e => {
  e.stopPropagation();
  if (brFile && !brSendBusy && !brAttaching) document.getElementById('br-file').click();
});
document.getElementById('br-file').addEventListener('change', async e => {
  const picked = [...e.target.files]; e.target.value = '';
  if (!picked.length || !brFile) return;
  brAttaching = true; document.getElementById('br-attach').disabled = true;
  for (const f of picked) {
    document.getElementById('br-attachstatus').textContent = tr('Adding {name}…', { name: f.name });
    if (f.size > 20 * 1048576) { document.getElementById('br-attachstatus').textContent = tr('{name} is too large (20 MB at most)', { name: f.name }); break; }
    if (!await brChangeFiles('attach', f.name, f)) break;
  }
  brAttaching = false; document.getElementById('br-attach').disabled = brSendBusy;
});
let brIssueUrl = null, brSendBusy = false;
// Send needs what the bug report service checks too (Tools/bugreport-worker reports.js):
// a title and description long enough to act on, and a GitHub username or an email to reply
// to. Create needs nothing: it captures the moment.
const BR_MIN_TITLE = 15, BR_MIN_DESC = 40;
const brOne = id => document.getElementById(id).value.replace(/\t/g, ' ').trim();
const brChars = s => [...s].length;
function brMissing() {
  const t = BR_MIN_TITLE - brChars(brOne('br-title'));
  if (t > 0) return tr('Title: {n} more characters', { n: t });
  const d = BR_MIN_DESC - brChars(document.getElementById('br-desc').value.trim());
  if (d > 0) return tr('Description: {n} more characters', { n: d });
  const gh = brOne('br-ghuser').replace(/^@/, ''), em = brOne('br-email');
  if (gh && !/^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}$/.test(gh)) return tr('Not a GitHub username');
  if (em && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(em)) return tr('Not an email address');
  if (!gh && !em) return tr('Add your GitHub username or email so we can reply');
  return '';
}
function brUpdateSend() {
  const need = brMissing();
  document.getElementById('br-need').textContent = brSendBusy ? '' : need;
  document.getElementById('br-send').disabled = brSendBusy || !!need;
}
for (const id of ['br-title', 'br-desc', 'br-ghuser', 'br-email'])
  document.getElementById(id).addEventListener('input', brUpdateSend);
try {
  document.getElementById('br-ghuser').value = localStorage.getItem('agopenweb.bugReportGitHub') || '';
  document.getElementById('br-email').value = localStorage.getItem('agopenweb.bugReportEmail') || '';
} catch (_) { }
document.getElementById('br-send').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (!brFile || brMissing()) return;
  const gh = brOne('br-ghuser').replace(/^@/, ''), em = brOne('br-email');
  try { localStorage.setItem('agopenweb.bugReportGitHub', gh); localStorage.setItem('agopenweb.bugReportEmail', em); } catch (_) { }
  document.getElementById('br-sendstatus').textContent = tr('Sending…');
  document.getElementById('br-send').disabled = true;
  transport.send('app.bugReport.send|' + [brFile, gh, em, brOne('br-title'), document.getElementById('br-desc').value].join('\t'));
});
document.getElementById('br-docs').addEventListener('pointerdown', e => {
  e.stopPropagation();
  openExternal('https://docs.agopengps.com/');
});
document.getElementById('br-openissue').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (brIssueUrl) openExternal(brIssueUrl);
});
document.getElementById('br-download').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (brFile) openExternal(location.origin + '/bugreports/' + encodeURIComponent(brFile));
});
document.getElementById('br-submit').addEventListener('pointerdown', e => {
  e.stopPropagation();
  // No title needed: the point is to capture the moment. The host names it "untitled".
  const t = document.getElementById('br-title').value.trim();
  document.getElementById('br-status').textContent = tr('Creating bug report…');
  transport.send('app.bugReport|' + t + '\t' + document.getElementById('br-desc').value);
});
