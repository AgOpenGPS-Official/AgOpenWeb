# Split Vehicle Configuration into separate Vehicle + Tool dialogs

> **Status:** Plan approved-in-progress; not yet implemented. Resume here.
> Branch off `develop` (PRs target develop, not master).

## Context

Today all equipment configuration lives in one monolithic `ConfigurationDialog`
with six left-strip tabs (Vehicle, Tool, U-Turn, Machine Control, Tram Lines,
Data Sources), all bound to a single `ConfigurationViewModel`. The underlying
**data model is already split** (issue #346): `ConfigurationStore` has separate
`Vehicle`/`Tool` sub-stores, separate `VehicleProfileService`/`ToolProfileService`
saving to `Vehicles/`/`Tools/` JSON, and `ConfigurationService.LoadProfiles(vehicle, tool)`
loads an arbitrary mix-and-match pair. A two-column picker
(`LoadVehicleToolDialogPanel`) already exists with Vehicle/Tool list columns and
New/Delete/Rename/Reset/Load buttons.

What's missing is the **UI split**: the operator can't yet mix-and-match a tractor
with a tool through dedicated config panels. This change splits the one dialog into
two tabbed dialogs reached from the picker (which becomes the hub), so a vehicle and
a tool are configured independently. It also corrects a data-model error: the single
`Tool.HitchLength` is overloaded — it serves as the *rigid* tool's working-center
distance for fixed tools **and** the *vehicle* hitch-pin distance for trailing/TBT
tools. Those are two different physical quantities and must be split.

### Decisions (confirmed with user)
- **Picker is the hub** — add "Configure Vehicle"/"Configure Tool" buttons to
  `LoadVehicleToolDialogPanel`; retire the old "Vehicle Settings" nav button.
- **Both new dialogs stay tabbed.**
- **Hitch model (corrected from AgOpen + user's physical definitions).** There are six
  hitch distances; the current single `Tool.HitchLength` conflates two of them, so the
  fix is to **split** it, not just move it. Ownership and measurement:

  | # | Tool type | Measurement | Owner / field |
  |---|-----------|-------------|---------------|
  | 1 | **Vehicle** | rear axle center → tractor hitch pin | **NEW `Vehicle.HitchLength`** — used only for trailing/TBT |
  | 2 | 3-pt rear (rigid) | axle center → implement working center (e.g. tiller shaft) | `Tool.HitchLength` (kept, redefined as rigid working-center; tool-dependent) |
  | 3 | front fixed (rigid) | rear axle center → front working center (e.g. disc shaft) | `Tool.HitchLength` (positive/front sign) |
  | 4 | trailing | implement tongue hitch → implement first axle | `Tool.TrailingHitchLength` (exists) |
  | 5 | TBT tank | tank tongue hitch → tank first axle | `Tool.TankTrailingHitchLength` (exists) |
  | 6 | TBT tool | tool tongue hitch → tool first axle | `Tool.TrailingHitchLength` (reused, stage 2) |

  The (a)-as-measured / (b)-decoupled question is settled by the measurements themselves:
  #2/#3 are combined axle→working-center values that live with the Tool (re-measure on
  tractor swap — rare for bolted rigid tools); #4/#5/#6 are measured on the implement so
  they're already tractor-independent (mix-and-match just works). Only **#1** is a genuine
  Vehicle property. All six feed u-turn pathing and the existing jack-knife detection, so
  the geometry must branch correctly by tool type.

### Reuse (no rewrites)
- The 6 tab UserControls in `Shared/AgValoniaGPS.Views/Controls/Dialogs/Configuration/`
  are self-contained (`x:DataType="vm:ConfigurationViewModel"`) and are reused as-is.
- The **same `ConfigurationViewModel` instance** backs both new dialogs (both edit the
  same live store; no DI change — the VM is constructed lazily in a command, not by DI).
- Visibility uses the existing plain-bool model (`IsDialogVisible` on the VM, verified
  at `ConfigurationDialog.axaml:29`), not the `ActiveDialog`/`DialogType` state machine.

---

## Part A — Split the hitch model (model + geometry + persistence + migration)

Do this first; it is independent of the UI split and the riskiest piece. The core move is
**splitting the overloaded `Tool.HitchLength`**: add `Vehicle.HitchLength` (#1, the tractor
hitch pin, used only by trailing/TBT) and keep `Tool.HitchLength` (#2/#3, the rigid
working-center, tool-dependent). Geometry branches by tool type.

**A1. Add `VehicleConfig.HitchLength`** (#1) — `Shared/AgValoniaGPS.Models/Configuration/VehicleConfig.cs`
(after the antenna block ~line 98). `double` default 1.8, rear axle → tractor hitch pin. Keep
unclamped/signed; trailing geometry takes `Math.Abs()` and applies the rear sign itself.
`Tool.HitchLength` (ToolConfig.cs:63) stays; clarify its comment as "rigid tool: axle center →
implement working center (front +, rear −)."

**A2. Geometry — branch the hitch reference by tool type** —
`Shared/AgValoniaGPS.Services/Tool/ToolPositionService.cs`
- `Update` (~line 114): the hitch distance feeding `_hitchPosition` depends on tool type.
  Rigid tools use the tool's working-center (#2/#3); trailing/TBT use the vehicle pin (#1):
  ```csharp
  var veh = ConfigurationStore.Instance.Vehicle;
  double hitchDistance = (tool.IsToolFrontFixed || tool.IsToolRearFixed)
      ? Math.Abs(tool.HitchLength)     // #2/#3 rigid working center (tool-dependent)
      : Math.Abs(veh.HitchLength);     // #1 tractor hitch pin (trailing/TBT)
  ```
  Sign logic (front +, rest −) is unchanged. For rigid tools `CalculateFixedToolPosition`
  still places the tool at `_hitchPosition` (now the working center) — correct.
  Trailing/TBT (`TrailingHitchLength`/`TankTrailingHitchLength`/`TrailingToolToPivotLength`,
  the implement arms #4/#5/#6) are unchanged — they're already tool-owned and decoupled.
- `:455` (the other `Math.Abs(tool.HitchLength)` site) — apply the same tool-type branch.

**A3. Repoint the other runtime consumers** (verified list):
- `Shared/AgValoniaGPS.ViewModels/MainViewModel.cs:626` — `Math.Abs(tool.HitchLength)`: confirm
  context (hitch-point calc) and apply the same tool-type branch (rigid → `Tool.HitchLength`,
  trailing/TBT → `Vehicle.HitchLength`).
- `Shared/AgValoniaGPS.Services/AutoSteer/AutoSteerService.cs:567` —
  `config.Tool.HitchLength + config.Tool.TrailingHitchLength` is a trailing-geometry sum, so
  the pin term → `config.Vehicle.HitchLength + config.Tool.TrailingHitchLength`.
- `Shared/AgValoniaGPS.Views/Controls/SkiaMapControl.cs:376` — `HitchLength = toolCfg.HitchLength`:
  check what the renderer draws; if it's the trailing hitch link, use `vehicleCfg.HitchLength`
  (in scope ~338), if the rigid mount keep `toolCfg.HitchLength`. Verify against the draw code.

**A4. Vehicle JSON** — `Shared/AgValoniaGPS.Services/Profile/VehicleProfileJsonService.cs`
- `VehicleDto`: add `public double? HitchLength { get; set; }` (nullable → detect absence for migration).
- `ToDto`: write `HitchLength = store.Vehicle.HitchLength`.
- `ApplyDtoToStore`: `store.Vehicle.HitchLength = dto.Vehicle?.HitchLength ?? double.NaN`
  (NaN sentinel = "not present"; resolved in A6).

**A5. Tool JSON** — `Shared/AgValoniaGPS.Services/Profile/ToolProfileJsonService.cs`
- **Keep** `Tool.HitchLength` fully (read line 172 + write line 124) — it remains a live tool
  field (#2/#3 rigid working center). No change here beyond confirming it persists.
- v1 reader `ProfileJsonServiceV1.cs` (read 271 / write 160) — unchanged.

**A6. One-way migration — seed `Vehicle.HitchLength` for legacy profiles** —
`Shared/AgValoniaGPS.Services/ConfigurationService.cs`, in `LoadProfiles` **after** the tool
load (~line 110, both files loaded), before `LoadAutoSteerConfig`:
```csharp
// Legacy profiles stored the trailing/TBT tractor-pin distance under Tool.HitchLength.
// Now #1 lives on the vehicle. If the vehicle file predates the split, seed it from the
// tool's legacy value so trailing/TBT setups keep working; it persists on next save.
// (Rigid setups keep their own Tool.HitchLength; harmless that Vehicle also gets a value.)
if (double.IsNaN(Store.Vehicle.HitchLength))
    Store.Vehicle.HitchLength = Store.Tool.HitchLength;
```
Ordering matters: vehicle loads before tool, so migrate after both. `ReloadCurrentProfile`
calls `LoadProfiles`, so it's covered.

**A6b. Defaults / legacy XML** —
- `VehicleProfileService.cs:316` (AOG XML import): the key `setVehicle_hitchLength` is the
  vehicle pin → write `store.Vehicle.HitchLength = GetDouble(settings,"setVehicle_hitchLength",1.8)`.
  (Leave the tool's own hitch import to its existing path.)
- `VehicleProfileService.cs:242` `CreateDefaultProfile`: add `store.Vehicle.HitchLength = 1.8;`.

**A7. Edit commands** — `Shared/AgValoniaGPS.ViewModels/ConfigurationViewModel.cs`
- Add a `EditVehicleHitchLengthCommand` (seed/set `Vehicle.HitchLength`) for the new Vehicle field.
- `EditHitchLengthCommand` (1083–1086): this currently backs the Vehicle-tab "Hitch Length"
  bound to `Tool.HitchLength`. **Move this field to the Tool dialog** (it's the rigid working
  center) — keep the command pointing at `Tool.HitchLength`, relabel to "Rigid Working-Center
  Distance" / show only for fixed tools.
- `EditToolHitchLengthCommand` (1145–1148): consolidate with the above (both edit
  `Tool.HitchLength`) — keep one.

**A8. UI bindings** (the field relocations land in Part C's tabs):
- Vehicle Dimensions tab gains a **Vehicle Hitch Length** field bound to `Vehicle.HitchLength`
  / `EditVehicleHitchLengthCommand`.
- `VehicleConfigTab.axaml:246` — the existing field binding `Tool.HitchLength` is the rigid
  working center; **move it out of the Vehicle tab into the Tool dialog's hitch sub-tab**,
  shown only when a fixed (front/rear) tool is selected.
- `ToolSubTabs/ToolHitchSubTab.axaml` — host the rigid working-center field (`Tool.HitchLength`)
  plus the existing trailing/tank fields, visibility-switched by tool type (mirror AgOpen's
  per-type show/hide).

---

## Part B — Shared input overlays (extract once, reuse twice)

The numeric-keypad / text-input / color-picker overlays (`ConfigurationDialog.axaml`
~179–497) are byte-identical for both dialogs and bind to VM props that already exist
(`IsNumericInputVisible`, `NumericInput*Command`, `IsTextInputVisible`,
`IsColorPickerVisible`, `PresetColors`, …). Extract them rather than duplicate 320 lines.

**New:** `Shared/AgValoniaGPS.Views/Controls/Dialogs/Configuration/ConfigInputOverlays.axaml` (+ `.axaml.cs`)
- Root `Panel`, `x:DataType="vm:ConfigurationViewModel"`, three overlay `Border`s lifted verbatim.
- Declare the `UintToBrushConverter` (today at `ConfigurationDialog.axaml:99`) locally.
- Inherits the parent dialog's DataContext (the shared VM), so bindings resolve unchanged.

---

## Part C — Two new tabbed dialogs

**New shells** under `Shared/AgValoniaGPS.Views/Controls/Dialogs/` (copy `ConfigurationDialog.axaml` shell: header, fixed-size Border, footer Apply ✓ bound to `ApplyCommand`):

- **`VehicleConfigDialog.axaml`** (+ `.axaml.cs`): `IsVisible="{Binding IsVehicleDialogVisible}"`,
  left-strip TabControl with `<config:VehicleConfigTab/>` (IconVehicle) + `<config:SourcesConfigTab/>`
  (IconSources). Drop `<config:ConfigInputOverlays Grid.Row="1" ZIndex="100"/>` into the content row.
- **`ToolConfigDialog.axaml`** (+ `.axaml.cs`): `IsVisible="{Binding IsToolDialogVisible}"`, tabs
  `<config:ToolConfigTab/>` (IconTool — Sections remains its nested sub-tab), `<config:UTurnConfigTab/>`
  (IconUTurn), `<config:MachineControlConfigTab/>` (IconMachine), `<config:TramConfigTab/>` (IconTram).
  Plus `ConfigInputOverlays`.
- The icon `ImageBrush` resources (`ConfigurationDialog.axaml` ~31–76) are used by both — move into a
  shared `ResourceDictionary` (e.g. `Configuration/ConfigIcons.axaml`) merged by both shells.

**VM visibility flags** — `ConfigurationViewModel.cs` (#region Dialog Visibility ~42–51):
replace `IsDialogVisible` with `IsVehicleDialogVisible` and `IsToolDialogVisible` (`SetProperty` bools).

**Show/Cancel commands** — `Shared/AgValoniaGPS.ViewModels/MainViewModel.Commands.Configuration.cs`:
replace `ShowConfigurationDialogCommand` with `ShowVehicleConfigDialogCommand` /
`ShowToolConfigDialogCommand` (+ Cancel variants). Construct the VM once (lazy `EnsureConfigVm()`),
wire `CloseRequested` to clear **both** flags (only one is open at a time). Declare the new `ICommand?`
properties in `MainViewModel.cs` near the existing ones.

**Register** — `DialogOverlayHost.axaml`: replace the single `ConfigurationDialog` (line 67) with
`VehicleConfigDialog` + `ToolConfigDialog`, both `DataContext="{Binding ConfigurationViewModel}"`,
declared **after** `LoadVehicleToolDialogPanel` (line 71) so they overlay the picker.

> **Shared-VM semantics to document:** Apply/Cancel operate on the whole store
> (`SaveProfiles` / `ReloadCurrentProfile`). Cancelling either dialog reloads the full
> vehicle+tool pair. This matches today's single-dialog behavior; not changed.

---

## Part D — Picker becomes the hub

**`LoadVehicleToolDialogViewModel.cs`**: add ctor `Action onConfigureVehicle, onConfigureTool`
+ `ConfigureVehicleCommand`/`ConfigureToolCommand` (`RelayCommand`s), matching the existing
Action-injection pattern (ctor lines ~33–55). "Configure" operates on the currently active/loaded
profile (operator Loads the pair first, then Configures).

**`LoadVehicleToolDialogPanel.axaml`**: add a "Configure Vehicle" button to the Vehicle column grid
(`Grid.Row="2" Grid.Column="0"`) and "Configure Tool" to the Tool column grid (`Grid.Column="2"`),
bound to those passthrough commands (avoids ancestor bindings since the inner Border rebinds
DataContext to `LoadVehicleToolDialogVm`).

**`MainViewModel.Commands.Configuration.cs`** `ShowLoadVehicleToolDialogCommand`: pass
`onConfigureVehicle: () => ShowVehicleConfigDialogCommand!.Execute(null)` and the Tool equivalent.

**`ConfigurationPanel.axaml`**: remove the "Vehicle Settings" `MenuButton` (lines 49–51); keep
"Load Profile" (the picker is now the single entry to configuration).

---

## Part E — Delete the obsolete dialog (last)

After B–D land and a clean grep, **delete** `ConfigurationDialog.axaml` + `.axaml.cs` and remove now-dead
members: `ConfigurationViewModel.IsDialogVisible`, the old `ShowConfigurationDialogCommand`/
`CancelConfigurationDialogCommand`. `DialogType.Configuration` / `IsConfigurationDialogVisible` in
`UIState.cs` were already vestigial — leave unless grep shows no refs.

---

## Verification

**Build:** `dotnet build AgValoniaGPS.sln` (Shared covers cross-platform parity; confirm the three
`ServiceCollectionExtensions.cs` compile untouched — no DI change expected).

**Tests** (`Tests/AgValoniaGPS.Services.Tests/`):
- Migration: tool profile with `hitchLength=2.3` + vehicle profile *without* `hitchLength` →
  `LoadProfiles` → assert `Store.Vehicle.HitchLength == 2.3` (legacy pin seeded).
- No-clobber: vehicle `hitchLength=1.1` + tool `9.9` → assert `Store.Vehicle.HitchLength == 1.1`.
- Round-trip: set both `Vehicle.HitchLength` and `Tool.HitchLength`, `SaveProfiles`, reload,
  assert both preserved independently (the split persists in two files).
- Geometry (the key correctness test): with a **rigid** tool, `ToolPositionService` uses
  `Tool.HitchLength` for the hitch position and ignores `Vehicle.HitchLength`; with a **trailing/TBT**
  tool it uses `Vehicle.HitchLength` for the pin and `Tool.TrailingHitchLength`/`TankTrailingHitchLength`
  for the arms. Set Vehicle vs Tool hitch to distinct values per tool type and assert the resulting
  tool/tank positions.

**Manual** (`dotnet run --project Platforms/AgValoniaGPS.Desktop/...`):
1. Config panel → "Load Profile" → picker shows "Configure Vehicle"/"Configure Tool" under their columns.
2. Configure Vehicle → Vehicle + GPS Data Sources tabs; Dimensions → tap the new **Vehicle Hitch Length** field → keypad overlay (proves `ConfigInputOverlays`) → Apply.
3. Configure Tool → Tool/Implement + U-Turn + Machine + Tram tabs; the hitch sub-tab shows the rigid working-center field for fixed tools and Trailing/Tank fields for trailing/TBT, switched by tool type; color picker on Sections works.
4. Closing a config dialog returns to the still-open picker.
5. Load a pre-migration profile → `Vehicle.HitchLength` is seeded from the legacy tool value; Save → vehicle JSON gains `hitchLength`, tool JSON keeps its own `hitchLength` (rigid working center).
6. Geometry sanity: a rigid tool and a trailing tool each render their tool at the correct distance after the tool-type branch (set Vehicle and Tool hitch to clearly different values to see which one each tool type honors).

## Risks / ordering
- **Safety-critical geometry**: `Tool.HitchLength` is overloaded today (rigid working-center
  AND trailing pin). The split must branch by tool type in *every* consumer (A2/A3) — a missed
  site silently mis-places the tool, corrupting u-turn paths and jack-knife detection. The
  per-tool-type geometry test (Verification) is the guard; run the `TestRunner` guidance harness too.
- **Migration must run after both files load** (vehicle loads first); use the NaN sentinel in `LoadProfiles`.
- **Sign convention**: keep `Vehicle.HitchLength` unclamped/signed; geometry `Math.Abs()`-es and
  applies the rear sign — avoid double-negation. Same for the kept `Tool.HitchLength`.
- **Z-order**: config dialogs declared after the picker in `DialogOverlayHost`.
- **Delete Part E last**, after a clean grep, to avoid breaking the build.
- Order: A → B → C → D → E. Branch off `develop` (a feature branch; keep until the whole split works end-to-end).
- Bump patch version in `./sys/version.h` when committing.
- Out of scope: a *new* jack-knife warning/prevention feature. This plan only ensures the hitch
  geometry remains correct so existing (`IsJackknifed`) and future logic have accurate inputs.

> Note: memory flags a config/state-storage audit with "UI paused" — this work is UI recomposition
> on the already-split data model and adds no new shadow stores, so it's compatible. Flagging for awareness.
