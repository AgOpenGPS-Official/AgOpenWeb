// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// The simulator bar.

// ---- simulator bar (Phase 6) — mirrors the native SimulatorPanel ----
// Like every command, the bar's need control (an observer changes nothing, #397). Panel *visibility* is client-local
// (the host's IsSimulatorPanelVisible is a native-only concept); enabled/speed/
// steer are host state, mirrored back over the Status frame.
const SIM = {
  bar: document.getElementById('simbar'), launch: document.getElementById('sim-launch'),
  enable: document.getElementById('sim-enable'), tenx: document.getElementById('sim-10x'),
  steer: document.getElementById('sim-steer'), steerVal: document.getElementById('sim-steerval'),
  speedVal: document.getElementById('sim-speedval'), gps: document.getElementById('sim-gps'),
};
let _steerDragging = false;     // suppress state→slider sync while the user drags
// Sim-bar visibility is host-driven (PersistentAppState.SimulatorPanelVisible) so the
// choice persists across app restarts. Shown until the first Status frame arrives.
function applySimBarVisible() {
  const open = !statusBar || statusBar.simPanelVisible !== false;
  SIM.bar.classList.toggle('open', open);
  SIM.launch.classList.toggle('show', !open);
}
applySimBarVisible();
SIM.bar.addEventListener('pointerdown', e => e.stopPropagation()); // don't pan the map
SIM.launch.addEventListener('pointerdown', e => e.stopPropagation());
// Generic argless commands (data-cmd) — fire on pointerdown for snappy feel.
for (const b of document.querySelectorAll('#simbar button[data-cmd]')) {
  b.addEventListener('pointerdown', e => {
    e.preventDefault(); e.stopPropagation();
    transport.send(b.dataset.cmd);
  });
}
// Steer slider → command-with-arg; optimistic local readout while dragging.
SIM.steer.addEventListener('input', () => {
  _steerDragging = true;
  const deg = parseFloat(SIM.steer.value);
  SIM.steerVal.textContent = deg.toFixed(1) + '°';
  transport.send('sim.setSteer|' + deg);
});
SIM.steer.addEventListener('change', () => { _steerDragging = false; });
// GPS button opens the SimCoords dialog (client-local open; OK sends sim.setCoords).
SIM.gps.addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation();
  if (statusBar && statusBar.simEnabled) return; // native guard: disable sim first
  openSimCoords();
});
document.getElementById('sim-close').addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation(); transport.send('sim.togglePanel');
});
SIM.launch.addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation(); transport.send('sim.togglePanel');
});
