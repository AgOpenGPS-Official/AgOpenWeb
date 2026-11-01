// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// Camera controls (pan, zoom, tilt), map-tap capture, simulator drive keys, configured hotkeys.

// ---- camera controls ----
// Issue #87: a wheel over a pop-up that can scroll (hotkey list, logs, wizards…)
// scrolls that pop-up; everywhere else it zooms the map. ctrl+wheel (trackpad pinch
// / browser page zoom) is always swallowed so the page itself never zooms.
function wheelScrollsPanel(e) {
  for (let el = e.target; el && el !== document.body && el.nodeType === 1; el = el.parentElement) {
    const oy = getComputedStyle(el).overflowY;
    if ((oy === 'auto' || oy === 'scroll') && el.scrollHeight > el.clientHeight) return true;
  }
  return false;
}
addEventListener('wheel', e => {
  if (!e.ctrlKey && wheelScrollsPanel(e)) return;
  e.preventDefault();
  pxPerM *= e.deltaY < 0 ? 1.1 : 0.9;
  pxPerM = Math.min(200, Math.max(0.2, pxPerM));
}, { passive: false });

// Block BROWSER zoom so only the map zooms. iOS Safari IGNORES the viewport's
// user-scalable=no, so a two-finger pinch (esp. one starting over a panel, whose
// touch-action:manipulation still permits page pinch) magnifies the whole page —
// chrome and all — off-screen ("erratic zoom"). Preventing Safari's gesture* events
// stops that; trackpad/ctrl+wheel page-zoom is already covered by the wheel handler's
// preventDefault above. Multi-touch on the map canvas is covered by touch-action:none.
for (const t of ['gesturestart', 'gesturechange', 'gestureend'])
  addEventListener(t, e => e.preventDefault(), { passive: false });
// Belt-and-suspenders for touch browsers without gesture* (e.g. some Android): a
// 2+-finger move is a pinch — swallow it so the page can't zoom/pan. Single-finger
// touches pass through to the map pan / panel scroll untouched.
addEventListener('touchmove', e => { if (e.touches.length > 1) e.preventDefault(); }, { passive: false });
// iOS double-tap-to-zoom is NOT a gesture* event and slips past per-element touch-action when
// rapid taps land between/across nav buttons → it magnifies the whole page (chrome off-screen)
// and thrashes layout (render stutter / audio glitch / LINK spike are that main-thread jank).
// Swallow the 2nd tap's default within 300 ms; the UI runs on pointerdown, so button presses
// still register.
let _lastTapEnd = 0;
addEventListener('touchend', e => {
  const now = Date.now();
  if (now - _lastTapEnd <= 300) e.preventDefault();
  _lastTapEnd = now;
}, { passive: false });

// #465: some Linux touch stacks follow a finger tap with an emulated mouse press when the
// finger lifts. The UI acts on pointerdown, so that second press shut the menu the finger had
// just opened (a stylus sends one press, and a second finger cancels the emulation). Drop it,
// before any other handler sees it: a mouse press right after a touch ended near the same
// spot, and a second press from a pointer that is already down (a real one never does that).
const GHOST_PRESS_MS = 800, GHOST_PRESS_PX = 30;
let _touchEnd = null;
const _pointersDown = new Set();
function noteTouchEnd(t, x, y) { _touchEnd = { t, x, y }; }
addEventListener('pointerup', e => {
  _pointersDown.delete(e.pointerId);
  if (e.pointerType === 'touch') noteTouchEnd(e.timeStamp, e.clientX, e.clientY);
}, true);
addEventListener('pointercancel', e => _pointersDown.delete(e.pointerId), true);
addEventListener('blur', () => _pointersDown.clear()); // a release we never saw
addEventListener('touchend', e => {
  const t = e.changedTouches[0];
  if (t) noteTouchEnd(e.timeStamp, t.clientX, t.clientY);
}, true);
addEventListener('pointerdown', e => {
  // Forget a pointer once it is caught pressing twice, so a lost release costs one press at most.
  const ghost = _pointersDown.delete(e.pointerId) || (e.pointerType === 'mouse' && _touchEnd
    && e.timeStamp - _touchEnd.t < GHOST_PRESS_MS
    && Math.hypot(e.clientX - _touchEnd.x, e.clientY - _touchEnd.y) < GHOST_PRESS_PX);
  if (ghost) { e.preventDefault(); e.stopImmediatePropagation(); return; }
  _pointersDown.add(e.pointerId);
}, true);

// A tap on the vehicle resets its direction, as in AgOpenGPS: the heading is learned again
// from the next forward travel. It is the way out when the first heading was taken while
// backing, after which (with an IMU) forward travel reads as reverse.
const VEHICLE_TAP_RADIUS = 44; // px
function vehicleTap(px, py) {
  const rp = renderPose();
  if (!rp) return;
  const p = w2s(rp.e, rp.n);
  if (!p || Math.hypot(p[0] - px, p[1] - py) > VEHICLE_TAP_RADIUS) return;
  const gps = config && config.gps;
  // With dual GPS the antennas give the heading: there is no direction to learn.
  if (gps && gps.isDualGps && !gps.autoDualFix && statusBar && !statusBar.dualHeadingMissing) return;
  if (!iHoldControl) { showToast(tr('Take control to reset the direction')); return; }
  transport.send('heading.resetDirection');
  showToast(tr('Direction reset. Drive forward above 1.5 kph.'));
}

// Pan + tap. A gesture that moves past TAP_SLOP px pans (and drops to Free mode); one
// that stays put is a TAP. Overlays (panels/toolbars) stopPropagation their pointerdown,
// so window-level pointerdown only fires for gestures that began on the map — gestureOnMap
// guards the pointerup tap against panel taps that bubble their pointerup.
// A finger (or pen) in a moving cab drifts further between press and release than a mouse does,
// so it gets a wider slop: at 5 px a touch tap on the vehicle was read as a pan and did nothing (#266).
const TAP_SLOP = 5, TAP_SLOP_TOUCH = 15; // px — below this, a press-release is a tap, not a pan
let dragging = false, moved = false, gestureOnMap = false, lastX = 0, lastY = 0, downX = 0, downY = 0, tapSlop = TAP_SLOP;
addEventListener('pointerdown', e => {
  // Stage-4 edit: grab a handle if the press starts on one (else fall through to pan).
  if (editSession) {
    const hi = editHitTest(e.clientX - mapL, e.clientY);
    if (hi >= 0) { editDragIdx = hi; gestureOnMap = false; dragging = false; return; }
  }
  gestureOnMap = true; dragging = true; moved = false;
  tapSlop = e.pointerType === 'mouse' ? TAP_SLOP : TAP_SLOP_TOUCH;
  downX = lastX = e.clientX; downY = lastY = e.clientY;
});
// A gesture the browser takes over (system gesture, palm rejection) ends without a pointerup.
addEventListener('pointercancel', () => { editDragIdx = -1; dragging = false; gestureOnMap = false; });
addEventListener('pointerup', e => {
  if (editDragIdx >= 0) { editDragIdx = -1; return; } // finished dragging an edit handle
  dragging = false;
  // A clean tap while a map-tap capture mode is armed → unproject and dispatch.
  if (gestureOnMap && !moved && mapTap) {
    const w = s2w(e.clientX - mapL, e.clientY);
    if (w) mapTap.onTap(w.e, w.n);
  } else if (gestureOnMap && !moved && !editSession) {
    vehicleTap(e.clientX - mapL, e.clientY);
  }
  gestureOnMap = false;
});
addEventListener('pointermove', e => {
  // #40: the crosshair letter is a MOUSE-hover affordance — only for a real pointer. On
  // touch/pen there's no hover to ride, and a tap's micro-move would drop the "big A" under
  // the finger/button and clash with the placed-dot label; touch relies on the dot letters
  // + the "Tap Point A/B" banner instead.
  // Only over the map itself: over a button or a menu it isn't where the next point goes (#399).
  if (mapTap && (abFlow === 'straight' || abFlow === 'curve') && e.pointerType === 'mouse') {
    if (e.target && e.target.id === 'ck') updateAbCursorLabel(e.clientX, e.clientY);
    else if (abLabelEl) abLabelEl.classList.remove('show');
  }
  if (editDragIdx >= 0 && editSession) {       // drag the grabbed handle
    const w = s2w(e.clientX - mapL, e.clientY);
    if (w) editSession.points[editDragIdx] = editSession.kind === 'headland' ? hlSnap(w.e, w.n) : { e: w.e, n: w.n };
    return;
  }
  if (!dragging) return;
  // Some tablets report a finger or pen just above the glass as a hover: moves with nothing
  // pressed. Those are not part of a drag, and must not use up the tap slop or pan the map.
  if (e.buttons === 0) return;
  if (!moved) {
    // Stay still until the slop is exceeded so a tap doesn't pan or drop follow mode.
    if (Math.hypot(e.clientX - downX, e.clientY - downY) <= tapSlop) return;
    moved = true; cameraMode = 2; lastX = e.clientX; lastY = e.clientY; // reset origin, no jump
    return;
  }
  // Pan in the rotated frame: invert the screen rotation so the grabbed point
  // tracks the cursor. Reduces to the plain north-up pan at rotation 0.
  const dsx = e.clientX - lastX, dsy = e.clientY - lastY;
  camE += (-_cosRR * dsx + _sinRR * dsy) / pxPerM;
  camN += (_sinRR * dsx + _cosRR * dsy) / pxPerM;
  lastX = e.clientX; lastY = e.clientY;
});
// ---- map-tap capture mode (Phase MT) — the reusable on-map point-picking primitive ----
// A feature arms capture with startMapTap({hint, onTap}); the next clean tap unprojects via
// s2w and calls onTap(e,n) (field metres). One mode at a time; the map stays pan/zoomable
// (only a tap captures) so the operator can frame the view first. Esc / endMapTap() exits.
let mapTap = null;
function startMapTap(cfg) {
  mapTap = cfg;
  document.body.classList.add('maptap'); // crosshair cursor (CSS)
  const h = document.getElementById('maptap-hint');
  h.textContent = cfg.hint || tr('Tap the map'); h.classList.add('show');
  // Touch cancel: only for bare flows (no draw toolbar) — flag placement. The AB/sat/headland
  // flows show the toolbar whose own Cancel handles them, so don't double it up.
  const tbUp = document.getElementById('draw-toolbar').classList.contains('show');
  document.getElementById('maptap-cancel').classList.toggle('show', !tbUp);
}
function endMapTap() {
  mapTap = null;
  document.body.classList.remove('maptap');
  document.getElementById('maptap-hint').classList.remove('show');
  document.getElementById('maptap-cancel').classList.remove('show'); // hide the touch-cancel pill
  if (abLabelEl) abLabelEl.classList.remove('show'); // #40 hide the A/B crosshair letter
  if (abDotLabelsEl) for (const s of abDotLabelsEl.children) s.style.display = 'none'; // + the dot letters
}
// True when the user is typing into a field — global hotkeys (tilt, sim drive) must not
// fire then (e.g. typing "300" into the boundary offset shouldn't toggle 3D tilt on "3").
function isTyping() {
  const el = document.activeElement;
  return !!el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.isContentEditable);
}
addEventListener('keydown', e => {
  if (e.key === 'Escape' && satBnd) { satCancel(); return; }  // cancel boundary draw-on-map
  if (e.key === 'Escape' && abFlow) { abCancel(); return; }   // cancel AB-line creation (tells host)
  if (e.key === 'Escape' && hlFlow) { endHeadlandDraw(); return; } // cancel headland draw
  if (e.key === 'Escape' && editSession) { endEdit(); return; }    // cancel on-map edit
  if (e.key === 'Escape' && mapTap) { endMapTap(); return; }  // cancel on-map capture
  if (isTyping() || boundHotkey(e)) return; // a configured hotkey owns this key (#98)
  if (e.key === 'f' || e.key === 'F') cameraMode = 3; // resume map-follow
  // 3D tilt: 3 toggles between top-down and 60°, [ / ] nudge the pitch.
  else if (e.key === '3') pitch = pitch > 0.001 ? 0 : DEFAULT_PITCH;
  else if (e.key === '[') pitch = Math.max(0, pitch - PITCH_STEP);
  else if (e.key === ']') pitch = Math.min(MAX_PITCH, pitch + PITCH_STEP);
});

// ---- sim drive keys (client→host commands) ----
// Arrow keys / space for desktop; the simulator bar (#simbar, below) covers touch.
// The host maps these ids to the matching VM command on its UI thread (allowlist).
const KEY_CMD = {
  ArrowLeft: 'sim.steerLeft', ArrowRight: 'sim.steerRight',
  ArrowUp: 'sim.speedUp', ArrowDown: 'sim.speedDown', ' ': 'sim.stop',
};
addEventListener('keydown', e => {
  if (isTyping() || boundHotkey(e)) return; // a configured hotkey owns this key (#98)
  const cmd = KEY_CMD[e.key];
  if (cmd) { e.preventDefault(); transport.send(cmd); }
});
// ---- configured hotkeys (#98) ----
// Bindings live on the host (File → Hotkeys, ConfigStore.Hotkeys) and arrive on the AppInfo
// frame. The client resolves key → action and sends the SAME command id as the matching
// button, so Tier-2 gating and the host allowlist apply unchanged. Screen actions open the
// web panel here — the host's HandleHotkey would open a native dialog the web never renders.
// A bound key takes priority over the built-in map/sim keys above (F, 3, [, ], arrows, space).
const HOTKEY_CMD = {
  AutoSteer: 'autosteer.toggle', CycleLines: 'track.cycle', SnapPivot: 'track.snapPivot',
  NudgeLeft: 'track.nudgeLeft', NudgeRight: 'track.nudgeRight',
  ManualSection: 'section.manual', AutoSection: 'section.master',
};
const HOTKEY_TIER1 = { Flag: 'flag.placeHere' }; // markers (still need control, like every command)
const HOTKEY_UI = {
  FieldMenu: () => document.getElementById('ln-fieldops').dispatchEvent(new PointerEvent('pointerdown')),
  VehicleSettings: () => document.getElementById('ln-vehicle').dispatchEvent(new PointerEvent('pointerdown')),
  SteerWizard: () => openSteerWizard(),
};
// Case-insensitive, so named keys match however they were saved: older builds stored
// "ARROWUP" for ArrowUp, which never matched and the hotkey never fired (#111, #98).
function normHotkey(k) { return k.toUpperCase(); }
// The action bound to this key event, or null. Modifier combos stay with the browser/OS.
function boundHotkey(e) {
  if (e.ctrlKey || e.metaKey || e.altKey || !appInfo) return null;
  const key = normHotkey(e.key);
  const hk = appInfo.hotkeys.find(h => h.key && normHotkey(h.key) === key);
  return hk ? hk.action : null;
}
// Hotkeys are off while typing, capturing a binding, or behind a modal (native blocked
// them while any dialog was open).
function hotkeysBlocked() {
  return isTyping()
    || document.getElementById('hotkeys').classList.contains('open')
    || !!document.querySelector('#dialoghost.open, .sw-backdrop.open, .wz-overlay.open');
}
addEventListener('keydown', e => {
  const action = boundHotkey(e);
  if (!action || hotkeysBlocked()) return;
  e.preventDefault();
  if (e.repeat) return; // holding a key must not re-toggle autosteer / sections
  if (HOTKEY_UI[action]) { HOTKEY_UI[action](); return; }
  if (HOTKEY_TIER1[action]) { transport.send(HOTKEY_TIER1[action]); return; }
  const sec = /^Section([1-8])$/.exec(action);
  const cmd = sec ? 'section.toggle|' + (sec[1] - 1) : HOTKEY_CMD[action];
  if (!cmd) return;
  if (!iHoldControl) { flashHint(tr('Observing — take control to use hotkeys')); return; }
  transport.send(cmd);
});
