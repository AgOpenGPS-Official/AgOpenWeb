<!--
AgOpenWeb
Copyright (C) 2024-2025 AgOpenWeb Contributors

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program. If not, see <https://www.gnu.org/licenses/>.
-->

# Contributing to AgOpenWeb

Thank you for your interest in contributing to AgOpenWeb! This document lists features that need implementation and provides guidance for contributors.

## Getting Started

1. Fork the repository
2. Clone your fork locally
3. Build the project following instructions in `CLAUDE.md`
4. Pick a feature from the list below
5. Create a feature branch off `develop` and implement
6. Submit a pull request targeting the `develop` branch

## Branch Strategy

- **`master`** - Stable releases only
- **`develop`** - Active development branch, PR target for all new work
- **Feature branches** - Create `feature/your-feature` off `develop` for each issue

## Key Documents

- **[Plans/ARCHITECTURE.md](Plans/ARCHITECTURE.md)** - Full architecture documentation: service communication, state management, domain models, data flow diagrams
- **[CLAUDE.md](CLAUDE.md)** - Build commands, key files reference, common development tasks
- **[PGN.md](PGN.md)** - UDP packet protocol for hardware communication

## Architecture Overview

- **Shared code**: Located in `Shared/` folder
  - `AgOpenWeb.Models/` - Data models
  - `AgOpenWeb.Services/` - Business logic
  - `AgOpenWeb.ViewModels/` - `MainViewModel`, the headless control brain
  - `AgOpenWeb.RemoteServer/` - Embedded web server + the web client (`wwwroot`): the only UI
  - `AgOpenWeb.RemoteWiring/` - Boots the backend; routes web commands to the ViewModel

- **Platform code**: Located in `Platforms/`, a thin web-view shell each
  - `AgOpenWeb.Desktop/` - Windows/macOS/Linux (Photino.NET window, or headless daemon)
  - `AgOpenWeb.iOS/` - iOS/iPadOS (WKWebView)
  - `AgOpenWeb.Android/` - Android (WebView + foreground service)

### Cross-Platform Parity Rule

**All code MUST go in `Shared/` unless it requires platform-specific APIs.** This is a hard architectural requirement, not a preference. Putting code in a single platform folder means the other platforms lose that feature.

**What goes in `Shared/`:** the web client (`wwwroot`), the view model, services, models, the web command wiring, UI strings.

**What goes in `Platforms/`:** the process entry point, the web-view shell (startup, keep-screen-on, external links), DI container setup, and platform services (battery, data root, imagery capture process).

**Example violations fixed in #187-192** (in the native-UI days, but the rule is the same): status bar indicators that existed only on Desktop, localization init that ran only on Desktop, a flag placement banner only on Desktop, screenshot capture duplicated across all three platforms.

## MVVM Discipline

AgOpenWeb follows strict MVVM layering. An earlier refactor cleaned up significant violations of this pattern — regressions are not welcome.

> **Pipeline computes, ViewModel coordinates, View binds.**

### What goes where

- **Services / pipeline** (`AgOpenWeb.Services/`): all domain computation. Geometry, guidance math, coordinate conversion, coverage painting, section logic, pathing, state machines. These are the units under test.
- **Models** (`AgOpenWeb.Models/`): data shapes. `*WorkingState` POCOs, `*State : ObservableObject` mirrors, records, DTOs, geometry primitives. Behavior is limited to pure helpers (e.g., `GeometryMath`).
- **ViewModels** (`AgOpenWeb.ViewModels/`): orchestration only. Expose bindable properties, wire commands to services, translate user intent into intents/service calls. ViewModels are thin.
- **Web client** (`AgOpenWeb.RemoteServer/wwwroot/`): presentation. It receives state frames and sends `id|arg` commands; it never computes guidance.

### Rules

1. **No domain computation in the ViewModel.** If you find yourself writing geometry, distance math, pathing, or multi-step business logic inside a `*ViewModel.cs`, stop — move it to a service and call the service from the VM. The VM's job is to coordinate, not to compute.
2. **No domain logic in the web client.** `app.js` renders what the host sends and sends commands back. Don't recompute guidance, coverage or geometry in the browser.
3. **Commands stay thin.** A command delegate should read as *"ask service X to do Y, optionally push an intent, optionally show a prompt."* If it's longer than that, the body belongs in a service method.
4. **No direct `State.*` mutation from commands for pipeline-owned state.** Push an intent through `IPipelineIntents` — see the Threading Model section. UI-only state (dialog visibility, panel position) is fine to mutate directly.
5. **No ViewModel references from services.** Services expose interfaces, raise events, or return results. The ViewModel subscribes/consumes. Dependency flows one direction.

### Why this matters

Services are unit-testable without a dispatcher, a view, or a mocked VM. Once computation leaks into the VM, that testability is gone and the VM becomes a 3000-line god object — the exact problem the earlier refactor fixed. Keep the layers clean.

## Threading Model

AgOpenWeb uses a strict one-way data flow driven by a dedicated background cycle worker. This is a hard architectural requirement, not a convention — violating it reintroduces the AgOpenGPS/WinForms failure mode where domain logic races on the UI thread.

![Threading model](Plans/Completed/threading_model.svg)

See [`Plans/Completed/threading_model_overview.svg`](Plans/Completed/threading_model_overview.svg) for the full picture (current → phases → target in one frame) and [`Plans/Completed/THREADING_MIGRATION_PLAN.md`](Plans/Completed/THREADING_MIGRATION_PLAN.md) for the historical migration plan (now complete).

### The invariant (non-negotiable)

> **Per-cycle GPS pipeline work runs on a dedicated cycle worker** (`Task.Run` per tick, with single-cycle-in-flight back-pressure via `Interlocked`). It does **not** run on the UI dispatcher, and it does **not** run on any I/O thread — UDP receive, NMEA parse, file watcher. I/O threads parse, hand off, and return immediately.

Only the cycle-worker failure mode is survivable: back-pressure drops the *next* tick, the current cycle completes uninterrupted, I/O and UI keep running. Running cycle work on the UI dispatcher drops frames during turns (violating the 24 FPS floor); running it on the UDP receive thread stalls packet ingestion and can lose fixes.

### Two state types per domain

- **`*WorkingState`** — plain POCO/record. Owned by the cycle worker. Mutated freely on the background thread. No `ObservableObject`, no `PropertyChanged`, no UI awareness. Single-writer.
- **`*State : ObservableObject`** — the UI-bound type. A one-way mirror. **The only writer is `ApplyGpsCycleResult` on the UI thread.** No service writes to it directly.

### Data flow

**Cycle → UI (snapshots):** Cycle worker mutates `*WorkingState` during a tick → builds an immutable `GpsCycleResult` snapshot at end of tick → posts to the UI dispatcher → `ApplyGpsCycleResult` writes the snapshot fields onto `State.*`, firing `PropertyChanged` and updating bindings.

**UI → Cycle (intents):** UI command writes to a thread-safe intent field/queue (`IPipelineIntents`) → cycle worker drains intents at the start of each tick → reacts on that tick → result appears in the UI on the same cycle's snapshot.

### Three rules, no exceptions

1. **Cycle worker never touches `*State : ObservableObject`.** Only `*WorkingState`.
2. **ViewModel never mutates `*State` from a service callback.** Only `ApplyGpsCycleResult`.
3. **UI commands push intents, they don't reach into cycle-worker state.**

### Common patterns to follow

- Adding a new domain state: create a `FooWorkingState` POCO, extend `GpsCycleResult` with a `Foo` snapshot record, mirror it in `ApplyGpsCycleResult`.
- Adding a new UI command that changes pipeline behavior: define a method on `IPipelineIntents`, push from the command, drain at the start of the cycle.
- Adding a new service that reads GPS/position: take `*WorkingState` as a parameter, don't inject `ApplicationState`. A service that needs field facts off the UI thread gets them pushed from the ViewModel as plain values (the pipeline's `SetLocalPlane` / `SetBoundary` / `SetHeadlandOn`, section control's `SetFieldContext`); a one-off read on the UI thread hands over a capture (`RuntimeStateCapture` for the bug-report dump).
- Avoid writing to `State.YouTurn`, `State.Guidance`, `State.Vehicle`, `State.Section` from anywhere except `ApplyGpsCycleResult`.

## Persisted numeric / date strings

Anything that lands on disk or on a wire (file I/O, NMEA / PGN packets,
NTRIP HTTP, AgShare uploads, ISO-XML, UDP) **must** format and parse
numbers and dates with `CultureInfo.InvariantCulture`. Without it,
`(42.0308).ToString("F8")` produces `"42,03080000"` in fi-FI / sv-SE /
de-DE / ..., which can break GPS NMEA parsing, PGN packet construction,
saved field metadata, or any other code that round-trips strings
between machines or processes.

The `CA1305` analyzer (`Specify IFormatProvider`) fences this. It is
enabled solution-wide as a **warning** for visibility, and promoted to
**error** in the persistence- and wire-format-shaped paths via
`.editorconfig` globs (`Services/Fields/`, `Services/AgShare/`,
`Services/IsoXml/`, `Services/Tram/`, `Services/Pipeline/`,
`Services/AutoSteer/PgnBuilder.cs`, `Services/NmeaParser*.cs`,
`Services/NtripClientService.cs`, `Services/FieldPlaneFileService.cs`,
`Simulators/**/Modules/`).

If you add a new persistence sink, either drop it under one of those
folders or extend the `.editorconfig` glob list. UI display code is
free to use `CurrentCulture` (be explicit about it though — pin one
or the other, never the implicit default).

## What Needs Doing

Open work is tracked on the [AgOpenWeb project board](https://github.com/orgs/AgOpenGPS-Official/projects/16). Pick a card, comment on the linked issue to claim it, and open your PR against `develop`.

## Translations

UI strings live in `Shared/AgOpenWeb.RemoteServer/wwwroot/i18n/en.json` (generated) and one JSON file per language, translated on the shared [AgOpenGPS Weblate project](https://hosted.weblate.org/engage/agopengps/) once the component is set up. Please don't hand-edit language files in PRs. How to add strings, and the Weblate settings, are in [Docs/TRANSLATIONS.md](Docs/TRANSLATIONS.md).

## How to Implement a Button Feature

1. **Add the button** in `Shared/AgOpenWeb.RemoteServer/wwwroot/index.html` (or build it in `app.js`), written in English.

2. **Send a command** from `app.js`:
   ```js
   document.getElementById('my-feature').addEventListener('pointerdown', e => {
     e.stopPropagation(); transport.send('mything.do');
   });
   ```

3. **Route it** in `Shared/AgOpenWeb.RemoteWiring/RemoteServerWiring.cs` to a ViewModel command or method (a `config.set` key goes in `RemoteServerWiring.Helpers.cs`). If it actuates anything (steering, sections, turns), it is Tier-2 and must require control.

4. **Show new state** by adding it to the DTO in `RemoteServer/Contracts.cs`, filling it in `SceneProjector`, encoding it in `WireCodec.cs` and decoding it in the same order in `wwwroot/transport.js`, then rendering it in `app.js`.

5. **Strings:** run `Tools/i18n-extract.py` so `en.json` includes any new text (CI checks this).

## Questions?

Open an issue on GitHub or reach out to the maintainers.
