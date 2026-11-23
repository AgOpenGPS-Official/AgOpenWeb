// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// Host prompts and failure notices, alert sounds.

// ---- Host prompt + failure notifications (#109) ----------------------------
// The host's ShowConfirmationDialog / ShowErrorDialog (e.g. "GPS far from field",
// settings recovery, "No boundary") arrive as a Prompt frame. It stays up until the
// host clears it (kind 0), so a reconnecting browser still sees it. Only the browser
// holding control can answer (prompt.answer is gated); the seq stops a stale tap
// answering a newer prompt.
let hostPrompt = null;
const HP = {
  root: document.getElementById('hostprompt'), title: document.getElementById('hp-title'),
  msg: document.getElementById('hp-msg'), check: document.getElementById('hp-check'),
  checkbox: document.getElementById('hp-checkbox'), checkLabel: document.getElementById('hp-checklabel'),
  ok: document.getElementById('hp-ok'), cancel: document.getElementById('hp-cancel'),
  wait: document.getElementById('hp-wait'),
};
let hpShownSeq = -1;
function renderHostPrompt() {
  const p = hostPrompt;
  if (!p || !p.kind) { HP.root.classList.remove('open'); hpShownSeq = -1; return; }
  const isError = p.kind === 2;
  HP.title.textContent = p.title || (isError ? tr('Error') : tr('Confirm'));
  HP.msg.textContent = p.message || '';
  HP.check.hidden = isError || !p.checkboxLabel;
  HP.checkLabel.textContent = p.checkboxLabel || '';
  if (hpShownSeq !== p.seq) HP.checkbox.checked = !!p.checkboxChecked; // keep the operator's tick on re-render
  HP.cancel.hidden = isError;
  HP.ok.textContent = p.confirmLabel || (isError ? tr('OK') : tr('Yes'));
  HP.cancel.textContent = p.cancelLabel || tr('No');
  HP.ok.disabled = HP.cancel.disabled = !iHoldControl;
  HP.wait.hidden = iHoldControl;
  HP.wait.textContent = lastControl.held
    ? tr('Waiting for {name} to answer', { name: lastControl.holderName || tr('the operator', {}) })
    : tr('Take control to answer');
  hpShownSeq = p.seq;
  HP.root.classList.add('open');
}
function answerHostPrompt(yes) {
  const p = hostPrompt;
  if (!p || !p.kind || !iHoldControl) return;
  transport.send('prompt.answer|' + p.seq + ',' + (yes ? 1 : 0) + ',' + (HP.checkbox.checked ? 1 : 0));
  hostPrompt = null; renderHostPrompt(); // the host's cleared frame follows
}
HP.root.addEventListener('pointerdown', e => e.stopPropagation()); // no dismiss, no map pan
HP.ok.addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); answerHostPrompt(true); });
HP.cancel.addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); answerHostPrompt(false); });

let toastTimer = null;
// AiO board message (PGN 221): AgOpenGPS's lblHardwareMessage — black text on salmon
// (warning) or bisque, shown for the seconds the module asks (#110).
let hwMsgTimer = 0;
function showHardwareMessage(text, seconds, warning) {
  const el = document.getElementById('hwmsg');
  el.textContent = text;
  el.style.background = warning ? 'salmon' : 'bisque';
  el.classList.add('show');
  clearTimeout(hwMsgTimer);
  if (seconds > 0) hwMsgTimer = setTimeout(() => el.classList.remove('show'), seconds * 1000);
}
// Dual GPS on, but the receiver sends no antenna heading ($PANDA, $GNTHS void, HPR
// quality 0), so the host uses the single-antenna heading (#157). The host holds the flag
// back while the receiver starts up (#313). Toast once each time it starts; take it down
// if a fix with a heading clears it while still showing.
const DUAL_HEADING_MISSING_MSG =
  tr("Dual GPS is on, but the receiver isn't sending a dual-antenna heading. Using single-antenna heading.");
let dualHeadingMissingShown = false;
function syncDualHeadingWarning(s) {
  const missing = !!s.dualHeadingMissing;
  if (missing === dualHeadingMissingShown) return;
  dualHeadingMissingShown = missing;
  if (missing) { showToast(DUAL_HEADING_MISSING_MSG, 10000); return; }
  const t = document.getElementById('toast');
  if (toastMsg === DUAL_HEADING_MISSING_MSG) { clearTimeout(toastTimer); t.classList.remove('show'); }
}
// GPS data lost: the host's debounced flag (it has already switched autosteer and the
// sections off); the panel clears as soon as data flows again, module or simulator.
// A link to the modules as the operator would say it: "cable (Ethernet 2)", "Wi-Fi (Wi-Fi)".
const LINK_CABLE = 1, LINK_WIFI = 2;
function linkWords(name, kind) {
  return kind === LINK_CABLE ? tr('cable ({name})', { name }) : kind === LINK_WIFI ? tr('Wi-Fi ({name})', { name }) : name;
}
// Cable and Wi-Fi: the fix is the Wi-Fi, never the cable. Module data needs the cable: on the Wi-Fi alone
// it comes late and in bursts (1.5 s, 8 at a time, half lost on the tester's router), and with one link
// left there is nothing to detect, so unplugging the cable would leave the app guiding on it unwarned.
function twoLinksCableAndWiFi(l) {
  return (l.kindA | l.kindB) === (LINK_CABLE | LINK_WIFI);
}
function twoLinksWho(l) {
  return tr('{ip} is heard by {a} and by {b}.', { ip: l.module, a: linkWords(l.linkA, l.kindA), b: linkWords(l.linkB, l.kindB) });
}
function syncGpsLost(s) {
  // A module on two network links: the host uses nothing from it and holds AutoSteer and the
  // sections off until one link is disconnected; this explains the GPS lost, so it takes its place.
  const two = !!s.onTwoLinks;
  document.getElementById('twolinks').classList.toggle('show', two);
  if (two) {
    const now = (s.secondLinks || []).filter(l => l.now);
    document.getElementById('twolinks-who').textContent = now.map(twoLinksWho).join(' ');
    const cableAndWiFi = now.length > 0 && now.every(twoLinksCableAndWiFi);
    document.getElementById('twolinks-fix').textContent = cableAndWiFi
      ? tr('Turn the Wi-Fi off (or take the tablet off that network) and keep the cable.')
      : tr('Disconnect one of the two.');
  }
  document.getElementById('gpslost').classList.toggle('show', !!s.gpsLost && !two);
}
let toastMsg = '';
function showToast(msg, ms = 4000) {
  const t = document.getElementById('toast');
  toastMsg = msg; // what was asked for; the element's text may be a translation
  t.textContent = msg; t.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.remove('show'), ms);
}

// ---- Alert sounds ----------------------------------------------------------
// The host (which may be a headless box with no speaker) decides WHAT is audible
// — it already applied each sound's config toggle — and pushes a SoundEffect id.
// We just play the matching .wav here, on the operator's device. Effect ids match
// the C# SoundEffect enum order. Browsers block audio until the page has had a user
// gesture, so we prime the elements on the first pointer/key event; until then an
// alert that fires before any interaction is silently dropped (unavoidable on web).
const Sounds = (() => {
  // index === (int)SoundEffect. Files as AgOpenGPS CSound: TF012 = U-turn failed,
  // Alarm10 = boundary alarm and the U-turn approach alarm (#110).
  const FILES = [
    'Alarm10',    // 0 BoundaryAlarm
    'TF012',      // 1 UTurnTooClose
    'SteerOn',    // 2 AutoSteerOn
    'SteerOff',   // 3 AutoSteerOff
    'HydUp',      // 4 HydraulicLiftUp
    'HydDown',    // 5 HydraulicLiftDown
    'rtk_lost',   // 6 RtkLost
    'rtk_back',   // 7 RtkRecovered
    'SectionOn',  // 8 SectionOn
    'SectionOff', // 9 SectionOff
    'Headland',   // 10 Headland
    'Alarm10',    // 11 YouTurnApproach
  ];
  const cache = new Map();   // name -> HTMLAudioElement (fallback path)
  let unlocked = false;

  // Web Audio: each .wav is fetched and DECODED ONCE into an AudioBuffer, and a play is
  // then just a buffer source — no work on the main thread. The old path cloned a
  // preloaded <audio> element per play, and a clone carries no decoded data, so every
  // alert re-decoded its file inline: a visible frame hitch on the bigger ones (Alarm10
  // is 335 KB) right as the sound started (#150). The element path stays as a fallback
  // for engines where decodeAudioData isn't available or fails.
  const AC = window.AudioContext || window.webkitAudioContext;
  const buffers = new Map();  // name -> AudioBuffer
  let actx = null;
  function ensureCtx() {
    if (!actx && AC) { try { actx = new AC(); } catch (_) { actx = null; } }
    return actx;
  }
  function decodeInto(name) {
    const ctx = ensureCtx();
    if (!ctx || buffers.has(name)) return;
    buffers.set(name, null); // in flight — don't fetch twice
    fetch('/sounds/' + name + '.wav')
      .then(r => r.arrayBuffer())
      .then(b => new Promise((res, rej) => { const p = ctx.decodeAudioData(b, res, rej); if (p && p.then) p.then(res, rej); }))
      .then(buf => buffers.set(name, buf))
      .catch(() => buffers.delete(name)); // fall back to the element path
  }

  function el(name) {
    let a = cache.get(name);
    if (!a) { a = new Audio('/sounds/' + name + '.wav'); a.preload = 'auto'; cache.set(name, a); }
    return a;
  }
  // Preload every distinct file so the first real alert isn't delayed by a fetch.
  for (const n of new Set(FILES)) { el(n); decodeInto(n); }

  function unlock() {
    if (unlocked) return;
    unlocked = true;
    // A context created before any gesture starts suspended; resume it on the first one.
    const ctx = ensureCtx();
    if (ctx && ctx.state === 'suspended') ctx.resume().catch(() => {});
    for (const n of new Set(FILES)) decodeInto(n);
    // Nudge each element into a "played once" state so later programmatic play()
    // (not tied to a gesture) is allowed by the autoplay policy. Do it MUTED: the pause
    // lands asynchronously in .then(), so an unmuted prime plays a burst of every alert
    // audibly on the first UI click — notably the Hyd up/down 3-pt-hitch sounds. Muted
    // playback still primes the element (and is always autoplay-allowed). Real plays clone
    // the element, so they're unaffected by this temporary mute; restore it afterward anyway.
    for (const a of cache.values()) {
      a.muted = true;
      a.play().then(() => { a.pause(); a.currentTime = 0; a.muted = false; }).catch(() => { a.muted = false; });
    }
    window.removeEventListener('pointerdown', unlock);
    window.removeEventListener('keydown', unlock);
  }
  window.addEventListener('pointerdown', unlock, { passive: true });
  window.addEventListener('keydown', unlock);

  return {
    play(id) {
      const name = FILES[id];
      if (!name) return;
      // Decoded buffer → a source node costs nothing to start, and repeats overlap.
      const buf = buffers.get(name);
      if (actx && buf) {
        try {
          if (actx.state === 'suspended') actx.resume().catch(() => {});
          const src = actx.createBufferSource();
          src.buffer = buf;
          src.connect(actx.destination);
          src.start();
          return;
        } catch (_) { /* fall through to the element path */ }
      }
      // Fallback: clone the preloaded element so rapid repeats overlap.
      try { el(name).cloneNode().play().catch(() => {}); } catch { /* not ready */ }
    },
  };
})();

// Mobile auto-quality. Phones/tablets get GPU-overloaded at Ultra resolution (large
// coverage + imagery textures), so on connect a mobile client asks the host to CAP the
// display quality at Medium (multiplier 2.5). The host command only ever coarsens and is
// idempotent, so it never raises a manually-chosen lower quality and never fights a manual
// change made afterwards (we send it ONCE per session). Desktop browsers are left untouched.
// iPadOS reports a desktop ("Macintosh") UA, so also treat a touch-capable "Macintosh".
const IS_MOBILE = /Mobi|Android|iPhone|iPad|iPod/i.test(navigator.userAgent)
  || (navigator.maxTouchPoints > 1 && /Macintosh/.test(navigator.userAgent));
let mobileQualityCapped = false;
function applyMobileQualityCap() {
  if (mobileQualityCapped || !IS_MOBILE) return;
  mobileQualityCapped = true;
  transport.send('display.capResolution|2.5'); // 2.5 = Medium
}

// The largest texture this browser's GPU holds (often 4096 on tablets). The host keeps the
// coverage grid within it, so coverage always fits one GPU render target; a bigger grid
// falls back to a 2D canvas that is copied whole several times over and can get a tablet's
// web view killed for memory. Read from CanvasKit's own context once it exists; sent once.
let maxTextureSide = 0;
let maxTextureSent = false;
function readMaxTextureSide() {
  try {
    // The context CanvasKit made: asking for the same type returns it, not a new one.
    const gl = ckcv.getContext('webgl2') || ckcv.getContext('webgl');
    if (!gl) return 0;
    return Math.min(gl.getParameter(gl.MAX_TEXTURE_SIZE), gl.getParameter(gl.MAX_RENDERBUFFER_SIZE)) | 0;
  } catch (e) { return 0; }
}
function reportMaxTexture() {
  if (maxTextureSent || !iHoldControl || maxTextureSide <= 0) return;
  maxTextureSent = true;
  transport.send('display.maxTexture|' + maxTextureSide);
}

// How this browser draws the map, for the bug report dump (system_info.txt on the host): the
// WebGL renderer (a tablet without a GPU driver shows Mesa's "llvmpipe": the map is drawn by
// the CPU and will never be smooth), its texture limit, the pixel ratio and canvas, and the
// frame rate and draw time it reaches. Every browser sends it, observers too: it describes the
// sender's own device. Sent from frame.js shortly after CanvasKit is up, then every minute,
// and just before a bug report is made.
let _drawMsSum = 0, _drawMsN = 0, _drawMsMax = 0;
function noteDrawMs(ms) { _drawMsSum += ms; _drawMsN++; if (ms > _drawMsMax) _drawMsMax = ms; }
// Where the rest of a frame's time goes (#922: a tablet at 1-2 frames a second whose map took
// 14 ms to draw). Counted since the last report: the frames drawn and the time from one to the
// next, how often the browser offered a frame at all (rAF), the time in the camera and in the
// DOM overlays, the time taken over the host's frames and between its ticks, and the browser's
// own count of tasks that held the page for over 50 ms. A slow page with a fast draw shows here
// whether the browser stopped offering frames (the GPU or the WebView), the page's own code
// held it, or the host's ticks came late.
const mkStat = () => ({ n: 0, sum: 0, max: 0 });
function addStat(s, v) { s.n++; s.sum += v; if (v > s.max) s.max = v; }
const statText = s => (s.sum / s.n).toFixed(1) + ' / ' + s.max.toFixed(1);
let _ft = null;
function newFrameTimes() {
  return { t0: performance.now(), raf: 0, gap: mkStat(), over100: 0, over500: 0, cam: mkStat(),
           overlay: mkStat(), host: mkStat(), tickGap: mkStat(), longTask: mkStat(),
           hid: document.hidden };
}
let _ftLastFrame = 0, _ftLastTick = 0;
function frameTimes() { return _ft || (_ft = newFrameTimes()); }
function noteRaf() { frameTimes().raf++; }
function noteFrameStart(now) {
  const f = frameTimes();
  if (_ftLastFrame) {
    const gap = now - _ftLastFrame;
    addStat(f.gap, gap);
    if (gap > 100) f.over100++;
    if (gap > 500) f.over500++;
  }
  _ftLastFrame = now;
}
function noteCameraMs(ms) { addStat(frameTimes().cam, ms); }
function noteOverlayMs(ms) { addStat(frameTimes().overlay, ms); }
function noteHostFrame(ms, isTick) {
  const f = frameTimes();
  addStat(f.host, ms);
  if (!isTick) return;
  const now = performance.now();
  if (_ftLastTick) addStat(f.tickGap, now - _ftLastTick);
  _ftLastTick = now;
}
// A hidden page gets no frames and its timers are slowed: the wait is not a slow frame.
document.addEventListener('visibilitychange', () => { frameTimes().hid = true; _ftLastFrame = 0; _ftLastTick = 0; });
// The slowest stretch since the page loaded, kept beside the newest: a report is written a
// minute or two after the map was slow, with the report's own dialog open.
let _ftSlowest = null;
try {
  new PerformanceObserver(list => { for (const e of list.getEntries()) addStat(frameTimes().longTask, e.duration); })
    .observe({ type: 'longtask', buffered: true });
} catch (e) { /* this browser doesn't report long tasks */ }
function glInfo() {
  // CanvasKit's own context when it has one (asking for the same type returns it); otherwise
  // a throwaway canvas, so a probe never takes the map canvas before CanvasKit does.
  const tryOn = cv => {
    try {
      let gl = cv.getContext('webgl2');
      if (gl) return { gl, kind: 'webgl2' };
      gl = cv.getContext('webgl');
      if (gl) return { gl, kind: 'webgl1' };
    } catch (e) { /* no WebGL */ }
    return null;
  };
  const got = (grCtx && tryOn(ckcv)) || tryOn(document.createElement('canvas'));
  if (!got) return { kind: 'none' };
  const gl = got.gl;
  const dbg = gl.getExtension('WEBGL_debug_renderer_info');
  return {
    kind: got.kind,
    renderer: dbg ? gl.getParameter(dbg.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER),
    vendor: dbg ? gl.getParameter(dbg.UNMASKED_VENDOR_WEBGL) : gl.getParameter(gl.VENDOR),
    version: gl.getParameter(gl.VERSION),
    maxTexture: gl.getParameter(gl.MAX_TEXTURE_SIZE),
  };
}
function clientDiagReport() {
  const f = [];
  const put = (k, v) => { if (v !== undefined && v !== null && v !== '') f.push(k + '=' + String(v).replace(/[\t\r\n]/g, ' ')); };
  let g;
  try { g = glInfo(); } catch (e) { g = { kind: 'error ' + (e && e.message || e) }; }
  put('gl', g.kind);
  put('renderer', g.renderer);
  put('vendor', g.vendor);
  put('glVersion', g.version);
  put('maxTexture', g.maxTexture);
  put('fps', fps.toFixed(1));
  if (_drawMsN > 0) put('drawMs', (_drawMsSum / _drawMsN).toFixed(1) + ' / ' + _drawMsMax.toFixed(1));
  const ft = frameTimes(), secs = (performance.now() - ft.t0) / 1000;
  if (secs > 0.5) {
    const rate = ft.gap.n / secs, s = statText;
    put('frames', rate.toFixed(1) + '/s over ' + secs.toFixed(0) + ' s' + (ft.hid ? ', page hidden in it' : ''));
    put('raf', (ft.raf / secs).toFixed(1) + '/s');
    if (ft.gap.n) put('frameGapMs', s(ft.gap) + ', ' + ft.over100 + ' over 100, ' + ft.over500 + ' over 500');
    if (ft.cam.n) put('cameraMs', s(ft.cam));
    if (ft.overlay.n) put('overlayMs', s(ft.overlay));
    if (ft.host.n) put('hostFrameMs', s(ft.host) + ', ' + (ft.host.n / secs).toFixed(0) + '/s');
    if (ft.tickGap.n) put('tickGapMs', s(ft.tickGap));
    put('longTasks', ft.longTask.n ? ft.longTask.n + ', ' + s(ft.longTask) + ' ms' : '0');
    // Twenty seconds at least, the page shown throughout: a short or hidden stretch says nothing.
    if (secs >= 20 && !ft.hid && ft.gap.n && (!_ftSlowest || rate < _ftSlowest.rate)) {
      _ftSlowest = { rate, at: Date.now(), text: rate.toFixed(1) + '/s over ' + secs.toFixed(0) + ' s; offered '
        + (ft.raf / secs).toFixed(1) + '/s; frame to frame ' + s(ft.gap) + ', ' + ft.over500 + ' over 500'
        + (_drawMsN ? '; draw ' + (_drawMsSum / _drawMsN).toFixed(1) + ' / ' + _drawMsMax.toFixed(1) : '')
        + (ft.cam.n ? '; camera ' + s(ft.cam) : '') + (ft.overlay.n ? '; overlays ' + s(ft.overlay) : '')
        + (ft.host.n ? '; host frames ' + s(ft.host) : '') + (ft.tickGap.n ? '; ticks ' + s(ft.tickGap) : '')
        + '; held ' + (ft.longTask.n ? ft.longTask.n + ', ' + s(ft.longTask) : '0') };
    }
  }
  if (_ftSlowest) put('slowest', _ftSlowest.text + '; ' + Math.round((Date.now() - _ftSlowest.at) / 60000) + ' min ago');
  put('canvas', ckcv.width + 'x' + ckcv.height);
  put('dpr', (window.devicePixelRatio || 1).toFixed(2) + (dpr !== (window.devicePixelRatio || 1) ? ' (drawn at ' + dpr + ')' : ''));
  put('screen', screen.width + 'x' + screen.height);
  put('skia', ckStatus);
  put('ua', navigator.userAgent);
  _drawMsSum = 0; _drawMsN = 0; _drawMsMax = 0; // the next report covers the frames since this one
  _ft = null;
  return f.join('\t').slice(0, 1900); // the host keeps 2000 (ClientDiagnostics)
}
function sendClientDiag() { try { transport.send('diag.client|' + clientDiagReport()); } catch (e) { /* diagnostics only */ } }

// An observer (a browser without control) sees everything and changes nothing (#397): the
// host drops its commands, so say why when a tap of its own was refused. The host's
// ObserverIds plus the hub's own control.* / diag.ping are what it may send.
const OBSERVER_IDS = new Set(['control.acquire', 'control.release', 'control.presence', 'diag.ping', 'diag.client', 'boundary.refresh', 'lists.refresh', 'import.scan']);
let _viewOnlyToastT = -Infinity;
const _send = transport.send;
transport.send = cmd => {
  const id = String(cmd).split('|')[0];
  if (!iHoldControl && !OBSERVER_IDS.has(id)) {
    const now = performance.now();
    // A setting typed into a box is always the operator's own edit (sent when the box loses
    // focus or on Enter, often after the tap's user activation has lapsed).
    const edit = id === 'config.set';
    if ((edit || (navigator.userActivation && navigator.userActivation.isActive)) && now - _viewOnlyToastT > 3000) {
      _viewOnlyToastT = now;
      showToast(tr('View only: another screen has control'));
    }
    if (edit) {
      // Put the box back to the host's value: it was not saved. The open panel re-reads the
      // config on the next frame, skipping a box that still has the focus, so let go of it.
      const a = document.activeElement;
      if (a && a.tagName === 'INPUT' && a.blur) a.blur();
      configDirty = true;
    }
    return;
  }
  _send(cmd);
};
