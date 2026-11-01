// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// The dialog host.

// ---- dialog host (Phase 6) — the web mirror of DialogOverlayHost ----
// One modal at a time over a dimming backdrop. SimCoords is the first card; later
// phases add more cards into the same host and reuse openDialog/closeDialog.
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
// Shared confirm (unified nav model) — replaces browser confirm() + the native
// ShowConfirmationDialog. Transparent light-dismiss scrim (backdrop tap / Cancel = no
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
