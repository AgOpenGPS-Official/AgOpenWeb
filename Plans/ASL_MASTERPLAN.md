# AgShareLive (ASL): an add-on to AgOpenWeb

**Status:** revised 2026-11-02 after the maintainers' review of the first version, and again after
their answer on the risks of full separation (§7); for approval. Nothing is built. Next: a short
spike on the connector side (§10, phase 0), then the standalone AOW changes of §7 one PR at a time.

**The rule this plan is built around.** AgShareLive is an **add-on** to AgOpenWeb (AOW), not part of
it. AOW must work fully for a user who never uses ASL, and nothing in a service they do not use may
be able to cause them a problem. If the add-on is removed, AOW still builds and runs.

**Scope of this document.** ASL is a separate, closed-source, free-to-use cloud service (private
repository, owned by AgOpenGPS-Official). This repository is public, so this plan describes what the
AOW maintainers need: what the add-on does, how it attaches to AOW, what changes in AOW (almost
nothing), and the open decisions. Server internals live in the private ASL repository.

Replaces the first version of this plan. The Field Selection / customer–farm plan is a separate
plan and does not depend on ASL; the REST job-sync plan is dropped.

## 1. What ASL is

The cloud side of AOW, for people who want it: organisations with members and roles; a web app for
fields, work orders and jobs; a live fleet map; watching a driver's screen (read only, with
consent); several machines on one field; public fields; a library of vehicle and implement
profiles; fleet management (products and application records, geofences, maintenance, planning).
All of that is delivered by the **ASL connector** (a separate program on the farmer's side) and the
cloud service. **None of it is in AOW.**

## 2. The five requirements and how the design meets them

| # | Requirement | How it is met | How it is kept true |
|---|---|---|---|
| 1 | **Off by default; nothing runs until paired.** Unpairing removes what pairing added. | The connector is a separate program that the user installs. AOW has no code that starts, finds or loads it, so with no connector installed there is nothing to run. Until it is paired, the connector itself opens no connection to the cloud and runs no timers. Unpairing removes what pairing added (§4). | A test that AOW starts and runs with the connector absent; the connector's own tests that an unpaired connector makes no network call. |
| 2 | **A clean code divide.** Own project, better its own process; core has no reference to it. | The connector is its **own process beside AOW**, as Rate Control and the VT gateway are, in the private ASL repository. Core AOW (MainViewModel, the GPS pipeline, field and job services, the file formats) is not touched and references nothing of ASL. | A CI check in AOW that fails if any project, package or source file under `Shared/` or `Platforms/` refers to ASL or the connector (§8). |
| 3 | **Public interfaces only.** | Files in the field folders; an ordinary observer WebSocket client; a web card (§3). ASL needs no change to `ControlAuthority`, `WebSocketHub` or `transport.js`; the separate AOW PRs of §7 are useful on their own. | The connector has no build reference to AOW internals beyond the published wire contract (§3.2); where something cannot be done through the public interfaces, §6 names it and says why. |
| 4 | **Failures stay in the add-on.** | AOW never calls the connector and never waits for it. A cloud outage, a slow link or a bad reply happens in the connector's process and shows in its own status. The connector is one more observer client and a file reader. | The hot-path budgets with a connector paired and attached (§8); a stalled observer test; errors only in the connector's card and log. |
| 5 | **No AOW format changes for the cloud's sake.** | Anything ASL needs beyond today's files goes in **its own sidecar file** in the field folder, which AOW ignores (§5). | A rule in the plan and in review: a format change must be useful to AOW without ASL, and is proposed on its own. |

## 3. How the connector attaches to AOW

The connector uses only what AOW already exposes.

### 3.1 Files: fields and jobs

- Fields and jobs are the **field folders on disk**, as `Docs/FIELD_FILE_FORMATS.md` describes them
  (`field.geojson`, sidecars, `jobs/<task>/job.json`, coverage tiles). The connector finds the data
  root the way AOW does (`AGOPENWEB_DATA`, else the default folder) and reads the folders.
- **Reading** is always safe: AOW writes files through a temp file and rename, as the format
  document says, so a reader never sees half a file.
- **Writing** (a field imported from the cloud, or a new version made in the web app): **the
  connector never writes AOW's own files.** It puts the new version in the field's **incoming
  folder** (the staging folder agreed with the maintainers, §7). When the field is opened, AOW checks
  what is there, keeps the old file and asks the operator. So there is no race with an open field, and
  the files AOW steers on only change with the operator's OK. A brand-new field is staged the same
  way. The connector writes by temp file and rename and only what the format document defines. Any
  outside tool can use the same path.
- **Planned jobs** arrive as a `job.json` with status *Planned* in the **job inbox** (§7); AOW lists
  them in the job list. The connector adds nothing else to the job.
- Anything that is the connector's own (links to cloud ids, last synced hashes, held-back versions)
  is in its own sidecar (§5).

### 3.2 Live data and watching: an ordinary observer client

- The connector opens an ordinary WebSocket client to AOW's existing endpoint on the same machine
  and **never takes control**: it sends no control claim and no command. A client without control
  is an observer and **cannot change anything** (#397: every command except the few `ObserverIds`
  is dropped), so a bug or a compromised cloud service in this path cannot steer, switch sections or
  change a setting.
- It receives the frames any browser receives (tick, scene, config, status, coverage) and uses them
  for the live fleet status and for watching.
- **Protocol version.** The connector reads the version in the handshake (§7) and decodes only a
  version it knows. On any other it shows "live view unavailable" in its own status; it never
  reports an error into AOW.
- **Watching a driver's screen.** The connector relays the frames, one way, to the cloud. The
  watcher's browser runs the real AOW web client, served by ASL from a copy of the client files that
  the connector took from the host's own HTTP endpoints for that AOW version. Each watching session
  gets **its own origin** (a per-session host name), so the client's root-relative asset paths and
  its `/ws` connection work **unmodified**; that is how `transport.js` stays untouched. Whatever the
  watcher does in the page is dropped by the relay: nothing flows back to AOW.
- **Consent and the cab indicator belong to AOW, not to a card** (§7). The connector opens **one
  observer connection per watching session**, so the host counts each watcher; AOW shows "N watching"
  in its status bar and has a *remote watching allowed* setting that the operator controls. With the
  setting off the connector gets no live feed for watching. Whether a card is open or visible no
  longer matters. ASL's own per-driver choice (*ask every time* / *always allow members of this
  organisation*) stays on top, in the card, as a further restriction, never as a replacement for AOW's
  indicator. How a remote observer is told apart from a LAN one (a label sent at connect) is part of
  that AOW change.
- The relay is rate limited and bounded; it drops frames rather than queueing them when the link is
  slow.

### 3.3 UI: a web card

- Everything the operator needs is a **web card** (`Plans/ADDON_WEB_CARDS_PLAN.md`): a page served
  by the connector (pairing and unpairing, status, consent, who is watching, planned orders, the
  group map of other machines). The user adds it with the existing bookmark list. AOW does not
  proxy it and does not know what it is.
- As for every web card, a browser **without control** sees it covered. Pairing, consent and
  settings therefore need the operator who holds control.
- **The card pane as a drawer** (`Plans/CARD_PANE_SPLIT_PLAN.md`, #513 and the drawer step): Closed
  (map full), Split (⅓ to ½) or Full; in Full a guidance strip stays visible and alarms show over
  everything; only the operator moves it, and above walking pace it cannot be pulled to Full. That
  gives a planning page all the room it needs and keeps ASL a plain web card. A Closed drawer hides
  the card without unloading it; switching between Floating and Split reloads the page once, so
  the page restores its state from the connector. Nothing safety-relevant depends on the card being
  visible (the indicator is AOW's, above).
- **No keyboard inside the card.** AOW's on-screen keyboard (`js/osk.js`) belongs to AOW's own page
  and does not reach into a card's iframe, which is on its own origin. On a kiosk box with no OS
  keyboard nothing can be typed in the card. Pairing is therefore a **device code**: the card
  shows a code and a QR/URL, and the sign-in happens on the user's phone or PC. No text field is
  needed in the card for pairing or consent.
- **Tests and CI.** The web client is now many classic scripts sharing one scope
  (`Docs/WEB_CLIENT.md`). The connector never loads or patches them: the copy it serves to
  watchers is the files as the host serves them (`index.html` plus `js/`, `css/`, `transport.js`),
  taken per AOW version, and the connector's version check refuses an AOW version it has not
  tested. A client that changes its file layout is no problem as long as `index.html` is the entry
  point.

### 3.4 Where the connector runs

| Platform | Connector |
|---|---|
| Windows, macOS, Linux (including the HAT) | A separate program or service beside AOW, like `aow-linkd`; talks to AOW on the same machine |
| Android | A separate app (foreground service) on the same device; AOW is reachable on localhost |
| iOS | Left out to start (§6). Later, the same connector code can run inside the iOS app over the same local interfaces |

## 4. Off by default, and what pairing adds

- AOW ships with no ASL code and no ASL setting. A user who never installs the connector gets no
  socket, timer, background task or startup work from ASL.
- An installed connector that is **not paired** is idle: no connection to the cloud, no timers, no
  observer connection to AOW.
- Pairing (in the connector's web card) adds only the following, and **Unpair** removes all of it:

| Added by pairing | Where | Removed by unpairing |
|---|---|---|
| The identity file (machine id, tokens, device key; encrypted, bound to the device) | The connector's own data folder | Deleted |
| The connector's sidecar files | In each field folder it has touched | Deleted (§5) |
| Its queue and cache | The connector's own data folder | Deleted |
| A bookmark for its web card | AOW's web card list (added by the user through AOW's own bookmark editor) | The user removes it; the card shows how |

The connector never writes AOW's settings. Unpairing never touches a field's own files, jobs or
coverage.

## 5. Sidecars: ASL's own files in the field folder

ASL keeps what it needs beyond AOW's files in a file of its own in the field folder, which AOW
ignores: for example `asl.json` (the cloud id of the field and its jobs, the last synced version and
hash, the link from a job to a cloud order, held-back incoming versions). AOW's files are never
edited to carry ASL data.

Two things the first version of this plan wanted as AOW format changes are **moved out of AOW**:
- the RTK correction source and reference frame of a guidance line, and a field-wide correction
  offset: ASL keeps them in its sidecar. If AOW finds them useful on its own (lines moving between
  networks and years), that is a separate AOW proposal on its own merits;
- parcel identity and crop per season: ASL keeps them in its sidecar and in the cloud. The customer
  and farm that AOW needs for ISOXML are in the separate Field Selection plan.

## 6. What cannot be done, and what the maintainers decided

Named, with the reason, as the review asked. The first version of this list was sent back as risks of
full separation; the answer on each is recorded here.

| # | Risk of full separation | Decision |
|---|---|---|
| 1 | **Two machines on one field.** A second machine's coverage cannot be on AOW's main map or in section control. | A **reference coverage layer** in AOW (§7): tiles from another job or folder, read only, in its own colour. Useful without ASL (last season's pass). **Section control from it is not done now**: a stale or wrong layer leaves strips unsprayed. If it is ever done, the operator switches it on per job, it is visible on screen, and it is ignored when it stops updating. Until then the group view (positions, the other machines' coverage) is in the connector's card. |
| 2 | **Planned jobs.** No *Planned* status; the operator would retype from a card. | A **job inbox** (§7): any tool drops a `job.json` with status *Planned* in a folder, and AOW lists it in the job list. Generic, optional fields in `FIELD_FILE_FORMATS.md`; fits Field Selection (#520). |
| 3 | **Watching and the cab indicator** depended on a web card being open. | Moved into AOW: "N watching" in the status bar and a *remote watching allowed* setting (§7). No card needed. |
| 4 | **Wire format** is undocumented and unversioned. | A **protocol version in the handshake**, bumped whenever the frames change, plus a **written description of the frames**, kept in step like the file-format spec (§7). No second feed. The connector refuses a version it does not know. AOW publishes no field-format library; the spec is there to build one on the ASL side. |
| 5 | **Writes** race with an opening field, and a bad cloud version could reach a field AOW steers on. | A **staging folder** per field (§3.1, §7): the add-on never writes AOW's own files, and AOW checks what is staged when the field opens, keeps the old file and asks the operator. |
| 6 | **iOS** has no second process. | **Left out to start.** If needed later, the same connector code can run inside the iOS app and talk to AOW over the same local interfaces; the boundary stays the same, only the hosting differs. |

Not now: section control from another machine's coverage, a field-format library, a generic add-on
banner API.

## 7. Changes in AOW: each its own PR, each useful without ASL

The ASL work needs no change to core AOW. The following are agreed as separate pull requests, each of
which has to make sense without ASL and can be reviewed alone. They are taken **one at a time**,
after the spike (§10, phase 0).

| PR | Change | Useful without ASL because |
|---|---|---|
| A | **Protocol version in the handshake**, bumped when the frames change, and the **wire docs** (a spec of the frames kept in step with `WireCodec`/`transport.js`, like `FIELD_FILE_FORMATS.md`) | A cached or mismatched web client against a newer host is a problem today; every add-on that reads frames needs it |
| B | **"N watching" in the status bar** and a **remote watching allowed** setting; a remote client says so at connect (a label) | LAN viewers (a second tablet, an adviser) are watchers too; the driver sees them without any card |
| C | **Reference coverage layer**: tiles from another job or folder, display only, own colour | Last season's pass; the sprayer seen from the spreader |
| D | **Job inbox and field staging folder**: a `job.json` with status *Planned* is listed in the job list; new versions of a field's files put in its incoming folder are checked on open and applied only with the operator's OK, the old file kept. Fields generic and optional in `FIELD_FILE_FORMATS.md`; after Field Selection (#520) | Planned work from any tool; any outside tool can deliver a field safely |

Also useful and still open for the maintainers, independent of ASL: a page for add-on authors in
`Docs/` (step 3 of `ADDON_WEB_CARDS_PLAN.md`); the hot-path budget test and a stalled-observer test
with an observer attached (§8); a CI check that core references no add-on (§8); publishing AOW's
host, port and data root so add-ons need not guess.

## 8. Keeping it true: checks

- **No reference from core.** A CI check in AOW fails if any project, package or source file under
  `Shared/` or `Platforms/` refers to ASL or the connector. (The connector is not in this
  repository; the check keeps it that way.)
- **Runs without it.** The existing tests and the `--headless` run need no connector.
- **Budgets unchanged.** The hot-path budget tests (`Tests/AgOpenWeb.Services.Tests/Performance`)
  run also with an observer client attached and a file watcher reading the data root; the counts do
  not change.
- **Observer cost.** The cost of an extra observer at 10 Hz is measured in the spike (§10) and kept as a test.
- **Stalled observer.** A test that a connected observer that stops reading does not slow the host.
- **The add-on's side** (in the private repository): an unpaired connector makes no network call and
  opens no socket; unpairing removes everything in §4; a cloud outage and a malformed reply produce
  only entries in the connector's status; the connector never sends a command or a control claim.

## 9. Open questions for the maintainers

- [ ] **Remote label (PR B):** how a remote watcher says so at connect, and whether *remote watching
  allowed* off refuses the connection or only the label.
- [ ] **Staging folder name and rules (PR D):** the name, what is checked on open, and what the
  operator is asked.
- [ ] **Job inbox location (PR D):** inside each field folder, or one folder for the data root?
- [ ] **Packaging:** the connector as its own download and service; does AOW's deployment (installers,
  the HAT image) want to list it, or stay out of it entirely?
- [ ] **Old AgShare client in AOW:** this plan does not touch it. Removing it later is a decision for
  the maintainers, not part of ASL.
- [ ] **Web card limits:** is a page that needs the operator's control (pairing) acceptable, as for
  other cards? (Pairing is a device code; no typing in the card, and nothing safety-relevant depends
  on the card being visible, §3.3.)

## 10. Phases (the connector and the cloud)

All of this is outside this repository; the AOW side is only §7. Each phase starts when the test of
the previous one has passed.

| Phase | Content | Test |
|---|---|---|
| 0 | **Spike** (a throwaway connector, no cloud): (a) what an **extra observer costs the host at 10 Hz**, with the hot-path budgets and CPU/memory with 1, 5 and 20 observers (also one per watcher, §3.2); (b) the **feed is complete** enough for the fleet status and for a watching copy; (c) detecting a **closed field** from the feed; (d) the **write race**, to check the staging approach (§3.1) rather than writing into open fields | A short report with numbers; decides what PRs A-D must contain |
| 1 | The connector: pairing (device code), the identity file, upload of one field folder (read only), the web card | A field folder goes to ASL and back, files byte for byte |
| 2 | Moving the account and importing from the existing AgShare; the connector stages incoming fields and planned jobs (PR D) | All fields of a test account arrive; the operator confirms on open |
| 3 | Status and the fleet map (the observer client) | An unpaired connector is silent; the fleet shows the last state |
| 4 | Watching (relay, per-session origin, one observer per watcher, AOW's indicator, PR B) | A watcher can never change anything; the driver sees "N watching" |
| 5 onward | Master data, orders as planned jobs, the group view with the reference layer (PR C), field editing, products and application records, fleet management, retention (see the private plan) | Per phase in the private plan |

Retention in the cloud is five years with advance notices, an archive and an export made by the
user. Organisations with roles and rights, platform administration and entitlements (Live Mode
starts as a trial for everyone) are on the service side.
