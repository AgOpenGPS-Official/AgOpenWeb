# MainViewModel refactor plan

**Status:** Agreed 26 Nov 2026: all items, under a total change freeze in early December 2026,
before R1. Done by chriskinal and Claude. The dead code removal goes in with the refactor. The
GPS cycle (item 8) was added the same day. See [Decided](#decided).

## Why

Not for elegance. The goal is code a person can review: a change to steering, sections, the GPS
cycle or a field's open and close can be judged from its diff, without reading the rest of
MainViewModel. Human review is the bottleneck, and the VM's shape is what makes it slow and
unreliable:

- **Review.** A change to the core file can't be judged from its diff: the file is 6,617 lines,
  with 314 private fields across the partials, a 460-line constructor and 53 event subscriptions.
  The code that most needs careful review is the hardest to find: the 100 Hz loop that moves the
  tool, decides the sections and sends PGN 254 and 239 (`OnControlLoopTicked`) is in the core
  file, and which VM fields it reads, from which thread, is visible nowhere. That's why #953 could
  not be taken on unit tests and a green build.
- **Field open and close.** What a field sets up is cleared somewhere else (`ClearFieldState`, an
  85-line list in the core file), and nothing ties the two together: whoever adds a feature has to
  remember the list, and the reviewer has to notice when they didn't. Since October six fixes were
  for something left behind after closing a field: the NTRIP connection (#286), the imagery
  (#309), coverage and tram lines (#310), a boundary recording (#625), the sections (#749), the
  recorded path (#956).
- **The GPS cycle.** `GpsPipelineService.ProcessCycle` is one method of about 1,100 lines, run
  on the pipeline's worker thread for every fix: it takes in the host loop's commands, judges the
  fix, fuses the heading, works out the antenna and reversing, feeds the pose estimator, ticks the
  U-turn state machine, applies the AutoSteer on and off rules, runs the guidance, the light bar,
  the headland, lift and tram lights, sends PGN 214 and builds the snapshot the VM reads. The
  class has 98 fields every step can read and write, and the order of the steps is nowhere but in
  the method. Of the 52 PRs that touched the file since October, 42 were fixes. It is the code
  that steers, and a change in one step can't be judged without reading all of it.
- **Merge conflicts.** Of the PRs merged between v26.7.7 and v26.7.8 (19 to 26 Nov 2026), 14
  touched MainViewModel and 10 of them the core file. Three of those 10 changed the same two
  places, the constructor and `StartHelloTimerAsync`, because every new feature is started there.
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
This plan does that for the whole VM, and for the GPS cycle the VM feeds, under a freeze.

## Scope

Nothing in the VM is out of scope. The VM stays as what `RemoteServerWiring` calls, and hands
each call to the service that owns it. The order and the scope are set by gain and risk, not by
open PRs: the enhancement PRs open now will be delivered in thematic phases and reworked at that
time anyway, so they are not a reason to leave anything out or to shape it differently.

| # | Domain | Where it is now | Size | Notes |
|---|---|---|---|---|
| 0 | Startup | the 460-line constructor and `StartHelloTimerAsync` | - | Each feature's partial gets its own start method (`StartModuleLostWatch()`, `StartDataQuality()`, ...), called from the constructor in today's order. A new feature then adds one call, not a block in the middle |
| 1 | Flags | core 2668-3033 | ~360 | Richard's `FieldFlags` (#953) may be used as the start where it fits; it does not set the design |
| 2 | Headland geometry | core 5413-6340 (`ClipHeadlandAtLine`, `LineSegmentIntersectsLine`, `CalculateHeading`, `CalculatePathLength`, ...) | ~900 | Pure geometry: to Models beside `GeometryMath`, easy to unit test |
| 3 | Headland builder state | `MainViewModel.Headland.cs`, `Headland.Remote.cs`, core 4059-4290 | ~1,600 | After 2; the undo state and the builder session |
| 4 | Boundary list, KML and ISO-XML import | core 4759-5382 | ~620 | File parsing out of the VM |
| 5 | Track persistence | core 2820-3033, 6377-6531 | ~370 | Load, save, migrate; `Commands.Track.cs` stays |
| 6 | Field lifecycle | `OpenFieldCoreAsync` (core 1695-1990), `CloseFieldAsync`, `ClearFieldState` (core 2137-2221), the save methods | ~600 | See below. Needs 1-5 first: they provide their open and close steps |
| 7 | Control path | `OnControlLoopTicked`, `OnRenderPullTick` (core 638-785), `GpsHandling.cs`, `SectionControl.cs`, `Guidance.cs`, the AutoSteer handlers (core 1306-1456) | ~1,200 | See below. With extra checks |
| 8 | GPS cycle | `GpsPipelineService.ProcessCycle` (Services, about 1,100 lines in one method; the class 2,673) | ~1,100 | See below. Last: the same path as 7 and the same seam (`Sync*ToPipeline`, the snapshot the VM reads), checked by the same tests |

### Field lifecycle (6)

The bug to design out is a step done on open, or during work, that is not undone on close.

- **Each domain owns both halves of its field state:** flags, headland, boundary, tracks, recorded
  path, coverage, tram, NTRIP and imagery each give one open step and one close step, side by
  side in the same file. A missing close is then seen next to its open.
- **A field session runs the steps in one declared order,** in one short file, with the reason
  for each place in it (AutoSteer off first; the recorded path ended before the sections go off,
  #956). The order is read in review, not dug out of a method.
- **Same steps, same order as today.** The session runs what `OpenFieldCoreAsync` and
  `ClearFieldState` run now; a step found missing is written down and fixed after the unfreeze,
  as any other bug.
- **A test of the whole cycle** (written before the freeze, against today's code): open a field,
  use every feature, close it, check that every domain reports nothing left; open another field,
  check that nothing of the first shows. A domain cannot join the session without a close step,
  so a new feature is covered by the test without anyone remembering it.

### Control path (7)

The algorithms are already services (`TrackGuidanceService`, `SectionControlService`, the U-turn
services, the GPS pipeline); what is in the VM is the glue: the 100 Hz tick, the 30 Hz render
pull, the GPS cycle and its `Sync*ToPipeline` steps, the section bar and the remote switches.

- **A control loop service owns the tick and its thread.** It states which thread it runs on and
  everything it reads that another thread writes, in one place. The VM sets its configuration
  through it and does not reach into it.
- **No thread changes.** What runs on the loop's thread, the host loop or the module's data
  thread today runs there after. The boundary becomes visible; it does not move. #957 showed how
  a change of thread breaks things tests rarely catch.
- **Why it is safe to move:** it is the best-tested code we have. The Vehicle & Tool sweep flips
  every setting and checks the steer angle, the PGNs and the sections; the job harnesses measure
  where product went on a 0.2 m grid; the machine module harness reads every PGN; the working day
  times every control tick; the hot-path budgets count the work in each cycle.
- **Extra checks for this step:** the working day's tick timings and its total allocated, the full
  sweep and the machine module's PGN counts, each within the spread of the two baseline nights.

### GPS cycle (8)

The same flaw as the field's open and close: the order of the steps is implicit, and every step
shares every field.

- **Each step its own method or class,** with what it reads and what it writes stated: the
  intents, the fix quality, the heading, the antenna and reversing, the estimator, the U-turn
  tick, the AutoSteer rules, the guidance, the light bar, the headland and lift, PGN 214, the
  snapshot.
- **The cycle is the list of steps** in today's order, in one place, with the reason for each
  place where the order matters (the U-turn tick before the guidance that steers along the turn).
- **Same steps, same order, same thread.** The cycle stays on the pipeline's worker thread; the
  intents stay the one way in from the host loop and the snapshot the one way out.
- **Hot path.** No allocation added: a step passes what it works out in fields of the cycle or in
  structs, not in new objects per fix. The GPS cycle's `WorkCounters` budget and the working
  day's total allocated (about 18.4 GB a night) are checked against F.
- **Extra checks for this step:** the control path's, and the job harnesses' pass order, turns
  and area sprayed, and the Vehicle & Tool sweep's steer angles, each within F's spread.

### Rules for the refactor branch

1. **No behaviour change.** No bug fixes or improvements mixed in, however small. A bug found is
   written down and fixed after the unfreeze.
2. **Moved code is moved, not edited,** apart from the calls into the new service. Each domain is
   one commit (the field lifecycle, the control path and the GPS cycle a few, each one reviewable), so
   `git diff --color-moved` shows what changed beyond the move.
3. **The wiring's surface stays the same.** The 192 public VM members that `RemoteWiring`,
   `RemoteServer`, the heads and the tests use keep their names, types and behaviour, so
   `RemoteServerWiring` does not change. A test added before the freeze (below) holds this.
4. **Threads stay as they are,** and every service says in its header which thread it runs on.
5. **Hot-path budgets unchanged.** `WorkCounters` budgets pass with the same numbers.
6. **Cross-platform parity.** A new service is registered in all three heads' DI (`Platforms/*/
   DependencyInjection/ServiceCollectionExtensions.cs`), or made by the VM itself.

## Dead code removal (the first commit of round 1)

The ~211 public members nothing uses, and the private fields and methods only they used. Found
by: declared in the VM, not named in `RemoteWiring`, `RemoteServer`, `Platforms`, `Tests` or
`Simulators`, and named no more than twice in the ViewModels and Services projects. Each is
checked by hand before it goes (a hotkey or a name built at run time would not show in a search).
The same sweep covers `ConfigurationViewModel` (1,849 lines) and `AutoSteerConfigViewModel`
(1,053), which are still used but date from the Avalonia panels too.

It goes in with the refactor, as the first commit on the branch, and is tested with round 1.
Done first, it makes the moves after it smaller and their diffs easier to read.

## Phase 1: before the freeze

1. **Announce the freeze** to the contributors at least a week ahead, with its dates: nothing is
   merged to develop while it lasts. Contributors may keep working on their branches. Open PRs are
   not waited for: bug fixes ready before the freeze are merged, the rest are rebased after it.
2. **Characterization tests** for each domain, written against develop and merged before the
   freeze. They drive the VM through its public members and check the results, so they pass
   unchanged before and after:
   - flags: add, rename, recolour, delete, delete all, save and load again;
   - headland: build from a boundary at several widths, clip at a line, undo, save and load;
   - boundary and import: a KML with one and several polygons, a bow-tie, an ISO-XML field;
   - tracks: save, load, the AgOpenGPS migration, a short A+ line lengthened;
   - field lifecycle: the whole cycle above, open, use, close, open another;
   - control path: a recorded GPS run fed through the VM (from a dump, as the dump replay harness
     does), the steer angles, PGNs and section states compared with today's. The control tick is
     timed, so the comparison allows the spread two runs of today's code show; if that spread is
     too wide to catch a change, the harnesses carry this step and the test is dropped.
   - GPS cycle: `ProcessCycle` driven directly with a recorded run of fixes and intents (no
     timer), every cycle's snapshot compared with today's: the guidance, the steer angle, the
     U-turn state, the light bar, the lift and tram state, PGN 214. It can be close to exact, but
     the cycle reads the wall clock itself (`DateTime.UtcNow` three times, `Stopwatch` twice) and
     no test fakes it yet: those reads go through `Clock.Current` first, as a change of its own
     before the freeze (same values in the app), so the test can set the time.
3. **The surface test:** a list of the public VM members the wiring uses (from reflection), checked
   in. The test fails when one is added, removed or changes type. It is updated on purpose after
   the refactor if a change is wanted, never as a side effect.

## Phase 2: the freeze

| Day | Step |
|---|---|
| 1 | Freeze starts at commit **F** on develop. Total change freeze: nothing is merged to develop until day 26 |
| 1-2 | **Testing before:** CI, the hot-path budgets, and the full nightly set on F on two nights: the second night shows the noise (on 25 and 26 Nov the finding codes matched and only timings and counts moved, e.g. a turn at 139.8 s against 139.5 s). The nightly keeps running on F every night of the freeze, so the baseline's spread grows with it. Manual check on hardware (below) |
| 3-6 | **Refactor, round 1:** the dead code removal, then items 0-5, one commit each |
| 7 | **Testing round 1:** CI, the hot-path budgets, the characterization and surface tests, all eleven harnesses as whole runs (no `TURNS` or `ONLY`) against F with `runner-compare.sh` on the harness host |
| 8-10 | Fixes from round 1. **Refactor, round 2:** the field lifecycle (6) |
| 11 | **Testing round 2:** the same set, against F |
| 12-14 | Fixes from round 2. **Refactor, round 3:** the control path (7) |
| 15 | **Testing round 3:** the same set, with the control path's extra checks, and the manual check |
| 16-20 | Fixes from round 3. **Refactor, round 4:** the GPS cycle (8), a step at a time |
| 21 | **Testing round 4:** the same set, with the GPS cycle's extra checks, and the manual check |
| 22-24 | **Fixes** for what the refactor broke, each with a unit test made from the finding |
| 25 | **Testing again:** the whole set and the manual check |
| 26 | **Merge and unfreeze** |

About four weeks with slack for a second round of fixes. Each round is tested on its own, so a
finding points at one round's commits, and the riskiest work comes last, on a base already tested.

### Pass mark (every testing round)

- CI green, the hot-path budgets and the surface test unchanged.
- Every harness: no finding new **and no finding gone** against F. A finding that disappears also
  means behaviour changed, and needs explaining before the merge.
- Measured values (area sprayed, turns, pass order, tick timings, PGN counts) within the spread F
  showed across its two nights.
- The manual check passes on the Desktop head and one tablet (rounds 3 and 4, and the last).

### Manual check on hardware

Short, the same before and after, done by whoever has the bench:

open a field and start a job; make an AB line; AutoSteer on the bench and off; sections Auto and
manual; build a headland, clip it, undo; place, rename and delete flags; import a KML boundary;
record and play a path; a U-turn on the simulator; NTRIP connected; save, close the field and
check nothing is left (map, sections, recorded path, NTRIP as configured); open another field;
reopen the first; a bug report dump.

## Phase 3: after the merge

- **Open PRs.** Bug fix PRs touching MainViewModel are rebased on the merge (by their author, or
  by us if the author agrees) and reviewed again in full, with the usual recipe. Enhancement PRs
  are reworked onto the new structure when their thematic phase comes, and reviewed then. #953 is
  closed in favour of domain 1, with Richard credited for what was used.
- **CLAUDE.md and `Docs/ARCHITECTURE.md`:** the new services, the thread each runs on, the field
  session's order, and the rule that a feature with field state gives an open and a close step.
- **The nightly** on develop the morning after is the last check; anything new in it is a
  refactor bug until shown otherwise.
- This plan moves to `Plans/Completed/` with what was done and what was learned.

## Decided

Agreed 26 Nov 2026:

1. **Scope:** all items, in one freeze: 0-7 as first agreed, and the GPS cycle (8), added the same
   day, which makes the freeze about four weeks.
2. **When:** early December 2026. The start date is set when the freeze is announced, at least a
   week ahead; the characterization tests and the surface test are merged before it.
3. **During the freeze:** nothing is merged to develop. Total change freeze.
4. **Who:** Claude does the refactor, the tests and the harness runs; chriskinal reviews each round
   and does the manual check on hardware.
5. **Dead code:** removed with the refactor, as the first commit of round 1, not ahead of it.
