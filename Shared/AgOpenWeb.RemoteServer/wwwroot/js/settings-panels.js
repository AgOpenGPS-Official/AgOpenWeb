// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// Screen & Alerts, vehicle configuration.

// Units (Screen & Alerts) → config bridge write.
const saPanel = document.getElementById('screenalerts');
// Units + device settings (keyboard/fullscreen/elevation) moved to App Settings (File menu).
// Screen & Alerts toggles → config.set|display.X; action rows (theme/quality) → command.
for (const b of saPanel.querySelectorAll('.sa-tgl[data-key]'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); cfgSend(b.dataset.key, b.classList.contains('active') ? '0' : '1'); });
for (const b of saPanel.querySelectorAll('.sa-act'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); transport.send(b.dataset.cmd); });
// On click, not pointerdown: the panel resizes under the finger once the new size applies.
// VT & Web Cards: this browser's layout, not the host's (CardPane).
for (const b of saPanel.querySelectorAll('.sa-cardlay'))
  b.addEventListener('pointerdown', e => {
    e.stopPropagation();
    CardPane.setLayout(b.dataset.lay);
    for (const x of saPanel.querySelectorAll('.sa-cardlay')) x.classList.toggle('active', x.dataset.lay === CardPane.layout);
  });
for (const b of saPanel.querySelectorAll('.sa-scale:not(.sa-cardlay)'))
  b.addEventListener('click', e => { e.stopPropagation(); cfgSend('display.uiScale', b.dataset.scale); });
// Guidance bar (#479): the vehicle's AutoSteer settings, not display.*. The style buttons
// pick one of two; On/Off is apart from them.
for (const b of saPanel.querySelectorAll('.sa-opt[data-val]'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); cfgSend(b.dataset.key, b.dataset.val); });
const saBarOn = document.getElementById('sa-baron'), saCmpp = document.getElementById('sa-cmpp');
saBarOn.addEventListener('pointerdown', e => { e.stopPropagation(); cfgSend(saBarOn.dataset.key, saBarOn.classList.contains('active') ? '0' : '1'); });
saCmpp.addEventListener('change', () => { const v = parseInt(saCmpp.value); if (Number.isFinite(v)) cfgSend('autosteer.cmPerPixel', v); });
// Aerial Imagery (#500): an open field without imagery shows off, and a tap offers to get it
// for the field's boundary; otherwise the button shows and flips the setting.
const saImagery = document.getElementById('sa-imagery');
const fieldLacksImagery = () => !!(scene && scene.hasField) && !scene.imagery;
saImagery.addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (!fieldLacksImagery()) { cfgSend('display.imageryVisible', saImagery.classList.contains('active') ? '0' : '1'); return; }
  showConfirm(tr('Aerial Imagery'), tr('This field has no aerial imagery. Get it for the field boundary now?'), () => {
    if (!config.display.imageryVisible) cfgSend('display.imageryVisible', '1');
    transport.send('boundary.captureImagery');
  });
});
const saExtra = document.getElementById('sa-extracount');
saExtra.addEventListener('change', () => { const v = parseInt(saExtra.value); if (Number.isFinite(v)) cfgSend('display.extraGuidelinesCount', v); });
function populateScreenAlerts() {
  if (!config || !config.display) return;
  const d = config.display;
  for (const b of saPanel.querySelectorAll('.sa-tgl[data-key]')) b.classList.toggle('active', !!d[b.dataset.key.split('.')[1]]);
  saImagery.classList.toggle('active', !!d.imageryVisible && !fieldLacksImagery());
  if (document.activeElement !== saExtra) saExtra.value = d.extraGuidelinesCount;
  document.getElementById('sa-quality').textContent = d.resolutionLabel || '—';
  for (const b of saPanel.querySelectorAll('.sa-scale:not(.sa-cardlay)')) b.classList.toggle('active', +b.dataset.scale === (d.uiScale || 100));
  for (const b of saPanel.querySelectorAll('.sa-cardlay')) b.classList.toggle('active', b.dataset.lay === CardPane.layout);
  const a = config.autosteer;
  if (a) {
    for (const b of saPanel.querySelectorAll('.sa-opt[data-val]'))
      b.classList.toggle('active', (b.dataset.val === '1') === !!a.steerBarEnabled);
    saBarOn.classList.toggle('active', !!a.lightbarEnabled);
    if (document.activeElement !== saCmpp) saCmpp.value = a.cmPerPixel;
  }
}
// Vehicle config panel — full native VehicleConfigDialog surface (Vehicle/GPS/Roll).
// Generic config controls keyed by data-key="<section>.<field>"; reused by later
// sub-phases. Writes via config.set; Save Profile via profile.save.
const vcPanel = document.getElementById('vehiclecfg'), vcName = document.getElementById('vc-name');
// Hitch-type labels (mirrors ConfigurationViewModel.HitchTypeOptions); value = code =
// index − 1 (code −1 → "Not available").
const HITCH_OPTS = [tr('Not available'), tr('Unknown'), tr('ISO 6489-3 Tractor drawbar'),
  tr('ISO 730 Three-point-hitch semi-mounted'), tr('ISO 730 Three-point-hitch mounted'),
  tr('ISO 6489-1 Hitch-hook'), tr('ISO 6489-2 Clevis coupling 40'), tr('ISO 6489-4 Piton type coupling'),
  tr('ISO 6489-5 CUNA hitch'), tr('ISO 24347 Ball type hitch'), tr('Chassis Mounted - Self-Propelled'),
  tr('ISO 5692-2 Pivot wagon hitch')];
const vcHitchSel = vcPanel.querySelector('.cfg-sel[data-key="vehicle.hitchType"]');
HITCH_OPTS.forEach((label, i) => { const o = document.createElement('option'); o.value = i - 1; o.textContent = label; vcHitchSel.appendChild(o); });
const vcFw = vcPanel.querySelector('.cfg-slider[data-key="gps.headingFusionWeight"]');
// Tabs.
for (const t of vcPanel.querySelectorAll('.cfg-tab'))
  t.addEventListener('pointerdown', e => {
    e.stopPropagation();
    for (const b of vcPanel.querySelectorAll('.cfg-tab')) b.classList.toggle('active', b === t);
    for (const body of vcPanel.querySelectorAll('.cfg-body')) body.hidden = (body.id !== t.dataset.tab);
  });
wireCfgControls(vcPanel);
vcHitchSel.addEventListener('change', () => cfgSend('vehicle.hitchType', vcHitchSel.value));
// Heading fusion = GPS share (0–1), shown like AgOpenGPS: "GPS 30% · IMU 70%" (#112).
const fusionLabel = w => tr('GPS {gps}% · IMU {imu}%', { gps: Math.round(w * 100), imu: 100 - Math.round(w * 100) });
vcFw.addEventListener('input', () => { document.getElementById('vc-hfw').textContent = fusionLabel(+vcFw.value); cfgSend('gps.headingFusionWeight', vcFw.value); });
document.getElementById('vc-save').addEventListener('pointerdown', e => { e.stopPropagation(); saveProfile(); });
// Zero Now captures the current roll as the offset, like AgOpenGPS Zero Roll and
// Tools › Roll Correction; it used to clear the offset (that's Remove Offset, #111).
document.getElementById('vc-rollzero').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); transport.send('roll.zeroCalibrate'); });
// Populate every control from the config frame. force (on open) fills all; otherwise
// skip the focused number input so we don't clobber what the user is typing.
function populateVehicleCfg(force) {
  if (!config || !config.vehicle) return;
  vcName.textContent = config.vehicle.name || '—';
  populateCfgControls(vcPanel, force);
  // Type-dependent measurement diagrams (mirror VehicleConfig.*ImageSource switches:
  // index by VehicleType 0 Tractor / 1 Harvester / 2 FourWD; '' = no image).
  const ty = Math.max(0, Math.min(2, cfgGet('vehicle.type') | 0));
  const setImg = (id, names) => {
    const el = document.getElementById(id), n = names[ty];
    if (!el) return;
    if (n) { const p = '/icons/' + n + '.png'; if (!el.src.endsWith(p)) el.src = p; el.style.display = ''; }
    else el.style.display = 'none';
  };
  setImg('vc-img-hitch', ['Hitch', '', 'HitchArticulated']);
  setImg('vc-img-wheelbase', ['WheelbaseTractor', 'WheelbaseHarvester', 'WheelbaseArticulated']);
  setImg('vc-img-track', ['TrackWidthTractor', 'TrackWidthHarvester', 'TrackWidthArticulated']);
  setImg('vc-img-antside', ['AntennaTractorTop', 'AntennaHarvesterTop', 'AntennaArticulatedTop']);
  setImg('vc-img-antoffset', ['AntennaTractorOffset', 'AntennaHarvesterOffset', 'AntennaArticulatedOffset']);
  vcHitchSel.value = cfgGet('vehicle.hitchType');
  const w = cfgGet('gps.headingFusionWeight') || 0;
  vcFw.value = w; document.getElementById('vc-hfw').textContent = fusionLabel(w);
  // Dual-only fields are live only in Dual GPS mode; reverse detection only in single
  // (mirrors the native enable/disable gating).
  const dual = !!cfgGet('gps.isDualGps');
  const setEn = (key, en) => { const el = vcPanel.querySelector('[data-key="' + key + '"]'); if (el) { el.disabled = !en; el.style.opacity = en ? '1' : '0.4'; } };
  ['gps.dualHeadingOffset', 'gps.dualReverseDistance', 'gps.autoDualFix', 'gps.dualSwitchSpeed'].forEach(k => setEn(k, dual));
  setEn('gps.reverseDetection', !dual);
}
