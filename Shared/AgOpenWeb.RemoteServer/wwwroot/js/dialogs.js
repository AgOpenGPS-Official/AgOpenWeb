// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// The dialog host and its cards: simulator coordinates, the lat/lon line, the shared confirm.

// ---- dialog host ----
// One modal at a time over a dimming backdrop; every card uses openDialog/closeDialog.
const dialogHost = document.getElementById('dialoghost');
let _confirmCb = null;
let _confirmBack = null; // reopens the dialog the confirm was asked from, after OK or Cancel
function openDialog(cardId) {
  for (const c of dialogHost.querySelectorAll('.dlg-card')) c.classList.toggle('open', c.id === cardId);
  dialogHost.classList.add('open');
}
function closeDialog() {
  hideKeyboard();
  dialogHost.classList.remove('open'); _confirmCb = null; _confirmBack = null;
}
// Lower the Android soft keyboard via the native bridge (window.agnative, wired by the Android
// head to InputMethodManager). No-op on iOS/desktop, where blur() already dismisses.
function nativeKeyboardHide() {
  try { if (window.agnative && window.agnative.hideKeyboard) window.agnative.hideKeyboard(); } catch (_) {}
}
function hideKeyboard() {
  const a = document.activeElement;
  if (a && typeof a.blur === 'function') a.blur(); // iOS WKWebView + desktop dismiss on blur
  nativeKeyboardHide();                            // Android: only InputMethodManager drops it
}
// Global safety net: on Android WebView a field's blur() never lowers the IME, and only the
// GPS dialog routes through closeDialog()/hideKeyboard. So whenever focus leaves ANY text field
// and doesn't land on another one — closing a config/tool dialog, tapping a button, tapping the
// map — drop the keyboard. The next-tick activeElement check (rather than relatedTarget, which
// Android sets unreliably) keeps the keyboard up during field-to-field navigation.
document.addEventListener('focusout', e => {
  if (!(e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement)) return;
  setTimeout(() => {
    const a = document.activeElement;
    const stillEditing = a && (a.tagName === 'INPUT' || a.tagName === 'TEXTAREA' || a.isContentEditable);
    if (!stillEditing) nativeKeyboardHide();
  }, 0);
});
// Shared confirm (instead of the browser's confirm()). Transparent light-dismiss scrim (backdrop tap / Cancel = no
// action); Confirm runs the callback. Hosted in the dialog host (now a transparent leaf).
function showConfirm(title, message, onConfirm, onBack) {
  _confirmCb = onConfirm || null;
  _confirmBack = onBack || null;
  document.getElementById('dlgc-title').textContent = title || tr('Confirm');
  document.getElementById('dlgc-msg').textContent = message || '';
  openDialog('dlg-confirm');
}
dialogHost.querySelector('.dlg-backdrop').addEventListener('pointerdown', e => {
  e.stopPropagation();
  // A sticky card (a long list to pick from) stays until it is closed: an outside tap on a screen this size is a slip.
  if (dialogHost.querySelector('.dlg-card.open.sticky')) return;
  // Backdrop-dismiss of the unsaved-coverage guard must resolve it (= Cancel), else the
  // host stays blocked with its dialog open and the field never closes.
  if (_ucovOpen) { _ucovOpen = false; transport.send('coverage.cancelClose'); }
  closeDialog();
});
dialogHost.addEventListener('pointerdown', e => e.stopPropagation()); // keep map from panning
const dlgLat = document.getElementById('dlg-lat'), dlgLon = document.getElementById('dlg-lon');
function openSimCoords() {
  // Pre-fill with the current vehicle position (from the Status frame).
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
