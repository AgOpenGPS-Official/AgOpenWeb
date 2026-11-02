# Parity checklists

AgOpenWeb started as a port of AgOpenGPS. These checklists are how we find out, area by area,
where it still behaves differently, before a stable release. Each file is one area with one
owner.

| Area | File | Owner |
|---|---|---|
| Tracks: creating and managing | [01-tracks.md](01-tracks.md) | _ |
| Guidance on the line | [02-guidance.md](02-guidance.md) | _ |
| U-turns | [03-u-turns.md](03-u-turns.md) | _ |
| Sections and coverage | [04-sections-coverage.md](04-sections-coverage.md) | _ |
| Boundary and headland | [05-boundary-headland.md](05-boundary-headland.md) | _ |
| Tram lines | [06-tram.md](06-tram.md) | _ |
| Fields and jobs | [07-fields-jobs.md](07-fields-jobs.md) | _ |
| NTRIP | [08-ntrip.md](08-ntrip.md) | _ |
| Modules and PGNs | [09-modules-pgns.md](09-modules-pgns.md) | _ |
| AutoSteer settings and the steer wizard | [10-autosteer-wizard.md](10-autosteer-wizard.md) | _ |
| Vehicle and tool configuration | [11-vehicle-tool.md](11-vehicle-tool.md) | _ |
| Display, camera and simulator | [12-display-camera-sim.md](12-display-camera-sim.md) | _ |
| Heads and installs | [13-heads-installs.md](13-heads-installs.md) | _ (one per platform) |

## How to run a checklist

1. **Side by side.** Open AgOpenGPS (the release you run in the field) next to AgOpenWeb (the
   latest [beta](https://github.com/AgOpenGPS-Official/AgOpenWeb/releases/tag/beta)). Use the
   same vehicle, tool and field in both: import the AgOpenGPS field into AgOpenWeb (it does
   this once when you open the folder) and copy the settings across by hand.
2. **Simulator first.** Run each item in both simulators. Fill in the **Sim** column.
3. **Then hardware.** Bench first, then the cab, with real modules. Fill in the **HW** column.
   A headless or desktop AgOpenWeb on the module network talks to the real boards: settings it
   sends are saved by the board.
4. **Mark each cell** `✓` (same as AgOpenGPS), `✗ #123` (different, with the issue), or
   `n/a` (the item doesn't apply to your setup, with a word on why).

## When something differs

- File an issue from the app: main menu → **Bug Report Dump**, describe what AgOpenGPS does
  and what AgOpenWeb does, then **Send Report**. Put the checklist item in the title, for
  example `[03-u-turns 3.4] Swap arrow ignored on the second turn`.
- One difference per issue. A screenshot of each app helps.
- Some differences are on purpose: one-tap interactions where AgOpenGPS takes two, resuming
  where you left off, the field file formats, the observer rule for a second screen. If you
  are unsure, file it anyway and say so; a maintainer will answer and close it if it is meant.
- What AgOpenGPS does wrong (a clear bug) we don't copy. Say so in the issue.

## Sign-off

An area is **signed off** when every item has a `✓` or `n/a` in both columns, or a `✗` whose
issue is fixed or deliberately declined. The owner puts their name and the date at the bottom of
the file.

## Reference

The AgOpenGPS source we compare against in code is in `AgOpen_Snapshot/` (taken 2026-01-24).
Where an item names a file there, that is where AgOpenGPS's behaviour is defined.
