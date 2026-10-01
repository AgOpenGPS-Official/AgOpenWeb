# Jobs: implement link, Finish on close, locked jobs

**Status:** proposed 2026-10-01.
**Prompted by:** the AgShare developer's request that coverage link to a job, and the job to a
field and an implement, with finished jobs archived (likely for European chemical-application
record keeping). Format consumers: [Docs/AGSHARE_FILE_FORMATS.md](../Docs/AGSHARE_FILE_FORMATS.md).
**Builds on:** [Completed/FIELDS_AND_JOBS_PLAN.md](Completed/FIELDS_AND_JOBS_PLAN.md); this
plan replaces its Decision #3 (resuming a `Done` job silently flips it back to in progress).

## Decisions

1. **No wizard.** The Open Field / Job screen stays a single screen. Wizards are limiting and
   hard to move back and forth in. The new pieces slot into the existing form and close flow.
2. **The implement is recorded on the job** as a snapshot, not just a profile name: a profile
   can be edited or deleted later, and the job's coverage only makes sense with the implement it
   was painted with.
3. **Closing a field with an active job asks: Finish job, or Keep open.** Keep open is today's
   behaviour (resumable). Finish marks the job finished and locks it.
4. **A finished job is locked, and can be unlocked through a confirm dialog.** The lock guards
   against accidental changes. It is not tamper-proofing: without cryptographic signing any file
   can be edited, and that is out of scope. Unlocking is recorded in `job.json`.
5. **Job type stays free text with suggestions**, unless AgShare needs a fixed list for filtering.
   Then: a fixed list plus "Other", with free text under "Other".

## Behaviour

### Starting a job
- The New Job form gains an **Implement** picker listing tool profiles, defaulting to the one
  loaded. Picking a different one loads it, as the profile picker does today.
- Starting the job writes the implement snapshot to `job.json`.
- Resuming an open job whose implement differs from the loaded tool profile asks whether to load
  the job's implement. Coverage keeps painting with the loaded tool either way.

### Closing
- Close Field with an active job shows: **Finish job** · **Keep open** · **Cancel**.
- Finish sets `status: "Done"`, `endedAt`, the totals (area, distance, U-turns), appends a
  `finished` entry to `history`, saves coverage, and locks the job.
- The existing unsaved-coverage guard (coverage painted with no job) is unchanged.

### Locked (finished) jobs
- The job lists (Open Field / Job, Resume Job) get a **status column**: a tick for finished,
  blank for open.
- Opening a finished job opens the field with its coverage shown **read-only**: no painting,
  no coverage saves, no `job.json` writes other than `lastOpenedAt`. A banner says the job is
  finished, with an **Unlock** button.
- **Unlock** asks for confirmation ("This job was finished on <date>. Unlock it to make
  changes?"). Confirming sets `status: "InProgress"`, clears `endedAt`, appends an `unlocked`
  entry to `history`, and resumes the job normally.
- Delete Job on a finished job asks for confirmation as well.

## job.json changes

`schemaVersion` stays 1 (no installed base). New fields:

```json
{
  "implement": {
    "profileName": "12m sprayer",
    "toolType": "Rear Fixed",
    "width": 12.0,
    "sectionWidths": [3.0, 3.0, 3.0, 3.0],
    "isMultiColoredSections": false,
    "sectionColors": [16711680, 65280, 255, 16776960],
    "singleCoverageColor": 3394611
  },
  "vehicleProfileName": "JD 6120M",
  "history": [
    { "action": "started",  "at": "2026-10-01T08:12:00-05:00" },
    { "action": "finished", "at": "2026-10-01T11:40:00-05:00" },
    { "action": "unlocked", "at": "2026-10-02T07:55:00-05:00" }
  ]
}
```

- `implement`: copied from `ConfigStore.Tool` and `ActiveToolProfileName` when the job starts,
  and refreshed while the job is open if the tool profile changes. Colours are RGB888 integers,
  as in the tool profile. `toolType` is `ToolConfig.CurrentToolType` (`Front Fixed`, `Rear Fixed`, `TBT`, `Trailing`).
- `vehicleProfileName`: informational.
- `history`: append-only list of `started`, `resumed`, `finished`, `unlocked`, each with an
  ISO 8601 time. `status` stays the source of truth for whether the job is locked.
- Jobs without these fields (created before this change) read as no implement and empty history.

## Work

1. `Job` / `JobJsonService`: `Implement` snapshot, `VehicleProfileName`, `History`; round-trip tests.
2. `JobService`: `FinishCurrentJob()` (replaces the unused `CloseCurrentJob(Done)`),
   `UnlockJob(field, task)`; `ResumeJob` refuses a `Done` job (callers unlock first).
3. Read-only mode in `MainViewModel`: a finished job blocks coverage painting and saves the
   same way a field-only open does today.
4. Web UI (`index.html` + wiring): Implement picker on the New Job form; Finish / Keep open /
   Cancel dialog on Close Field; status tick column in both job lists; finished-job banner with
   Unlock and its confirm dialog.
5. AgShare doc: move the `job.json` fields above from "planned" to the spec once merged.
6. Verify in the headless app: start a job with an implement, Finish, reopen (read-only, banner),
   Unlock, paint, Keep open, resume.

## Out of scope

- Cryptographic signing or any other tamper-proofing.
- Product, rate and weather records for chemical applications. Worth asking the AgShare developer
  whether these are what "archive" is ultimately for.
- Sending jobs and coverage to AgShare (waits on AgShare's API for full fields).
