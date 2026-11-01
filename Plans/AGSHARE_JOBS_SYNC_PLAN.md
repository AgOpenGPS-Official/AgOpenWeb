# AgShare: send customer, farm and jobs; receive planned jobs

**Status:** **superseded by [ASL_MASTERPLAN.md](ASL_MASTERPLAN.md)** (§8 work orders and
executions, §9 sync). This plan assumed REST endpoints on the existing AgShare API; AgShareLive
(a new service, file-faithful, WebSocket) replaces that. Kept for reference until the master plan is
approved; then it moves to `Completed/`.
Original status: proposed 2026-11-01. Nothing is built, and the AgShare side is not agreed yet.
**Prompted by:** the request that the Customer / Farm data and all job information can go to
AgShare, and that jobs prepared in AgShare can later be sent to AgOpenWeb to work.
**Builds on:**
- [FIELD_SELECTION_AND_CLIENTS_PLAN.md](FIELD_SELECTION_AND_CLIENTS_PLAN.md): Customer / Farm on
  the field and on the job (phase 1 of that plan is a prerequisite).
- [JOB_FINISH_LOCK_IMPLEMENT_PLAN.md](JOB_FINISH_LOCK_IMPLEMENT_PLAN.md): implement snapshot,
  `history`, finished = locked.
- [Docs/FIELD_FILE_FORMATS.md](../Docs/FIELD_FILE_FORMATS.md) sections 5–7: job.json, coverage
  tiles, and the instructions for AgShare.

## Where we are

`Shared/AgOpenWeb.Services/AgShare/` talks to AgShare with an API key (`Authorization: ApiKey …`):
`PUT /api/fields/{guid}`, `GET /api/fields`, `GET /api/fields/{guid}`, `GET /api/fields/public`.
The payload is the old AgOpenGPS shape: name, origin, convergence, outer boundary, holes, AB
lines and curves. A field is tied to its AgShare GUID by `agshare.txt`. **No** headland, flags,
jobs, coverage, owner or planned work travels in either direction. `FIELD_FILE_FORMATS.md`
already says the client will be extended "once AgShare's API for it exists".

## Goals

1. **Up:** a field's Customer / Farm, and every job on it (`job.json` with implement snapshot and
   history, optionally the coverage tiles), reach AgShare.
2. **Down:** jobs created in AgShare for a field appear in AgOpenWeb as **Planned** jobs, ready
   to start, with their field, customer, farm, work type and notes filled in.
3. Both work offline-first: nothing is lost when there is no network; it goes when there is.

Not goals here: AgShare's own UI, billing or reporting, products and rates (see the Finish/Lock
plan's out-of-scope), fleet or multi-user task assignment.

## Agree first (before any code)

Per `FIELD_FILE_FORMATS.md` section 7, AgShare's API and storage are the AgShare developer's to
change. Step 0 of this plan is therefore a short contract, in `Docs/AGSHARE_SYNC_API.md`, agreed
with that developer, covering the endpoints and payloads below. Everything under "Proposed
contract" is a proposal for that conversation, not a decision.

## Decisions to approve (AgOpenWeb side)

1. **Owner data travels embedded first.** Field and job payloads carry `customer` and `farm`
   objects (id + the ISOXML-shaped values) instead of AgShare getting separate customer/farm
   endpoints. No new resources to start; AgShare can list owners from the fields it holds. Own
   endpoints can follow if AgShare wants to manage them.
2. **Jobs are keyed by the `id` GUID already in `job.json`.** AgShare keeps it; a job planned in
   AgShare is created there with a GUID and keeps it when it reaches AgOpenWeb.
3. **Who owns what.** Definition of a job (task name, work type, notes, customer, farm, implement
   hint, planned date) is AgShare's until the job is started. Execution data (status after
   Planned, start/end times, history, totals, coverage) is AgOpenWeb's. A finished (locked) job
   is never overwritten by a download.
4. **Coverage upload is opt-in.** Settings: *Send job details* (on by
   default once AgShare is enabled), *Send coverage* (off by default; tiles are large). Coverage
   goes only for finished jobs, so a half-painted job is never published.
5. **Private by construction.** Customer names, addresses, phone and email are personal data.
   They are never sent for a field marked public in AgShare (`isPublic`), and a public field's
   jobs are not sent either. The upload dialog says what goes. Needs the AgShare developer's
   confirmation that private fields are not visible to other accounts.
6. **A sync outbox, not a button only.** Uploads are queued on the job's events (finish, edit
   owner, unlock) and sent when `INetworkWatcher` says the network is there, the same way the
   bug report outbox works. The manual *AgShare Upload* stays.

## Proposed contract (for the AgShare developer)

All under the existing API-key auth. JSON, camelCase, invariant numbers, as the rest of the API.

| Direction | Call | Body / result |
|---|---|---|
| Up | `PUT /api/fields/{fieldId}` | Existing payload plus `customer`, `farm`, and the optional parts of `FIELD_FILE_FORMATS.md` once AgShare stores them |
| Up | `PUT /api/fields/{fieldId}/jobs/{jobId}` | The `job.json` content (incl. `implement`, `history`, `customer`, `farm`, totals) |
| Up | `PUT /api/fields/{fieldId}/jobs/{jobId}/coverage/{tile}` | One tile file as stored (section 5), with its CRC; `GET …/coverage` lists the tiles and CRCs so only changed tiles are sent |
| Down | `GET /api/jobs?status=planned&since=<time>` | Planned jobs for the account, each with `fieldId`, `jobId`, definition, `customer`, `farm`, `updatedAt` |
| Down | `GET /api/fields/{fieldId}/jobs` | All jobs of a field (for a full sync and for jobs finished on another device) |
| Status back | `PATCH /api/jobs/{jobId}` | `{ status, startedAt, endedAt }` so AgShare shows a planned job as started or done |

`customer`: `{ id, lastName, firstName, street, postalCode, city, country, phone, email }`.
`farm`: `{ id, designator, street, postalCode, city, country, customerId }`. The built-in
*No Customer* / *No Farm* are sent as absent, not as objects.

## AgOpenWeb changes

### Job model (`job.json`, `JobJsonService`)

- New status **`Planned`** (before `InProgress`). Job lists show it; Resume on a Planned job
  starts it: status `InProgress`, `startedAt` set, a `started` entry in `history`, and the tool
  profile named in the `implement` hint is offered (never loaded silently).
- New properties: `agShareJobId` (same as `id`, kept for clarity in the spec only if the
  maintainers prefer a separate key), `source` (`"local"` or `"agshare"`), `plannedFor` (date,
  optional), `syncedAt`, and `syncState` (`"synced"`, `"pending"`).
- `schemaVersion` stays 1. Jobs without these read as `source: "local"`, never synced.
- `Docs/FIELD_FILE_FORMATS.md` section 4 gets the new properties and status in the same PR.

### Services (`Shared/AgOpenWeb.Services/AgShare/`)

- `AgShareClient`: the calls above. Keep one place that builds the payloads from the job and
  field services; nothing in the ViewModel builds JSON.
- `AgShareJobSyncService` (new, behind an interface): `QueueJob`, `UploadJobAsync`,
  `UploadCoverageAsync` (diff by tile CRC), `PullPlannedJobsAsync`, and the outbox
  (`agshare-outbox` folder, one small file per pending item, retried on `INetworkWatcher`).
- `PullPlannedJobsAsync`: for each planned job, find the local field by `agshare.txt`. If the
  field is not on this machine, download it first (existing `AgShareDownloaderService`), then
  create `jobs/<task>/job.json` with status `Planned`. A job whose `jobId` already exists locally
  is updated only while still `Planned`. Task-name collisions get a suffix.
- On a customer/farm that is not in the local registry, add it (same rule as opening a copied
  field in the Clients plan); never touch the built-ins.

### Conflict rules

| Case | Result |
|---|---|
| Planned in AgShare, edited there before start | Local Planned job is updated |
| Planned job already started locally | AgShare changes to its definition are ignored; local wins |
| Finished local job, AgShare has an older copy | Local is uploaded over it (locked jobs are authoritative) |
| Same job changed on two devices | Higher `history` length wins; on a tie the later `updatedAt`; the loser is kept as `<task> (conflict)` and a toast says so |
| Job deleted locally | Not deleted in AgShare. A deletion sync is a later question |
| Job deleted in AgShare | Local job stays; if still Planned it is marked abandoned, not removed |

### Web client and wiring

- **AgShare menu / Settings:** *Send job details*, *Send coverage*, *Fetch planned jobs
  automatically*, and a last-sync line with an error text. Existing `AgShareDto` frame carries
  the status; `config.set|conn.agShare…` keys for the toggles.
- **Jobs screen** (from the Field Selection plan): a sync badge per job (synced / waiting /
  failed), a **Planned** group at the top, and a *Fetch planned jobs* button. **Field
  Selection** shows a small count of planned jobs per field.
- New commands (need control): `agshare.syncJobs`, `agshare.pullPlanned`, `agshare.retry|jobId`.
  Status goes out on the existing AgShare frame.
- Planned jobs also appear in the cross-field **Resume Job** list, so a contractor can start
  today's work from one list.

### Tests

- `Services.Tests` with a fake `HttpMessageHandler` (the existing client is concrete; add a seam
  rather than a real server): payload content for field + job, tile diff by CRC, outbox retry
  and ordering, public-field exclusion, pull creates `Planned` job and downloads a missing field,
  local-started job not overwritten, locked job not overwritten, conflict copy, built-ins sent as
  absent.
- Round trip with the format doc: a job file produced here parses against the spec fields.

## Order of work

| Step | Content |
|---|---|
| 0 | Agree the contract with the AgShare developer; write `Docs/AGSHARE_SYNC_API.md` |
| 1 | Job model: `Planned`, new properties, spec update (needs Clients plan phase 1 and the Finish/Lock plan) |
| 2 | Upload: field owner + job details, outbox, status in the AgShare frame |
| 3 | Upload: coverage tiles (diff by CRC) |
| 4 | Download: planned jobs, conflict rules, Planned in the job lists |
| 5 | UI: badges, settings, fetch button (together with, or just after, the Field Selection screens) |

Steps 2–4 can ship as separate PRs; each is usable alone. Nothing here changes the app for a user
who has AgShare off.

## Risks

- **AgShare is someone else's service.** Until step 0 is agreed the plan is only a request. The
  AgOpenWeb side can still be built against the fake handler, so the app is not blocked.
- **Coverage size.** A season of jobs on many fields is large. Tile diffs by CRC, finished jobs
  only, and the off-by-default setting keep it bounded; measure on a real field before enabling by
  default.
- **Personal data.** See decision 5; also add a line to `Docs/SECURITY_THREAT_MODEL.md` for the
  new data leaving the device.
- **Clock and identity.** Conflict handling relies on `history`/`updatedAt`; device clocks in the
  cab can be wrong. Prefer `history` length first, as above.

## Open questions

1. Embedded owner objects (proposed) or AgShare-managed customer/farm lists from the start?
2. Should AgShare be able to assign a planned job to a specific vehicle or driver now, or is the
   `implement` hint enough? (Proposed: hint only.)
3. Is deleting a job in AgShare meant to remove it on devices? (Proposed: no, see conflict table.)
4. Does the AgShare developer want planned jobs as plain jobs, or as ISOXML `TSK` that AgOpenWeb
   imports through the existing ISOXML path? (Proposed: plain jobs; `TSK` stays the later export.)
5. Coverage: send as tiles (proposed, byte-identical, decodable by AgShare per section 5) or as a
   rendered GeoJSON polygon for display only?
