// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// Control authority (operator / observer) and the right nav's actions.

// ---- control authority: operator / observer (#397) ----
// Take/Release single-holder control + a Tier-2 stub. Only the holder may
// actuate; the holder must heartbeat (presence) or the host revokes it (deadman).
// Control is implicit and by connection order: the first browser to connect is the
// controller (server-assigned); others observe. No take/release UI. A status line
// shows which role this browser has.
// Status-bar role indicator (left of Modules). Reflects both connection state
// (Disconnected when the socket is down) and the implicit control role.
function renderRole() {
  const t = document.getElementById('sb-roletext'), d = document.getElementById('sb-roledot');
  if (!t) return;
  let label, color;
  if (connState !== 'connected') { label = tr('Disconnected'); color = '#ff5a5a'; }
  else if (iHoldControl)         { label = tr('Operator');     color = '#39FF6A'; }
  else if (lastControl.held)     { label = tr('Observer');     color = '#ff7a3d'; }
  else                           { label = tr('No operator');  color = '#9fb3cc'; }
  t.textContent = label; t.style.color = color; d.style.background = color;
}
function updateControlUi() {
  iHoldControl = lastControl.held && lastControl.holderId === myClientId;
  if (iHoldControl) { applyMobileQualityCap(); reportMaxTexture(); } // host-wide: sent once we may (#397)
  if (typeof renderHostPrompt === 'function') renderHostPrompt(); // who may answer
  if (typeof updateAsGated === 'function') updateAsGated(); // re-gate AutoSteer actions
  if (document.getElementById('recpath').classList.contains('open')) renderRecPath(); // re-gate Play
  renderRole();
}
// Reclaim the operator seat when it is free. Control is keyed to the WebSocket
// connection server-side and acquired once, implicitly, at connect. A browser
// REFRESH opens a new connection while the old one is still registered as holder,
// so the new connection's implicit acquire is denied; once the stale holder is
// dropped/deadman-swept the seat goes empty but the new connection never retried —
// leaving the Tier-2 right-nav menu permanently inop until a server restart. Claiming
// the empty seat here (driven by the control-state frame the drop/sweep emits, plus
// connect/hello) makes the operator role survive a refresh. Guarded on !held so it
// never takes the seat from a live operator — only fills a vacant one (deadman model).
function claimSeatIfFree() {
  if (connState === 'connected' && myClientId && !iHoldControl && !lastControl.held)
    transport.send('control.acquire|Browser');
}
// Heartbeat keeps our hold alive while we control; a lapse trips the host deadman
// (disengage autosteer + sections).
setInterval(() => { if (iHoldControl) transport.send('control.presence'); }, 500);
updateControlUi();

// Right-nav toolbar actuation. Each button sends its Tier-2 command,
// but only while we hold control — the host hub enforces the same gate. No
// per-action confirm: holding control IS the deliberate gate (a single tap), and the deadman + failsafe cover a lost controller.
function rnSend(cmd) { if (iHoldControl) transport.send(cmd); }
// NB: use a direct lookup here, not the RN object — RN is a `const` defined later
// in the render section, so referencing it here would hit the temporal dead zone
// and throw at load (stuck "connecting…").
let rnCycleAt = 0, rnLastTrack = null; // last cycle tap (ms), track name seen then
const rnRoot = document.getElementById('rightnav');
if (rnRoot) {
  rnRoot.addEventListener('pointerdown', e => e.stopPropagation()); // don't pan the map
  // pointerdown, like the rest of the touch UI: a touch web view doesn't reliably fire
  // 'click' (#56, #111).
  const wireRn = (id, cmd) => { const el = document.getElementById(id); if (el) el.addEventListener('pointerdown', e => { e.preventDefault(); rnSend(cmd); }); };
  wireRn('rn-contour', 'contour.toggle');
  wireRn('rn-contourlock', 'contour.lock'); // #110
  // Track picking (#336). After a cycle tap, renderRightNav flashes the new track's name.
  wireRn('rn-cycle', 'track.cycle');
  wireRn('rn-cycleback', 'track.cycleBack');
  for (const id of ['rn-cycle', 'rn-cycleback'])
    document.getElementById(id).addEventListener('pointerdown', () => { if (iHoldControl) rnCycleAt = Date.now(); });
  wireRn('rn-autotrack', 'track.autoTrack');
  wireRn('rn-manual', 'section.manual');
  wireRn('rn-auto', 'section.master');
  wireRn('rn-isobus', 'isobus.sectionControl'); // #312
  wireRn('rn-youturn', 'youturn.toggle');
  wireRn('rn-steer', 'autosteer.toggle');
}
