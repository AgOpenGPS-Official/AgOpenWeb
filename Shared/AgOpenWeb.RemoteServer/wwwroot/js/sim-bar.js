// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// The simulator bar.

// ---- simulator bar ----
// Like every command, the bar's need control (an observer changes nothing, #397). Panel *visibility* is client-local
// (not the host's IsSimulatorPanelVisible); enabled/speed/
// steer are host state, mirrored back over the Status frame.
const SIM = {
  bar: document.getElementById('simbar'), launch: document.getElementById('sim-launch'),
  enable: document.getElementById('sim-enable'), tenx: document.getElementById('sim-10x'),
  steer: document.getElementById('sim-steer'), steerVal: document.getElementById('sim-steerval'),
  speedVal: document.getElementById('sim-speedval'), gps: document.getElementById('sim-gps'),
  drive: document.getElementById('sim-drive'),
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
// Bench test: the simulator moves the real steering. The host keeps the choice (never saved,
// off again when the simulator is turned off or the steer module goes silent). Asked before it
// goes on; off at once.
SIM.drive.addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation();
  if (statusBar && statusBar.simDrivesSteering) { transport.send('sim.driveSteering|0'); return; }
  showConfirm(tr('Move the real steering?'),
    tr('With this on, AutoSteer on the simulator makes the steer module turn the real wheels or motor. Use it for a bench test only, never on a tractor with the engine running. It goes off by itself when the simulator is turned off.'),
    () => transport.send('sim.driveSteering|1'));
});
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
  if (statusBar && statusBar.simEnabled) return; // disable the sim first
  openSimCoords();
});
document.getElementById('sim-close').addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation(); transport.send('sim.togglePanel');
});
SIM.launch.addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation(); transport.send('sim.togglePanel');
});
