# Missed-strip assistant

Open a field with a boundary and recorded coverage, select a saved straight AB line,
and disengage AUTO. The assistant samples the native coverage map beside the active
guidance line. A continuous strip on one side produces a proposed shift with at least
10 cm overlap. Ambiguous sides, large mapping gaps, short segments and excessive
shifts are rejected. The analysis never changes guidance.

Use the Missed-strip assistant button to analyze manually. After a stable selected
pass, a small side card also offers a proposal automatically. Choose explicitly:

- **Save base line shift** moves the saved AB endpoints and retains the current pass correction.
- **Save guiding line shift** keeps the AB endpoints and saves the current pass correction plus the proposed shift.

Saving rechecks field, selected pass, exact displayed geometry, direction, machine
width/offset, control authority and freshly mapped coverage. Proposals belong to one
connection, expire after two minutes, and cannot be applied twice. A complete replacement
is persisted in the native field GeoJSON before activation; a failed write leaves the
running line untouched. Reopening the field retains the chosen shift.

## Code map

- Services/FieldAssistants/GaplessAlignment.cs: side/continuity/overlap analysis, no UI.
- Services/FieldAssistants/Modules/GuidanceModule.cs: proposal lifecycle and fresh validation.
- Services/Track/SavedGuidanceShift.cs: pure base-versus-guiding replacement geometry.
- RemoteWiring/GuidanceAssistantHost.cs: owner-loop snapshot and command bridge.
- ViewModels/MainViewModel.Gapless.Remote.cs: native persist-then-select command.
- RemoteServer/wwwroot/assistants/guidance.js: manual dialog and automatic side card.

All logic and UI are shared by desktop, Android and iOS. No speech SDK, ad assets or
other downstream migration modules are required. Tests use synthetic coverage and
geometry; field operation remains to be validated on a tractor.
