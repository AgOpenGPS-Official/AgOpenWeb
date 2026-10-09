# Individual finite row paths

Each row has its own recorded GPS path, rather than an AB offset generated from
implement width. Open a field and use **Individual rows**:

1. **A · start row** begins recording the canonical vehicle pivot.
2. Drive along the real row. **B · save row** saves the continuous path in that field.
3. Repeat for more rows, select a row or enable automatic selection, then choose
   **Follow saved rows**. Drive within the configured 10–200 cm capture distance.
4. A captured row is highlighted. Press AUTO explicitly to engage native guidance.

The selector and finite segment capture run on the dedicated GPS cycle worker.
Pipeline intents install an immutable request; the owner loop never computes guidance.
The actual selected row appears in the standard scene and uses native curve guidance.
Pass number/nudge stay zero. AB shifts, pass jumps, generated parallel passes, extensions,
manual/automatic U-turns and automatic AB switching do not alter row geometry.

Leaving the finite path, invalid GPS, reverse or contour conflict drops capture and
disengages AUTO. Changing mode/options/selection disengages AUTO; the module never
engages it by itself. Field switch and restart start with row guidance disabled.
Recording rejects stale positions, large gaps, jumps and mixed simulation/live points.
Camera vision is outside this feature: these paths are recorded GPS geometry.

## Persistence and code map

`individual-rows.json` lives in the open field, contains IDs, source, UTC timestamps,
simulation markers, origin and local metre coordinates. Save uses an atomic replacement
with `.bak`; malformed/discontinuous geometry or a different origin is rejected.
The older AGO `SavedRows.json` can be imported with the same field origin; it is retained.

- Services/SavedRows/SavedRowTracks.cs: format, migration, recorder and finite selector.
- Services/SavedRows/IndividualRowsGuidance.cs: cycle-worker selection.
- Models/Pipeline/IndividualRowsRequest.cs and Services/Pipeline/PipelineIntents.cs: atomic handoff.
- Services/Pipeline/GpsPipelineService.cs: native guidance and disengagement.
- Services/FieldAssistants/Modules/SavedRowsModule.cs: field-scoped recording and RPC.
- RemoteWiring/SavedRowsHost.cs: fresh authority and native configuration bridge.
- RemoteServer/wwwroot/assistants/savedrows.js and row-map.js: controls and actual path overlay.

Tests cover persistence/origin validation, recording/capture, connection authority,
field changes and native pipeline behavior including finite endpoints and prohibited
parallel/nudge/turn behavior. Synthetic tests are not tractor field validation.
The recorder/selector was ported from downstream AGO SavedRowTracks.cs and
FormGPS.SavedRows.cs, retaining the project GPL license.
