// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// The config controls every settings panel uses: read, send, save, wire and fill.

function cfgGet(key) {
  if (!config) return undefined;
  // Write-only preset derived from the offset's sign, so its buttons can show which is active (#238).
  if (key === 'vehicle.antennaSide') {
    const off = config.vehicle && config.vehicle.antennaOffset;
    return typeof off !== 'number' ? undefined : off < -0.005 ? 'left' : off > 0.005 ? 'right' : 'center';
  }
  // The same for the tool's pivot direction: positive is the working line behind the tool's
  // wheels, negative ahead of them (AgOpenGPS setTool_trailingToolToPivotLength).
  if (key === 'tool.pivotSide') {
    const d = config.tool && config.tool.trailingToolToPivotLength;
    return typeof d !== 'number' ? undefined : d > 0.005 ? 'behind' : d < -0.005 ? 'ahead' : 'zero';
  }
  const p = key.split('.'); return config[p[0]] && config[p[0]][p[1]];
}
// A setting changed since the settings panel opened: closing the Vehicle, Tool or AutoSteer
// panel then saves the profile, as AgOpenGPS does when its settings form closes. Unsaved, a
// change was lost when the key went off before the exit save (POWERCUT-UNSAVED-PROFILE-EDIT-LOST).
let cfgEditedSinceOpen = false;
function cfgSend(key, val) { cfgEditedSinceOpen = true; transport.send('config.set|' + key + ':' + val); }
// Save Profile acts on press, but the box still being typed in sends its value only when it loses
// focus, which comes after the press: the save went out first and missed the last edit. Blur it
// before saving so its 'change' reaches the host ahead of the save.
function saveProfile() {
  const a = document.activeElement;
  if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) a.blur();
  transport.send('profile.save');
  cfgEditedSinceOpen = false;
}

// Generic config controls keyed by data-key="<section>.<field>" — reused by the
// Vehicle and Tool panels. Tab strips, selects, sliders and dynamic lists wire per
// panel. cfg-typebtn active state compares the config value to data-active (falling
// back to data-val) so name-valued buttons (tool type) can match an int field.
// Slider readout formatter (data-fmt): f0/f1/f2 fixed decimals, p0 = value×100 %,
// pct = value %, deg = value °. Used by generic .cfg-slider controls.
function fmtRo(v, fmt) {
  const n = Number(v);
  switch (fmt) {
    case 'f0': return n.toFixed(0);
    case 'f1': return n.toFixed(1);
    case 'f2': return n.toFixed(2);
    case 'p0': return Math.round(n * 100) + '%';
    case 'pct': return Math.round(n) + '%';
    case 'deg': return Math.round(n) + '°';
    default: return String(v);
  }
}
function wireCfgControls(panel) {
  // A data-unit input holds a DISPLAY value; readUnitInput converts it back to the
  // stored metric unit (and rounds whole-unit fields). Without data-unit it is the
  // old raw parseFloat, so untagged controls are unaffected.
  for (const inp of panel.querySelectorAll('.cfg-num'))
    inp.addEventListener('change', () => { const v = readUnitInput(inp); if (Number.isFinite(v)) cfgSend(inp.dataset.key, v); });
  for (const b of panel.querySelectorAll('.cfg-tgl'))
    b.addEventListener('pointerdown', e => { e.stopPropagation(); cfgSend(b.dataset.key, b.classList.contains('active') ? '0' : '1'); });
  for (const b of panel.querySelectorAll('.cfg-typebtn'))
    b.addEventListener('pointerdown', e => { e.stopPropagation(); cfgSend(b.dataset.key, b.dataset.val); });
  // .cfg-act = config.set action buttons; .rn-gated ones carry data-cmd (a gated
  // command, not a config key) and are wired separately, so exclude them here.
  for (const b of panel.querySelectorAll('.cfg-act[data-key]:not(.rn-gated)')) // keyless = wired by hand
    b.addEventListener('pointerdown', e => { e.stopPropagation(); cfgSend(b.dataset.key, b.dataset.val); });
  for (const sel of panel.querySelectorAll('.cfg-isel'))
    sel.addEventListener('change', () => cfgSend(sel.dataset.key, sel.value));
  // Sliders with a data-fmt are generic: drag updates the readout live; the value
  // commits on release ('change') to avoid flooding the bridge. (Sliders without
  // data-fmt — e.g. the Vehicle heading-fusion slider — are wired by hand.)
  for (const s of panel.querySelectorAll('.cfg-slider[data-fmt]')) {
    const ro = panel.querySelector('.cfg-ro[data-for="' + s.dataset.key + '"]');
    guardSliderTap(s, ro); // first: its listeners run before the two below
    s.addEventListener('input', () => { if (ro) ro.textContent = fmtRo(s.value, s.dataset.fmt); });
    s.addEventListener('change', () => cfgSend(s.dataset.key, s.value));
    wireSliderNudge(s, ro);
  }
}
// A press on a slider's bar away from its thumb jumped the value to that spot and sent it: one
// stray tap set Ackermann 0 and Max Steer Angle 61 on a tester's tractor (13 Nov 2026: the
// tablet's keyboard had covered the boxes; a tap on the bar at 64 % gives 61-62, at its left
// end 0). Now only the thumb moves a slider: a press on the bar leaves the value as it was.
// Dragging the thumb, − / + and typing the value work as before.
const SLIDER_THUMB_REACH_PX = 22; // half the thumb (8 px) and a finger's slack
// In screen pixels, as clientX: the panels grow with the UI size (uiZoom), and some engines
// report a zoomed element's rect unzoomed (zoomRect).
function sliderThumbX(s) {
  const r = zoomRect(s), min = Number(s.min), max = Number(s.max), thumb = 16 * uiZoom;
  const f = max > min ? (Number(s.value) - min) / (max - min) : 0;
  return r.left + thumb / 2 + f * (r.width - thumb);
}
function guardSliderTap(s, ro) {
  let held = null; // the value when a press began on the bar, not on the thumb
  const keep = e => {
    if (held === null) return;
    e.stopImmediatePropagation();         // neither the readout nor cfgSend sees it
    s.value = held;                        // the browser moved it to the finger: put it back
    if (ro) ro.textContent = fmtRo(s.value, s.dataset.fmt);
  };
  let press = null; // the release listeners of the press under way
  s.addEventListener('pointerdown', e => {
    held = Math.abs(e.clientX - sliderThumbX(s)) <= SLIDER_THUMB_REACH_PX * uiZoom ? null : s.value;
    // The release is heard on the window: a mouse let go off the slider left it held, and the
    // arrow keys did nothing until the next press. After the release's own 'change', so the
    // arrow keys on a focused slider work as before.
    // Only the pointer that began the press ends it: a second finger lifted from the screen
    // (a palm, the other hand on another slider) let the guard go while the first still dragged.
    if (press) press.abort();
    const c = press = new AbortController();
    const end = up => {
      if (up.pointerId !== e.pointerId) return;
      c.abort(); setTimeout(() => { if (press === c) held = null; }, 0);
    };
    for (const ev of ['pointerup', 'pointercancel'])
      window.addEventListener(ev, end, { capture: true, signal: c.signal });
  });
  s.addEventListener('input', keep);
  s.addEventListener('change', keep);
}
// A slider is hard to set to one exact value with a finger, so each gets − / + buttons (one
// step a tap, repeating while held; sent once on release) and its value box can be tapped to
// type the value. Typed values are in the readout's terms (p0: percent) and are clamped and
// snapped to the slider's range and step.
function sliderSnap(s, v) {
  const min = Number(s.min), max = Number(s.max), step = Number(s.step) || 1;
  const dec = (String(s.step).split('.')[1] || '').length;
  return Number(Math.min(max, Math.max(min, min + Math.round((v - min) / step) * step)).toFixed(dec));
}
function wireSliderNudge(s, ro) {
  const show = () => { if (ro) ro.textContent = fmtRo(s.value, s.dataset.fmt); };
  const set = v => { s.value = sliderSnap(s, v); show(); };
  for (const dir of [-1, 1]) {
    const b = document.createElement('button');
    b.type = 'button'; b.className = 'as-nudge'; b.textContent = dir < 0 ? '−' : '+';
    b.title = dir < 0 ? tr('Down one step') : tr('Up one step');
    let timer = null;
    const stop = () => {
      if (timer === null) return;
      clearTimeout(timer); timer = null; delete s.dataset.nudging;
      cfgSend(s.dataset.key, s.value);
    };
    b.addEventListener('pointerdown', e => {
      e.preventDefault(); e.stopPropagation();
      b.setPointerCapture?.(e.pointerId);
      s.dataset.nudging = '1';                                     // populateCfgControls leaves it alone
      const nudge = () => set(Number(s.value) + dir * (Number(s.step) || 1));
      nudge();
      const repeat = () => { nudge(); timer = setTimeout(repeat, 80); };
      timer = setTimeout(repeat, 450);
    });
    for (const ev of ['pointerup', 'pointercancel', 'lostpointercapture']) b.addEventListener(ev, stop);
    if (dir < 0) s.before(b); else s.after(b);
  }
  if (!ro) return;
  ro.classList.add('as-valedit');
  ro.title = tr('Tap to type a value');
  ro.addEventListener('pointerdown', e => {
    e.preventDefault(); e.stopPropagation();
    if (ro.hidden) return;
    const pct = s.dataset.fmt === 'p0';
    const input = document.createElement('input');
    input.className = 'as-valbox num-field';
    input.value = pct ? Math.round(Number(s.value) * 100) : s.value;
    makeNumericField(input);
    ro.hidden = true; ro.after(input); input.focus(); input.select();
    input.addEventListener('click', () => input.select(), { once: true }); // the tap's release lands here and drops the selection
    let done = false;
    const close = commit => {
      if (done) return;
      done = true;
      const v = parseFloat(input.value);
      if (commit && Number.isFinite(v)) { set(pct ? v / 100 : v); cfgSend(s.dataset.key, s.value); }
      input.remove(); ro.hidden = false;
    };
    input.addEventListener('keydown', ev => { ev.stopPropagation(); if (ev.key === 'Enter') close(true); else if (ev.key === 'Escape') close(false); });
    input.addEventListener('blur', () => close(true));
  });
}
function populateCfgControls(panel, force) {
  for (const inp of panel.querySelectorAll('.cfg-num')) {
    if (!force && document.activeElement === inp) continue;
    const val = cfgGet(inp.dataset.key);
    if (typeof val === 'number') writeUnitInput(inp, val);
  }
  for (const b of panel.querySelectorAll('.cfg-tgl')) {
    const on = !!cfgGet(b.dataset.key);
    b.classList.toggle('active', on);
    if (!b.dataset.keepLabel) b.textContent = on ? tr('On') : tr('Off');
  }
  for (const b of panel.querySelectorAll('.cfg-typebtn'))
    b.classList.toggle('active', String(cfgGet(b.dataset.key)) === (b.dataset.active != null ? b.dataset.active : b.dataset.val));
  // Segmented preset buttons (antenna Left / Center / Right) mark the current choice.
  for (const b of panel.querySelectorAll('.ln-seg .cfg-act[data-key][data-val]'))
    b.classList.toggle('active', String(cfgGet(b.dataset.key)) === b.dataset.val);
  for (const s of panel.querySelectorAll('.cfg-slider[data-fmt]')) {
    if ((!force && document.activeElement === s) || s.dataset.nudging) continue;
    const val = cfgGet(s.dataset.key);
    if (typeof val === 'number') {
      s.value = val;
      const ro = panel.querySelector('.cfg-ro[data-for="' + s.dataset.key + '"]');
      if (ro) ro.textContent = fmtRo(val, s.dataset.fmt);
    }
  }
  for (const sel of panel.querySelectorAll('.cfg-isel')) {
    if (document.activeElement === sel) continue;
    const val = cfgGet(sel.dataset.key);
    if (typeof val === 'number') sel.value = String(val);
  }
}
// Tab strip switcher: tabs + bodies share a data-strip id (so nested strips don't
// cross-toggle). Active tab → its data-tab body shown, siblings in the strip hidden.
function wireTabStrip(panel, stripId) {
  for (const t of panel.querySelectorAll('.cfg-tab[data-strip="' + stripId + '"]'))
    t.addEventListener('pointerdown', e => {
      e.stopPropagation();
      for (const b of panel.querySelectorAll('.cfg-tab[data-strip="' + stripId + '"]')) b.classList.toggle('active', b === t);
      for (const body of panel.querySelectorAll('.cfg-body[data-strip="' + stripId + '"]')) body.hidden = (body.id !== t.dataset.tab);
    });
}
