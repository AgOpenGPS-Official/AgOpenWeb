# Other vehicles and other machines' coverage (PR E, PR F)

**Status:** proposed, for discussion with the maintainers; nothing is built. Belongs with [ASL_MASTERPLAN.md](ASL_MASTERPLAN.md) (PR 518) section 7: PR E (sections 1-7 below) and PR F (section 8) would sit next to PRs A-D. PR F builds on PR C and on PR E's file channel. Both are useful without ASL: any tool can write the files described here.

## 1. What and why

Operators working on the same field want to see each other on their own map, as a vehicle in a
different colour. Today an add-on cannot put anything on AOW's map: the observer feed only flows
out of AOW. This plan adds one small, generic, **display-only** layer, "other vehicles".

It is useful without ASL: any tool (a second AOW on the LAN, a fleet tool, a test script) can write
the same file.

At most **5 machines work on one field** at the same time, so a machine shows at most 4 others.

## 2. What the operator sees

- A tractor image in a different colour, drawn like the own-vehicle image, at the other machine's
  position and heading.
- The own colour is reserved; the other machines get their colour from a small palette that the
  add-on chooses.
- One piece of text next to it: the other machine's **current pass number** ("Pass 12").
- Nothing else on the main map from this layer: no implement, no sections, no name. Details live in
  the add-on's web card (for example what the other machine has done, in a field information tab),
  which AOW does not know about. The other machine's coverage is a separate, optional layer
  (section 8).

## 3. How it gets into AOW

**A small file in the data root, written by the add-on, read by AOW.** Chosen because it follows
the rules of the ASL plan: a public interface (files), AOW never calls or waits for the add-on, no
new endpoint, no change to `ControlAuthority` or `WebSocketHub`.

Proposed file: `<data root>/external/peers.json` (name and place are an open question, see 6).

```json
{
  "version": 1,
  "updated": "2026-11-15T13:45:10Z",
  "peers": [
    { "id": "7c1f...", "color": "#E4572E", "lat": 52.12345678, "lon": 5.12345678,
      "headingDeg": 87.5, "speedMs": 2.8, "pass": 12, "t": "2026-11-15T13:45:08Z" }
  ]
}
```

- Written by temp file and rename, as `FIELD_FILE_FORMATS.md` already requires of writers.
- Numbers use InvariantCulture; units are stated in the doc (degrees for heading, metres per second
  for speed, UTC for times).
- The add-on updates the file at a **fixed interval of 10 seconds**.
- The add-on writes only the machines of the current field group. The pass number is computed on
  the other machine and sent; the receiving AOW does not compute it.

## 4. Rules on the AOW side

- **Display only.** The vehicle layer is never read by guidance, section control, coverage or the
  U-turn logic.
- **Off the hot path.** The file is read when it changes (watcher or a slow timer), never in the
  broadcast loop (as #604). The hot-path budget tests must not change.
- **Bounded and defensive.** A file size limit and a maximum of 4 other machines (5 per field);
  each entry validated on its own; one bad entry is skipped, a bad file leaves the previous layer in
  place; no crash path.
- **Stale vehicles.** A vehicle that has not been seen for longer than a threshold fades and is then
  removed from the map, and it is removed when its machine closes (thresholds indicative, to be
  agreed; the update interval itself is fixed at 10 s).
- **Position between updates** is extrapolated on the client from heading and `speedMs`, up to a
  short maximum after the last update (proposal; open question 4 in section 6).
- **Switchable.** A view setting "show other vehicles", **on by default**. It draws something only
  when the file exists, so without an add-on nothing changes.
- **Wire:** the layer reaches browsers as a new frame, so it belongs in the wire docs and the
  protocol version of PR A.

## 5. What the vehicle layer does not do

No names or operator identity in AOW. No change to AOW's file formats, profiles or profile IDs.
No implement, sections or coverage on the main map from this layer. Section control from another
machine's coverage is a separate, optional step with its own switch (section 8).

## 6. Open questions (vehicle layer)

1. Is a file in the data root acceptable as the way in? The alternatives were a narrow
   authenticated endpoint on AOW, or UDP on the local network. Both add a listener and an
   authentication story to AOW; the file does not.
2. Name and location of the file (data root, or per field folder).
3. Should the add-on guide (`Docs/ADD_ON_CARDS.md`) get a section on this, next to the
   observer-feed section planned with PR A?
4. Between the fixed 10 s updates, extrapolate positions on the client from heading and `speedMs`
   (proposal), or show the last position plus its age?

## 7. Interplay with the other PRs

- **PR A** (protocol version, wire docs): carries the new frame.
- **PR C** (reference coverage layer): the display of another machine's coverage; section 8 adds the
  optional use of it by section control.
- **PR B** (N watching): unrelated; the privacy mode on the add-on side switches sharing off.

## 8. Other machines' coverage in section control (PR F, optional, off by default)

`ASL_MASTERPLAN.md` section 6 (risk 1) says section control from another machine's coverage is not
done now, and that if it ever is, the operator switches it on per job, it is visible, and it is
ignored when it stops updating. This section is that "if", with the conditions made concrete.

### 8.1 What it is: one shared map as a view, each machine keeps its own

Under option A (8.3) up to 5 machines on the same field work with **one shared coverage map** and
react to each other, in both directions. It is built as a **view**, not as a merge into AOW's own
files:

- Each machine paints and saves **only its own** coverage, as today (its own detection grid, its own
  tiles in its own job folder).
- The other machines' finished coverage arrives as a **second, read-only grid** beside it (8.6 for
  the file route).
- Section control and the map use the **union** of the two. Coverage is only ever added during work
  (bits only go to 1), so a union has no conflicts and the order in which updates arrive does not
  matter.
- The jobs carry the same link to the shared set. The link lives in the add-on's own sidecar, not in
  `job.json` (no format change for the cloud's sake).

How section control reads coverage today: `SectionControlService` asks
`CoverageMapService.GetSegmentCoverageMulti` for each section, which samples one 0.1 m bit grid
through `IsCellCovered`. The second grid is consulted in the same place, only under A.

### 8.2 What it never does

- It does not change the own coverage grid, its saved tiles, the own **statistics** (area, overlap)
  or the application record. Each machine keeps its own statistics and its own application record.
- Outside AOW, the add-on side may add the machines' parts together (X + Y = Z) for a whole-field
  view and reports. That is not stored in any AOW job.
- What the other machines have done is also shown in the add-on's web card.

### 8.3 The operator's choice: option A or B

AOW cannot tell reliably whether another machine's work is the same kind of work: `Job.WorkType` is
free text ("spraying", "Spuiten", ...). So the operator decides, with one general option:

- **A. Take the other machines' work into account for overlap.** Section control also treats the
  other machines' finished coverage on this field as already worked, and the machines react to each
  other (when they are all on A).
- **B. The other machines' coverage has no influence on my coverage mapping.** (Default.) Only the
  machine's own coverage counts.

The choice is made per machine. If X is on A and Y is on B, X reacts to Y but not the reverse.

The only automatic condition is the **same field** (the field folder's identity).

Making the choice an informed one:
- When the operator switches to A, the screen names the other machines and shows each one's job
  label and work type text as the other side sent it, so a fertilizer pass is not taken for a
  sprayed one by mistake.
- The work type text is for display only. AOW does not compare it and the free-text `WorkType` field
  is not changed.
- The responsibility for "this is the same work" is explicitly the operator's. The visible indicator
  (8.4) stays on while A is in effect.

### 8.4 Switches

- Default **B** (off).
- A setting to use A **for all jobs**, and a per-job switch that sets A or B for the current job,
  overriding the overall setting.
- While A is in effect, a visible indicator on the screen and its own colour on the map.
- A machine whose coverage is being used by another shows a small indicator of that in its own card.

### 8.5 Lifetime and deletion

- A **vehicle** disappears from the map when its machine closes, or when its last-seen age passes the
  threshold.
- **Under B:** the other machines' coverage (shown, but without influence) disappears when the own
  field is closed and the other machine's job is finished.
- **Under A:** the jobs stay linked to the same shared set. Each job **keeps only its own part** on
  disk, and no job stores the union. After closing, the whole-field picture (X + Y = Z) lives with
  the add-on.
- **Closing a field or a job** clears the coverage in memory only (`ClearAll()` in the field-close
  and field-open paths); the saved tiles stay with the job. It never touches the other machines.
- **Deleting the applied area.** The operator command "Delete applied area" (AgOpenGPS parity,
  `DeleteAppliedAreaConfirmed`) does wipe a job's coverage. Under A it clears **only the machine's
  own part**, and the add-on removes that part from the shared set; the other machines' parts stay.

### 8.6 Safety measures

The failure to prevent is a wrong or stale "done" area switching a section off and leaving a strip
untreated.

- Only strips that the other machines have **finished** count, never the pass they are on now. With
  an update interval of about 10 s the layer lags, and this keeps a lag from mattering.
- The external coverage is **shrunk by a fixed margin** (for example a few decimetres) so edges and
  position differences between machines do not create gaps. Margin to be agreed.
- **Same reference frame:** machines on different RTK networks can differ by metres. The add-on
  keeps the correction source in its own sidecar; A can only be switched on when the frames match,
  as declared in the tile set's metadata.
- A bad, huge or damaged tile set is skipped (size limits, each file read on its own), never a crash.
- At most 4 other tile sets are read (5 machines per field).
- Tile sets are written by the add-on in the **existing coverage tile format** into a separate
  folder under `external/`, by temp file and rename, manifest last, as `FIELD_FILE_FORMATS.md` says.

### 8.7 Hot path

The second-grid lookup runs only under A. The hot-path budgets (coverage lookups per cycle) must be
tested under B (unchanged) and under A (a stated, bounded increase, with 4 other grids).

### 8.8 Open questions (coverage)

1. Margin size, and how the matching reference frame is declared and checked (to be measured in the
   spike of `ASL_MASTERPLAN.md` section 10).
2. Folder layout under `external/` for tile sets, and who cleans it up.
3. Is the operator's responsibility for "same work" (8.3) acceptable, or is an automatic check on
   top wanted (for example a controlled work category)?
