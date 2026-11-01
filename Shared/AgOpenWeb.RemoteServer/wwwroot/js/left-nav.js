// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// The left nav and its menu panels (open, close, chain).

// ---- left nav (Phase 9) — config/settings panels via the config bridge ----
// Vertical button bar; each button toggles a non-modal panel (only one open at a
// time). Panels READ from the config frame (or existing frames, e.g. units =
// Status.isMetric) and WRITE via `config.set|key:value` (needs control; the host applies it
// to ConfigurationStore). Grows one entry per sub-phase.
// Navigation: top-level buttons open a panel; sub-panels (vehicle/tool config) are
// reached from the hub and carry a Back button. One panel open at a time.
const LN_NAV_PANELS = ['screenalerts', 'tools', 'rollcorr', 'fieldtools', 'fieldbuilder', 'offsetfix', 'importtracks', 'recpath', 'boundarymenu', 'boundaryplayer', 'kmlboundary', 'vehtoolhub', 'vehiclecfg', 'toolcfg', 'autosteercfg', 'networkio', 'ntripsettings', 'systemdata', 'ntripprofiles', 'ntripeditor', 'smartwas', 'fieldops', 'fieldsandjobs', 'newfield', 'fromexisting', 'isoimport', 'kmlimport', 'resumejob', 'agsettings', 'agupload', 'agdownload', 'filemenu', 'appsettings', 'language', 'viewsettings', 'logviewer', 'hotkeys', 'help', 'about', 'bugreport'];
// Watch-the-tractor panels opt OUT of the light-dismiss scrim — the map must stay
// interactive (pan/zoom to follow the tractor while capturing). They close only via
// the header (Back / ✕).
// The bug report panel too: leave it open while driving and press the button the
// moment a problem shows, since the dump holds only the last 60 s of GPS data.
const NO_SCRIM = new Set(['smartwas', 'recpath', 'boundaryplayer', 'fieldbuilder', 'bugreport', 'ntripsettings', 'ntripprofiles', 'ntripeditor']);
const lnScrim = document.getElementById('ln-scrim');
function lnCloseAll() {
  for (const id of LN_NAV_PANELS) document.getElementById(id).classList.remove('open');
  lnScrim.classList.remove('open');
  document.getElementById('ln-screenalerts').classList.remove('active');
  document.getElementById('ln-vehicle').classList.remove('active');
  document.getElementById('ln-autosteer').classList.remove('active');
  document.getElementById('ln-network').classList.remove('active');
  document.getElementById('ln-fieldops').classList.remove('active');
  document.getElementById('ln-filemenu').classList.remove('active');
  document.getElementById('ln-tools').classList.remove('active');
  document.getElementById('ln-fieldtools').classList.remove('active');
}
function lnOpen(panelId, navBtnId, onOpen) {
  // Opening a menu leaves an AB line being drawn on the map, like Esc (#399).
  if (abFlow === 'straight' || abFlow === 'curve') abCancel();
  lnCloseAll();
  document.getElementById(panelId).classList.add('open');
  if (!NO_SCRIM.has(panelId)) lnScrim.classList.add('open'); // transparent light-dismiss
  if (navBtnId) document.getElementById(navBtnId).classList.add('active');
  if (onOpen) onOpen();
}
// Outside tap on the scrim closes the chain and is CONSUMED (stopPropagation keeps the
// map from panning / the background from actuating).
lnScrim.addEventListener('pointerdown', e => { e.stopPropagation(); lnCloseAll(); });
for (const id of LN_NAV_PANELS) document.getElementById(id).addEventListener('pointerdown', e => e.stopPropagation());
document.getElementById('ln-screenalerts').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (document.getElementById('screenalerts').classList.contains('open')) lnCloseAll();
  else lnOpen('screenalerts', 'ln-screenalerts', populateScreenAlerts);
});
document.getElementById('ln-vehicle').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const anyOpen = ['vehtoolhub', 'vehiclecfg', 'toolcfg'].some(id => document.getElementById(id).classList.contains('open'));
  if (anyOpen) lnCloseAll(); else lnOpen('vehtoolhub', 'ln-vehicle', refreshHub);
});
document.getElementById('ln-autosteer').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (document.getElementById('autosteercfg').classList.contains('open')) lnCloseAll();
  else lnOpen('autosteercfg', 'ln-autosteer', () => {
    // Always open collapsed (left pane only), like the native panel.
    asPanel.classList.remove('expanded');
    document.getElementById('as-expand').textContent = tr('▶ Full');
    populateAutoSteer(true);
  });
});
document.getElementById('ln-network').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (document.getElementById('networkio').classList.contains('open')) lnCloseAll();
  else lnOpen('networkio', 'ln-network', renderNetworkIo);
});
document.getElementById('ln-fieldops').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const anyOpen = ['fieldops', 'fieldsandjobs', 'newfield'].some(id => document.getElementById(id).classList.contains('open'));
  if (anyOpen) lnCloseAll(); else lnOpen('fieldops', 'ln-fieldops', renderFieldOps);
});
document.getElementById('ln-tools').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (document.getElementById('tools').classList.contains('open')) lnCloseAll();
  else lnOpen('tools', 'ln-tools', renderToolsPanel);
});
// Tools fly-out items. Steer Wizard + Log Viewer reuse existing flows; Roll
// Correction is an inert placeholder (matches the native button, which has no
// command bound). The three chart buttons toggle their floating chart cards.
document.getElementById('tl-wizard').addEventListener('pointerdown', e => {
  e.stopPropagation(); lnCloseAll();
  if (typeof openSteerWizard === 'function') openSteerWizard();
});
// Log Viewer is a shortcut to the App Log Viewer (the File menu owns the same panel);
// track which parent opened it so Back returns to the right place.
let logViewerParent = 'filemenu';
document.getElementById('tl-logviewer').addEventListener('pointerdown', e => {
  e.stopPropagation(); logViewerParent = 'tools'; lnOpen('logviewer', 'ln-tools', renderLogViewer);
});
document.getElementById('lv-back').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (logViewerParent === 'tools') lnOpen('tools', 'ln-tools', renderToolsPanel);
  else lnOpen('filemenu', 'ln-filemenu');
});
// Roll Correction → the wizard's roll-cal piece as a standalone chain panel.
document.getElementById('tl-rollcorr').addEventListener('pointerdown', e => {
  e.stopPropagation(); lnOpen('rollcorr', 'ln-tools', renderRollCorr);
});
document.getElementById('rc-back').addEventListener('pointerdown', e => {
  e.stopPropagation(); lnOpen('tools', 'ln-tools', renderToolsPanel);
});
document.getElementById('rc-invert').addEventListener('pointerdown', e => {
  e.stopPropagation(); cfgSend('roll.isRollInvert', cfgGet('roll.isRollInvert') ? '0' : '1');
});
document.getElementById('rc-zero').addEventListener('pointerdown', e => {
  e.stopPropagation(); transport.send('roll.zeroCalibrate');
});
for (const b of document.querySelectorAll('.tl-chartbtn'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); toggleChart(b.dataset.chart); });
for (const b of document.querySelectorAll('.chart-x'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); setChartOpen(b.dataset.chart, false); });
// Field Tools fly-out.
document.getElementById('ln-fieldtools').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const anyOpen = ['fieldtools', 'offsetfix'].some(id => document.getElementById(id).classList.contains('open'));
  if (anyOpen) lnCloseAll(); else lnOpen('fieldtools', 'ln-fieldtools');
});
// Field Tools work on the open field; without one, say so instead of asking (#305).
function requireField() {
  if (scene && scene.hasField) return true;
  showToast(tr('Open a field first'));
  return false;
}
document.getElementById('ft-deleteapplied').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (!requireField()) return;
  showConfirm(tr('Delete Applied Area'),
    tr('Delete all applied area coverage? This cannot be undone.'),
    () => transport.send('field.deleteApplied'));
});
document.getElementById('ft-offsetfix').addEventListener('pointerdown', e => {
  e.stopPropagation(); lnOpen('offsetfix', 'ln-fieldtools', renderOffsetFix);
});
document.getElementById('of-back').addEventListener('pointerdown', e => {
  e.stopPropagation(); lnOpen('fieldtools', 'ln-fieldtools');
});
document.getElementById('ft-importtracks').addEventListener('pointerdown', e => {
  e.stopPropagation(); lnOpen('importtracks', 'ln-fieldtools', renderImportTracks);
});
document.getElementById('it-back').addEventListener('pointerdown', e => {
  e.stopPropagation(); lnOpen('fieldtools', 'ln-fieldtools');
});
// Recorded Path.
document.getElementById('ft-recpath').addEventListener('pointerdown', e => {
  e.stopPropagation(); lnOpen('recpath', 'ln-fieldtools', renderRecPath);
});
document.getElementById('rp-back').addEventListener('pointerdown', e => {
  e.stopPropagation(); lnOpen('fieldtools', 'ln-fieldtools');
});
document.getElementById('rp-tab-rec').addEventListener('pointerdown', e => { e.stopPropagation(); recPathTab = 0; renderRecPath(); });
document.getElementById('rp-tab-play').addEventListener('pointerdown', e => { e.stopPropagation(); recPathTab = 1; renderRecPath(); });
document.getElementById('rp-start').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('recpath.start'); });
document.getElementById('rp-stop').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('recpath.stop'); });
document.getElementById('rp-save').addEventListener('pointerdown', e => {
  e.stopPropagation(); transport.send('recpath.save|' + document.getElementById('rp-name').value);
});
document.getElementById('rp-playbtn').addEventListener('pointerdown', e => { e.stopPropagation(); rnSend('recpath.play'); });
document.getElementById('rp-resume').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('recpath.cycleResume'); });
document.getElementById('rp-reverse').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('recpath.reverse'); });
// Boundary recording menu.
document.getElementById('ft-boundary').addEventListener('pointerdown', e => {
  e.stopPropagation(); if (!requireField()) return; transport.send('boundary.refresh'); lnOpen('boundarymenu', 'ln-fieldtools', renderBoundaryMenu);
});
document.getElementById('bm-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('fieldtools', 'ln-fieldtools'); });
