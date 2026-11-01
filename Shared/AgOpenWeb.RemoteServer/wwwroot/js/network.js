// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// Network IO, NTRIP profiles and settings.

// ---- Network IO (Phase 9) — modules / scan / subnet / host IPs / NTRIP. Reads ride
// the Status frame; writes are config.set (module-present), net.scan, net.subnet and
// ntrip.* profile CRUD, all needing control. The NTRIP editor keeps its
// editing buffer client-side; Save sends every field at once. ----
const nioPanel = document.getElementById('networkio');
nioPanel.addEventListener('pointerdown', e => e.stopPropagation());
for (const cb of nioPanel.querySelectorAll('.nio-chk'))
  cb.addEventListener('change', () => cfgSend(cb.dataset.key, cb.checked ? '1' : '0'));
document.getElementById('nio-scan').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('net.scan'); });
const nioO1 = document.getElementById('nio-o1'), nioO2 = document.getElementById('nio-o2'), nioO3 = document.getElementById('nio-o3');
const nioSubnetBtn = document.getElementById('nio-subnet');
nioSubnetBtn.addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (!iHoldControl) return; // gated; host re-checks
  const o1 = clampOct(nioO1.value), o2 = clampOct(nioO2.value), o3 = clampOct(nioO3.value);
  showConfirm(tr('Change Module Subnet'),
    tr('Set ALL connected modules to subnet {subnet}.x and restart them?', { subnet: o1 + '.' + o2 + '.' + o3 }),
    () => transport.send('net.subnet|' + o1 + '.' + o2 + '.' + o3));
});
document.getElementById('nio-ntprofiles').addEventListener('pointerdown', e => { e.stopPropagation(); openNtripProfiles(); });
function clampOct(v) { let n = parseInt(v); if (!Number.isFinite(n)) n = 0; return Math.max(0, Math.min(255, n)); }
function nioDotColor(configured, ok) { return !configured ? '#6b7280' : (ok ? '#22c55e' : '#ef4444'); }
let _lastModuleSubnet = '';
function renderNetworkIo() {
  const s = statusBar; if (!s) return;
  const setMod = (cfgId, dotId, ipId, configured, ok, ip) => {
    const cb = document.getElementById(cfgId);
    if (document.activeElement !== cb) cb.checked = !!configured;
    document.getElementById(dotId).style.background = nioDotColor(configured, ok);
    document.getElementById(ipId).textContent = ip || '—';
  };
  setMod('nio-gps-cfg', 'nio-gps-dot', 'nio-gps-ip', s.gpsConf, s.gpsOk, s.gpsIp);
  setMod('nio-as-cfg', 'nio-as-dot', 'nio-as-ip', s.autoSteerConf, s.autoSteerOk, s.autoSteerIp);
  setMod('nio-ma-cfg', 'nio-ma-dot', 'nio-ma-ip', s.machineConf, s.machineOk, s.machineIp);
  setMod('nio-imu-cfg', 'nio-imu-dot', 'nio-imu-ip', s.imuConf, s.imuOk, s.imuIp);
  // Seed the subnet octets from the detected /24 when it changes (a PGN 203 reply),
  // unless the operator is editing that box — mirrors the native ModuleSubnet binding.
  // A module on a subnet this device isn't on (a phone hotspot, #334) can answer a scan
  // but its data never arrives. Offer the subnet to move it to, as AgIO's Auto Set does:
  // the scan reply's bytes 9–11, the subnet the scan came from (AgOpenGPS firmware). AiO
  // v26 repeats the module's own subnet there; then, with this device on exactly one
  // network, offer that network's /24.
  const usable = n => /^\d+\.\d+\.\d+$/.test(n || '') && !n.startsWith('127.') && !n.startsWith('169.254.') && !n.startsWith('0.');
  const hostNets = [...new Set((s.hostIps || '').split('\n')
    .filter(ip => /^\d+\.\d+\.\d+\.\d+$/.test(ip))
    .map(ip => ip.split('.').slice(0, 3).join('.')).filter(usable))];
  const fromScan = usable(s.scanReplySubnet) && s.scanReplySubnet !== s.moduleSubnet ? s.scanReplySubnet : '';
  const offer = fromScan || (hostNets.length === 1 && s.moduleSubnet && hostNets[0] !== s.moduleSubnet ? hostNets[0] : '');
  const elsewhere = !!s.moduleSubnet && (!!offer || (hostNets.length > 0 && !hostNets.includes(s.moduleSubnet)));
  const seed = offer || s.moduleSubnet;
  const mismatch = document.getElementById('nio-subnet-mismatch');
  mismatch.hidden = !elsewhere;
  if (elsewhere) mismatch.textContent = offer
    ? tr('The modules are on {m}.x, this device is on {h}.x. Send moves the modules to {h}.x.', { m: s.moduleSubnet, h: offer })
    : tr('The modules are on {m}.x, which isn\'t one of this device\'s networks.', { m: s.moduleSubnet });
  const seedKey = (s.moduleSubnet || '') + '|' + (seed || '');
  if (s.moduleSubnet && seedKey !== _lastModuleSubnet) {
    _lastModuleSubnet = seedKey;
    const p = seed.split('.');
    if (p.length === 3) {
      if (document.activeElement !== nioO1) nioO1.value = p[0];
      if (document.activeElement !== nioO2) nioO2.value = p[1];
      if (document.activeElement !== nioO3) nioO3.value = p[2];
    }
  }
  if (!nioO1.value && document.activeElement !== nioO1) nioO1.value = 192;
  if (!nioO2.value && document.activeElement !== nioO2) nioO2.value = 168;
  if (!nioO3.value && document.activeElement !== nioO3) nioO3.value = 5;
  document.getElementById('nio-hostips').textContent = s.hostIps || '—';
  document.getElementById('nio-ntrip-dot').style.background = s.ntripConnected ? '#22c55e' : '#6b7280';
  document.getElementById('nio-ntrip-status').textContent = ntripHeadline(s);
  const who = document.getElementById('nio-ntrip-who'), ns0 = s.ntripSession;
  who.textContent = s.ntripEnabled && ns0 && ns0.state !== 0 ? ntripWho(ns0) : '';
  who.hidden = !who.textContent;
  document.getElementById('nio-ntrip-bytes').textContent = Math.floor((s.ntripBytes || 0) / 1024).toLocaleString() + ' KB';
  // Where the corrections go: the GPS module's own address, or the subnet broadcast.
  document.getElementById('nio-ntrip-dest').textContent = !s.ntripDestination ? ''
    : s.ntripUnicast ? tr('Corrections go to the GPS module at {ip}', { ip: s.ntripDestination })
    : tr('Corrections are broadcast to {ip}', { ip: s.ntripDestination });
  const p100 = document.getElementById('nio-pgn100');
  if (document.activeElement !== p100) p100.checked = !!s.sendCorrectedPosition;
  renderNtripRx(s);
  renderNtripSwitch(s);
  nioSubnetBtn.classList.toggle('disabled', !iHoldControl);
}

// The NTRIP session (NTRIP plan, Phase 4): a plain headline and what to do about it, the
// service in use with the switch, the last switch notice, the checks and the mode. state is
// NtripState (0 stopped, 1 connecting, 2 connected, 3 streaming, 4 waiting, 5 gave up),
// lastError NtripCategory (1 no network, 2 unreachable, 3 login, 4 mount point, 5 busy,
// 6 no corrections, 7 stream lost, 8 not understood; -1 none).
const NT_OPEN = 2, NT_STREAMING = 3, NT_WAITING = 4, NT_GAVE_UP = 5;
function ntripHeadline(s) {
  const ns = s.ntripSession;
  if (!s.ntripEnabled) return tr('Off');
  if (!ns || ns.state === 0) return s.ntripStatus || tr('Not Connected');
  if (ns.state === NT_STREAMING) return ns.usingFallback ? tr('Corrections arriving (fallback)') : tr('Corrections arriving');
  if (ns.state === 1) return tr('Connecting…');
  if (ns.state === NT_OPEN) return tr('Connected, waiting for corrections');
  if (ns.lastError < 0) return s.ntripStatus || tr('Not Connected');
  switch (ns.lastError) {
    case 3: return tr('Login refused');
    case 4: return tr('Mount point not found');
    case 5: return tr('Service is busy');
    case 2: return tr('Cannot reach the service');
    case 1: return tr('No network');
    case 6: return tr('Connected, but no corrections');
    case 7: return tr('Corrections stopped');
    default: return tr('Service not understood');
  }
}
function ntripAdvice(s) {
  const ns = s.ntripSession;
  if (!s.ntripEnabled || !ns || (ns.state !== NT_WAITING && ns.state !== NT_GAVE_UP)) return '';
  const again = ns.state === NT_WAITING ? ' ' + tr('Trying again in {n} s.', { n: Math.round(ns.retrySeconds) }) : '';
  switch (ns.lastError) {
    case 3: return tr('Check the user name and password.') + ' ' + tr('Some casters also answer like this when the mount point is wrong.') + again;
    case 4: return tr('Check the mount point. Capitals matter.') + again;
    case 5: return tr('The service is busy, starting up, or this login is already in use.') + again;
    case 2: return tr('Check the address and the internet connection.') + again;
    case 1: return tr('There is no network connection.') + again;
    case 6: return tr('Some services need the position: check that the GPS has one.') + again;
    case 7: return tr('The connection dropped.') + again;
    default: return tr('Check the address, port and mount point.') + again;
  }
}
// What works and what does not, in the order a connection is made.
function ntripChecks(s) {
  const ns = s.ntripSession, r = s.ntripRtcm, open = ns.state === NT_OPEN || ns.state === NT_STREAMING, err = ns.lastError;
  const newest = pred => r ? r.types.filter(x => pred(x.type)).reduce((m, x) => Math.min(m, x.last), Infinity) : Infinity;
  const obs = newest(rtcmIsObs), base = newest(t => t === 1005 || t === 1006), session = r ? r.sessionSeconds : 0;
  // Nothing yet is "still waiting" until the stream has run for a while; once there is one, it is late when it is old.
  const late = (age, soon, old) => Number.isFinite(age) ? age > old : session > soon;
  const c = (ok, bad, text) => ({ cls: ok ? 'ok' : bad ? 'bad' : 'wait', text });
  return [
    c(open, err === 1 || err === 2, open ? tr('Connected to the service') : err === 1 || err === 2 ? tr('Cannot connect to the service') : tr('Connecting to the service')),
    c(open, err === 3, open ? tr('Logged in') : err === 3 ? tr('Login refused') : tr('Login not checked yet')),
    c(open, err === 4, open ? tr('Mount point found') : err === 4 ? tr('Mount point not found') : tr('Mount point not checked yet')),
    c(obs <= 5, open && (err === 6 || err === 7 || late(obs, 10, 10)),
      obs <= 5 ? tr('Corrections arriving') : open && late(obs, 10, 10) ? tr('No corrections arriving') : tr('Waiting for corrections')),
    c(base <= 60, open && late(base, 30, 60),
      base <= 60 ? tr('Base station position received') : open && late(base, 30, 60) ? tr('No base station position') : tr('Waiting for the base station position')),
    c(Number.isFinite(s.age) && s.age <= 5 && open, open && Number.isFinite(s.age) && s.age > 5,
      Number.isFinite(s.age) ? tr('Correction age {n} s', { n: s.age.toFixed(0) }) : tr('Correction age unknown')),
  ];
}
// The one switch of the Network IO card, only with a fallback. It always asks first: RTK may be lost.
function renderNtripSwitch(s) {
  const ns = s.ntripSession, btn = document.getElementById('nio-svc-switch');
  btn.hidden = !(s.ntripEnabled && ns && ns.hasFallback);
  if (btn.hidden) return;
  btn.textContent = ns.usingFallback ? tr('Switch to main') : tr('Switch to fallback');
  btn.classList.toggle('disabled', !iHoldControl);
}
// The profile and the service in use, as one line: "Home · MOUNT@caster.example:2101".
const ntripWho = ns => [ns.profile, ns.service].filter(Boolean).join(' · ');
function renderNtripSession(s) {
  const ns = s.ntripSession, on = !!s.ntripEnabled && !!ns;
  const advice = document.getElementById('nio-ntrip-advice');
  advice.textContent = ntripAdvice(s);
  advice.hidden = !advice.textContent;

  // Which service is in use (only with a fallback); the switch itself is on the Network IO card.
  const svcText = document.getElementById('nio-svc-text');
  const who = on ? ntripWho(ns) : '';
  svcText.hidden = !(on && (ns.hasFallback || who));
  if (!svcText.hidden) svcText.textContent = (ns.hasFallback ? (ns.usingFallback ? tr('Using the fallback service') : tr('Using the main service')) + (who ? ': ' : '') : '') + who;
  const note = document.getElementById('nio-ntrip-notice');
  const text = ns && ns.mainIsBack && !ns.notice ? tr('The main service answers again.') : (ns ? ns.notice : '');
  note.hidden = !text;
  note.textContent = text;

  const box = document.getElementById('nio-checks');
  const showChecks = on && ns.state !== 0;
  box.hidden = !showChecks;
  if (showChecks) {
    const rows = ntripChecks(s);
    document.getElementById('nio-checks-sum').textContent = tr('Checks: {ok} of {n} fine', { ok: rows.filter(x => x.cls === 'ok').length, n: rows.length });
    if (box.open) document.getElementById('nio-checks-rows').innerHTML = rows.map(x =>
      '<div class="nio-chk-row ' + x.cls + '"><span class="mark">' + (x.cls === 'ok' ? '✓' : x.cls === 'bad' ? '✕' : '…') + '</span><span>' + esc(x.text) + '</span></div>').join('');
  }

  const mode = ns && ns.profilePerField ? 1 : 0;
  for (const b of document.querySelectorAll('#nio-mode .cfg-act')) {
    b.classList.toggle('active', +b.dataset.v === mode);
    b.classList.toggle('disabled', !iHoldControl);
  }
  document.getElementById('nio-mode-hint').textContent = mode
    ? tr('A loaded field uses its own profile. The fallback service is not used.')
    : tr('One profile is in use, with its fallback service. Loading a field changes nothing.');
}
document.getElementById('nio-checks').addEventListener('toggle', () => { if (statusBar) renderNtripSession(statusBar); });
document.getElementById('nio-svc-switch').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const ns = statusBar && statusBar.ntripSession;
  if (!iHoldControl || !ns) return;
  const toMain = ns.usingFallback;
  showConfirm(toMain ? tr('Switch to the main service') : tr('Switch to the fallback service'),
    tr('GPS may lose RTK and the position may shift a little.') + ' ' + (toMain ? tr('Switch to the main service?') : tr('Switch to the fallback service?')),
    () => {
      // The service may have changed while the question was open (an automatic switch): then there is nothing left to do.
      const now = statusBar && statusBar.ntripSession;
      if (now && now.usingFallback === toMain) transport.send('ntrip.switch|' + (toMain ? 'main' : 'fallback'));
    });
});
for (const b of document.querySelectorAll('#nio-mode .cfg-act'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); if (iHoldControl) cfgSend('conn.ntripProfilePerField', b.dataset.v); });


// NTRIP Settings — chain sub-panel of Network IO: the connection in detail, the mode, the options and the
// profiles. The Network IO card keeps only the first lines and the two buttons.
const ntsPanel = document.getElementById('ntripsettings');
function openNtripSettings() { lnOpen('ntripsettings', 'ln-network', renderNtripSettings); }
document.getElementById('nio-ntset').addEventListener('pointerdown', e => { e.stopPropagation(); openNtripSettings(); });
document.getElementById('nts-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('networkio', 'ln-network', renderNetworkIo); });
document.getElementById('nts-x').addEventListener('pointerdown', e => { e.stopPropagation(); lnCloseAll(); });
for (const cb of ntsPanel.querySelectorAll('.nio-chk'))
  cb.addEventListener('change', () => cfgSend(cb.dataset.key, cb.checked ? '1' : '0'));
function renderNtripSettings() {
  const s = statusBar; if (!s) return;
  document.getElementById('nts-dot').style.background = s.ntripConnected ? '#22c55e' : '#6b7280';
  document.getElementById('nts-status').textContent = ntripHeadline(s);
  renderNtripSession(s);
  renderNtripRtcm(s);
  const bc = document.getElementById('nio-rtcm-bc');
  if (document.activeElement !== bc) bc.checked = !!s.rtcmBroadcast;
  const nen = document.getElementById('nio-ntrip-on');
  nen.textContent = s.ntripEnabled ? tr('NTRIP active') : tr('NTRIP disabled');
  nen.style.borderColor = s.ntripEnabled ? '#22c55e' : '';
}
// Active connects the default profile (even when its auto-connect is off), Disabled drops the connection.
document.getElementById('nio-ntrip-on').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (statusBar) cfgSend('conn.ntripEnabled', statusBar.ntripEnabled ? '0' : '1');
});

// What the caster is sending (RTCM plan, Phase 5): the receiver's fix and correction age,
// a plain warning when the base position or the observations are missing, and the message
// types with how often each comes. The receiver can keep reporting RTK Fixed for minutes
// with no corrections arriving, so the age and these warnings are what show a problem.
const RTCM_SYSTEMS = ['GPS', 'GLONASS', 'Galileo', 'SBAS', 'QZSS', 'BeiDou', 'NavIC'];
const rtcmIsMsm = t => t >= 1071 && t <= 1137 && t % 10 >= 1 && t % 10 <= 7;
const rtcmIsObs = t => (t >= 1001 && t <= 1004) || (t >= 1009 && t <= 1012) || rtcmIsMsm(t);
function rtcmDescribe(t) {
  if (rtcmIsMsm(t)) return tr('{system} observations (MSM{n})', { system: RTCM_SYSTEMS[Math.floor((t - 1070) / 10)] || '?', n: t % 10 });
  if (t >= 1001 && t <= 1004) return tr('GPS observations (legacy)');
  if (t >= 1009 && t <= 1012) return tr('GLONASS observations (legacy)');
  if (t >= 4001 && t <= 4095) return tr('Proprietary');
  switch (t) {
    case 1005: return tr('Base position');
    case 1006: return tr('Base position and antenna height');
    case 1007: case 1008: return tr('Antenna descriptor');
    case 1013: return tr('System parameters');
    case 1019: return tr('GPS ephemeris');
    case 1020: return tr('GLONASS ephemeris');
    case 1033: return tr('Receiver and antenna descriptor');
    case 1042: return tr('BeiDou ephemeris');
    case 1044: return tr('QZSS ephemeris');
    case 1045: case 1046: return tr('Galileo ephemeris');
    case 1230: return tr('GLONASS code-phase biases');
    default: return '';
  }
}
// The one thing wrong with the stream that the operator should know about, or ''.
function ntripRtcmWarning(r) {
  if (r.unframed) return tr('This mount point does not send RTCM 3. Its data is forwarded as it comes.');
  const newest = pred => r.types.filter(x => pred(x.type)).reduce((m, x) => Math.min(m, x.last), Infinity);
  const base = newest(t => t === 1005 || t === 1006), obs = newest(rtcmIsObs);
  if (obs === Infinity) return r.sessionSeconds > 10 ? tr('No satellite observations from the caster.') : '';
  if (obs > 10) return tr('No satellite observations for {n} s.', { n: Math.round(obs) });
  if (base === Infinity) return r.sessionSeconds > 30 ? tr('No base position from the caster. The receiver cannot get RTK without it.') : '';
  if (base > 60) return tr('No base position received for {n} s.', { n: Math.round(base) });
  return '';
}
// The receiver's fix and the age of its corrections: the line of the Network IO card.
function renderNtripRx(s) {
  const r = s.ntripConnected ? s.ntripRtcm : null;
  const rx = document.getElementById('nio-ntrip-rx');
  rx.textContent = r ? tr('Receiver: {fix}, correction age {age} s', { fix: s.fixText || '—', age: Number.isFinite(s.age) ? s.age.toFixed(0) : '—' }) : '';
  rx.classList.toggle('nio-aged', !!r && s.age > 5);
}
function renderNtripRtcm(s) {
  const r = s.ntripConnected ? s.ntripRtcm : null;
  const warn = document.getElementById('nio-ntrip-warn');
  const box = document.getElementById('nio-rtcm');
  box.hidden = !r;
  const w = r ? ntripRtcmWarning(r) : '';
  warn.hidden = !w;
  warn.textContent = w;
  if (!r) return;
  document.getElementById('nio-rtcm-sum').textContent =
    tr('{n} messages, {bad} bad checksums, {r} replaced by newer', { n: r.messages.toLocaleString(), bad: r.checksumFailures, r: r.notSent });
  if (!box.open) return;   // the rows are only built while the table is showing
  const secs = v => Number.isFinite(v) ? (v < 10 ? v.toFixed(1) : Math.round(v)) + ' s' : '—';
  // Ephemerides come in bursts, so a mean spacing says nothing about them. A row is marked
  // late only where lateness matters: observations and the base position.
  const bursty = t => t === 1019 || t === 1020 || (t >= 1041 && t <= 1046);
  const late = x => rtcmIsObs(x.type) ? x.last > 5 : (x.type === 1005 || x.type === 1006) && x.last > 60;
  document.getElementById('nio-rtcm-rows').innerHTML = r.types.map(x =>
    '<tr' + (late(x) ? ' class="stale"' : '') + '><td class="num" translate="no">' + x.type + '</td><td>' + esc(rtcmDescribe(x.type)) +
    '</td><td class="num" translate="no">' + (bursty(x.type) ? '—' : secs(x.every)) + '</td><td class="num" translate="no">' + secs(x.last) + '</td></tr>').join('');
}
document.getElementById('nio-rtcm').addEventListener('toggle', () => { if (statusBar) renderNtripRtcm(statusBar); });

// System Data — chain sub-panel of Network IO, opened from the GPS line's arrow. The AgIO /
// AgOpenGPS "System Data" forms in one card. Position, fix and speed are the values the
// status bar already has; the rest rides Status as systemData. Refreshed per Status frame
// (~2 Hz) while open.
const sdPanel = document.getElementById('systemdata');
const SD = {};
for (const id of ['lat', 'lon', 'e', 'n', 'alt', 'fix', 'sats', 'hdop', 'age', 'hz', 'missed', 'rej', 'joined', 'dropped', 'src', 'incomplete',
  'speed', 'roll', 'pitch', 'yaw', 'hdual', 'himu', 'hf2f', 'hused', 'sentences'])
  SD[id] = document.getElementById('sd-' + id);
document.getElementById('nio-gps-more').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('systemdata', 'ln-network', renderSystemData); });
document.getElementById('sd-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('networkio', 'ln-network', renderNetworkIo); });
document.getElementById('sd-x').addEventListener('pointerdown', e => { e.stopPropagation(); lnCloseAll(); });
function renderSystemData() {
  const s = statusBar; if (!s) return;
  const d = s.systemData, t = lastTick;
  const num = (v, dec, suffix) => Number.isFinite(v) ? v.toFixed(dec) + (suffix || '') : '—';
  const deg = v => Number.isFinite(v) ? (((v % 360) + 360) % 360).toFixed(1) + '°' : '—';
  const set = (el, text) => { if (el.textContent !== text) el.textContent = text; };
  set(SD.lat, num(s.lat, 7)); set(SD.lon, num(s.lon, 7));
  set(SD.e, t ? fmtUnit(t.e, 'm', 2) : '—'); set(SD.n, t ? fmtUnit(t.n, 'm', 2) : '—');
  set(SD.alt, Number.isFinite(s.altitude) ? fmtUnit(s.altitude, 'm', 1) : '—');
  set(SD.fix, s.fixText || '—');
  set(SD.sats, s.sats != null ? String(s.sats) : '—');
  set(SD.hdop, num(s.hdop, 2));
  set(SD.age, num(s.age, 1, ' s'));
  SD.age.classList.toggle('sd-bad', s.age > 5);
  set(SD.speed, t ? toDisplayUnit(t.speed * 3.6, 'kmh').toFixed(1) + ' ' + unitLabel('kmh') : '—');
  set(SD.roll, tick && typeof tick.roll === 'number' ? tick.roll.toFixed(1) + '°' : '—');
  set(SD.hused, t ? deg(t.heading * 180 / Math.PI) : '—');
  // A real module is sending when a sentence came in the last few seconds; otherwise the
  // rate and counters describe an old session and read as "—".
  const live = !!d && d.sentences.some(x => x.type !== 'REJECTED' && x.age < 5);
  set(SD.hz, live ? num(d.rateHz, 1, ' Hz') : '—');
  set(SD.missed, d ? String(d.missed) : '—');
  // Why lines were refused: a bad checksum is a corrupted or mis-split line (look at the
  // bridge); an unknown sentence is one this build does not decode (look at the receiver's
  // output set).
  const rejParts = d && d.rejected ? [d.badChecksum ? tr('{n} checksum', { n: d.badChecksum }) : '',
    d.unknownSentence ? tr('{n} unknown', { n: d.unknownSentence }) : ''].filter(Boolean) : [];
  set(SD.rej, d ? String(d.rejected) + (rejParts.length ? ' (' + rejParts.join(', ') + ')' : '') : '—');
  set(SD.joined, d ? String(d.joinedLines) : '—');
  set(SD.dropped, d ? String(d.droppedBytes) : '—');
  set(SD.incomplete, d ? String(d.incompleteEpochs) : '—');
  set(SD.src, d && d.source ? d.source : '—');
  set(SD.pitch, d ? num(d.pitch, 1, '°') : '—');
  set(SD.yaw, d ? num(d.yawRate, 1, ' °/s') : '—');
  set(SD.hdual, d ? deg(d.dualHeading) : '—');
  set(SD.himu, d ? deg(d.imuHeading) : '—');
  set(SD.hf2f, d ? deg(d.fixToFixHeading) : '—');
  SD.hdual.classList.toggle('sd-bad', !!s.dualHeadingMissing);
  const ago = v => tr('{n} s ago', { n: v < 10 ? v.toFixed(1) : Math.round(v) });
  const rows = (d ? d.sentences : []).map(x =>
    '<div class="sd-sent' + (x.age > 2 ? ' stale' : '') + '"><div class="sd-senthead"><span' + (x.type === 'REJECTED' ? '>' + esc(tr('Not accepted', {})) : ' translate="no">' + (x.type === 'INSPVAX' ? '#' : '$') + esc(x.type)) +
    '</span><span>' + esc(ago(x.age)) + '</span></div><div class="sd-senttext" translate="no">' + esc(x.text) + '</div></div>');
  const html = rows.length ? rows.join('')
    : '<div class="sa-hint">' + esc(s.gpsSentence === 'SIM' ? tr('The simulator is supplying the position; no sentence is being received.', {})
      : tr('No sentence has been received from the GPS module.', {})) + '</div>';
  // Leave the block alone while text in it is selected, so a sentence can be copied.
  const sel = window.getSelection && window.getSelection();
  if (!(sel && !sel.isCollapsed && SD.sentences.contains(sel.anchorNode))) SD.sentences.innerHTML = html;
}

// NTRIP Profiles — chain sub-panel of Network IO (mirrors NtripProfilesDialogPanel).
// Native chain model: opening REPLACES the parent (lnOpen closes everything else);
// Back reopens the parent fly-out (Network IO); Close → map.
let ntSelId = null;
function openNtripProfiles() { lnOpen('ntripprofiles', 'ln-network', renderNtripList); }
document.getElementById('nt-back').addEventListener('pointerdown', e => { e.stopPropagation(); openNtripSettings(); });
document.getElementById('nt-x').addEventListener('pointerdown', e => { e.stopPropagation(); lnCloseAll(); });
document.getElementById('nt-add').addEventListener('pointerdown', e => { e.stopPropagation(); openNtripEditor(null); });
document.getElementById('nt-edit').addEventListener('pointerdown', e => { e.stopPropagation(); const p = selectedNtProfile(); if (p) openNtripEditor(p); });
document.getElementById('nt-del').addEventListener('pointerdown', e => {
  e.stopPropagation(); const p = selectedNtProfile();
  if (p) showConfirm(tr('Delete NTRIP Profile'), tr("Delete NTRIP profile '{name}'?", { name: p.name }),
    () => { transport.send('ntrip.delete|' + p.id); ntSelId = null; });
});
document.getElementById('nt-default').addEventListener('pointerdown', e => {
  e.stopPropagation(); const p = selectedNtProfile(); if (p) transport.send('ntrip.setDefault|' + p.id);
});
let _ntripUseKey = '';
// The profile the client is connected with: by its name, which the host sends with the session.
function ntripProfileInUse(p) {
  const s = statusBar, ns = s && s.ntripSession;
  return !!(s && s.ntripEnabled && ns && ns.state !== 0 && ns.profile && ns.profile === p.name);
}
// Use: connect with the selected profile now. It asks first when a session is running: RTK may be lost.
document.getElementById('nt-use').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const p = selectedNtProfile(), s = statusBar;
  if (!p || !iHoldControl) return;
  if (!s || !s.ntripEnabled) { showToast(tr('NTRIP is disabled. Switch it on in NTRIP Settings first.')); return; }
  if (ntripProfileInUse(p)) return;
  const go = () => transport.send('ntrip.use|' + p.id);
  const ns = s.ntripSession;
  if (ns && ns.state !== 0) showConfirm(tr('Use this profile'), tr('GPS may lose RTK and the position may shift a little.') + ' ' + tr('Connect with {name}?', { name: p.name }), go);
  else go();
});
function selectedNtProfile() { return (ntripProfiles && ntripProfiles.profiles.find(p => p.id === ntSelId)) || null; }
function renderNtripList() {
  const list = document.getElementById('nt-list');
  list.innerHTML = '';
  const profs = ntripProfiles ? ntripProfiles.profiles : [];
  if (!profs.length) { list.innerHTML = '<div style="padding:18px;color:#8294ab;text-align:center">No profiles</div>'; return; }
  for (const p of profs) {
    const row = document.createElement('div');
    row.className = 'nio-ntrow' + (p.id === ntSelId ? ' sel' : '');
    row.innerHTML = '<div><div class="nt-name"></div><div class="nt-mount"></div></div>'
      + '<div class="nt-caster"></div><div class="nt-def">' + (p.isDefault ? '★' : '') + '</div>';
    row.querySelector('.nt-name').textContent = p.name;
    const inUse = ntripProfileInUse(p);
    row.querySelector('.nt-mount').textContent = p.mountPoint + (inUse ? ' · ' + tr('in use') : '');
    if (inUse) row.querySelector('.nt-name').style.color = '#22c55e';
    // With a fallback the column names both services; without one it is just the caster.
    const caster = row.querySelector('.nt-caster');
    const fbHost = p.fallback && p.fallback.host;
    for (const [label, host] of fbHost ? [[tr('Main:') + ' ', p.casterHost], [tr('FB:') + ' ', fbHost]] : [['', p.casterHost]]) {
      const line = document.createElement('div');
      line.textContent = label + host;
      line.title = line.textContent;
      caster.appendChild(line);
    }
    row.addEventListener('pointerdown', ev => { ev.stopPropagation(); ntSelId = p.id; renderNtripList(); });
    list.appendChild(row);
  }
}

// Edit NTRIP Profile — chain sub-panel of the profiles list (mirrors
// NtripProfileEditorPanel). Opening REPLACES the list; Back reopens the list; Close →
// map; Save returns to the list (native NavigateBack). Client-side buffer.
let nteBuf = null, _nteTestActive = false, _nteTestWhich = '';   // which Test button ran: '' main, 'fb' fallback
function openNtripEditor(p) {
  nteBuf = p
    ? { id: p.id, associatedFields: (p.associatedFields || []).slice() }
    : { id: '', associatedFields: [] };
  document.getElementById('nte-title').textContent = p ? tr('Edit NTRIP Profile') : tr('New NTRIP Profile');
  document.getElementById('nte-name').value = p ? p.name : 'New Profile';
  document.getElementById('nte-host').value = p ? p.casterHost : '';
  document.getElementById('nte-port').value = p ? p.casterPort : 2101;
  document.getElementById('nte-mount').value = p ? p.mountPoint : '';
  document.getElementById('nte-user').value = p ? p.username : '';
  document.getElementById('nte-pass').value = p ? p.password : '';
  document.getElementById('nte-auto').checked = p ? !!p.autoConnect : true;
  document.getElementById('nte-default').checked = p ? !!p.isDefault : false;
  const fb = p && p.fallback;
  document.getElementById('nte-fb-host').value = fb ? fb.host : '';
  document.getElementById('nte-fb-port').value = fb ? fb.port : 2101;
  document.getElementById('nte-fb-mount').value = fb ? fb.mount : '';
  document.getElementById('nte-fb-user').value = fb ? fb.username : '';
  document.getElementById('nte-fb-pass').value = fb ? fb.password : '';
  document.getElementById('nte-fb-auto').checked = fb ? !!fb.autoSwitch : false;
  document.getElementById('nte-fb-after').value = fb ? fb.after : 2;
  document.getElementById('nte-teststatus').textContent = '';
  document.getElementById('nte-fb-teststatus').textContent = '';
  _nteTestActive = false;
  _nteTestWhich = '';
  lnOpen('ntripeditor', 'ln-network', renderNteFields);
}
document.getElementById('nte-back').addEventListener('pointerdown', e => { e.stopPropagation(); openNtripProfiles(); });
document.getElementById('nte-x').addEventListener('pointerdown', e => { e.stopPropagation(); lnCloseAll(); });
function renderNteFields() {
  const box = document.getElementById('nte-fields');
  box.innerHTML = '';
  const fields = ntripProfiles ? ntripProfiles.availableFields : [];
  if (!fields.length) { box.innerHTML = '<div style="color:#8294ab;padding:4px">No fields</div>'; return; }
  for (const f of fields) {
    const lbl = document.createElement('label');
    const cb = document.createElement('input'); cb.type = 'checkbox';
    cb.checked = nteBuf.associatedFields.includes(f);
    cb.addEventListener('change', () => {
      if (cb.checked) { if (!nteBuf.associatedFields.includes(f)) nteBuf.associatedFields.push(f); }
      else nteBuf.associatedFields = nteBuf.associatedFields.filter(x => x !== f);
    });
    lbl.appendChild(cb); lbl.appendChild(document.createTextNode(f));
    box.appendChild(lbl);
  }
}
// Find mount points — the host reads the caster's source table and sends it nearest first; a tap fills the
// Mount field of the main service or the fallback.
let _ntmFor = '';
function findMounts(fb) {
  const p = fb ? 'nte-fb-' : 'nte-';
  const host = document.getElementById(p + 'host').value.trim();
  const port = document.getElementById(p + 'port').value || '2101';
  const user = document.getElementById(p + 'user').value, pass = document.getElementById(p + 'pass').value;
  _ntmFor = fb ? 'fallback' : 'main';
  document.getElementById('ntm-filter').value = '';
  if (!host) {
    ntripMounts = { seq: 0, forService: _ntmFor, status: 'error:' + tr('Error: Caster host is required'), items: [] };
  } else {
    ntripMounts = { seq: ntripMounts.seq, forService: _ntmFor, status: 'loading', items: [] };
    // pass is the host's mask for a stored password; the id lets the host use that one (#397)
    transport.send('ntrip.mountpoints|' + [host, port, user, pass, (nteBuf && nteBuf.id) || '', fb ? '1' : '0'].join('\t'));
  }
  renderNtripMounts();
  openDialog('dlg-ntmounts');
}
function renderNtripMounts() {
  const card = document.getElementById('dlg-ntmounts');
  if (!card.classList.contains('open') || ntripMounts.forService !== _ntmFor) return;
  const loading = ntripMounts.status === 'loading';
  const filter = document.getElementById('ntm-filter').value.trim().toLowerCase();
  const items = ntripMounts.items.filter(m => !filter || (m.mount + ' ' + m.name + ' ' + m.country).toLowerCase().includes(filter));
  const MAX_ROWS = 200;   // a big network has over a thousand: the nearest first, the rest through the search
  const shown = items.slice(0, MAX_ROWS);
  const partial = ntripMounts.status === 'partial';   // the list is shown all the same
  const count = items.length > MAX_ROWS ? tr('{n} mount points, showing {m}. Search to narrow it down.', { n: items.length, m: MAX_ROWS })
    : tr('{n} mount points', { n: items.length });
  document.getElementById('ntm-status').textContent = loading ? tr('Loading...')
    : partial ? count + ' ' + tr('Only part of the list arrived.')
    : ntripMounts.status.startsWith('error:') ? ntripMounts.status.slice(6)   // the caster's own reason, as the host worded it
    : count;
  const list = document.getElementById('ntm-list');
  list.innerHTML = '';
  for (const m of shown) {
    const row = document.createElement('button');
    row.className = 'ntm-row';
    const km = Number.isFinite(m.km) ? (m.km < 10 ? m.km.toFixed(1) : Math.round(m.km)) + ' km' : '';
    row.innerHTML = '<span class="ntm-top"><span class="ntm-mount" translate="no"></span><span class="ntm-km" translate="no"></span></span><span class="ntm-sub"></span>';
    row.querySelector('.ntm-mount').textContent = m.mount;
    row.querySelector('.ntm-km').textContent = km;
    row.querySelector('.ntm-sub').textContent = [m.name, m.format, m.systems, m.country, m.needsPosition ? tr('needs your position') : ''].filter(Boolean).join(' · ');
    row.addEventListener('pointerdown', e => {
      e.stopPropagation();
      document.getElementById(_ntmFor === 'fallback' ? 'nte-fb-mount' : 'nte-mount').value = m.mount;
      closeNtripMounts();
    });
    list.appendChild(row);
  }
}
document.getElementById('ntm-filter').addEventListener('input', renderNtripMounts);
// Closing the list also tells the host to drop it: it is not kept, and a client that connects later is not sent it.
function closeNtripMounts() { closeDialog(); transport.send('ntrip.mountpoints.clear'); }
document.getElementById('ntm-close').addEventListener('pointerdown', e => { e.stopPropagation(); closeNtripMounts(); });
document.getElementById('nte-find').addEventListener('pointerdown', e => { e.stopPropagation(); findMounts(false); });
document.getElementById('nte-fb-find').addEventListener('pointerdown', e => { e.stopPropagation(); findMounts(true); });
document.getElementById('nte-test').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const host = document.getElementById('nte-host').value.trim();
  const port = document.getElementById('nte-port').value || '2101';
  const mount = document.getElementById('nte-mount').value.trim();
  const user = document.getElementById('nte-user').value, pass = document.getElementById('nte-pass').value;
  const st = document.getElementById('nte-teststatus');
  if (!host) { st.textContent = tr('Error: Caster host is required'); return; }
  if (!mount) { st.textContent = tr('Error: Mount point is required'); return; }
  _nteTestActive = true;
  _nteTestWhich = '';
  for (const id of ['nte-teststatus', 'nte-fb-teststatus']) document.getElementById(id).textContent = '';
  st.textContent = tr('Testing connection...');
  // pass is the host's mask for a stored password; the id lets the host use that one (#397)
  transport.send('ntrip.test|' + [host, port, mount, user, pass, (nteBuf && nteBuf.id) || '', '0'].join('\t'));
});
document.getElementById('nte-fb-test').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const host = document.getElementById('nte-fb-host').value.trim();
  const port = document.getElementById('nte-fb-port').value || '2101';
  const mount = document.getElementById('nte-fb-mount').value.trim();
  const user = document.getElementById('nte-fb-user').value, pass = document.getElementById('nte-fb-pass').value;
  const st = document.getElementById('nte-fb-teststatus');
  if (!host) { st.textContent = tr('Error: Caster host is required'); return; }
  if (!mount) { st.textContent = tr('Error: Mount point is required'); return; }
  _nteTestActive = true;
  _nteTestWhich = 'fb';
  for (const id of ['nte-teststatus', 'nte-fb-teststatus']) document.getElementById(id).textContent = '';
  st.textContent = tr('Testing connection...');
  transport.send('ntrip.test|' + [host, port, mount, user, pass, (nteBuf && nteBuf.id) || '', '1'].join('\t'));
});
document.getElementById('nte-save').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (!nteBuf) return;
  const fields = [
    nteBuf.id || '',
    document.getElementById('nte-name').value.trim(),
    document.getElementById('nte-host').value.trim(),
    document.getElementById('nte-port').value || '2101',
    document.getElementById('nte-mount').value.trim(),
    document.getElementById('nte-user').value,
    document.getElementById('nte-pass').value,
    document.getElementById('nte-auto').checked ? '1' : '0',
    document.getElementById('nte-default').checked ? '1' : '0',
    nteBuf.associatedFields.join(','),
    // The fallback service; no host means none (the host removes it).
    document.getElementById('nte-fb-host').value.trim() ? '1' : '0',
    document.getElementById('nte-fb-host').value.trim(),
    document.getElementById('nte-fb-port').value || '2101',
    document.getElementById('nte-fb-mount').value.trim(),
    document.getElementById('nte-fb-user').value,
    document.getElementById('nte-fb-pass').value,
    document.getElementById('nte-fb-auto').checked ? '1' : '0',
    document.getElementById('nte-fb-after').value || '2',
  ];
  transport.send('ntrip.save|' + fields.join('\t'));
  openNtripProfiles(); // native SaveNtripProfileCommand ends in NavigateBack → list
});
