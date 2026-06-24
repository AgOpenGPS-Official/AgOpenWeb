<!--
AgOpenWeb
Copyright (C) 2024-2026 AgOpenWeb Contributors
Licensed under GNU GPL v3. See LICENSE.md.
-->

# Threading Migration — Session Handoff (2026-04-20)

A continuation note for the next context after compaction. Reads as
"picking up where I left off" — the plans under `Plans/` remain the
authoritative source of scope, decisions, and acceptance.

## Where we are

**Branch:** `feature/threading-phase-a` (holds all of Phases A–F;
PR #259 draft against `develop`).

**Current phase:** Phase C — YouTurn end-to-end on the cycle worker.
Commits shipped through C5; in smoke-test verification of the
goal-point regression fix. Phases A and B are complete.

**Last action before compaction:** launched Desktop for smoke-test of
commit `ee3859d` (goal-point fix) after user reported the dot jumping
to the field origin during auto-U-turn. App closed; awaiting the
user's verification. When they confirm, proceed with C6.

## Phase C commit timeline

| Commit | Scope | Status |
|---|---|---|
| `03928ce` | C2: cycle holds `YouTurnWorkingState` (scaffold only) | shipped |
| `bf204dc` | C3: state machine takes `YouTurnWorkingState`, MVM bridges | shipped |
| `25c2042` | C1: TMP-008 resolved as "works as designed" + Issues #260 #261 | shipped |
| `dc5cb08` | **C4+C5: tick moves to cycle; snapshot mirrored in ApplyGpsCycleResult** | shipped |
| `ee3859d` | fix: populate `goalE/goalN` in YouTurn guidance branch | shipped, awaiting smoke |

C1 was done after C2/C3 chronologically because the TMP-008
investigation happened later. Commit order reflects dependency,
not calendar order.

## Phase C plan §5 commit map (remaining)

- **C6 — migrate manual-trigger commands to intents.**
  `TriggerManualYouTurnLeft/Right` in `MainViewModel.YouTurn.cs`
  replace their state-machine calls with
  `_intents.RequestManualYouTurn(turnLeft)`. Cycle worker drains the
  intent in `ProcessCycle` and calls `_youTurnStateMachine.TriggerManual`
  on the cycle thread with cycle-owned `_youTurn` and `_guidanceWorking`.
  Removes the UI-thread bridge's manual branch and the
  `SetYouTurnWorkingState` push (the temporary lock-protected path
  added in C4+C5).
- **C7 — migrate `ClearYouTurnState` to intent.**
  `ClearYouTurnState()` in `MainViewModel.YouTurn.cs` becomes
  `_intents.RequestClearYouTurn()`. Cycle drains and calls
  `YouTurnStateMachine.ClearState(_youTurn)`. Removes the remaining
  UI-thread bridge helpers + calls.
- **C8 — delete flat YouTurn fields from `GpsCycleResult`.**
  `IsInYouTurn`, `YouTurnTriggered`, `YouTurnCompleted` removed;
  all consumers read from `result.YouTurn?.*`. Add `JustCompleted`
  to `YouTurnSnapshot` for the completion signal currently carried
  in the flat `YouTurnCompleted` field. Update cycle emission +
  ApplyGpsCycleResult reader.
- **C9 — tests + line-count check + parking-lot review.**
  `YouTurnCycleTests.cs` (state machine runs on cycle,
  intent drain triggers manual turn, snapshot emits on state change),
  `ApplyGpsCycleResultTests.cs` extensions (State.YouTurn written
  from snapshot), re-measure `MainViewModel.YouTurn.cs` line count
  (target: under 100; currently ~235 due to bridge helpers — C6/C7
  remove most of them), walk parking lot (§4 of parking lot doc)
  with Phase-C-close review log entries.

After C9: Phase C hand-off to Phase D is clean per plan §6 / §9.

## Decisions locked (don't re-litigate)

From Phase C plan §2:

1. **TMP-001 (snapshot identity)** — reuse list references. Natural
   consequence of the state machine replacing `TurnPath` only on turn
   start/end; `SetProperty`'s reference equality elides `PropertyChanged`
   when the list ref is unchanged. No explicit value-equality code.
2. **`YouTurnEffects` dissolves into the snapshot.** Not done yet —
   the cycle's Tick still returns the record internally, and C4+C5
   route `effects.StatusMessage` + `effects.TurnCompleted` through
   `GpsCycleResult.StatusMessage` + `YouTurnCompleted`. C8 finishes
   dissolution when flat fields are deleted.
3. **Partial Phase D scope creep accepted.** `GuidanceWorkingState`
   threaded through the state machine path (Tick / TriggerManual /
   ClearState signatures all take it). Full `GuidanceState` migration
   is still Phase D's scope; C4+C5 only touched the state-machine
   call path.

## Active parking lot items relevant to Phase C

**Resolved** (§5 of parking lot): TMP-003 (service name),
TMP-004 (UdpGpsQueue), TMP-008 (manual U-turn).

**Open and relevant:**
- **TMP-001** — lock-level decision made; C8 emits snapshot with
  reused list ref when state machine keeps the same list. Review at
  C9 close.
- **TMP-002** — C6/C7 picks up the YouTurn command carve-out decision
  (current plan: 100 ms intent latency is fine for manual turn; no
  carve-out needed).
- **TMP-005** — C8 removes the flat YouTurn fields; Guidance fields
  wait for Phase D.
- **TMP-007** — re-measure MainViewModel.YouTurn.cs line count after
  C7 and log result on TMP-007.

See `Plans/THREADING_MIGRATION_PARKING_LOT.md` for the full state and
review-log protocol.

## Key architecture as of now

- **Cycle worker** (`GpsPipelineService.ProcessCycle` on `Task.Run`):
  owns `_youTurn` (`YouTurnWorkingState`) and `_guidanceWorking`
  (`GuidanceWorkingState`). Runs the YouTurn Tick when autosteer +
  track + youturn-enabled + valid headland guards are met. Writes
  to POCOs (no PropertyChanged fires). Emits `GpsCycleResult` with
  populated `YouTurn` and `Guidance` snapshots. Flat YouTurn fields
  on `GpsCycleResult` still populated for backward compat — deleted
  in C8.
- **UI thread** (`MainViewModel.ApplyResults.cs`): mirrors
  `result.YouTurn` → `State.YouTurn` (all fields),
  `result.Guidance` → `State.Guidance.IsHeadingSameWay` +
  `HowManyPathsAway` (subset, full set in Phase D), map-service
  pushes (`SetYouTurnPath`, `SetNextTrack`, `SetIsInYouTurn`).
  Calls `SyncGuidanceStateToPipeline()` after `HowManyPathsAway`
  changes so the cycle's `_passNumber` cache stays fresh.
- **Manual trigger / clear** (`MainViewModel.YouTurn.cs`): UI-thread
  bridge still runs the state machine inline with local POCOs
  (YouTurnWorkingState + GuidanceWorkingState), copies back to
  observable state, pushes the updated YouTurn working state into
  the pipeline via `SetYouTurnWorkingState`. Retires in C6/C7.
- **Tests**: 773 passing (93 Models + 380 Services + 116 UI + 184
  ViewModels). Phase A's shape-mirror tests still lock
  `YouTurnState` ↔ `YouTurnWorkingState` and
  `GuidanceState` ↔ `GuidanceWorkingState` field parity — any drift
  fails a test.

## Recent non-plan finds

- Phase B's `_gpsData` reuse (commit `60b1537` fix) was a race source
  that caused UI-hover freezes. Resolved by allocating fresh
  `GpsData` per packet.
- TMP-008's visual offset was diagnosed as "manual U-turn anchors at
  tractor position, not the pass line" — AgOpen parity work, not a
  threading bug. Filed as Issues #260 (immediate-turn-at-tractor) and
  #261 (free-drive line follows tractor). Both in the AgOpenWeb
  GitHub Project "Planning" column.
- Phase C C4+C5 eliminated a one-cycle lag that had been masking a
  pre-existing goal-dot-at-origin bug during YouTurn. Fixed in
  `ee3859d` by returning `GoalPoint.Easting/Northing` from
  `CalculateYouTurnGuidance` and assigning into the cycle's
  `goalE/goalN`.

## When you pick this up

1. Ask the user how the smoke test went for `ee3859d`. If the goal
   dot now tracks correctly during the auto-U-turn, acknowledge and
   move on. If it's still wrong, look at `CalculateYouTurnGuidance`
   and `MainViewModel.ApplyResults.cs`'s `SetGuidancePoints` call
   path.
2. Proceed with **C6 — manual-trigger intent migration**. Plan §5
   Commit 6. ~30 lines of net change in `MainViewModel.YouTurn.cs`
   and `GpsPipelineService.ProcessCycle`.
3. After C7, re-measure line counts and note on TMP-007.
4. C9 brings the phase to smoke test + parking-lot review.

## Don't do

- Don't mutate the plans (`THREADING_*.md`) without an explicit
  revision commit. Phase B's plan got amended for scope creep —
  that was a commit (`0a23c37`).
- Don't merge to `develop` until the whole feature works end-to-end.
  The user explicitly chose the all-phases-one-branch approach;
  PR #259 stays draft until Phase F.
- Don't skip parking-lot reviews at phase boundaries. They're part
  of the acceptance for each phase.
- Don't delete the `// Phase C C4 bridges` comment in
  `MainViewModel.YouTurn.cs` until C6/C7 actually removes the
  bridge code — the comment is a roadmap marker.

## Useful commands

```bash
# Current state
git log --oneline develop..HEAD

# Build + test the full solution (Desktop-only, skips iOS ILLink issue)
dotnet build AgOpenWeb.sln -p:DesktopOnly=true
dotnet test Tests/AgOpenWeb.Models.Tests/AgOpenWeb.Models.Tests.csproj --nologo -p:DesktopOnly=true
dotnet test Tests/AgOpenWeb.Services.Tests/AgOpenWeb.Services.Tests.csproj --nologo -p:DesktopOnly=true
dotnet test Tests/AgOpenWeb.UI.Tests/AgOpenWeb.UI.Tests.csproj --nologo -p:DesktopOnly=true
dotnet test Tests/AgOpenWeb.ViewModels.Tests/AgOpenWeb.ViewModels.Tests.csproj --nologo -p:DesktopOnly=true

# Run the app for smoke tests
dotnet run --project Platforms/AgOpenWeb.Desktop/AgOpenWeb.Desktop.csproj -c Debug
```
