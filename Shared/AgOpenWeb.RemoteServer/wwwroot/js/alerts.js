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
function syncGpsLost(s) {
  document.getElementById('gpslost').classList.toggle('show', !!s.gpsLost);
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

// An observer (a browser without control) sees everything and changes nothing (#397): the
// host drops its commands, so say why when a tap of its own was refused. The host's
// ObserverIds plus the hub's own control.* / diag.ping are what it may send.
const OBSERVER_IDS = new Set(['control.acquire', 'control.release', 'control.presence', 'diag.ping', 'boundary.refresh']);
let _viewOnlyToastT = -Infinity;
const _send = transport.send;
transport.send = cmd => {
  const id = String(cmd).split('|')[0];
  if (!iHoldControl && !OBSERVER_IDS.has(id)) {
    const now = performance.now();
    if (navigator.userActivation && navigator.userActivation.isActive && now - _viewOnlyToastT > 3000) {
      _viewOnlyToastT = now;
      showToast(tr('View only: another screen has control'));
    }
    return;
  }
  _send(cmd);
};
