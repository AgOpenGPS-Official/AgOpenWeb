# MainViewModel refactor plan

**Status:** Draft for discussion, not agreed. Proposed as a planned piece of work in a quiet
period before R1, under a code freeze. The dead code removal (Phase 0) may go in at any time.

## Why

Not for elegance. MainViewModel is where most change lands, and its size now costs us in three
ways we can see:

- **Merge conflicts.** Of the PRs merged between v26.7.7 and v26.7.8 (19 to 26 Nov 2026), 14
  touched MainViewModel and 10 of them the core file. Three of those 10 changed the same two
  places, the constructor and `StartHelloTimerAsync`, because every new feature is started there.
- **Review.** A change to the core file can't be judged from its diff: the file is 6,617 lines,
  with 314 private fields across the partials, a 460-line constructor, and 53 event subscriptions
  across the partials. That's why #953 could not be taken on unit tests and a green build.
- **Dead surface.** About 211 public members (182 of them commands) are declared and never used:
  what's left of the Avalonia dialogs, removed in October. They make the file look larger and more
  tangled than it is, and a reader can't tell live code from dead.

## What was tried before

| When | What | Outcome |
|---|---|---|
| Dec 2025 | `STATE_CENTRALIZATION_PLAN.md`: every field into an `ApplicationState`, the VM to ~500 lines, in one sweep | Not done as written. Too big to do while the app kept changing |
| Jan 2026 | `MAINVIEWMODEL_REFACTORING_PLAN.md`: code moved into partial files unchanged | Done in two days. Merge conflicts fell, but a partial is the same class: the core file is back from ~1,500 to 6,617 lines, and the coupling is unchanged |
| Apr 2026 | U-turn logic moved out of the VM into services, in five phases (#256) | Worked: `YouTurnStateMachine`, the pass book-keeping and the path creation are services with their own tests, and the U-turn work since then has been made there |

The lesson: moving code into partial files does not hold, and a big sweep does not get done.
What holds is taking one domain at a time out into a service with its own tests, as #256 did.
This plan does that, for a few chosen domains, under a freeze.

## Scope

### In: domains taken out of the VM

Each becomes a service (or, for pure geometry, static code in Models). The VM keeps the same
public members, which call the service. Order: item 0 first, as the most frequent conflict, then
the most self-contained.

The scope is set by what gains most for the risk, not by open PRs. The enhancement PRs open now
will be delivered in thematic phases and reworked at that time anyway, so they are not a reason
to leave anything out or to shape it differently.

| # | Domain | Where it is now | Size | Notes |
|---|---|---|---|---|
| 0 | Startup | the 460-line constructor and `StartHelloTimerAsync` | - | Each feature's partial gets its own start method (`StartModuleLostWatch()`, `StartDataQuality()`, ...), called from the constructor in today's order. A new feature then adds one call, not a block in the middle. Same calls, same order: no behaviour change |
| 1 | Flags | core 2668-3033 | ~360 | Richard's `FieldFlags` (#953) may be used as the start, where it fits; it does not set the design |
| 2 | Headland geometry | core 5413-6340 (`ClipHeadlandAtLine`, `LineSegmentIntersectsLine`, `CalculateHeading`, `CalculatePathLength`, ...) | ~900 | Pure geometry: to Models beside `GeometryMath`, easy to unit test |
| 3 | Headland builder state | `MainViewModel.Headland.cs`, `Headland.Remote.cs`, core 4059-4290 | ~1,600 | After 2; the undo state and the builder session |
| 4 | Boundary list, KML and ISO-XML import | core 4759-5382 | ~620 | File parsing out of the VM |
| 5 | Track persistence | core 2820-3033, 6377-6531 | ~370 | Load, save, migrate; `Commands.Track.cs` stays |

About 3,800 lines leave the core file and the headland partials. The core file would be about
3,500 lines; that is not a target, just what is left.

### Out: not touched

- **The control loop, GPS handling, guidance, section control, AutoSteer handlers**
  (`OnControlLoopTicked`, `GpsHandling.cs`, `Guidance.cs`, `SectionControl.cs`). Little to gain:
  the algorithms are already services (`TrackGuidanceService`, `SectionControlService`, the U-turn
  services, the GPS pipeline), and what is left in the VM is the glue between them (`Guidance.cs`
  124 lines, `SectionControl.cs` 266, `GpsHandling.cs` 514). The most risk: it runs 10 to 100
  times a second, partly on the module's thread, so what breaks is timing and threads, which tests
  catch least (#957's race showed once in one scenario run, never in a nightly). None of the 10
  PRs to the core file in the week to v26.7.8 touched it.
- **Field open/close/save** (core 1607-2226). It is coordination, which is the VM's job: closing a
  field turns AutoSteer off, ends the recorded path, turns the sections off, saves the coverage and
  ends the job, in an order that matters (#956 had to place its step exactly). A field service
  would need nearly every other service, the same tangle under another name. Reconsidered after
  this round, if the process has worked.
- **The state centralization** of Dec 2025, sub-ViewModels for panels, renames, and anything in
  `RemoteServerWiring` or the web client.

### Rules for the refactor branch

1. **No behaviour change.** No bug fixes or improvements mixed in, however small. A bug found is
   written down and fixed after the unfreeze.
2. **Moved code is moved, not edited,** apart from the calls into the new service. Each domain is
   one commit, so `git diff --color-moved` shows what changed beyond the move.
3. **The wiring's surface stays the same.** The 192 public VM members that `RemoteWiring`,
   `RemoteServer`, the heads and the tests use keep their names, types and behaviour, so
   `RemoteServerWiring` does not change. A test added before the freeze (below) holds this.
4. **Threads stay as they are.** Code that runs on the host loop still runs on the host loop; a
   handler called from the module's data thread is not moved to another thread or made to call
   one. #957 showed how a change of thread breaks things that tests rarely catch.
5. **Hot-path budgets unchanged.** `WorkCounters` budgets pass with the same numbers.
6. **Cross-platform parity.** A new service is registered in all three heads' DI (`Platforms/*/
   DependencyInjection/ServiceCollectionExtensions.cs`), or made by the VM itself.

## Phase 0: dead code removal (any time, before the freeze)

The ~211 public members nothing uses, and the private fields and methods only they used. Found
by: declared in the VM, not named in `RemoteWiring`, `RemoteServer`, `Platforms`, `Tests` or
`Simulators`, and named no more than twice in the ViewModels and Services projects. Each is
checked by hand before it goes (a hotkey or a name built at run time would not show in a search).
CI and one nightly on develop are enough for this; it is what was agreed for #953.

Doing this first makes the freeze shorter and the refactor's diffs smaller.

## Phase 1: before the freeze

1. **Announce the freeze** to the contributors at least a week ahead, with its dates and what is
   allowed during it (below). Open PRs are not waited for: bug fixes ready before the freeze are
   merged, the rest are rebased after it.
2. **Characterization tests** for each domain in scope, written against develop and merged before
   the freeze. They drive the VM through its public members and check the results, so they pass
   unchanged before and after:
   - flags: add, rename, recolour, delete, delete all, save and load again;
   - headland: build from a boundary at several widths, clip at a line, undo, save and load;
   - boundary and import: a KML with one and several polygons, a bow-tie, an ISO-XML field;
   - tracks: save, load, the AgOpenGPS migration, a short A+ line lengthened.
3. **The surface test:** a list of the public VM members the wiring uses (from reflection), checked
   in. The test fails when one is added, removed or changes type. It is updated on purpose after
   the refactor if a change is wanted, never as a side effect.

## Phase 2: the freeze

| Day | Step |
|---|---|
| 1 | Freeze starts at commit **F** on develop. Only safety fixes are merged (also applied to the refactor branch) |
| 1-2 | **Testing before:** CI, the hot-path budgets, and the full nightly set on F on two nights: the second night shows the noise (on 25 and 26 Nov the finding codes matched and only timings and counts moved, e.g. a turn at 139.8 s against 139.5 s). Manual check on hardware (below) |
| 2-5 | **The refactor**, on a branch from F, one domain per commit, in the order above |
| 6 | **Testing after:** CI, the hot-path budgets, the characterization and surface tests, then all eleven harnesses as whole runs (no `TURNS` or `ONLY`) on the branch against F with `runner-compare.sh` on the harness host, and the manual check |
| 7-8 | **Fixes** for what the refactor broke, each with a unit test made from the finding |
| 9 | **Testing again:** the same set as day 6 |
| 10 | **Merge and unfreeze** |

About two weeks, with slack for a second round of fixes.

### Pass mark (days 6 and 9)

- CI green, the hot-path budgets and the surface test unchanged.
- Every harness: no finding new **and no finding gone** against F. A finding that disappears also
  means behaviour changed, and needs explaining before the merge.
- Measured values (area sprayed, turns, pass order, tick timings) within the spread F showed
  across its two nights.
- The manual check passes on the Desktop head and one tablet.

### Manual check on hardware

Short, the same before and after, done by whoever has the bench:

open a field and start a job; make an AB line; AutoSteer on the bench and off; sections Auto and
manual; build a headland, clip it, undo; place, rename and delete flags; import a KML boundary;
a U-turn on the simulator; save, close and reopen the field; a bug report dump.

## Phase 3: after the merge

- **Open PRs.** Bug fix PRs touching MainViewModel are rebased on the merge (by their author, or
  by us if the author agrees) and reviewed again in full, with the usual recipe. Enhancement PRs
  are reworked onto the new structure when their thematic phase comes, and reviewed then. #953 is
  closed in favour of domain 1, with Richard credited for what was used.
- **CLAUDE.md and `Docs/ARCHITECTURE.md`:** the partial file table and the new services.
- **The nightly** on develop the morning after is the last check; anything new in it is a
  refactor bug until shown otherwise.
- This plan moves to `Plans/Completed/` with what was done and what was learned. A second round
  (field open/close) is decided then, not now.

## To decide

1. **Scope:** startup and all five domains, or fewer (startup, flags and the headland are the
   clearest wins).
2. **When:** which quiet period before R1, and the freeze dates.
3. **During the freeze:** safety fixes only, or also small fixes outside MainViewModel.
4. **Who:** who does the refactor and who does the manual check on hardware.
5. **Phase 0 now:** whether the dead code goes in ahead of everything else.
