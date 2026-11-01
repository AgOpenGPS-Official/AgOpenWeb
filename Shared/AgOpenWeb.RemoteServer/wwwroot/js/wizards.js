// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// Steer wizard, vehicle and tool picker.

// ---- Steer Wizard (Phase 9) — full-screen, host-driven. The host runs the real
// SteerWizardViewModel; we rebuild #wz-content per step from the Wizard frame, edit via
// the existing config.set bridge, and forward nav (wizard.*) + gated calibration
// (wizard.action). Editable values display from the Config frame. ----
const wzOverlay = document.getElementById('wizard-overlay');
const wzContent = document.getElementById('wz-content');
// stepKind|index of the currently-built content (rebuild on change — and on a unit
// flip, which onUnitsChanged forces by clearing this).
let _wzKey = '';
function openSteerWizard() { _wzKey = ''; wizard = null; transport.send('wizard.open'); wzOverlay.classList.add('open'); }
function wzClose(send) { if (send) transport.send('wizard.cancel'); wzOverlay.classList.remove('open'); wizard = null; _wzKey = ''; }
document.getElementById('wz-cancel').addEventListener('pointerdown', e => { e.stopPropagation(); wzClose(true); });
document.getElementById('wz-skip').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('wizard.skip'); });
document.getElementById('wz-back').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('wizard.back'); });
document.getElementById('wz-next').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (wizard && wizard.isLast) { transport.send('wizard.finish'); wzClose(false); }
  else transport.send('wizard.next');
});
// Delegated content events: data-cfg="key:val" (config.set), data-hw="n" (hardware
// level), data-act="Cmd" (gated wizard.action), data-tgl="key" (toggle), data-cfgnum
// (number input) on change.
wzContent.addEventListener('pointerdown', e => {
  const el = e.target.closest('[data-cfg],[data-hw],[data-act],[data-tgl]'); if (!el) return;
  e.stopPropagation();
  if (el.dataset.cfg) { const i = el.dataset.cfg.indexOf(':'); cfgSend(el.dataset.cfg.slice(0, i), el.dataset.cfg.slice(i + 1)); }
  else if (el.dataset.hw != null) transport.send('wizard.hw|' + el.dataset.hw);
  else if (el.dataset.act) rnSend('wizard.action|' + el.dataset.act);
  else if (el.dataset.tgl) cfgSend(el.dataset.tgl, cfgGet(el.dataset.tgl) ? '0' : '1');
});
wzContent.addEventListener('change', e => {
  const inp = e.target.closest('[data-cfgnum]'); if (!inp) return;
  const v = readUnitInput(inp); if (Number.isFinite(v)) cfgSend(inp.dataset.cfgnum, v);
});
// Per-step content. Editable values read from the Config frame; live values get ids
// (data-live) refreshed each frame. esc() guards interpolated strings.
function esc(s) { return String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }
function wzVal(key, dflt) { const v = cfgGet(key); return typeof v === 'number' ? v : (dflt || 0); }
// `unit` is the STORED unit key ('m', 'kmh', …) or a plain suffix ('deg', '') that
// carries no conversion. Unit-keyed fields render the display value and tag the input
// so the change handler converts back; applyUnits() re-steps them on a unit flip.
function wzNum(label, sub, key, step, unit) {
  const known = UNIT_DEFS[unit];
  const val = known ? roundDisplay(toDisplayUnit(wzVal(key), unit), unit) : wzVal(key);
  const istep = known && !isMetric() ? known.impStep : (step || '0.01');
  return '<div class="wz-fld"><label>' + esc(label) + '</label><div class="sub">' + esc(sub || '') + '</div>' +
    '<input class="wz-num" data-cfgnum="' + key + '"' + (known ? ' data-unit="' + unit + '" data-mstep="' + (step || '0.01') + '"' : '') +
    ' type="number" step="' + istep + '" value="' + val + '"><span class="wz-unit">' + esc(known ? unitLabel(unit) : (unit || '')) + '</span></div>';
}
function wzSeg(label, sub, key, opts) { // opts: [[text,val],...]
  return '<div class="wz-row"><div class="lbl">' + esc(label) + (sub ? '<div class="sub">' + esc(sub) + '</div>' : '') + '</div><div class="wz-seg">' +
    opts.map(o => '<button data-cfg="' + key + ':' + o[1] + '" data-activekey="' + key + '" data-activeval="' + o[1] + '">' + esc(o[0]) + '</button>').join('') + '</div></div>';
}
function wzTgl(label, sub, key) {
  return '<div class="wz-row"><div class="lbl">' + esc(label) + (sub ? '<div class="sub">' + esc(sub) + '</div>' : '') + '</div><button class="wz-tgl" data-tgl="' + key + '" data-tglkey="' + key + '">Off</button></div>';
}
// CPD / Ackermann circle test (#154): Record plus the feedback a wizard owes the operator —
// why Record can't start (or a low-fix warning), live progress, and the outcome.
function wzCircleTest() {
  return '<div class="wz-center wz-circle"><div class="wz-btnrow"><button class="wz-testbtn" data-act="StartRecording" id="wz-recbtn">Record</button></div>' +
    '<div class="wz-hint" data-live="hint"></div><div class="wz-desc wz-phase" data-live="phase"></div><div class="wz-result" data-live="result"></div></div>';
}
function wzLive(lbl, key) { return '<div class="wz-live"><div class="big" data-live="' + key + '">—</div><div class="lbl">' + esc(lbl) + '</div></div>'; }
function buildWizardContent(w) {
  const cfg = config || {}, veh = cfg.vehicle || {}, ast = cfg.autosteer || {}, roll = cfg.roll || {};
  const ty = Math.max(0, Math.min(2, (veh.type | 0)));
  const head = '<div class="wz-h1">' + esc(w.title) + '</div><div class="wz-desc">' + esc(w.description) + '</div>';
  switch (w.stepKind) {
    case 'welcome': return head + '<div class="wz-center"><div class="wz-okcard"><div class="t">AutoSteer</div><div class="s">Configuration Wizard</div></div></div>';
    case 'vehicleType': {
      const c = (i, ic, t) => '<div class="wz-card" data-cfg="vehicle.type:' + i + '" data-activekey="vehicle.type" data-activeval="' + i + '"><img src="/icons/' + ic + '.png"><div class="t">' + t + '</div></div>';
      return head + '<div class="wz-cards">' + c(0, 'WheelbaseTractor', tr('Tractor')) + c(1, 'WheelbaseHarvester', tr('Harvester')) + c(2, 'WheelbaseArticulated', tr('4WD / Articulated')) + '</div>';
    }
    case 'hardware': {
      const c = (i, ic, t, s) => '<div class="wz-card" data-hw="' + i + '" data-activehw="' + i + '"><img src="/icons/' + ic + '.png"><div class="t">' + t + '</div><div class="s">' + s + '</div></div>';
      return head + '<div class="wz-cards">' + c(0, 'NavigationSettings', tr('GPS Only'), tr('Light bar guidance only')) + c(1, 'AutoSteerConf', tr('AutoSteer'), tr('Motor/valve, WAS, IMU')) + c(2, 'Settings48', tr('Full Setup'), tr('Steering + section control')) + '</div>';
    }
    case 'dimensions':
      return head + '<div class="wz-panels"><div class="wz-panel"><img class="dia" src="/icons/' + ['WheelbaseTractor', 'WheelbaseHarvester', 'WheelbaseArticulated'][ty] + '.png">' + wzNum(tr('Wheelbase'), tr('Front to rear axle distance'), 'vehicle.wheelbase', '0.01', 'm') + '</div>' +
        '<div class="wz-panel"><img class="dia" src="/icons/' + ['TrackWidthTractor', 'TrackWidthHarvester', 'TrackWidthArticulated'][ty] + '.png">' + wzNum(tr('Track width'), tr('Left to right wheel center'), 'vehicle.trackWidth', '0.01', 'm') + '</div></div>';
    case 'antenna':
      return head + '<div class="wz-panels"><div class="wz-panel"><img class="dia" src="/icons/' + ['AntennaTractorTop', 'AntennaHarvesterTop', 'AntennaArticulatedTop'][ty] + '.png">' +
        wzNum(tr('Pivot distance'), tr('Antenna to rear axle (+ = ahead)'), 'vehicle.antennaPivot', '0.01', 'm') + wzNum(tr('Antenna height'), tr('Above ground level'), 'vehicle.antennaHeight', '0.01', 'm') + '</div>' +
        '<div class="wz-panel"><img class="dia" src="/icons/' + ['AntennaTractorOffset', 'AntennaHarvesterOffset', 'AntennaArticulatedOffset'][ty] + '.png"><div class="wz-fld"><label>Lateral Offset</label><div class="sub">From centerline (+ = right)</div><div class="wz-seg">' +
        ['left', 'center', 'right'].map(v => '<button data-cfg="vehicle.antennaSide:' + v + '" data-activekey="vehicle.antennaSide" data-activeval="' + v + '">' + v[0].toUpperCase() + v.slice(1) + '</button>').join('') + '</div></div></div></div>';
    case 'hwconfig':
      return head + '<div class="wz-rows">' +
        wzSeg(tr('Steer enable method'), null, 'autosteer.externalEnable', [[tr('None'), 0], [tr('Switch'), 1], [tr('Button'), 2]]) +
        wzSeg(tr('Motor driver'), null, 'autosteer.motorDriver', [['IBT2', 0], ['Cytron', 1]]) +
        wzSeg(tr('A/D converter'), null, 'autosteer.adConverter', [[tr('Differential'), 0], [tr('Single'), 1]]) +
        wzTgl(tr('Invert steer enable relay'), tr('Inverts the relay that enables the motor'), 'autosteer.invertRelays') +
        wzTgl(tr('Danfoss valve'), tr('Enable for Danfoss hydraulic steering'), 'autosteer.danfossEnabled') + '</div>';
    case 'roll':
      // Reuse the map's roll gauge: a green bar that rotates around (100,40) by the
      // live roll, with pink scale marks and the degree readout.
      return head + '<div class="wz-center"><svg width="260" height="104" viewBox="0 0 200 80">' +
        '<path fill="none" stroke="#E91E90" stroke-width="2.5" stroke-linecap="round" d="M 70,12 A 22,28 0 0 0 70,68 M 59,20 L 51,20 M 52,30 L 44,30 M 48,40 L 40,40 M 52,50 L 44,50 M 59,60 L 51,60"/>' +
        '<path fill="none" stroke="#E91E90" stroke-width="2.5" stroke-linecap="round" d="M 130,12 A 22,28 0 0 1 130,68 M 141,20 L 149,20 M 148,30 L 156,30 M 152,40 L 160,40 M 148,50 L 156,50 M 141,60 L 149,60"/>' +
        '<rect id="wz-roll-bar" x="50" y="38" width="100" height="4" rx="2" fill="#2ECC71"/>' +
        '<circle cx="100" cy="40" r="3" fill="#fff"/>' +
        '<text id="wz-roll-deg" x="100" y="32" text-anchor="middle" fill="#fff" font-size="24" font-weight="bold" font-family="system-ui,sans-serif">0.0</text>' +
        '</svg></div>' +
        '<div class="wz-rows">' + wzTgl(tr('Invert roll'), null, 'roll.isRollInvert') +
        '<div class="wz-row"><div class="lbl">Zero Roll</div><button class="wz-testbtn" data-act="ZeroRoll">Zero Roll</button></div>' +
        '<div class="wz-row"><div class="lbl">Roll Zero Offset</div><div class="lbl"><span data-live="rollzero">—</span></div></div></div>';
    case 'was':
      return head + wzLive(tr('Live Steer Angle'), 'angle') +
        '<div class="wz-rows"><div class="wz-row"><div class="lbl">Zero WAS</div><button class="wz-testbtn" data-act="ZeroWas">Zero WAS</button></div>' +
        wzTgl(tr('Invert WAS'), tr('Enable if steering reads backwards'), 'autosteer.invertWas') +
        '<div class="wz-row"><div class="lbl">WAS Offset</div><div class="lbl"><span data-live="wasoffset">—</span> counts</div></div></div>';
    case 'motor':
      return head + wzLive(tr('Live Steer Angle'), 'angle') + '<div class="wz-center"><button class="wz-testbtn" data-act="StartTest">Start Motor Test</button>' +
        '<div class="wz-hint" data-live="hint"></div><div class="wz-desc" data-live="phase"></div><div class="wz-desc" data-live="result"></div></div>';
    case 'maxangle':
      return head + wzLive(tr('Live Steer Angle'), 'angle') + '<div class="wz-center"><button class="wz-testbtn" data-act="StartTest">Start Max Angle Test</button>' +
        '<div class="wz-hint" data-live="hint"></div><div class="wz-desc" data-live="phase"></div><div class="wz-desc" data-live="result"></div></div>';
    case 'cpd':
      return head + '<div class="wz-testtop"><div class="wz-prereq"><div class="ttl">Prerequisites</div><div class="it">GPS: <b data-live="fix">—</b> (RTK Fixed recommended)</div><div class="it">Speed: <b data-live="speed">—</b> ' + esc(tr('(aim for ~{speed})', { speed: fmtUnit(5, 'kmh', 0) })) + '</div></div>' +
        wzLive(tr('Live Steer Angle'), 'angle') + '</div>' + wzCircleTest() +
        '<div class="wz-rows">' + wzNum(tr('Counts Per Degree'), null, 'autosteer.countsPerDegree', '1', '') + '</div>';
    case 'ackermann':
      return head + '<div class="wz-testtop"><div class="wz-prereq"><div class="ttl">Prerequisites</div><div class="it">GPS: <b data-live="fix">—</b> (RTK Fixed recommended)</div><div class="it">Speed: <b data-live="speed">—</b> ' + esc(tr('(aim for ~{speed})', { speed: fmtUnit(5, 'kmh', 0) })) + '</div></div>' +
        wzLive(tr('Live Steer Angle'), 'angle') + '</div>' + wzCircleTest() +
        '<div class="wz-rows">' + wzNum(tr('Ackermann'), tr('100 = neutral'), 'autosteer.ackermann', '1', '') + '</div>';
    case 'gains':
      return head + '<div class="wz-rows">' + wzTgl(tr('Guidance algorithm (Stanley)'), tr('Pure Pursuit default; Stanley more responsive at low speed'), 'autosteer.isStanleyMode') +
        wzNum(tr('Proportional gain (Kp)'), tr('Start at 10, increase for faster correction'), 'autosteer.proportionalGain', '1', '') +
        wzNum(tr('Integral gain (Ki)'), tr('Start at 0, only increase for drift'), 'autosteer.integralGain', '0.01', '') +
        wzNum(tr('Steer response hold'), tr('Look-ahead (higher = smoother)'), 'autosteer.steerResponseHold', '0.1', '') +
        wzNum(tr('Side hill compensation'), tr('Degrees per degree of roll (0-1.0)'), 'autosteer.sideHillCompensation', '0.01', '') + '</div>';
    case 'speed':
      return head + '<div class="wz-rows">' +
        wzNum(tr('Min steer speed'), tr('Engage above this speed'), 'autosteer.minSteerSpeed', '0.1', 'kmh') +
        wzNum(tr('Max steer speed'), tr('Safety cutoff speed'), 'autosteer.maxSteerSpeed', '0.1', 'kmh') +
        wzTgl(tr('Turn Sensor'), tr('Steering wheel encoder'), 'autosteer.turnSensorEnabled') +
        wzTgl(tr('Pressure sensor'), tr('Hydraulic stall detection'), 'autosteer.pressureSensorEnabled') +
        wzTgl(tr('Current sensor'), tr('Motor current obstruction detection'), 'autosteer.currentSensorEnabled') +
        wzTgl(tr('Steer in reverse'), tr('Allow steering while reversing'), 'autosteer.steerInReverse') +
        wzNum(tr('Deadzone heading'), tr('Heading error tolerance'), 'autosteer.deadzoneHeading', '0.01', 'deg') + '</div>';
    case 'finish':
      return head + '<div class="wz-center"><div class="wz-okcard"><div class="ok">OK</div><div class="t">Configuration saved!</div><div class="s">Click Finish to close the wizard</div></div></div>';
    default: return head;
  }
}
function renderWizard() {
  const w = wizard;
  if (!w) return;
  // Chrome (every frame).
  asSetText('wz-step', tr('Step {n} of {total}', { n: w.stepIndex + 1, total: w.totalSteps }));
  document.getElementById('wz-progressbar').style.width = (w.totalSteps ? (w.stepIndex + 1) / w.totalSteps * 100 : 0) + '%';
  asSetText('wz-was', (w.statusWas || 0).toFixed(1) + '°'); asSetText('wz-roll', (w.statusRoll || 0).toFixed(1) + '°');
  asSetText('wz-gps', w.statusGps || '—'); asSetText('wz-speed', fmtUnit(w.statusSpeed || 0, 'kmh', 1)); asSetText('wz-pwm', w.statusPwm | 0);
  const next = document.getElementById('wz-next');
  next.textContent = w.isLast ? tr('Finish') : tr('Next');
  next.classList.toggle('disabled', !(w.isLast || w.canNext));
  document.getElementById('wz-back').classList.toggle('disabled', !w.canBack);
  document.getElementById('wz-skip').style.display = w.canSkip ? '' : 'none';
  // Rebuild content only on step change.
  const key = w.stepKind + '|' + w.stepIndex;
  if (key !== _wzKey) { _wzKey = key; wzContent.innerHTML = buildWizardContent(w); }
  // Active states (selection cards / segments / toggles) + live values, every frame.
  for (const el of wzContent.querySelectorAll('[data-activekey]'))
    el.classList.toggle('active', String(cfgGet(el.dataset.activekey)) === el.dataset.activeval);
  for (const el of wzContent.querySelectorAll('[data-activehw]'))
    el.classList.toggle('active', w.hardwareLevel === +el.dataset.activehw);
  for (const el of wzContent.querySelectorAll('[data-tglkey]')) {
    const on = !!cfgGet(el.dataset.tglkey); el.classList.toggle('on', on); el.textContent = on ? tr('On') : tr('Off');
  }
  for (const el of wzContent.querySelectorAll('[data-act]')) el.classList.toggle('disabled', !iHoldControl);
  const live = (k, v) => { for (const el of wzContent.querySelectorAll('[data-live="' + k + '"]')) el.textContent = v; };
  live('angle', (w.liveAngle || 0).toFixed(1) + '°'); live('roll', (w.liveRoll || 0).toFixed(2) + '°');
  live('error', (w.liveError || 0).toFixed(1)); live('phase', w.testPhase || ''); live('result', w.testResult || '');
  live('fix', w.fixLabel || (w.statusGps || '—')); live('speed', fmtUnit(w.statusSpeed || 0, 'kmh', 1));
  live('rollzero', wzVal('roll.rollZero').toFixed(2)); live('wasoffset', wzVal('autosteer.wasOffset') | 0);
  // Roll gauge (roll-calibration step): rotate the bar by the live roll, like the map.
  const rb = document.getElementById('wz-roll-bar');
  if (rb) { const rv = w.liveRoll || w.statusRoll || 0; rb.setAttribute('transform', 'rotate(' + rv.toFixed(2) + ' 100 40)'); const rd = document.getElementById('wz-roll-deg'); if (rd) rd.textContent = rv.toFixed(1); }
  live('hint', w.recordHint || '');
  const rec = document.getElementById('wz-recbtn');
  if (rec) {
    rec.textContent = w.testActive ? tr('Stop') : tr('Record'); rec.dataset.act = w.testActive ? 'StopRecording' : 'StartRecording';
    // CPD / Ackermann report whether Record can start; the hint says why not.
    if (w.stepKind === 'cpd' || w.stepKind === 'ackermann')
      rec.classList.toggle('disabled', !iHoldControl || (!w.testActive && !w.canRecord));
  }
  // Keep number fields in step with the store (a test result or another client can change
  // it) — except the one being typed in.
  for (const el of wzContent.querySelectorAll('input[data-cfgnum]')) {
    if (el === document.activeElement) continue;
    const u = el.dataset.unit, known = u && UNIT_DEFS[u];
    const v = String(known ? roundDisplay(toDisplayUnit(wzVal(el.dataset.cfgnum), u), u) : wzVal(el.dataset.cfgnum));
    if (el.value !== v) el.value = v;
  }
}

function renderSettings() {
  // Re-read the open config panel(s) when a fresh config frame arrives.
  if (configDirty) {
    configDirty = false;
    if (vcPanel.classList.contains('open')) populateVehicleCfg(false);
    if (tcPanel.classList.contains('open')) populateToolCfg(false);
    if (saPanel.classList.contains('open')) populateScreenAlerts();
    if (asPanel.classList.contains('open')) populateAutoSteer(false);
    if (document.getElementById('viewsettings').classList.contains('open')) renderViewSettings();
  }
  // AutoSteer live telemetry rides every status frame (not just config changes).
  if (asPanel.classList.contains('open')) renderAutoSteerLive();
  // Smart-WAS stats refresh while its modal is open.
  if (swPanel.classList.contains('open')) populateSmartWas();
  // Steer Wizard: re-render while open (host-driven; live every frame).
  wizardDirty = false;
  if (wzOverlay.classList.contains('open')) renderWizard();
  // Re-read the hub when a fresh profiles frame arrives.
  if (profilesDirty) { profilesDirty = false; if (document.getElementById('vehtoolhub').classList.contains('open')) refreshHub(); }
  // Network IO panel: module/NTRIP readouts ride the Status frame → refresh each frame.
  if (nioPanel.classList.contains('open')) renderNetworkIo();
  if (ntsPanel.classList.contains('open')) renderNtripSettings();
  if (sdPanel.classList.contains('open')) renderSystemData();
  // The profile list marks the one in use: redrawn only when that changes, not on every frame.
  const useKey = ntripProfiles ? ntripProfiles.profiles.map(ntripProfileInUse).join() : '';
  if (useKey !== _ntripUseKey) { _ntripUseKey = useKey; if (document.getElementById('ntripprofiles').classList.contains('open')) renderNtripList(); }
  // NTRIP test result rides the Status frame while a test is in flight.
  if (_nteTestActive && document.getElementById('ntripeditor').classList.contains('open') && statusBar)
    document.getElementById(_nteTestWhich === 'fb' ? 'nte-fb-teststatus' : 'nte-teststatus').textContent = statusBar.ntripTestStatus || '';
  // Re-read the NTRIP list/editor fields when a fresh profiles frame arrives.
  if (ntripDirty) {
    ntripDirty = false;
    if (document.getElementById('ntripprofiles').classList.contains('open')) renderNtripList();
    if (document.getElementById('ntripeditor').classList.contains('open')) renderNteFields();
  }
  // Field Operations: status pill rides scene/status; Fields-and-Jobs lists re-read on a
  // fresh FieldOps frame.
  if (document.getElementById('fieldops').classList.contains('open')) renderFieldOps();
  if (document.getElementById('newfield').classList.contains('open')) renderNewField();
  if (fieldOpsDirty) {
    fieldOpsDirty = false;
    if (document.getElementById('fieldsandjobs').classList.contains('open')) renderFieldsAndJobs();
    if (document.getElementById('resumejob').classList.contains('open')) renderResumeJob();
  }
  if (agShareDirty) {
    agShareDirty = false;
    if (document.getElementById('agsettings').classList.contains('open')) renderAgSettings();
    if (document.getElementById('agupload').classList.contains('open')) renderAgUpload();
    if (document.getElementById('agdownload').classList.contains('open')) renderAgDownload();
  }
  // App Settings units/toggles ride config+status (every frame while open).
  if (document.getElementById('appsettings').classList.contains('open')) renderAppSettings();
  if (appInfoDirty) {
    appInfoDirty = false;
    if (document.getElementById('language').classList.contains('open')) renderLanguage();
    if (document.getElementById('logviewer').classList.contains('open')) renderLogViewer();
    if (document.getElementById('hotkeys').classList.contains('open')) renderHotkeys();
    if (document.getElementById('about').classList.contains('open')) renderAbout();
    if (document.getElementById('bugreport').classList.contains('open')) renderBugReport();
  }
}

// ---- Vehicle & Tool picker hub (Phase 9) — mirrors LoadVehicleToolDialogViewModel ----
const HUB = {
  summary: document.getElementById('vth-summary'),
  curVeh: document.getElementById('vth-curveh'), curTool: document.getElementById('vth-curtool'),
  vehList: document.getElementById('vth-vehlist'), toolList: document.getElementById('vth-toollist'),
  vehPrev: document.getElementById('vth-vehprev'), toolPrev: document.getElementById('vth-toolprev'),
  vehName: document.getElementById('vth-vehname'), toolName: document.getElementById('vth-toolname'),
  status: document.getElementById('vth-status'), load: document.getElementById('vth-load'),
};
function hubSend(cmd, arg) { transport.send(arg == null ? cmd : cmd + '|' + arg); }
function fillHubList(sel, entries, activeName) {
  const prev = sel.value;
  sel.innerHTML = '';
  for (const e of entries) {
    const o = document.createElement('option');
    o.value = e.name; o.textContent = e.name + (e.name === activeName ? '  ●' : '');
    sel.appendChild(o);
  }
  sel.value = entries.some(e => e.name === prev) ? prev : activeName;
}
function hubPreview(entries, name) { const e = entries.find(x => x.name === name); return e ? e.preview : ''; }
function hubUpdatePreviews() {
  if (!profiles) return;
  HUB.vehPrev.textContent = hubPreview(profiles.vehicles, HUB.vehList.value);
  HUB.toolPrev.textContent = hubPreview(profiles.tools, HUB.toolList.value);
  HUB.summary.textContent = tr('Selected: {name}', { name: (HUB.vehList.value || profiles.activeVehicle) +
    ' / ' + (HUB.toolList.value || profiles.activeTool) });
  HUB.load.disabled = (HUB.vehList.value === profiles.activeVehicle && HUB.toolList.value === profiles.activeTool);
}
function refreshHub() {
  if (!profiles) return;
  HUB.curVeh.textContent = 'Current: ' + profiles.activeVehicle;
  HUB.curTool.textContent = 'Current: ' + profiles.activeTool;
  fillHubList(HUB.vehList, profiles.vehicles, profiles.activeVehicle);
  fillHubList(HUB.toolList, profiles.tools, profiles.activeTool);
  hubUpdatePreviews();
}
HUB.vehList.addEventListener('change', () => { HUB.vehName.value = HUB.vehList.value; hubUpdatePreviews(); });
HUB.toolList.addEventListener('change', () => { HUB.toolName.value = HUB.toolList.value; hubUpdatePreviews(); });
for (const b of document.querySelectorAll('#vehtoolhub .hub-cbtn'))
  b.addEventListener('pointerdown', e => {
    e.stopPropagation();
    const kind = b.dataset.kind, act = b.dataset.act;
    const list = kind === 'vehicle' ? HUB.vehList : HUB.toolList;
    const nameInput = kind === 'vehicle' ? HUB.vehName : HUB.toolName;
    if (act === 'new') {
      const nm = (nameInput.value || '').trim();
      if (!nm) { HUB.status.textContent = tr('Type a name, then New'); return; }
      hubSend('profile.new', kind + '\t' + nm); HUB.status.textContent = tr('Creating {name}…', { name: nm });
    } else if (act === 'delete') {
      const nm = list.value;
      if (nm) showConfirm(tr('Delete Profile'), tr("Delete {kind} profile '{name}'?", { kind, name: nm }),
        () => hubSend('profile.delete', kind + '\t' + nm));
    } else if (act === 'rename') {
      const old = list.value, nw = (nameInput.value || '').trim();
      if (!old || !nw) { HUB.status.textContent = tr('Select a profile, type a new name, then Rename'); return; }
      hubSend('profile.rename', kind + '\t' + old + '\t' + nw);
    } else if (act === 'reset') {
      showConfirm(tr('Reset Profile'), tr('Reset Default {kind} profile?', { kind }), () => hubSend('profile.reset', kind));
    }
  });
HUB.load.addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (HUB.load.disabled || !profiles) return;
  hubSend('profile.load', (HUB.vehList.value || profiles.activeVehicle) + '\t' + (HUB.toolList.value || profiles.activeTool));
  lnCloseAll(); // native closes the picker on load
});
document.getElementById('vth-vehconfig').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (HUB.vehList.value) hubSend('profile.configureVehicle', HUB.vehList.value);
  lnOpen('vehiclecfg', 'ln-vehicle', () => populateVehicleCfg(true));
});
document.getElementById('vth-toolconfig').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (HUB.toolList.value) hubSend('profile.configureTool', HUB.toolList.value);
  lnOpen('toolcfg', 'ln-vehicle', () => populateToolCfg(true));
});
