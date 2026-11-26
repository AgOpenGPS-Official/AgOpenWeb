# Smart WAS Calibration — Implementation Plan (issue #223)

## 0. Summary of the port

`CSmartWAS.cs` is a 282-line self-contained statistical analyzer. The port lifts cleanly into AgOpenWeb as one service plus one ViewModel plus one dialog, because the upstream class only depends on three external things — all of which already exist in AgOpenWeb:

| Upstream dependency | AgOpenWeb equivalent |
|---|---|
| `mf.isBtnAutoSteerOn` | `IAutoSteerService.IsEngaged` |
| `mf.avgSpeed` (km/h) | `VehicleState.Speed` (m/s) — **scale change required** |
| `mf.guidanceLineDistanceOff` (mm) | `VehicleState.CrossTrackError` (m) — **scale change required** |
| `Properties.VehicleSettings.Default.setArdSteer_setting0 & 1` | `ConfigurationStore.Instance.AutoSteer.InvertWas` |
| Per-cycle steer-angle sample | `SteerModuleData.ActualSteerAngle` flowing through `AutoSteerService.ProcessSteerData` |

No new PGNs; no upstream behavior beyond the analyzer needs porting.

---

## 1. File map (upstream → AgOpenWeb)

| Role | New file | Namespace |
|---|---|---|
| Analyzer / service implementation | `Shared/AgOpenWeb.Services/AutoSteer/SmartWasCalibrationService.cs` | `AgOpenWeb.Services.AutoSteer` |
| Service interface | `Shared/AgOpenWeb.Services/Interfaces/ISmartWasCalibrationService.cs` | `AgOpenWeb.Services.Interfaces` |
| Snapshot DTO (results bundle for UI) | embedded in interface file as `readonly struct SmartWasSnapshot` | `AgOpenWeb.Services.Interfaces` |
| Dialog ViewModel | `Shared/AgOpenWeb.ViewModels/SmartWasViewModel.cs` | `AgOpenWeb.ViewModels` |
| Dialog AXAML | `Shared/AgOpenWeb.Views/Controls/Dialogs/SmartWasDialogPanel.axaml` | `AgOpenWeb.Views.Controls.Dialogs` |
| Dialog code-behind | `Shared/AgOpenWeb.Views/Controls/Dialogs/SmartWasDialogPanel.axaml.cs` | `AgOpenWeb.Views.Controls.Dialogs` |
| Service unit tests | `Tests/AgOpenWeb.Services.Tests/SmartWasCalibrationServiceTests.cs` | n/a |
| ViewModel/headless UI tests | `Tests/AgOpenWeb.UI.Tests/SmartWasViewModelTests.cs` | n/a |

Edits (no new file):
- `Shared/AgOpenWeb.Models/State/UIState.cs` — add enum + visibility property + `OnPropertyChanged` line
- `Shared/AgOpenWeb.Views/Controls/DialogOverlayHost.axaml` — add `<dialogs:SmartWasDialogPanel/>`
- `Shared/AgOpenWeb.ViewModels/AutoSteerConfigViewModel.cs` — add `OpenSmartWasCommand`, accept a `Action? openSmartWas` in ctor
- `Shared/AgOpenWeb.ViewModels/MainViewModel.cs` — add `SmartWasViewModel` property
- `Shared/AgOpenWeb.ViewModels/MainViewModel.Commands.Configuration.cs` — wire `ShowAutoSteerConfig` to pass the Smart WAS opener; add `ShowSmartWasCommand`
- `Shared/AgOpenWeb.Services/AutoSteer/AutoSteerService.cs` — emit a single new internal hook into Smart WAS at the end of `ProcessSteerData`
- `Shared/AgOpenWeb.Views/Controls/Dialogs/AutoSteerConfigPanel.axaml` — re-enable the unused "Wizard" button cell (Grid.Column 0) into a "Smart WAS" entry, or add a fifth button column. Currently that button is `IsEnabled="False" Opacity="0.5"`; the simplest path is to repurpose it and bind `Command="{Binding OpenSmartWasCommand}"`.
- All three `Platforms/*/DependencyInjection/ServiceCollectionExtensions.cs` — register `ISmartWasCalibrationService`

---

## 2. Service interface — `ISmartWasCalibrationService`

```csharp
public interface ISmartWasCalibrationService
{
    // Lifecycle
    void Start();   // begin accumulating samples (gated by IsEngaged + speed + XTE)
    void Stop();    // pause accumulation; analysis remains valid
    void Reset();   // clear buffer + analysis

    // Sample sink — called by AutoSteerService.ProcessSteerData on the UDP receive thread
    void AddSample(double steerAngleDegrees);

    // Apply support — shifts the existing buffer so a follow-up Apply doesn't double-correct
    void ApplyOffsetCorrection(double offsetDegrees);

    // Snapshot for UI (atomic read of all six numbers + flags)
    SmartWasSnapshot GetSnapshot();

    // Fired on the UDP receive thread after each AnalyzeData() — UI MUST marshal
    event EventHandler<SmartWasSnapshot>? SnapshotChanged;

    // Lightweight polled state
    bool IsCollecting { get; }
}

public readonly struct SmartWasSnapshot
{
    public bool IsCollecting   { get; init; }
    public int  SampleCount    { get; init; }
    public double Mean         { get; init; }
    public double Median       { get; init; }
    public double StdDev       { get; init; }
    public double RecommendedOffset { get; init; }   // degrees; sign already negated from median
    public double Confidence       { get; init; }    // 0..100
    public bool   HasValidCalibration { get; init; }
}
```

Internal layout mirrors `CSmartWAS` exactly: `MAX_SAMPLES=2000`, `MIN_SAMPLES=200`, `MIN_SPEED_KMH=2.0`, `MAX_ANGLE_DEG=15.0`, `MAX_DIST_OFF_M=0.5` (converted from upstream's 500mm), `List<double> _history` guarded by `_dataLock`, `_meanAngle/_medianAngle/_stdDeviation/_recommendedOffset/_confidenceLevel/_hasValidCalibration` private fields, `AnalyzeData()`, `CalculateStatistics()`, `CalculateConfidence()` — preserved byte-for-byte logic.

The service takes its gating inputs **by injected reference** rather than reading globals. Constructor:

```csharp
public SmartWasCalibrationService(
    IAutoSteerService autoSteerService,    // IsEngaged
    ApplicationState  appState)            // appState.Vehicle.Speed (m/s), appState.Vehicle.CrossTrackError (m)
```

(Plus reading `ConfigurationStore.Instance.AutoSteer.InvertWas` on each `AddSample` — same singleton pattern `AutoSteerService` already uses for sensor checks.)

---

## 3. Sample input wiring — **direct call from `AutoSteerService.ProcessSteerData`**

I considered all three options and chose **direct call**. Rationale:

| Option | Rate | Thread | Verdict |
|---|---|---|---|
| Subscribe to `IAutoSteerService.StateUpdated` | ~10 Hz (cycle worker) | Cycle worker | **Rejected** — upstream collects at ~50 Hz (PGN 253 rate); 10 Hz cuts effective sample density 5×. With `MIN_SAMPLES=200`, that's 20s vs 4s of driving data to first valid analysis. Operationally meaningful difference on short straight runs. |
| Subscribe to `IUdpCommunicationService.DataReceived` directly | ~50 Hz | UDP receive thread | **Rejected** — duplicates the PGN 253 parse already happening in `AutoSteerService.ProcessSteerData`. Two parsers for one packet is a smell. |
| **Direct call from `AutoSteerService.ProcessSteerData`** | ~50 Hz | UDP receive thread | **Selected** — the parse is already done; we already have `steerData.ActualSteerAngle` in hand. One extra method call per packet. No new subscriptions, no dispatcher hops on the hot path. |

**Concrete change to `AutoSteerService`:**

Add a nullable `ISmartWasCalibrationService? _smartWas` field (settable via `SetSmartWasService(ISmartWasCalibrationService)` to break the DI cycle — `AutoSteerService` is constructed first, then Smart WAS is constructed with `IAutoSteerService` injected, then `MainViewModel` calls `_autoSteerService.SetSmartWasService(_smartWasService)` once at startup, exactly the same pattern as `SetTramLineService` at AutoSteerService.cs:96–99).

At the end of `ProcessSteerData(byte[] data)` (after the existing `_state.ActualSteerAngle = steerData.ActualSteerAngle;` write), add:

```csharp
_smartWas?.AddSample(steerData.ActualSteerAngle);
```

The gating (IsEngaged, speed, XTE, |angle|<15°, IsCollecting flag) lives entirely inside `SmartWasCalibrationService.AddSample` — `AutoSteerService` stays dumb about Smart WAS rules. This keeps the AutoSteer hot path one extra branch (the null check) when Smart WAS isn't running.

**Threading note:** `AddSample` runs on the UDP receive thread, mutates `_history` under `_dataLock`, and fires `SnapshotChanged` on the same thread. The ViewModel handler must `Dispatcher.UIThread.Post` (PR #320 hazard).

---

## 4. Apply flow

Click "Apply" in the dialog → `SmartWasViewModel.ApplyCommand` executes (UI thread):

1. Read snapshot: `var snap = _smartWas.GetSnapshot();`
2. Bail if `!snap.HasValidCalibration` (button is also disabled when invalid; double-check defensively).
3. Compute counts: `int offsetCounts = (int)Math.Round(snap.RecommendedOffset * config.AutoSteer.CountsPerDegree);`
4. Apply to config: `config.AutoSteer.WasOffset += offsetCounts;` — **additive**, exactly like the manual `ZeroWasCommand` at AutoSteerConfigViewModel.cs:439–450. The module's WAS offset is in raw counts.
5. Mark dirty: `_configService.Store.MarkChanged();`
6. Shift the buffer to prevent double-correction: `_smartWas.ApplyOffsetCorrection(snap.RecommendedOffset);` — preserves upstream behavior; analysis re-runs internally with the new buffer.
7. Send PGN 252 (Steer Settings carries `WasOffset`): use the existing `PgnBuilder.BuildSteerSettingsPgn(config.AutoSteer)` and `_udpService.SendToModules(pgn)` — same code path the manual zero hits via `SendSteerSettingsPgn`. Optionally also send PGN 251 for consistency with `SendAndSaveCommand`. Recommend just 252 (matches `ZeroWasCommand`'s minimal send pattern, since only `WasOffset` changed).
8. Persist: `_configService.SaveProfile(config.ActiveProfileName);` (matches `SendAndSaveCommand`).

Buffer keeps accumulating after Apply — operator can verify the new offset is producing a near-zero recommendation.

---

## 5. UI dialog design

**Layout (Border, 360 wide × ~480 tall, centered overlay matching existing dialog conventions):**

```
┌─ Smart WAS Calibration ─────────────────[X]─┐
│                                             │
│ Status:  Collecting · 612 / 200 min         │
│                                             │
│ ┌─ Statistics ────────────────────────────┐ │
│ │  Mean       :  -0.42 °                  │ │
│ │  Median     :  -0.38 °                  │ │
│ │  Std Dev    :   0.21 °                  │ │
│ └─────────────────────────────────────────┘ │
│                                             │
│ ┌─ Recommendation ────────────────────────┐ │
│ │  Offset     :  +0.38 °  (+38 counts)    │ │
│ │  Confidence :  76 %                     │ │
│ │  [progress bar 0-100]                   │ │
│ └─────────────────────────────────────────┘ │
│                                             │
│ ┌─ Drive at >2 km/h with autosteer on ──┐   │
│ │  XTE within ±50 cm.                   │   │
│ └───────────────────────────────────────┘   │
│                                             │
│ [Reset]  [Stop]  [Apply]    [Close]         │
└─────────────────────────────────────────────┘
```

**Bindings (all on `SmartWasViewModel`):**
- `SampleCountText` — "{0} / 200 min" string
- `IsCollecting` — drives Start/Stop button toggle
- `Mean`, `Median`, `StdDev` — formatted doubles
- `RecommendedOffsetDegrees`, `RecommendedOffsetCounts` — formatted
- `Confidence` — double 0–100, also drives `ProgressBar.Value`
- `HasValidCalibration` — drives Apply button `IsEnabled`
- `StatusMessage` — derived from `IsCollecting` + `IsAutoSteerEngaged` ("Waiting for autosteer engage...", "Collecting", "Stopped — N samples", "Need 200 samples (have 78)")

**Validation:**
- Apply disabled unless `HasValidCalibration` (matches upstream's `confidence > 40 && |offset| < 10°` rule).
- Reset is always enabled.
- Start ↔ Stop is a single toggle button; label changes via binding.

**Where the Smart WAS button surfaces in the UI tree:**

The most natural place is the AutoSteerConfigPanel bottom-action row. There's already an unused "Wizard" button at Grid.Column=0 (AutoSteerConfigPanel.axaml:1085–1092 — `IsEnabled="False" Opacity="0.5"`, no command bound). Re-purpose that cell as "Smart WAS":

```xml
<Button Grid.Column="0" Margin="2" Padding="4"
        Background="#DD2196F3"
        Command="{Binding OpenSmartWasCommand}">
    <StackPanel HorizontalAlignment="Center">
        <Image Source="avares://AgOpenWeb.Views/Assets/Icons/SteerWheel.png"
               Width="32" Height="32" Stretch="Uniform" HorizontalAlignment="Center"/>
        <TextBlock Text="Smart WAS" FontSize="10" HorizontalAlignment="Center"
                   Foreground="{DynamicResource SystemControlForegroundBaseHighBrush}"/>
    </StackPanel>
</Button>
```

Choose any existing icon (`SteerWheel.png` is the closest semantic). The `OpenSmartWasCommand` lives on `AutoSteerConfigViewModel` and invokes the `Action? _openSmartWas` callback passed in by `MainViewModel`, mirroring the existing `_launchWizard` pattern at AutoSteerConfigViewModel.cs:43, 70, 75, 982. Closing the AutoSteer config panel before showing Smart WAS isn't necessary — the AutoSteerConfigPanel uses its own `IsPanelVisible` flag (not `UIState.ActiveDialog`), so they coexist; the Smart WAS dialog floats on top.

---

## 6. DialogType enum addition

Edit `Shared/AgOpenWeb.Models/State/UIState.cs`:

1. Inside the `DialogType` enum, append `SmartWas` as the last member.
2. In `UIState`, add the convenience property near the others around line ~116:
   ```csharp
   public bool IsSmartWasDialogVisible => ActiveDialog == DialogType.SmartWas;
   ```
3. In the giant `OnPropertyChanged(...)` block in `ActiveDialog.set` (around lines 41–73), add `OnPropertyChanged(nameof(IsSmartWasDialogVisible));`.

Then in `Shared/AgOpenWeb.Views/Controls/DialogOverlayHost.axaml`, append (after the `AutoSteerConfigPanel` line at 68):

```xml
<dialogs:SmartWasDialogPanel DataContext="{Binding SmartWasViewModel}"/>
```

The `SmartWasDialogPanel.axaml` itself binds `IsVisible` via the centralized `UIState` route (matches most other dialogs — `OffsetFixDialogPanel` binds via `State.UI.IsOffsetFixPanelVisible`). The dialog opens via `State.UI.ShowDialog(DialogType.SmartWas)` and closes via `State.UI.CloseDialog()`.

---

## 7. DI wiring (all three platforms)

In each of:
- `Platforms/AgOpenWeb.Desktop/DependencyInjection/ServiceCollectionExtensions.cs`
- `Platforms/AgOpenWeb.iOS/DependencyInjection/ServiceCollectionExtensions.cs`
- `Platforms/AgOpenWeb.Android/DependencyInjection/ServiceCollectionExtensions.cs`

Add immediately after the `AutoSteerService` registration:

```csharp
// Smart WAS calibration (statistical WAS zero analyzer)
services.AddSingleton<ISmartWasCalibrationService, SmartWasCalibrationService>();
```

In `MainViewModel.cs` constructor, after `(_autoSteerService as AutoSteerService)?.SetTramLineService(_tramLineService)` at line 257, add:
```csharp
(_autoSteerService as AutoSteerService)?.SetSmartWasService(_smartWasService);
_smartWasService.Start(); // service is started but not collecting until user clicks Start in dialog
```

Add `ISmartWasCalibrationService _smartWasService` to the field list and the constructor signature in MainViewModel.cs around lines 70/185.

---

## 8. Tests

### 8.1 Pure math — unit-testable, no hardware

`Tests/AgOpenWeb.Services.Tests/SmartWasCalibrationServiceTests.cs`. Construct the service with a stub `IAutoSteerService` (NSubstitute, `IsEngaged.Returns(true)`) and a real `ApplicationState` whose `Vehicle.Speed` and `Vehicle.CrossTrackError` we set directly. Tests:

1. **Mean/median/stddev correctness** — feed 200 samples drawn from `N(μ=-0.5, σ=0.2)` (deterministic seed), assert mean within tolerance, median within tolerance, stddev within tolerance, `RecommendedOffset ≈ +0.5°`.
2. **Confidence math** — feed exactly the normal distribution (matches expected1Std=0.68 and expected2Std=0.95), assert `Confidence ≥ 90`.
3. **Confidence penalizes large offsets** — feed `N(μ=8, σ=0.1)`, assert magnitudeScore drives Confidence down; `HasValidCalibration` becomes `false` once `|offset| ≥ 10`.
4. **Min sample gate** — 199 samples → `HasValidCalibration == false`; 200th sample triggers analysis.
5. **MAX_SAMPLES rolling buffer** — after 2001 samples, `SampleCount == 2000`.
6. **Gating: speed** — `Vehicle.Speed = 0.5 m/s` (1.8 km/h) → samples rejected; `0.6 m/s` → accepted.
7. **Gating: XTE** — `Vehicle.CrossTrackError = 0.51` m → rejected; `0.49` → accepted.
8. **Gating: angle** — sample with `|angle| > 15°` → rejected.
9. **Gating: not engaged** — `IsEngaged.Returns(false)` → rejected even when `IsCollecting=true`.
10. **InvertWas** — set `ConfigStore.AutoSteer.InvertWas=true`, feed +1°, assert stored sample is -1°.
11. **ApplyOffsetCorrection** — accumulate samples with median = -0.4, call `ApplyOffsetCorrection(0.4)`, assert new median ≈ 0 and new `RecommendedOffset` ≈ 0.
12. **Reset** — clears buffer, zeros all stats, `HasValidCalibration=false`.
13. **Start/Stop** — `Stop` halts accumulation but preserves analysis.

### 8.2 ViewModel / threading

`Tests/AgOpenWeb.UI.Tests/SmartWasViewModelTests.cs` (Avalonia headless):

1. **Apply writes WasOffset additively** — set existing `WasOffset=10`, `CountsPerDegree=100`, recommended offset `+0.5°`. After Apply, `WasOffset == 60`.
2. **Apply calls `ApplyOffsetCorrection`** — verify via NSubstitute.
3. **Apply sends PGN 252** — verify `_udpService.SendToModules` called once with a PGN whose first byte matches `STEER_SETTINGS`.
4. **Apply disabled when `!HasValidCalibration`** — `ApplyCommand.CanExecute()` returns false.
5. **`SnapshotChanged` from background thread reaches UI properties** — fire the event from a `Task.Run`, then `await Dispatcher.UIThread.InvokeAsync(() => {})` to drain, then assert ViewModel `SampleCount` reflects the snapshot. Catches the PR #320 hazard.
6. **Close button restores `UIState.ActiveDialog == None`.**

### 8.3 Needs hardware (manual QA, document only)
- Validating that real-world WAS jitter at 50 Hz produces sensible distributions
- Validating that the 200-sample threshold is reachable in a realistic field run
- Validating that PGN 252 over UDP causes the steering module to actually re-zero

---

## 9. Order of operations (each step compiles independently)

1. **Models** — add `SmartWas` to `DialogType` enum + `IsSmartWasDialogVisible` property + `OnPropertyChanged` line. (Compiles standalone; harmless dead code.)
2. **Service interface** — create `ISmartWasCalibrationService.cs` with the snapshot struct. (Compiles; nothing implements it yet.)
3. **Service implementation** — create `SmartWasCalibrationService.cs` (full port of CSmartWAS math + new gating). (Compiles standalone; nothing calls `AddSample` yet.)
4. **Service tests** — `SmartWasCalibrationServiceTests.cs`. Run them; lock in math correctness before any UI work. (Should pass independently.)
5. **DI registrations** — add `AddSingleton<ISmartWasCalibrationService, SmartWasCalibrationService>()` to all three platform `ServiceCollectionExtensions`. (Compiles; service is now resolvable.)
6. **Wire AutoSteerService → Smart WAS** — add `_smartWas` field, `SetSmartWasService()` setter, single-line call in `ProcessSteerData`. (Compiles; samples now flow but no UI consumes them.)
7. **MainViewModel field + constructor param + setter call** — pulls everything together at app startup. (Compiles; service is wired and producing snapshots, but nothing renders them.)
8. **SmartWasViewModel** — full ViewModel with `Apply`, `Start`, `Stop`, `Reset`, `Close` commands; subscribes to `SnapshotChanged` and marshals via `Dispatcher.UIThread.Post`. (Compiles; not yet visible.)
9. **MainViewModel `SmartWasViewModel` property + `ShowSmartWasCommand`** + register in `MainViewModel.Commands.Configuration.cs` similar to `ShowAutoSteerConfig`. Pass an opener `Action` into `AutoSteerConfigViewModel` constructor. (Compiles; commands exist but no XAML to invoke them.)
10. **`SmartWasDialogPanel.axaml/.cs`** — full dialog. (Compiles; not yet referenced.)
11. **Register dialog in `DialogOverlayHost.axaml`** — single line. (Now visible in app, openable via `State.UI.ShowDialog(DialogType.SmartWas)` from any VM with state access.)
12. **Add Smart WAS button to `AutoSteerConfigPanel.axaml`** (re-enable the disabled Wizard cell) + bind `OpenSmartWasCommand`. (End-to-end working.)
13. **ViewModel/UI tests** — `SmartWasViewModelTests.cs`, then full `dotnet test Tests/`.
14. **Manual smoke** — run desktop, open AutoSteer config, click Smart WAS, verify dialog opens, dummy-feed via simulator, watch numbers tick.

---

## 10. Risk callouts

1. **Unit conversions, not just refactors** — upstream's `MIN_SPEED_KMH = 2.0` compares against `mf.avgSpeed` (km/h). AgOpenWeb's `VehicleState.Speed` is m/s. Compare against `0.5556` (or convert each sample to km/h first via `* 3.6`). Same for XTE: upstream's `MAX_DIST_OFF_MM = 500` compares against millimeters; AgOpenWeb's `CrossTrackError` is meters, so the constant becomes `0.5`. Get this wrong and the gating effectively disables itself or never opens. Add a test that verifies units.

2. **InvertWas semantics drift risk** — upstream reads `Properties.VehicleSettings.Default.setArdSteer_setting0 & 1`. In AgOpenWeb, the equivalent is `ConfigurationStore.Instance.AutoSteer.InvertWas`. Verify these are wired the same way to the module: `AutoSteerConfig.cs` line 543 `if (InvertWas) result |= 0x01;` confirms parity. ✓

3. **Threading on `SnapshotChanged`** — fired from the UDP receive thread. The ViewModel's subscriber MUST `Dispatcher.UIThread.Post` before mutating any INPC properties. The PR #320 commit (`ea9516c`) is the canonical reference. Add an explicit headless test (test #5 above) that fires the event off-thread and asserts the UI property updated after a dispatcher drain.

4. **Sample buffer memory + lock contention** — `MAX_SAMPLES = 2000` `double` = 16 KB, fine. `_dataLock` is held during `AnalyzeData()` which sorts a copy of the list every analysis call (every sample once buffer ≥ MIN). At 50 Hz, that's a sort of up to 2000 doubles 50× per second. LINQ `.OrderBy().ToList()` is fine for 2000 elements, but `_history.Sum(...)` and `_history.Average()` scan the list under the lock. This is exactly what upstream does, so it works in practice. Document it; don't optimize prematurely.

5. **`AutoSteerService.SetSmartWasService` cycle** — the same setter-injection pattern as `SetTramLineService` exists for a reason: `AutoSteerService` and `SmartWasCalibrationService` both want each other in their constructors. The setter pattern lets DI build them in any order. Don't try to use constructor injection on both sides.

6. **Apply click during low-confidence state** — Apply button disable on `!HasValidCalibration` covers UI; service-side `GetSnapshot()` always returns the latest, so any race where the UI sees stale `HasValidCalibration=true` is harmless because Apply is additive — applying a small offset is a no-op on the next analysis. Verified by upstream's identical design.

7. **Buffer survives Stop** — upstream `Stop()` only flips `IsCollecting=false`, doesn't reset. Operator-friendly: pause to look at numbers, resume. Preserve this.

8. **`mf.isBtnAutoSteerOn` vs `IsEngaged` semantics** — upstream gates on the *button*, not the actual engaged state. AgOpenWeb's `IsEngaged` is the engaged state. Equivalent for steady-state operation; differs during the brief engage/disengage transient. Acceptable.

9. **AutoSteerConfigPanel coexistence** — the Smart WAS dialog uses `UIState.ActiveDialog` (so opening it closes any other dialog), but `AutoSteerConfigPanel` uses its own `IsPanelVisible` flag (NOT in `UIState`'s dialog state machine). This means opening Smart WAS from the AutoSteer panel does NOT auto-close the AutoSteer panel. Behavior matches the existing wizard launch (`WizardCommand` at AutoSteerConfigViewModel.cs:978–984 explicitly sets `IsPanelVisible = false`). Decide: do we want Smart WAS to auto-close AutoSteerConfig or float on top? **Recommend: float on top** — operator likely wants both readouts visible. If float-on-top causes z-order issues, set `IsPanelVisible = false` in the opener like the wizard does.

10. **`appState.Vehicle` field naming** — confirm `ApplicationState.Vehicle` exposes `Speed` (m/s) and `CrossTrackError` (m). VehicleState.cs:104 confirms `CrossTrackError` is a public field. The `VehicleState` is a struct in `ApplicationState`; the service reads `_appState.Vehicle.CrossTrackError`. (Or use `VehicleStateSnapshot` from `IAutoSteerService.LatestSnapshot` — also valid, snapshot already has both fields. Pick one: the snapshot is updated every cycle via the cycle worker, ~10 Hz; `_state` direct read is up-to-the-instant. Direct read is fine since this is gating, not display.)

---

## 11. Effort estimate

After reading the upstream code (clean, ~280 lines, no surprises) and confirming AgOpenWeb patterns are a tight match:

| Phase | Hours |
|---|---|
| Service port (math + gating, with unit conversions) | 1.5 |
| Service unit tests (13 cases) | 1.5 |
| Wiring through `AutoSteerService` setter + DI registrations × 3 platforms + MainViewModel | 0.75 |
| `SmartWasViewModel` (commands, threading, snapshot subscription) | 1.5 |
| `SmartWasDialogPanel.axaml` (layout, bindings, validation) | 2.0 |
| UIState DialogType + DialogOverlayHost registration + AutoSteerConfigPanel button | 0.5 |
| Headless UI tests (6 cases including the cross-thread regression) | 1.5 |
| Manual desktop smoke + iOS simulator parity check | 1.0 |
| Buffer + risk callouts (units, threading) | 0.75 |
| **Total** | **~11 hours** |

Realistic single-PR landing window: **1.5 working days**. The "~1 day" earlier guess was light because the AXAML dialog and the ViewModel testing weren't priced in. Code itself is short; the surface area (DI × 3 platforms, `UIState` enum + AXAML registration + button placement + headless test for thread-marshalling) is what eats the day.

---

### Critical Files for Implementation

- `/Users/chris/Code/AgOpenWeb3/Shared/AgOpenWeb.Services/AutoSteer/AutoSteerService.cs`
- `/Users/chris/Code/AgOpenWeb3/Shared/AgOpenWeb.Models/State/UIState.cs`
- `/Users/chris/Code/AgOpenWeb3/Shared/AgOpenWeb.ViewModels/AutoSteerConfigViewModel.cs`
- `/Users/chris/Code/AgOpenWeb3/Shared/AgOpenWeb.Views/Controls/DialogOverlayHost.axaml`
- `/Users/chris/Code/AgOpenWeb3/Platforms/AgOpenWeb.Desktop/DependencyInjection/ServiceCollectionExtensions.cs`
