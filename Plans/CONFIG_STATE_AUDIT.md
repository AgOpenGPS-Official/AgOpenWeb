# Config & State Storage Audit + Remediation Design

**Branch:** `audit/config-state` (off `develop`, commit `faf12a8`)
**Date:** 2026-05-26
**Status:** Design for review — no code changes yet. Full remediation (incl. building
the persistent application-state tier) to follow on this branch once approved.

---

## 1. Purpose

We keep hitting the same class of bug: a user-meaningful value isn't sourced from the
central store, so it silently fails to persist or gets clobbered on save. This is not a
series of independent bugs — it is a **foundational architecture gap**. This document
audits every config/state value's lifecycle, catalogs every violation with `file:line`,
identifies the root cause, and specifies a remediation that makes the central stores the
*enforced* single source of truth.

## 2. Target architecture — three central stores, one rule

> **Rule: read from the central store at point of use; never keep a local copy, and never
> write to a private/DTO shadow.**

| # | Tier | Store | Backing | Question it answers |
|---|------|-------|---------|---------------------|
| 1 | **Configuration** (persisted) | `ConfigurationStore` | `appsettings.json` (+ vehicle/tool profile JSON, AutoSteer sidecar) | *How should the app behave?* (user preferences/settings) |
| 2 | **Application state** (persisted) | **(new) `ApplicationStateStore`** | **(new) `appstate.json`** | *Where was the app when it closed?* (restore for continuity) |
| 3 | **Application state** (ephemeral) | `ApplicationState` | none (in-memory) | *What is true right now?* (runtime telemetry/flags) |

**Classification rule used throughout this doc:**
- The user deliberately sets it to control behavior and expects it stable → **tier 1 (config)**.
- It reflects the app's last position/view/selection and is restored for continuity → **tier 2 (persistent state)**.
- It is recomputed every run and needn't survive a restart → **tier 3 (ephemeral)**.

The canonical tier-2 example (per review): **window size & position**. You don't "configure"
your window geometry as a preference — the app restores where it was. Same logic applies to
panel positions, last camera view, last-opened field pointer, and last simulator position.

## 3. Current reality vs. target

| Tier | Exists? | Reality on `develop` |
|------|---------|----------------------|
| 1 Config | ✅ | `ConfigurationStore` + `ConfigurationService` ↔ `appsettings.json`. Works, but mapping is hand-written and asymmetric (see §5). |
| 2 Persistent state | ❌ **MISSING as a store** | Its contents are scattered: window geometry, panel pos, last field pointer, sim last-position, first-run/last-run all crammed into the **`AppSettings` config DTO**; field geometry/coverage in field files. |
| 3 Ephemeral state | ✅ | `ApplicationState` — explicitly "single source of truth for ALL runtime state." Correctly transient. |

**The missing tier-2 store is the structural root.** Because persistent *state* has no home,
it's jammed into the config DTO (`AppSettings`), and the guard test has to special-case it as
"managed elsewhere." Two tiers are conflated; one is absent.

## 4. Root cause of the recurring bug

`Tests/AgValoniaGPS.Services.Tests/ConfigurationServiceMappingTests.cs`
→ `Completeness_AllPersistableProperties_AreMapped` enumerates **`typeof(AppSettings).GetProperties()`**.
It only proves every *AppSettings* field round-trips. It is **structurally blind to store
properties that have no AppSettings backing at all.** So you can add a `DisplayConfig` toggle,
wire it to the UI, and it silently never persists — the guard stays green. That blind spot is
exactly how the recurring failures slip through.

## 5. Violations catalog

### A. Store properties with no persistence home (reset every launch)
The live form of "mapping color never persisted."

**12 `DisplayConfig` toggles mapped in NEITHER direction of `ConfigurationService`** (these
stay tier-1 config — they're behavior preferences):

| Store property | Defined | Written from UI |
|----------------|---------|-----------------|
| `Is2DMode` | DisplayConfig.cs:66 | DisplaySettingsService / ViewSettings / Navigation |
| `IsNorthUp` | DisplayConfig.cs:73 | DisplaySettingsService / ViewSettings / Navigation |
| `PolygonsVisible` | DisplayConfig.cs:153 | ConfigurationViewModel.cs:1581 |
| `SpeedometerVisible` | DisplayConfig.cs:160 | ConfigurationViewModel.cs:1587 |
| `DayStartHour` | DisplayConfig.cs:188 | (no writer wired) |
| `NightStartHour` | DisplayConfig.cs:195 | (no writer wired) |
| `LineSmoothEnabled` | DisplayConfig.cs:257 | ConfigurationViewModel.cs:1659 |
| `DirectionMarkersVisible` | DisplayConfig.cs:264 | ConfigurationViewModel.cs:1665 |
| `SectionLinesVisible` | DisplayConfig.cs:271 | ConfigurationViewModel.cs:1671 |
| `UTurnButtonVisible` | DisplayConfig.cs:279 | ConfigurationViewModel.cs:1700 |
| `LateralButtonVisible` | DisplayConfig.cs:286 | ConfigurationViewModel.cs:1706 |
| `HardwareMessagesEnabled` | DisplayConfig.cs:323 | ConfigurationViewModel.cs:1738 |

**Entire `ConfigStore.Tram` section** — not in `AppSettings`, not in profile JSON. Written from
`MainViewModel.cs:226,233`, `Commands.Track.cs:1036/1060/1070/1080/1102/1110/1122`,
`FieldBuilderDialogPanel.axaml.cs:861`. Lost on restart. *(Tram is tier-1 config — needs a home.)*

### B. DTO-bypass writes the store later clobbers
Pattern: write `settings.X =` + raw `Save()`, then the next `SaveAppSettings()` overwrites `X`
from a stale store value (`ApplyStoreToAppSettings` re-derives `X` from the store).

| Value | Site | Why it clobbers |
|-------|------|-----------------|
| AgShare server / API key / enabled | `MainViewModel.Commands.Boundary.cs:194-197` | store `Connections.AgShare*` never updated; `ApplyStoreToAppSettings:501-503` overwrites from store |
| `SimulatorEnabled` | `MainViewModel.Simulator.cs:150-151` | writes DTO + `State.Simulator.IsEnabled`, never `store.Simulator.Enabled`; `:508` overwrites from store |

### C. Multi-home values kept in sync by hand
- **Simulator start coords** live in **four** places: live `_simulatorService`, `AppSettings`
  DTO, `ConfigStore.Simulator`, and `SimulatorState`. `SetSimulatorCoordinates`
  (`MainViewModel.Simulator.cs:261-294`) writes three of them and is correct **only** because of
  an explicit defensive mirror (comment at `:279-281`). Remove either write and it silently
  diverges. The kickoff's "sim coords clobbered" bug is currently *defended, not absent* — and
  the defense is the exact SoT smell we're eliminating.

### D. Save-path inconsistency
- Desktop **`App.axaml.cs:119` exit uses raw `settingsService.Save()`** (no store→DTO sync),
  relying on `MainWindow.OnClosing` having already run `SaveAppSettings()`
  (`MainWindow.axaml.cs:337,350`). Mobile correctly uses `SaveAppSettings()`
  (iOS `MainView.axaml.cs:77`/`AppDelegate.cs:88`, Android `MainView.axaml.cs:65`/`MainActivity.cs:78`).
  If the closing path is skipped, store edits since the last explicit save are lost on Desktop.

### E. NOT violations (corrects the kickoff's priors)
- **`DisplaySettingsService`** on `develop` is **already a clean delegating facade** over
  `ConfigStore.Display` (`DisplaySettingsService.cs:32`) — no shadow state. Only smell: it emits
  its own change events instead of relying on `DisplayConfig`'s `INotifyPropertyChanged`. The UI
  branch deletes it; this branch should **not** touch it to avoid colliding with that work.
- **`AudioServiceBase.IsEnabled`** (`Audio/AudioServiceBase.cs:23-29`) is a private toggle with
  no persistence, but **nothing ever assigns it** — dead, not divergent. Per-sound gates correctly
  read `ConfigStore.Display.*Sound`.
- **`ApplicationState`** is overwhelmingly correct ephemeral state. Borderline items below.

## 6. Inventory: where every value should live

### 6a. `AppSettings` (today) → target tier

| AppSettings property | Target | Notes |
|----------------------|--------|-------|
| WindowWidth/Height/X/Y, WindowMaximized | **Tier 2** | "where the app was" |
| SimulatorPanelX/Y, SimulatorPanelVisible | **Tier 2** | panel position/visibility |
| CameraZoom, CameraPitch, CameraMode | **Tier 2** | last view (`CameraMode` confirmed tier-2) |
| IsDayMode | **Tier 2** | last resolved/chosen mode (the `AutoDayNight` *toggle* is config) |
| SimulatorLatitude/Longitude/Speed/SteerAngle | **Tier 2** | last sim position |
| CurrentFieldName, LastOpenedField | **Tier 2** | pointer only; field data stays in files (per review) |
| IsFirstRun, LastRunDate, HasMigratedIsMetric | **Tier 2** | app-lifecycle/meta |
| StartFullscreen, KeyboardEnabled | Tier 1 | behavior preferences |
| GridVisible, CompassVisible, SpeedVisible, HeadlandDistanceVisible, SvennArrowVisible, ElevationLogEnabled, FieldTextureVisible/Moveable, ExtraGuidelines(+Count) | Tier 1 | display preferences |
| AutoDayNight, DisplayResolutionMultiplier | Tier 1 | preferences |
| AutoSteerSound, UTurnSound, HydraulicSound, SectionsSound | Tier 1 | preferences |
| IsMetric | Tier 1 | device preference (already source-of-truth here) |
| Ntrip*, AgShare*, GpsUpdateRate, UseRtk, SimulatorEnabled | Tier 1 | connection/mode config |
| Language | Tier 1 | preference |
| FieldsDirectory | Tier 1 | storage-location preference |
| LastUsedVehicleProfile, LastUsedToolProfile | Tier 1 | active-config pointer (stays with config) |
| HotkeyBindings | Tier 1 | preference |

⚠ = ambiguous, needs sign-off during review.

### 6b. `DisplayConfig` not-yet-mapped → target tier
Of the 12 toggles in §5A, **`Is2DMode` and `IsNorthUp` are tier-2** (view orientation = last view);
the remaining 10 stay **Tier 1** (display preferences).

### 6c. `ApplicationState` sub-states → tier
| Sub-state | Verdict |
|-----------|---------|
| Vehicle, Guidance, Connection, RecordedPath, UI, Section | ✅ Tier 3 (ephemeral) — correct |
| Field | Tier 3, but pointer (which field) → Tier 2; geometry stays in files |
| Simulator (lat/lon/speed/steer/enabled) | Currently mirrors a separate `ConfigStore.Simulator`+`AppSettings` pair → reroute: persistable bits to **Tier 2**, this object stays the ephemeral live mirror |
| `YouTurnState.IsEnabled` | **Tier 3** (ephemeral) — stays as-is |
| `BoundaryRecState.IsDrawRightSide/IsDrawAtPivot/BoundaryOffset` | **Tier 2** — last-used recording setup, restored for continuity |

## 7. Remediation plan

Ships atomically (build order is dev convenience only, per project convention). DI/persistence
changes touch **Desktop + iOS + Android** in the same commit.

### Step 1 — Build the persistent application-state tier (store #2)
- New model `ApplicationStateSnapshot` (POCO/DTO of tier-2 values) in `AgValoniaGPS.Models`.
- New `IApplicationStateService` + `ApplicationStateService` in `AgValoniaGPS.Services`, persisting
  `appstate.json` in `Documents/AgValoniaGPS/` via the existing `AtomicJsonFile` (same `.bak`
  crash-safe recovery, camelCase, `Populate` creation-handling as `SettingsService`).
- Load on startup (alongside `LoadAppSettings`); save on the same hooks that already call
  `SaveAppSettings` (Desktop `MainWindow.OnClosing`, iOS/Android lifecycle).
- The *runtime* mirror stays `ApplicationState` (tier 3); the service hydrates tier-3 from the
  snapshot on load and projects tier-3 → snapshot on save — symmetric, like `ConfigurationService`.
- Register in all three `ServiceCollectionExtensions.cs`.

### Step 2 — Migrate tier-2 values out of `AppSettings`
- Move the §6a Tier-2 rows into `ApplicationStateSnapshot`; remove them from `AppSettings` and from
  `ApplyAppSettingsToStore`/`ApplyStoreToAppSettings`.
- **One-way migration** (per File Format Philosophy): on first load where `appstate.json` is absent
  but a legacy `appsettings.json` still carries these fields, seed the snapshot from them, then stop
  writing them to `appsettings.json`. No data loss for existing users.
- Field pointer (`CurrentFieldName`/`LastOpenedField`) → snapshot; field data remains in files.

### Step 3 — Give config orphans a home
- Add the **10 tier-1** `DisplayConfig` toggles (all of §5A except `Is2DMode`/`IsNorthUp`) to
  `AppSettings` + both `Apply` methods. `Is2DMode`/`IsNorthUp` go to the tier-2 snapshot (Step 2).
- **`ConfigStore.Tram` is field-scoped** → persist it in the field/job files (not `AppSettings`).
  Tram lanes belong to a field, so they save and restore with the field, alongside boundary/track
  data — not as a global setting.

### Step 4 — Eliminate DTO-bypass + multi-home writes
- AgShare confirm (`Boundary.cs:194-197`): write `ConfigStore.Connections.AgShare*`, then
  `SaveAppSettings()`. Remove the raw DTO write + `Save()`.
- `SimulatorEnabled` (`Simulator.cs:150-151`): write `ConfigStore.Simulator.Enabled`, then save.
- Sim coords (`Simulator.cs:261-294`): single path — dialog → tier-2 snapshot (or
  `ConfigStore.Simulator` if classified config) → save. All readers read the central store; drop the
  manual mirror and the redundant DTO write.

### Step 5 — Save-path parity
- Desktop `App.axaml.cs:119`: replace raw `settingsService.Save()` with the proper
  `SaveAppSettings()` + new `SaveState()`.

### Step 6 — Make the contract enforceable (the real fix)
Replace the AppSettings-only completeness test with **bidirectional, store-driven guards**:
1. **Config completeness:** enumerate every public property of every `ConfigurationStore`
   sub-config; assert each is either (a) round-tripped through `Apply*`, (b) on an explicit
   `PersistedViaProfileJson` allowlist, or (c) on an explicit `IntentionallyTransient` allowlist.
   A new store property with no home ⇒ **red**.
2. **State completeness:** same shape for `ApplicationStateSnapshot` ↔ tier-2 fields.
3. **No-bypass guard:** a test/analyzer check that production code outside `ConfigurationService`/
   `ApplicationStateService` does not assign `*.Settings.<X>` for store-backed properties
   (grep-based CI check acceptable, mirroring existing CI greps).

## 8. Risks & mitigations
- **Existing user data** in `appsettings.json` (window pos, last field, sim pos): handled by the
  one-way migration in Step 2 — read once, seed `appstate.json`, then drop from `appsettings.json`.
- **UI branch rebase:** `feature/ui-collapse-screen-alerts` (paused at `9e3987e`) will rebase onto
  this. It already deletes `DisplaySettingsService` and moves display state to `ConfigStore.Display`;
  this branch leaves that file alone (see §5E) to minimize conflict. Coordinate the 12-toggle
  additions so they don't double-apply.
- **Three-platform DI drift:** every new registration lands in Desktop + iOS + Android together.

## 9. Resolved decisions (signed off 2026-05-26)
1. **CameraMode** → **Tier 2** (last view).
2. **IsDayMode / Is2DMode / IsNorthUp** → **Tier 2** (last view).
3. **Tram** → **field-scoped** — persist in field/job files, not AppSettings.
4. **YouTurnState.IsEnabled** → **ephemeral** (tier 3, unchanged). **BoundaryRec draw prefs** → **Tier 2**.
5. **`appstate.json`** → **separate file** (own `AtomicJsonFile`, mirrors `appsettings.json`).

## 10. Deliverables checklist — COMPLETE (2026-05-27)
Implemented as named below (the snapshot/service ended up as `PersistentAppState` +
`PersistentStateService`, serializing the store object directly rather than a separate DTO).
- [x] `PersistentAppState` store + `IPersistentStateService`/`PersistentStateService`
- [x] `appstate.json` via `AtomicJsonFile`; load/save wired on all 3 platforms
- [x] One-way migration from legacy `appsettings.json`
- [x] Tier-2 values removed from `AppSettings`/`DisplayConfig`/`SimulatorConfig` + `Apply*`
- [x] 10 tier-1 Display toggles given AppSettings homes; **Tram persisted per-field**
      (`TramConfigFileService` → `TramConfig.json`, loaded on field-open / saved on field-close,
      alongside the pre-existing per-field `TramSystems.json`)
- [x] AgShare / SimulatorEnabled / sim-coords routed through the central store; `Language`
      uses `SaveAppSettings`
- [x] Desktop exit → `SaveAppSettings()` + state `Save()`
- [x] Bidirectional config (`DisplayConfig`/`SimulatorConfig`) + state completeness guards;
      no-bypass guard (`NoBypassWritesTests` scans VM/Views for `*.Settings.<X> =`)
- [x] All tests green; new tier-2 round-trip + legacy-migration tests

**Note on Tram:** the audit assumed all of `ConfigStore.Tram` was unpersisted. In fact tram
*Systems* already persisted per-field, and `Passes`/`DisplayMode` mirror `ConfigStore.Guidance`
(vehicle-profile config). Only the remaining scalars (`TramWidth`, `StartPass`, `IsOuterInverted`,
`Alpha`, `IsDisplayTramControl`, `IsEnabled`) lacked a home; those now persist per-field.
`Passes`/`DisplayMode` were intentionally left on the Guidance sync to avoid double-sourcing —
a smaller follow-up could consolidate them if desired.

---

## 11. Follow-up scope — de-ambient the VM (added 2026-06-14, not scheduled)

**Different axis from §1–§10.** Everything above is about *data single-source-of-truth* — which
of the three stores owns a value. This item is about *ambient framework coupling*: the ViewModel
reaching into framework/global statics **out of the air** instead of receiving them through its
constructor. It's the same "not injected, just grabbed" smell, one layer down, and it's exactly
what `Plans/ARCHITECTURE.md` flags under "Tight Coupling Points → Historical, needs refactor" —
which the completed storage audit deliberately did **not** touch.

### 11.1 Injectable `IDispatcher` (the concrete item)
The VM depends directly on `Avalonia.Threading.Dispatcher.UIThread` — a static from the
*presentation* framework — in **~10 `MainViewModel` partials**: ~18 `Post`, 5 `InvokeAsync`,
7 `CheckAccess`. By the project's own MVVM rule (VM coordinates, doesn't bind itself to the View
framework), this should be an injected `IDispatcher`:
- Define `IDispatcher` (Post / InvokeAsync / CheckAccess) in `AgValoniaGPS.Services` (or Models).
- Avalonia-backed implementation registered in **Desktop + iOS + Android** (three-platform DI rule).
- VM partials take `IDispatcher` via ctor; replace every `Dispatcher.UIThread.*` call.
- Behavior-preserving for the current apps; independently shippable; no data-tier change.

**Payoff:** (a) VM unit tests stop needing `Avalonia.Headless` *just to supply a dispatcher* — an
inline/synchronous `IDispatcher` suffices; (b) it unblocks the headless web-UI host, which has a
VM consumer with no UI thread (see `Plans/REMOTE_WEB_UI_SPLIT.md` §9.1 + the
`Spikes/HeadlessHostSpike` proof — the spike had to pull in `Avalonia.Headless` purely for this).

**Why it wasn't already done (and wasn't wrong to defer):** until the headless host, every VM
consumer was an Avalonia View on a real UI thread, so `Dispatcher.UIThread` was correct everywhere
and an abstraction would have had exactly one implementation. Defensible YAGNI; the web UI is the
forcing function that makes it earn its keep.

### 11.2 Sibling: static store access ✅ DONE
The same "ambient, not injected" smell applied to `ConfigurationStore.Instance` and
`ApplicationState.Instance`, read statically across services and VMs (also flagged in
`ARCHITECTURE.md`). The storage audit (§1–§10) made these the enforced SoT but left them as
**statics**. Resolved on `audit/de-static-configstore` (v26.5.48):

- **`ConfigurationStore`** is now registered in DI on all 3 platforms
  (`services.AddSingleton(_ => ConfigurationStore.Instance)` — DI-resolved instance ≡ `.Instance`,
  one object) and **injected via constructor** into all 26 Services/ViewModels that previously grabbed
  the static (`MainViewModel` + 8 partials via a `_configStore` field behind the existing
  `ConfigStore`/`Vehicle`/`Tool`/`Guidance` accessors; 18 service classes). A few static helpers that
  could not hold a field got a trailing `ConfigurationStore` parameter
  (`NmeaParserServiceFast.ParseIntoState`, `GpsFixQualityValidator.IsAcceptable`,
  `DebugDumpService.CreateDump`); `AudioServiceBase` threads it through the 3 platform subclass ctors.
- **Reset-settings hot-swap removed.** "Reset All Settings" used to call
  `ConfigurationStore.SetInstance(new ConfigurationStore())` — *replacing the object*, which would
  strand every injected reference. It now resets **in place**: `ResetToDefaults()` → `Save()` →
  `LoadAppSettings()` reapplies the default DTO into the *same* store instance (identity + PropertyChanged
  subscriptions preserved). This is the correct model regardless of injection.
- **`ApplicationState`** was already injected in production (the VM takes it via ctor); its `.Instance`
  survived only in tests, so no production change was needed.
- The static `Instance`/`SetInstance` accessors remain **only** as the seam for framework-instantiated
  Avalonia Views (the XAML loader news them up outside DI — 7 View files keep reading the shared
  singleton) and for test setup. A new guard test `NoAmbientStoreAccessTests` source-scans
  `Shared/AgValoniaGPS.{Services,ViewModels}` and fails CI if `ConfigurationStore.Instance` /
  `ApplicationState.Instance` regrows in business logic. 1502 tests green.

### 11.3 Injectable `ITimer` / scheduler (the remaining headless blocker)
The same axis again — an ambient Avalonia type instantiated *inside* the VM, this time
`Avalonia.Threading.DispatcherTimer`. The `MainViewModel` ctor creates ~5 of them (status-strip
rotation, clock, autosave, auto-day/night, simulator). `DispatcherTimer` captures
`Dispatcher.UIThread` at construction, so it **throws in a process with no Avalonia dispatcher** —
which is the concrete reason a headless host still needs `Avalonia.Headless` even after §11.1.
After the marshalling calls were abstracted, this is the last thing that *functionally* ties a
headless boot to Avalonia (the remaining `Avalonia.Point`/`Color`/`Application.Current` uses are
value-type math or null-guarded and don't block headless execution).

- Define `ITimer` / `IScheduler` (create a periodic callback with an interval; start/stop; change
  interval) and inject it like `IUiDispatcher`. Richer than the dispatcher because timers have
  lifecycle (start/stop/interval changes), so the interface is a little larger.
- Avalonia-backed impl wraps `DispatcherTimer` (Views, alongside `AvaloniaUiDispatcher`); a
  threaded/`PeriodicTimer`-based impl serves headless hosts and tests.
- Replace the ~5 ctor `DispatcherTimer` sites; `DispatcherTimer` then exists only in the View layer.

**Why now / why noted:** surfaced by the remote/web-UI **Phase 0 retest** after §11.1 merged — the
marshalling hurdle cleared, but the spike still had to keep `Avalonia.Headless` purely for these
timers. It is **not** blocking Phase 1 "alongside the Avalonia app" (the live app supplies a real
dispatcher + timers); it is the prerequisite for the **fully Avalonia-free cab-PC host**
(`Plans/REMOTE_WEB_UI_SPLIT.md` Phase 4/5). If the web-UI work proceeds, fix it there; otherwise it
stands as a standalone follow-up.

**Status:**
- §11.1 `IUiDispatcher` — **DONE**, merged to `develop` (PR #470, 2026-06-14).
- §11.2 static store de-static-ing — **DONE** (`audit/de-static-configstore`, v26.5.48). `ConfigurationStore`
  registered in DI + injected into all 26 Services/VMs; reset-settings hot-swap replaced with in-place
  reload; static `Instance` kept only as the View + test seam, guarded by `NoAmbientStoreAccessTests`.
- §11.3 `ITimer`/scheduler — **DONE** (PR #471). `IUiTimer` + `IUiTimerFactory`
  (Services); impls: `AvaloniaUiTimer` (Views, wraps `DispatcherTimer` — the 3
  platforms), `ManualUiTimer` (tests, framework-free, non-firing), `ThreadingUiTimer`
  (headless host, `System.Threading.Timer`-based, fires with no Avalonia). All 7
  VM `DispatcherTimer` sites (status-strip, clock, auto-day/night, autosave,
  simulator, render-pull, status-tick) now go through the injected factory.

With §11.1 + §11.3 done, the VM no longer instantiates **any** Avalonia type at
runtime that a headless boot would trip on (dispatcher + timers both abstracted).
With §11.2 done, the business logic no longer reaches for the ambient store
singletons either — **§11 is complete**. The remaining `ConfigurationStore.Instance`
uses live only in framework-instantiated Views and test setup (the sanctioned seam).

All surfaced by the remote/web-UI Phase 0 spike + retest.

---

## 12. Runtime field-geometry/selection SoT — NOT covered by §1–§10 (added 2026-06-14)

**Different axis again.** §1–§10 audited *config & persisted* values; §11 audited *ambient
framework coupling*. This section is the gap both missed: **runtime domain state** (the field's
boundary, headland, origin, tracks, active/selected track, section/recording readouts). The
audit's own §2 Rule — *"read from the central store at point of use; never keep a local copy"* —
was enforced for config but **never for runtime state**, so the same datum is copied across up to
three layers:

- **Layer 1 — the `Field` model (`ActiveField`):** the persisted form (`Boundary`, `Origin`, `Name`).
- **Layer 2 — `ApplicationState` sub-states (`FieldState`, `GuidanceState`, …):** the service-read form.
- **Layer 3 — `MainViewModel` private fields:** a UI-bindable copy (`_currentBoundary`, `_currentHeadlandLine`, …).

This is why every remote/web-UI projection of a datum was a guessing game (it cost two debugging
detours on the web client). Findings below are **grep-verified** (Explore sweep + manual confirm).

### 12.1 Field-geometry/selection cluster — collapse to one canonical home

| Datum | Homes (file) | Canonical (services read) | Action |
|---|---|---|---|
| **Boundary** | `Field.Boundary` · `FieldState.CurrentBoundary` · VM `_currentBoundary` | **`FieldState.CurrentBoundary`** (GpsPipelineService:1657, SectionControlService:878/890/927) | keep `Field.Boundary` for save; **drop VM copy** — bind to state |
| **Headland** | `Boundary.HeadlandPolygon` · `FieldState.HeadlandLine` · VM `_currentHeadlandLine` | **`FieldState.HeadlandLine`** (SectionControlService:922) | keep polygon for save; **drop VM copy**. (`_previousHeadlandLine` = undo, separate, keep) |
| **Field origin** | `Field.Origin` · `FieldState.OriginLatitude/Longitude` + `LocalPlane` · VM `_fieldOriginLatitude/Longitude` | **`FieldState` Origin + `LocalPlane`** (pipeline/AutoSteer coord conversion) | **drop VM copy** |
| **Field name** | `Field.Name` · `FieldState.FieldName` (computed) · VM `_currentFieldName` | **`Field.Name`** (FieldState.FieldName computes from it — fine) | **drop VM copy** |
| **Tracks** | `FieldState.Tracks` · VM `SavedTracks` (hand-synced on field load) | pick one (likely `FieldState.Tracks`) | collapse to one collection |
| **Active track** | `FieldState.ActiveTrack` · `GuidanceState.ActiveTrack` · `GpsPipelineService._activeTrack` (cycle-local, lock-guarded) · VM `SelectedTrack` | pipeline `_activeTrack` is legit working copy; **`FieldState.ActiveTrack`** is the state SoT | dedupe the `FieldState`/`GuidanceState` mirror; keep pipeline working copy |

### 12.2 Dead state — delete

- **`FieldState.Boundaries`** (ObservableCollection) + **`FieldState.HasBoundary`** — **0 writers, 0 readers** (verified). The lone `AgShareFieldParser` write targets a *result* object, not state.
- **`FieldState.SelectedTrack`** — **0 references** anywhere (verified). All selection flows through VM `SelectedTrack`.

### 12.3 VM display-shadow fields — bind directly to state, delete the field ✅ DONE

`MainViewModel` mirrored several read-only state values purely for binding. Investigation
split these into two cases — collapse the genuine mirrors, **delete** the dead ones:
- `_boundaryPointCount` / `_boundaryAreaHectares` ↔ `BoundaryRecState.PointCount/AreaHectares`
  — genuine hand-synced mirrors (bound in `BoundaryPlayerPanel` + `StartWorkSessionDialogPanel`).
  **Collapsed** to pass-through properties on `State.BoundaryRec` (getter reads state; private
  setter writes state + `OnPropertyChanged`); backing fields removed; the recording handlers no
  longer write state twice.
- `_activeSections` (`ActiveSections`) and `_currentGuidanceLine` (`CurrentGuidanceLine`) — turned
  out to be **dead**: never written, never read/bound anywhere (the only `ActiveSections` hits in
  the repo are an unrelated `VirtualMachineModule`). `SectionState.ActiveSectionCount` is *computed*
  from the section array, not a hand-synced mirror. Both VM properties + backing fields **deleted**.

Resolved in `audit/state-sot-fix-12-3` (v26.5.46); 1502 tests green.

### 12.4 Cross-state / simulator duplication ✅ DONE

Investigated each flagged copy; outcome was **delete dead state + confirm one legit mirror** —
no risky cross-state collapse was warranted.

- **Active track in `GuidanceState` and `FieldState`** — *not* cruft. `FieldState.ActiveTrack`
  is the selection SoT (set by the `SelectedTrack` command, read by the map/pipeline/DebugDump).
  `GuidanceState.ActiveTrack` is the **observable mirror of `GuidanceWorkingState`** (the Phase D D7
  property-for-property snapshot mirror, enforced by `GuidanceWorkingStateTests`) — semantically "the
  track the *cycle* is guiding on," which lags selection by one cycle *by design*. Distinct semantics,
  correct-by-design, part of a live + tested contract → **kept**, reclassified clean (§12.5).
- **Vehicle ↔ Simulator position** — there was **no real runtime copy**. `SimulatorState`
  (`ApplicationState.Simulator`) turned out to be **entirely dead**: every field write-only or
  unreferenced (`Latitude/Longitude/Easting/Northing/Heading/FixQuality/SatelliteCount` had zero refs;
  `IsEnabled/IsRunning/Speed/TargetSpeed/SteerAngle` were write-only). It was superseded during §1–§10
  by `PersistentState.Simulator*` (appstate.json) + `_simulatorService` (live pose) — the sim feeds
  `VehicleState` through the GPS pipeline like a real receiver (a legit producer→consumer flow, not a
  shadow). **Deleted `SimulatorState`** + its `ApplicationState` property + `Reset()` call + all dead
  write sites in `MainViewModel.Simulator.cs`.
- **`GuidanceState.SteerAngle` → `SimulatorState.SteerAngle`** feedback "sync" — wrote into the dead
  `SimulatorState`; **removed** with the deletion above. (The real steer feedback to the sim goes
  through `_simulatorService.Tick(SimulatorSteerAngle)`.)
- **`_simulatorLocalPlane`** (VM-local, flagged for review) — reviewed: a legit input-stage bootstrap
  helper. It converts the sim's synthetic WGS84 → local coords *before* the cycle has created
  `State.Field.LocalPlane`, already uses the field origin when one exists (value-consistent), and is
  reset on field/coord changes. Not a competing home → stays VM-local.

Resolved in `audit/state-sot-fix-12-4` (v26.5.47); 1502 tests green.

### 12.5 Confirmed CLEAN — out of scope (so the target list is bounded)

Verified single-owner / not duplicated — **do not touch**:
- Working-state classes: `GuidanceWorkingState`, `YouTurnWorkingState` (cycle-worker-owned),
  `TrackGuidanceState` (per-loop PID/filter), `ModuleSwitchState` (IPC DTO), `SensorState` (live IMU singleton).
- `ConnectionState`, `RecordedPathState`, most of `YouTurnState` — clean.
- **`UIState` dialog-visibility properties are LIVE** — **39 `State.UI.IsXVisible` bindings in AXAML**
  (an Explore sweep miscalled these "dead" by not grepping `.axaml`; corrected here).
- Genuine VM-local UI state (tab index, wizard step, dialog selections, `_pending*`, perf counters, `_currentFps/_currentTime`).
- **`GuidanceState.ActiveTrack`** — the Phase D D7 observable mirror of `GuidanceWorkingState.ActiveTrack`
  (the track the cycle is guiding on); distinct from `FieldState.ActiveTrack` (selection SoT). Kept (§12.4).
- **`_simulatorLocalPlane`** (VM-local) — input-stage bootstrap plane for the sim's WGS84→local
  conversion; reviewed clean under §12.4 (uses field origin when present, reset on field/coord change).

### 12.6 Fix order

1. ✅ **Field-geometry cluster (12.1)** + delete dead (12.2): collapse boundary/headland/origin/name to
   their canonical home, remove the VM shadows, delete `FieldState.Boundaries`/`HasBoundary`/`SelectedTrack`.
2. ✅ **VM display shadows (12.3):** rebind to state, delete fields.
3. ✅ **Cross-state/sim dedupe (12.4):** delete dead `SimulatorState`; confirm `GuidanceState.ActiveTrack` legit.
Each ships with the `NoBypassWritesTests`-style guard extended to flag *runtime-state* local copies,
so this class can't silently regrow.

**Status:** ✅ **COMPLETE.** §12.1/§12.2 (field-geometry + dead deletion), §12.3 (primitive display
mirrors), and §12.4 (cross-state/sim) all resolved. Domain-typed VM shadows **and** primitive display
mirrors are now zero; the only remaining intentional cross-state reference (`GuidanceState.ActiveTrack`)
is a documented, tested observable-mirror. `StateShadowGuardTests` guards domain-typed regrowth in CI.

### 12.7 `SectionState` per-section on/off was dead (never written) — ✅ RESOLVED

`ApplicationState.Sections` (`SectionState`) held per-section on/off flags
(`_sectionActive[]`, `Section1..8Active`, `GetSectionActive`/`SetSectionActive`/`SetAllSections`/
`GetAllSectionsAsBits`) plus `ActiveSectionCount`, `NumberOfSections`, `IsMasterOn`,
`IsManualMode`/`IsAutoMode`, `IsSectionControlInHeadland` — **every member verified dead**
(0 production writers, 0 production readers). The authoritative per-section on/off is
`ISectionControlService.SectionStates[i].IsOn` (the source coverage paints from); every consumer
already reads that.

**Resolved (2026-06-16, `audit/config-apply-gap` v26.5.49):** decision = **delete** (mirrors the
§12.4 `SimulatorState` deletion). Removed the whole `SectionState` class (file deleted) +
`ApplicationState.Sections` property + its `Reset()` call. 1504 tests green.

---

## 13. Config flags persist but aren't applied to the live renderer/behavior — ✅ MOSTLY RESOLVED

A **distinct axis** from §1–§12: the **apply gap**. Several `ConfigStore.Display.*` flags persist
correctly but nothing connected them to the running map/behavior, so toggling them (from the
Settings / Screen-&-Alerts panel — and the web client, which writes the same flag the menu binds to)
did nothing on the **native** app. Surfaced by the web-UI Screen & Alerts work; the web is faithful —
this was native config→renderer wiring.

### 13.1 Display toggles disconnected from the renderer

- **Grid** — three reps (`Display.GridVisible` / `_displaySettings.IsGridOn` /
  `SkiaMapControl.IsGridVisible` StyledProperty); the renderer read only the StyledProperty, pushed
  only from the *on-screen-button* path, so the *Settings* toggle was dead. **Fixed:** collapsed to one
  source — the map control reads `ConfigStore.Display.GridVisible` directly and repaints on
  `Display.PropertyChanged`. Deleted the `IsGridVisible` StyledProperty + `SetGridVisible` from the
  control, both interfaces (`ISharedMapControl`, `IMapService`, iOS `IMapControl`), all 3 platform
  `MapService` impls, and the Desktop/iOS/Android push/binding sites. Both toggles now drive the SoT.
- **Svenn Arrow** (`Display.SvennArrowVisible`), **Headland-Distance HUD**
  (`Display.HeadlandDistanceVisible`), **Extra Guidelines** (`Display.ExtraGuidelines` + count) — these
  had config flags + UI toggles + unused `MapRenderState` fields but **no draw code anywhere**.
  **Implemented** (ported from AgOpenGPS): Extra Guidelines (parallel reference lines either side of the
  active track, green over black shadow, zoom-gated); Svenn lookahead arrow (yellow triangle ahead of
  the wheelbase, sized off the visible world span); Headland HUD (screen-space rounded box, yellow /
  red-on-warning, **displaying the pipeline's already-computed `HeadlandProximityDistance`** —
  `State.Field.HeadlandProximityDistance`, not a renderer recompute).
- **The backbone:** a single `ConfigStore.Display.PropertyChanged` subscription in `SkiaMapControl`
  now triggers a repaint, so every `displayCfg`-sourced flag (grid, Svenn, headland HUD, extra
  guidelines, field texture, line smoothing) applies **live**, from either the on-screen buttons or
  the Settings panel.
- **Display Quality** (`Display.DisplayResolutionMultiplier`) — ⏳ **still open.** It *is* applied, but
  only at coverage-bitmap **init** (field open); changing it mid-session has no live effect because
  that needs a coverage-bitmap rebuild + reprojection (a coverage-system change, not a draw wire).
  Tracked as its own follow-up to avoid a rushed coverage rebuild.

### 13.2 Cross-wiring side effect — ✅ RESOLVED

Toggling `Display.UTurnButtonVisible` (a *display-visibility* preference) also hid the right-nav
auto-U-turn **arming** toggle (`ToggleYouTurnCommand`, the only control that enables auto-uturn),
making the behavior unreachable. **Fixed:** `IsUTurnButtonVisible` no longer reads
`Display.UTurnButtonVisible` — the arming/direction controls are gated only by autosteer + track
state (+ `HasBoundary`), so they're always reachable; the flag now governs **only** the on-map U-turn
overlay (`IsUTurnOverlayVisible`).

### 13.3 Guard

Added `DisplayRenderFlagsAppliedTests` (source-scan, NoBypassWrites-style): asserts every
render-affecting `Display.*` flag is actually read by `SkiaMapControl`. The reflection shadow-guard
can't catch an *apply* gap; this does, at the wiring layer.

---

## 14. Final two items — ✅ RESOLVED (2026-06-16, v26.5.50)

Both items previously deferred from the apply-gap branch are now done:

1. **§13.1 Display Quality live re-apply** — ✅ DONE. The detection-bits coverage source is
   resolution-independent (only the display bitmap's cell size scales with the multiplier), so a live
   rebuild is lossless. Added `ISharedMapControl.RebuildCoverageBitmapForResolutionChange()` (+
   `IMapService` + 3 platform forwards): it recomputes the cell size at the current bounds, recreates
   the display bitmap, and repaints from the detection cells — **preserving camera state** (unlike
   `InitializeCoverageBitmapWithBounds`, which is a field-open and recenters). `CycleDisplayResolutionCommand`
   calls it when a field is open, so Quality changes take effect immediately instead of only on next
   field open.
2. **§12.1 `_currentFieldName`** — ✅ DONE. Collapsed to a read-only pass-through
   `CurrentFieldName => State.Field.ActiveField?.Name ?? string.Empty`; all 6 writes removed; the
   field/job label re-raises from the existing `State.Field.PropertyChanged` (`FieldName`) subscription.
   Fixed 3 **latent SoT bugs** uncovered in the process — the copy / KML-import / ISO-XML-import flows
   set `IsFieldOpen = true` but never set `ActiveField`; they now call `SetActiveField` (the KML flow
   does it before `SetCurrentBoundary` so the boundary attaches to the active field). 1504 tests green.

**§1–§14 of this audit are now complete.**
