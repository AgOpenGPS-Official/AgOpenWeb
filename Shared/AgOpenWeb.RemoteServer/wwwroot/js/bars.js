// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// Unsaved-coverage guard, section bar, bottom nav, the on-screen U-turn / lateral buttons.

// ---- unsaved-coverage guard (server-driven) ----
// CloseFieldCommand opens DialogType.UnsavedCoverage on the host when coverage was painted
// with no open job. The host has no UI, so we render the prompt here off the Status flag and
// resolve it with the matching host commands (SaveCoverageToJob / DiscardCoverageAndClose /
// CancelUnsavedCoverage). Without this the host opens a dialog nothing shows and the close hangs.
let _ucovOpen = false;
function syncUnsavedCov() {
  const want = !!(statusBar && statusBar.unsavedCoveragePrompt);
  if (want && !_ucovOpen) { _ucovOpen = true; openDialog('dlg-unsavedcov'); }
  else if (!want && _ucovOpen) {
    _ucovOpen = false;
    if (document.getElementById('dlg-unsavedcov').classList.contains('open')) closeDialog();
  }
}
// Resolve optimistically (close now); the host command also flips the Status flag false, so
// the next frame is a no-op rather than a re-open.
function ucovResolve(cmd) { _ucovOpen = false; transport.send(cmd); closeDialog(); }
document.getElementById('ucov-save').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); ucovResolve('coverage.saveJob'); });
document.getElementById('ucov-discard').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); ucovResolve('coverage.discardClose'); });
document.getElementById('ucov-cancel').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); ucovResolve('coverage.cancelClose'); });

// ---- section bar — per-section colour strip + manual toggle ----
// Read state (per-section ColorCode + master/manual mode) already rides the Tick;
// each tap cycles one section Off->Auto->On (Tier-2 — gated by control authority on
// both ends). Buttons are (re)built when the section count changes; colours update
// per frame. Delegated click handler reads the section index off data-idx.
const sectionBar = document.getElementById('sectionbar');
sectionBar.addEventListener('pointerdown', e => {
  e.stopPropagation(); // don't pan the map
  const zb = e.target.closest('button[data-zone]');
  if (zb) { if (iHoldControl) transport.send('section.toggleZone|' + zb.dataset.zone); return; }
  const btn = e.target.closest('button[data-idx]');
  if (btn && iHoldControl) transport.send('section.toggle|' + btn.dataset.idx);
});

// ---- bottom nav — field-tools toolbar + Flags/AB-line flyouts ----
// Direct-action field tools. Tier-2 ids (data-t2: track.* / headland.* / youturn.skip*)
// are dimmed and fire only while we hold control; every other command needs it too (#397). Toggle
// READ state rides the Tick (tick.tools); AB-dependent buttons key off activeTrackName.
const bottomNav = document.getElementById('bottomnav');
const bnFlags = document.getElementById('bn-flyout-flags');
const bnAb = document.getElementById('bn-flyout-ab');
bottomNav.addEventListener('pointerdown', e => {
  e.stopPropagation(); // don't pan the map
  const btn = e.target.closest('button[data-cmd]');
  if (!btn) return;
  if (btn.hasAttribute('data-t2') && !iHoldControl) return; // gated; host re-checks
  transport.send(btn.dataset.cmd);
});
// Delete contour paths — confirm first (#107); not a data-cmd so the generic handler skips it.
document.getElementById('bn-delcontours').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (!iHoldControl) return;
  showConfirm(tr('Delete Contour Paths'), tr('Delete all recorded contour paths in this field? Coverage and AB lines are kept.'),
    () => transport.send('track.deleteContours'));
});
// ---- On-screen U-Turn (yellow) / Lateral (cyan) buttons over the map ----
// Bare glyph buttons as in AgOpenGPS; shown per the Screen & Alerts
// "On-Screen Buttons" toggles. U-turn → manual you-turn L/R, lateral → snap the track
// one pass (1 tool width) L/R.
const onScreenBtns = document.getElementById('onscreenbtns');
onScreenBtns.addEventListener('pointerdown', e => {
  const btn = e.target.closest('button[data-cmd]');
  if (!btn) return;
  e.stopPropagation(); // tap the glyph, don't pan the map
  // #50: mid-turn snaps/u-turns are ignored by the pipeline (they'd shift the
  // displayed track while the tractor stays committed to the executing arc, then
  // seek across after). Block them here with a hint instead of silently no-op'ing.
  if (tick && tick.op && tick.op.executing) { flashHint(tr('Finish the U-turn first')); return; }
  transport.send(btn.dataset.cmd);
});
function applyOnScreenButtons() {
  const d = config && config.display;
  const hasField = !!(scene && scene.hasField); // only with a field open
  // U-turn (manual you-turn) and Lateral (snap track) only act while AutoSteer is engaged,
  // so hide them otherwise (issue #36). Runs every frame via renderBottomNav, so it tracks
  // engage/disengage live.
  // Neither in contour mode (AgOpenGPS skips DrawManUTurnBtn then) (#110).
  const asActive = !!(tick && tick.op && tick.op.autoSteer && !tick.op.contour);
  document.getElementById('osb-uturn').hidden = !(hasField && asActive && d && d.uTurnButtonVisible);
  document.getElementById('osb-lateral').hidden = !(hasField && asActive && d && d.lateralButtonVisible);
}
function bnToggleFly(fly, btn) {
  const open = !fly.classList.contains('open');
  bnFlags.classList.remove('open'); bnAb.classList.remove('open');
  document.getElementById('bn-flags').classList.remove('menuopen');
  document.getElementById('bn-abmenu').classList.remove('menuopen');
  if (open) { fly.classList.add('open'); btn.classList.add('menuopen'); }
}
document.getElementById('bn-flags').addEventListener('pointerdown', e => { e.stopPropagation(); bnToggleFly(bnFlags, e.currentTarget); });
// "Place Flag on Map" — arm tap capture; the next map tap drops a flag at the
// tapped field point via flag.placeAt (host PlaceFlagAtWorldPosition). No data-cmd, so the
// bottomNav delegate ignores it. Closes the flyout, then captures one tap.
function armPlaceFlagOnMap() {
  startMapTap({
    hint: tr('Tap the map to place a flag'),
    onTap: (e2, n2) => { transport.send('flag.placeAt|' + e2.toFixed(3) + ',' + n2.toFixed(3)); endMapTap(); },
  });
}
document.getElementById('bn-flag-onmap').addEventListener('pointerdown', e => {
  e.stopPropagation();
  bnFlags.classList.remove('open');
  document.getElementById('bn-flags').classList.remove('menuopen');
  armPlaceFlagOnMap();
});
