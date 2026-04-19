<!--
AgValoniaGPS
Copyright (C) 2024-2026 AgValoniaGPS Contributors

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

# Threading Migration Parking Lot

The single index of open decisions, deferred work, and cross-phase
follow-ups for the threading migration. Supplements
[THREADING_MIGRATION_PLAN.md](THREADING_MIGRATION_PLAN.md) and every
per-phase plan under `Plans/`.

---

## 1. Purpose

Per-phase plans intentionally *don't* resolve every open question — they
lock only what that phase needs to ship. Everything else gets parked
here so it can't fall through the cracks between phases.

Two failure modes this document exists to prevent:

- **Silent deferral.** A phase plan says "decided later" and "later"
  never arrives because no one remembers.
- **Silent creep.** A decision gets made ad hoc mid-phase without being
  traced back to the architectural concern that motivated it.

Every deferred item lives here with a **why**, a **who/when owns it**,
and a **dated review log**. Nothing gets dropped silently — items are
either resolved and archived (§5) or kept open with an explicit reason.

---

## 2. Review protocol

At the end of every phase, before the phase's PR merges:

1. Walk §4 (Open items) top to bottom.
2. For each open item, answer three questions:
   - **Did this phase surface new information that changes the answer?**
     If yes, update the "Why parked" field or move to §5 as resolved.
   - **Should this be resolved in the phase we're closing?**
     If yes, pull it into the current PR. A deferred item is
     cheaper to close when the relevant code is already touched.
   - **Does the next phase need this resolved before it can start?**
     If yes, the next phase's plan opens with "resolve TMP-NNN" as
     its first step.
3. Append a dated line to the item's review log, even if the answer is
   "still parked" — the log proves the item was considered, not
   ignored.

Review happens in the PR description, inline against the item IDs. A
merge without a parking-lot review is a convention violation; same
weight as merging without tests.

---

## 3. Entry shape

Every open item carries:

- **ID** — `TMP-NNN` (threading migration parking). Stable across the
  lifetime of the item — once assigned, never reused, even after
  resolution.
- **Title** — short phrase.
- **Status** — one of: `Open`, `In review`, `Resolved` (resolved items
  move to §5).
- **Raised in** — phase where the item was first deferred.
- **Decide by** — the phase where the item *must* be resolved, or
  `Optional` if it's a nice-to-have.
- **Source** — pointer to the originating discussion (parent plan
  section, per-phase plan section, conversation artifact).
- **Why parked** — the reason, concrete. Not "we'll figure it out" —
  "this becomes testable only when X exists" or "this depends on the
  shape of Y which isn't visible yet."
- **What the decision is** — the specific question to answer, phrased
  so someone walking in cold knows what they're choosing between.
- **Review log** — dated notes from each end-of-phase review.

---

## 4. Open items

### TMP-001 — Snapshot identity vs equality

- **Status:** Open
- **Raised in:** Phase A
- **Decide by:** Phase C (Commit 1, before `YouTurnSnapshot` is populated)
- **Source:** parent plan §6.1; Phase A plan §8

**Why parked.** Becomes a testable question only when the cycle worker
actually builds a `YouTurnSnapshot` every tick. In Phase A nothing
populates it, so there's nothing to construct or compare. The `record`
type chosen in Phase A Commit 2 supports either answer.

**What the decision is.** Either:
- (a) the cycle worker reuses list instances (e.g., `TurnPath`,
  `SnakeSequence`) when the state didn't change, so
  `SetProperty`'s reference check elides `PropertyChanged` firing, or
- (b) the `ApplyGpsCycleResult` path does value-equality checks before
  writing, letting the cycle worker build fresh snapshots every tick.

(a) is faster but forces the cycle worker to track instance identity
across ticks. (b) is simpler but spends cycles on equality checks on
the UI thread.

**Review log.**
- 2026-04-19 — Parked during Phase A planning. Type-level decision made
  (records) that keeps both paths open.
- 2026-04-19 (Phase A close) — Phase A ships `YouTurnSnapshot` and
  `GuidanceSnapshot` as records with `IReadOnlyList<T>?` for list fields,
  which supports either future approach. Still parked; Phase C owns.

---

### TMP-002 — Synchronous UI command carve-outs

- **Status:** Open
- **Raised in:** Phase A
- **Decide by:** Per migration phase (Phase C for YouTurn commands,
  Phase D for Guidance commands)
- **Source:** parent plan §6.3; Phase A plan §8

**Why parked.** Can only be answered by walking every UI command whose
current behavior writes to `State.*` and asking "is a ≤100 ms delay
(one cycle tick) visible to the user?" That inventory is
phase-specific: Phase C covers `TriggerManualYouTurn*`, `ClearYouTurnState`;
Phase D covers nudge / snap / reverse-heading. Phase A migrates nothing,
so the question has no subject.

**What the decision is.** For each migrated command, either:
- (a) it goes through the intent queue (default), accepting up to one
  tick of latency, or
- (b) it's added to an explicit carve-out list with a written rationale
  for why sub-tick latency is required.

Carve-outs are the only sanctioned bypass of the one-way flow.

**Review log.**
- 2026-04-19 — Parked during Phase A planning. No commands migrated
  yet.
- 2026-04-19 (Phase A close) — Still nothing migrated; no commands
  touched in Phase A. Phase C's plan must open with the carve-out
  inventory for `TriggerManualYouTurnLeft/Right` and `ClearYouTurnState`.

---

### TMP-003 — Phase B unified service name

- **Status:** Open
- **Raised in:** Phase A
- **Decide by:** Phase B (first decision of Phase B's plan)
- **Source:** parent plan §6.6; Phase A plan §8

**Why parked.** The name depends on what Phase B's consolidated cycle
owner actually does. If it stays a thin orchestrator around the
existing services, `GpsPipelineService` is the right keep-name. If it
absorbs tool-position / section / coverage logic, a new name may
reflect reality better (e.g., `CycleWorker`, `GpsCycle`). The
implementation diff makes the choice obvious; speculating before
writing it is bike-shedding.

**What the decision is.** Name for the single class that hosts
`ProcessCycle`, subscribes to `UdpGpsQueue` (TMP-004), and emits
`GpsCycleResult`.

**Review log.**
- 2026-04-19 — Parked during Phase A planning. The §0 invariant (cycle
  runs on `Task.Run` with `Interlocked` back-pressure) governs
  regardless of name.
- 2026-04-19 (Phase A close) — Phase A added `IPipelineIntents` and a
  `Drain()` call inside `GpsPipelineService`, i.e. the existing service
  accepted a new responsibility cleanly. Weak signal that the name can
  stay, but Phase B's implementation still decides.

---

### TMP-004 — `UdpGpsQueue` introduction

- **Status:** Open
- **Raised in:** Phase A (discovered via Ideal Threading Model SVG)
- **Decide by:** Phase B
- **Source:** `threading_model.svg` (I/O-to-cycle handoff, orange box
  "Parsed GPS position / UdpGpsQueue — handoff only")

**Why parked.** Today the I/O-to-cycle handoff is event-driven:
`GpsPipelineService` subscribes to a `GpsDataUpdated` event and kicks
off `Task.Run` per event. The SVG shows a named `UdpGpsQueue` as the
eventual shape — NMEA parser pushes a `Position`, cycle worker pulls at
tick boundary. Phase B replaces the event wiring with the queue as part
of unifying the two parsers. Phase A does not touch this; name is
reserved so Phase B doesn't drift.

**What the decision is.** Queue type (bounded vs unbounded,
last-wins vs FIFO), the producer/consumer wiring, whether it's a
`Channel<T>` or a hand-rolled single-slot volatile field.

**Review log.**
- 2026-04-19 — Parked during Phase A planning. Phase A uses the
  existing event wiring; no behavior change.
- 2026-04-19 (Phase A close) — I/O wiring untouched by Phase A, event
  path intact. Still parked for Phase B.

---

### TMP-005 — Removal of flat YouTurn / Guidance fields on `GpsCycleResult`

- **Status:** Open
- **Raised in:** Phase A
- **Decide by:** Phase C (for YouTurn fields) / Phase D (for Guidance
  fields)
- **Source:** Phase A plan §3 ("Out of scope")

**Why parked.** Phase A adds `YouTurnSnapshot? YouTurn` and
`GuidanceSnapshot? Guidance` to `GpsCycleResult` as new nullable fields.
Existing flat fields (`IsInYouTurn`, `YouTurnTriggered`,
`YouTurnCompleted`, `SteerAngle`, `CrossTrackError`, `GoalPointEasting`,
`GoalPointNorthing`, `HasGuidance`) remain for backward compatibility
so nothing breaks mid-migration. Removal happens when all consumers are
swapped to read the snapshot.

**What the decision is.** Confirm Phase C's final commit removes
YouTurn flat fields and Phase D's final commit removes Guidance flat
fields, not sooner (breaks mid-branch builds) and not later (leaves
dead fields in the snapshot).

**Review log.**
- 2026-04-19 — Parked during Phase A planning. Owned by Phase C / D
  explicitly in their plans.
- 2026-04-19 (Phase A close) — Flat fields on `GpsCycleResult` remain
  (`IsInYouTurn`, `YouTurnTriggered`, `YouTurnCompleted`, `SteerAngle`,
  `CrossTrackError`, `GoalPointEasting`, `GoalPointNorthing`,
  `HasGuidance`). New `YouTurn` / `Guidance` snapshot fields sit
  alongside them as `null` placeholders. No removal in Phase A; still
  parked.

---

### TMP-006 — Enforcement of "no direct `State.*` writes from services"

- **Status:** Open
- **Raised in:** Phase A (from parent plan acceptance criteria)
- **Decide by:** Optional — latest viable is Phase F (final
  whole-effort acceptance)
- **Source:** parent plan §7 ("Enforce via a Roslyn analyzer if
  practical, otherwise via grep in CI")

**Why parked.** Useful but not load-bearing for any phase to ship.
Enforcement matters at steady state to keep the invariant from eroding
— a Roslyn analyzer is the structural answer, a CI grep is the
pragmatic answer. Either works; neither needs to be in place before
Phases C or D prove the pattern. Worth doing once the pattern is
established, because the analyzer rules become unambiguous.

**What the decision is.** Roslyn analyzer (compile-time enforcement,
higher build cost, false-positive risk) vs CI grep (zero build cost,
can miss aliased writes). Or both.

**Review log.**
- 2026-04-19 — Parked during Phase A planning. No phase currently
  owns it.
- 2026-04-19 (Phase A close) — No services migrated in Phase A; the
  invariant has nothing to enforce yet. Still parked — revisit at the
  end of Phase C when the first migrated service exists.

---

### TMP-007 — `MainViewModel` line-count baseline

- **Status:** Open
- **Raised in:** Phase A (from parent plan acceptance criteria)
- **Decide by:** Before Phase C starts (its acceptance references the
  baseline)
- **Source:** parent plan §7 ("baseline captured before Phase C")

**Why parked.** The parent plan asserts `MainViewModel` partials'
total line count will drop measurably. That's only verifiable against a
recorded baseline. Capture is trivial — one `wc -l` over the
`MainViewModel.*.cs` files — but needs to happen before Phase C so the
number is real.

**What the decision is.** When and where to record the baseline.
Suggest: add a line to the parent plan §7 with the recorded number the
day Phase C's branch is cut; commit alongside the Phase C PR.

**Review log.**
- 2026-04-19 — Parked during Phase A planning. Trivial but will be
  forgotten if not tracked.
- 2026-04-19 (Phase A close) — **Baseline captured** at end of Phase A
  (same commit base Phase C will branch from): `MainViewModel.cs` =
  5,148 lines, 23 partial files totaling 14,136 lines overall.
  Notable per-phase targets:
  - `MainViewModel.YouTurn.cs` = 206 lines (Phase A acceptance target:
    under 100 lines after Phase C).
  - `MainViewModel.GpsHandling.cs` = 436 lines (Phase C removes the
    `YouTurnStateMachine.Tick` call from this file).
  Phase C's final commit compares against this baseline.

---

### TMP-008 — Manual U-turn does not execute when triggered

- **Status:** Open
- **Raised in:** Phase A (discovered during the acceptance smoke test)
- **Decide by:** Phase C (before it migrates
  `TriggerManualYouTurnLeft/Right` onto the intent queue — a buggy
  starting state makes a migration impossible to verify)
- **Source:** Phase A smoke test on 2026-04-19 — operator pressed right
  manual-turn button; a point dropped and a dashed yellow line drew to
  the front of the tractor, but the tractor stayed on the guidance
  line instead of turning.

**Why parked.** Pre-existing bug, not a Phase A regression. Verified by
`git diff develop..feature/threading-phase-a --` over
`MainViewModel.YouTurn.cs`, `MainViewModel.GpsHandling.cs`,
`YouTurnStateMachine.cs`, and `MainViewModel.ApplyResults.cs` — zero
lines changed. Phase A's only runtime effect is a discarded
`Drain()` call in `GpsPipelineService.ProcessCycle`, with no
side-effects on any state the YouTurn machine reads.

Two plausible root causes, both pre-existing:
1. `YouTurnStateMachine.TriggerManual` bails at line 246 when
   `!isAutoSteerEngaged`, posting status "Enable autosteer first"
   without creating a path. If auto-steer wasn't engaged during the
   smoke test, that's working-as-designed — but the user saw a yellow
   dashed line, which suggests *something* ran. Need to check whether
   the rendered visual is the turn path, the Pure Pursuit goal-point,
   or a dropped marker.
2. Auto-steer was engaged, the path was generated (`SetYouTurnPath`
   called), but the autosteer loop isn't switching from guidance line
   to turn path. That would be a bug in the path-follow handoff.

**What the decision is.** Reproduce once with verbose logging on;
determine which of the two causes applies. Then either (a) confirm
"enable autosteer first" is the correct behavior and document it, or
(b) fix the path-follow handoff before Phase C migrates the trigger.

**Review log.**
- 2026-04-19 — Added during Phase A close after smoke test surfaced
  the issue. Parked because Phase A had no mandate to fix pre-existing
  YouTurn bugs, and because Phase C needs to inherit a working manual
  U-turn to verify its migration doesn't regress it.

---

## 5. Resolved items

Empty. Items move here on resolution with their final review log
entry and the PR that closed them.

---

## 6. Cross-references

- [THREADING_MIGRATION_PLAN.md](THREADING_MIGRATION_PLAN.md) — the
  parent plan; every item here originates from a section there or from
  a per-phase plan.
- [THREADING_PHASE_A_PLAN.md](THREADING_PHASE_A_PLAN.md) — Phase A
  implementation plan; its §8 points back to this document.
- [threading_model.svg](threading_model.svg) — target threading model;
  names referenced here should match names shown there.
