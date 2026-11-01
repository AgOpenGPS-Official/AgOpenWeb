// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// The on-screen keyboard.

// On-screen keyboard (App Settings › On-screen Kbd; AgOpenGPS FormKeyboard for text,
// FormNumeric for numbers) (#110). For touch boxes with no OS keyboard (kiosk/Linux): when
// on, focusing a text field opens a QWERTY panel, a numeric field a number pad, and the OS
// keyboard is suppressed (inputmode=none). Keys edit the field in place and fire 'input', so
// every existing handler sees them; OK sends Enter and closes. Leaving the field another way
// (tapping the next box, a button, the map) also commits it, as the OS keyboard does. Shift
// works as on a phone: one tap capitalises the next letter, a second tap straight after locks
// it (caps lock) until Shift is tapped again.
// Replace or edit (#516): the keyboard opens with the value selected ("fresh"). The first
// character key replaces the whole value; ⌫ first, or a tap in the field, keeps the value to
// correct it. Cancel puts back the value the field had when the keyboard opened. A tap outside
// on another field, a button or another control confirms (so Save right after typing saves the
// typed value); a tap on an empty spot, the map or a dialog backdrop cancels.
const OSK = (() => {
  const root = document.getElementById('osk');
  const SHIFT_LOCK_MS = 400;                  // second Shift tap within this locks caps
  let target = null, shift = 0, shiftAt = 0;  // shift: 0 off, 1 next letter, 2 caps lock
  let edited = false;                         // keys changed the field since its last 'change'
  let orig = '';                              // the value when the keyboard opened (Cancel)
  // The first key replaces the value. Kept apart from the selection: a tap's release can
  // collapse the selection on mobile engines, and the first key must replace it even then.
  let fresh = false;
  const TEXT = ['1234567890', 'qwertyuiop', 'asdfghjkl', 'zxcvbnm'];
  const NUM = ['789', '456', '123', '-0.'];
  // #+= swaps the letter rows for symbols, for web addresses, caster logins and passwords (the
  // digits and - _ . stay). It stays until abc is tapped; each new field starts on the letters.
  const SYM = [':/?=&@#%+~', '()[]{}<>!', ',;\'"^|\\$*`'];
  let sym = false;
  const on = () => !!(config && config.display && config.display.keyboardEnabled);
  const isField = el => el instanceof HTMLTextAreaElement
    || (el instanceof HTMLInputElement && ['text', 'password', 'search', 'email', 'url', ''].includes(el.type) && !el.readOnly && !el.disabled);
  // When the keyboard hides the field, a line above the keys shows the field's label and text
  // (like the text box at the top of AgOpenGPS's keyboard form).
  const pv = document.createElement('div'); pv.className = 'osk-row osk-pv'; pv.hidden = true;
  pv.innerHTML = '<span class="osk-pv-lbl"></span><span class="osk-pv-txt"></span>';
  const [pvLbl, pvTxt] = pv.children, caret = document.createElement('i');
  function render() {
    const num = !!(target && target.dataset.numField);
    root.classList.toggle('num', num);
    const rows = (num ? NUM : sym ? [TEXT[0], ...SYM] : TEXT).map(r => [...r].map(c => ({ k: c, t: shift ? c.toUpperCase() : c })));
    const cancel = { k: 'ESC', t: tr('Cancel'), c: 'wide esc' };
    if (num) rows.push([{ k: 'CLR', t: 'Clear' }, { k: 'BS', t: '⌫' }, { k: 'ESC', t: tr('Cancel'), c: 'esc' }, { k: 'OK', t: 'OK', c: 'ok' }]);
    else {
      if (!sym) rows[3].unshift({ k: 'SH', t: shift === 2 ? '⇪' : '⇧', c: ['wide', 'wide on', 'wide on lock'][shift] });
      rows[3].push({ k: 'BS', t: '⌫', c: 'wide' });
      rows.push([{ k: 'SYM', t: sym ? 'abc' : '#+=', c: 'wide' }, { k: '-', t: '-' }, { k: '_', t: '_' }, { k: ' ', t: 'space', c: 'xwide' }, { k: '.', t: '.' }, { k: 'CLR', t: 'Clear', c: 'wide' }, cancel, { k: 'OK', t: 'OK', c: 'wide ok' }]);
    }
    root.replaceChildren(pv);
    for (const r of rows) {
      const row = document.createElement('div'); row.className = 'osk-row';
      for (const b of r) {
        const btn = document.createElement('button'); btn.type = 'button';
        btn.textContent = b.t; btn.dataset.k = b.k; if (b.c) btn.className = b.c;
        row.appendChild(btn);
      }
      root.appendChild(row);
    }
  }
  function insert(s) {
    const el = target; if (!el) return;
    if (fresh) { fresh = false; selectAll(el); }  // the first key replaces the whole value
    const a = el.selectionStart ?? el.value.length, b = el.selectionEnd ?? a;
    try { el.setRangeText(s, a, b, 'end'); } catch (_) { el.value += s; }
    edited = true;
    el.dispatchEvent(new Event('input', { bubbles: true }));
  }
  // The browser only fires 'change' on blur after a user edit, and text set by these keys does
  // not count as one, so a value typed here and left without OK never reached its handler (the
  // box showed it, the host never got it). Fire 'change' once on the way out instead.
  function commit(el) {
    if (!edited) return;
    edited = false;
    el.dispatchEvent(new Event('change', { bubbles: true }));
  }
  function selectAll(el) { try { el.setSelectionRange(0, el.value.length); } catch (_) {} }
  function caretToEnd(el) { const n = el.value.length; try { el.setSelectionRange(n, n); } catch (_) {} }
  // Put the value back as it was when the keyboard opened. 'input' lets live handlers (the
  // numeric filter, previews) follow; no 'change', so nothing is committed.
  function restore() {
    const el = target; if (!el) return;
    fresh = false; edited = false;
    if (el.value !== orig) { el.value = orig; el.dispatchEvent(new Event('input', { bubbles: true })); }
  }
  function echo() {
    const el = target; if (!el || pv.hidden) return;
    const v = el.type === 'password' ? '•'.repeat(el.value.length) : el.value;
    if (fresh && v) {  // the value is selected: the first key replaces it
      const sel = document.createElement('span'); sel.className = 'sel'; sel.textContent = v;
      pvTxt.replaceChildren(sel); pvTxt.scrollLeft = 0; return;
    }
    const a = Math.min(el.selectionEnd ?? v.length, v.length);
    pvTxt.replaceChildren(v.slice(0, a), caret, v.slice(a));
    pvTxt.scrollLeft = Math.max(0, caret.offsetLeft - pvTxt.clientWidth + 24); // keep the caret in sight
  }
  function place() {
    pv.hidden = true;
    pv.hidden = target.getBoundingClientRect().bottom <= root.getBoundingClientRect().top;
    const lb = (target.labels && target.labels[0]) || target.previousElementSibling; // <label for> or <label></label><input>
    pvLbl.textContent = lb && lb.tagName === 'LABEL' ? lb.textContent.trim() : '';
    echo();
  }
  function key(k) {
    const el = target; if (!el) return;
    if (k === 'SH') {
      const now = performance.now();
      shift = shift === 1 && now - shiftAt < SHIFT_LOCK_MS ? 2 : shift ? 0 : 1;
      shiftAt = now; render(); return;
    }
    if (k === 'SYM') { sym = !sym; render(); return; }
    if (k === 'OK') {
      edited = false; fresh = false; // the 'change' below commits it
      el.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', bubbles: true }));
      el.dispatchEvent(new Event('change', { bubbles: true }));
      close(); el.blur(); return;
    }
    if (k === 'ESC') { restore(); close(); el.blur(); return; }
    if (k === 'CLR') { fresh = false; el.value = ''; edited = true; el.dispatchEvent(new Event('input', { bubbles: true })); return; }
    if (k === 'BS') {
      if (fresh) { fresh = false; caretToEnd(el); } // ⌫ first: keep the value, remove its last character
      const a = el.selectionStart ?? el.value.length, b = el.selectionEnd ?? a;
      if (a !== b) insert('');
      else if (a > 0) { try { el.setRangeText('', a - 1, a, 'end'); } catch (_) { el.value = el.value.slice(0, -1); } edited = true; el.dispatchEvent(new Event('input', { bubbles: true })); }
      return;
    }
    insert(shift ? k.toUpperCase() : k);
    if (shift === 1) { shift = 0; render(); }
  }
  function open(el) {
    if (el === target) return; // focus came back to the field the keyboard is already on
    target = el; shift = 0; edited = false;
    orig = el.value; fresh = true;
    if (!el.dataset.oskIm) el.dataset.oskIm = el.getAttribute('inputmode') ?? '';
    el.setAttribute('inputmode', 'none'); // no OS keyboard on top of ours
    sym = false;
    render(); root.hidden = false; place();
    // Show the value selected. The tap that focused the field may still drop a caret on its
    // release, so select again once that has landed.
    selectAll(el);
    const again = () => { if (target === el && fresh) { selectAll(el); echo(); } };
    el.addEventListener('click', again, { once: true });
    setTimeout(again, 0);
  }
  function close() {
    if (target && target.dataset.oskIm !== undefined) {
      if (target.dataset.oskIm) target.setAttribute('inputmode', target.dataset.oskIm); else target.removeAttribute('inputmode');
      delete target.dataset.oskIm;
    }
    target = null; fresh = false; root.hidden = true;
  }
  // Keep focus in the field while tapping keys (pointerdown would blur it), and don't pan the map.
  root.addEventListener('pointerdown', e => {
    e.preventDefault(); e.stopPropagation();
    const b = e.target.closest('button'); if (b) { key(b.dataset.k); echo(); }
  });
  // A tap anywhere else while the keyboard is up. Runs capturing on window, before focus moves
  // and before handlers that stop the event (the dialog backdrop): the value must be back
  // before the field's own blur handlers (rename flows, the steer value box) commit it.
  const CONTROL = 'input, textarea, select, button, a[href], label, [role="button"], [role="tab"], [role="checkbox"], [role="switch"], [role="option"], [contenteditable="true"]';
  window.addEventListener('pointerdown', e => {
    const el = target; if (!el || root.contains(e.target)) return;
    if (e.target === el) {                                                  // tap in the field: edit, caret where tapped
      if (!fresh) return;
      fresh = false; echo();
      // A touch on a selected value can leave the selection in place (Chromium on Android
      // shows handles instead of a caret); then the next key would still replace it all.
      el.addEventListener('click', () => {
        if (target === el && el.value && el.selectionStart === 0 && el.selectionEnd === el.value.length) { caretToEnd(el); echo(); }
      }, { once: true });
      return;
    }
    // Another field or a button: confirm (focusout). Focus moves by itself to a field; a button
    // may stop that on its pointerdown (the slider nudges), so the field is left now.
    const c = e.target.closest && e.target.closest(CONTROL);
    if (c) { if (!isField(c)) el.blur(); return; }
    restore(); close(); el.blur();                                          // empty spot, map, backdrop: cancel
  }, true);
  document.addEventListener('focusin', e => { if (on() && isField(e.target)) open(e.target); else if (!root.contains(e.target)) close(); });
  document.addEventListener('focusout', e => {
    if (e.target !== target) return;
    commit(target); // now, not in the timeout: a Save pressed next must see the value first
    setTimeout(() => { if (document.activeElement !== target) close(); }, 0);
  });
  window.addEventListener('resize', () => { if (target) place(); });
  return { close };
})();
// Uniform numeric input, everywhere. A tablet's decimal keypad (Android Samsung / iOS) has no
// minus key, so signed values couldn't be typed. Every numeric field is instead a type=text
// input with the FULL keyboard (which has "-"), and a filter keeps only digits, one decimal
// point, and a single leading minus. This converts every input[type=number] (config dialogs,
// tool dims, offsets, IP octets, …) and any added later — some config inputs are built
// dynamically — so all fields behave identically. parseFloat/parseInt readers are unaffected.
function filterNumericValue(s) {
  s = s.replace(/[^0-9.\-]/g, '');                                 // digits, dot, minus only
  s = s.replace(/(?!^)-/g, '');                                    // minus only at the start
  const d = s.indexOf('.');
  if (d !== -1) s = s.slice(0, d + 1) + s.slice(d + 1).replace(/\./g, ''); // one dot
  return s;
}
function makeNumericField(el) {
  if (el.dataset.numField) return;
  el.dataset.numField = '1';
  if (el.type === 'number') el.type = 'text';
  el.removeAttribute('inputmode');                                 // full keyboard (no flash, has "-")
  // It's a number field on a full (text) keyboard, so suppress the keyboard's auto-shift/caps,
  // autocorrect, suggestions and spellcheck — otherwise the tablet opens these with caps-lock
  // on (and inconsistently between fields), which is just noise for numeric entry.
  el.setAttribute('autocapitalize', 'none');
  el.setAttribute('autocorrect', 'off');
  el.setAttribute('autocomplete', 'off');
  el.setAttribute('spellcheck', 'false');
  el.classList.add('num-field');
}
function scanNumericFields(root) {
  // Accept the document (nodeType 9) AND elements (nodeType 1); the feature checks below
  // harmlessly skip text nodes the MutationObserver may pass. (An earlier nodeType===1 guard
  // made the initial scanNumericFields(document) a no-op, so static fields like the hitch
  // length kept the decimal keypad while only dynamically-added fields got converted.)
  if (!root) return;
  if (root.matches && root.matches('input[type="number"], .num-field')) makeNumericField(root);
  if (root.querySelectorAll)
    root.querySelectorAll('input[type="number"], .num-field').forEach(makeNumericField);
}
// Filter as the user types — delegated so dynamically-added fields are covered with no re-wiring.
document.addEventListener('input', e => {
  const el = e.target;
  if (!(el instanceof HTMLInputElement) || !el.dataset.numField) return;
  const before = el.value, after = filterNumericValue(before);
  if (after !== before) {
    const pos = Math.max(0, el.selectionStart - (before.length - after.length));
    el.value = after;
    try { el.setSelectionRange(pos, pos); } catch (_) {}
  }
});
scanNumericFields(document);
new MutationObserver(muts => { for (const m of muts) for (const n of m.addedNodes) scanNumericFields(n); })
  .observe(document.documentElement, { childList: true, subtree: true });
function openSimCoords() {
  // Pre-fill with the current vehicle position (from the Status frame), matching
  // the native dialog's GetSimulatorPosition() seed.
  dlgLat.value = statusBar && statusBar.lat != null ? statusBar.lat.toFixed(8) : '';
  dlgLon.value = statusBar && statusBar.lon != null ? statusBar.lon.toFixed(8) : '';
  openDialog('dlg-simcoords');
  dlgLat.focus();
}
document.getElementById('dlg-cancel').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); closeDialog(); });
// Coordinates line (#335, AgOpenGPS Build Tracks "Lat/Lon + Heading" / "Lat/Lon"): A is
// pre-filled with the vehicle's position, as AgOpenGPS does; the host converts through the
// field's local plane and reports anything it refuses.
let llMode = 'latLonHeading';
const llEl = id => document.getElementById(id);
function llFill(pt) {
  if (!statusBar || statusBar.lat == null || (!statusBar.lat && !statusBar.lon)) return;
  llEl('ll-lat-' + pt).value = statusBar.lat.toFixed(8);
  llEl('ll-lon-' + pt).value = statusBar.lon.toFixed(8);
}
function openLatLonLine(mode) {
  llMode = mode;
  const ab = mode === 'latLonAB', pivot = mode === 'latLonPivot';
  llEl('ll-title').textContent = pivot ? tr('Water Pivot from its Centre') : ab ? tr('AB Line from Two Points') : tr('AB Line from Point and Heading');
  llEl('ll-a-label').textContent = pivot ? tr('Centre') : 'A';
  llEl('ll-heading-row').hidden = ab || pivot;
  llEl('ll-b-row').hidden = !ab;
  for (const id of ['ll-lat-a', 'll-lon-a', 'll-heading', 'll-lat-b', 'll-lon-b']) llEl(id).value = '';
  llEl('ll-err').textContent = '';
  llFill('a');
  openDialog('dlg-latlonab');
}
document.querySelectorAll('[data-ll-fill]').forEach(b => b.addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation(); llFill(b.dataset.llFill);
}));
llEl('ll-cancel').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); closeDialog(); });
llEl('ll-ok').addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation();
  if (document.activeElement && document.activeElement.blur) document.activeElement.blur(); // commit the edit
  const num = id => { const v = llEl(id).value.trim(); return v === '' ? NaN : +v; };
  const okLat = v => Number.isFinite(v) && Math.abs(v) <= 90, okLon = v => Number.isFinite(v) && Math.abs(v) <= 180;
  const latA = num('ll-lat-a'), lonA = num('ll-lon-a');
  let err = !okLat(latA) || !okLon(lonA) ? tr('Point A needs a latitude (−90 to 90) and longitude (−180 to 180)') : '';
  let cmd = '';
  if (!err && llMode === 'latLonPivot') cmd = 'track.latLonPivot|' + [latA, lonA].join(',');
  else if (!err && llMode === 'latLonAB') {
    const latB = num('ll-lat-b'), lonB = num('ll-lon-b');
    if (!okLat(latB) || !okLon(lonB)) err = tr('Point B needs a latitude (−90 to 90) and longitude (−180 to 180)');
    else cmd = 'track.latLonAB|' + [latA, lonA, latB, lonB].join(',');
  } else if (!err) {
    const h = num('ll-heading');
    if (!Number.isFinite(h)) err = tr('Enter a heading in degrees');
    else cmd = 'track.latLonHeading|' + [latA, lonA, h].join(',');
  }
  if (err) { llEl('ll-err').textContent = err; return; }
  closeDialog();
  transport.send(cmd);
});

document.getElementById('dlg-ok').addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation();
  // Commit any in-progress field edit before reading: type=text inputs can hold uncommitted
  // text until blur, and preventDefault above keeps focus on the field.
  if (document.activeElement && document.activeElement.blur) document.activeElement.blur();
  const lat = parseFloat(dlgLat.value), lon = parseFloat(dlgLon.value);
  if (Number.isFinite(lat) && Number.isFinite(lon) && Math.abs(lat) <= 90 && Math.abs(lon) <= 180)
    transport.send('sim.setCoords|' + lat + ',' + lon);
  closeDialog();
});
document.getElementById('dlgc-cancel').addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation();
  const back = _confirmBack; closeDialog(); if (back) back();
});
document.getElementById('dlgc-ok').addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation();
  const cb = _confirmCb, back = _confirmBack; closeDialog(); if (cb) cb(); if (back) back();
});
