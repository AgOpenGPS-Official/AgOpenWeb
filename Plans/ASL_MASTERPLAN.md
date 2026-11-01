# AgShareLive (ASL): an add-on to AgOpenWeb

**Status:** revised 2026-11-02 after the maintainers' review of the first version; for approval.
Nothing is built.

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
| 3 | **Public interfaces only.** | Files in the field folders; an ordinary observer WebSocket client; a web card (§3). No change to `ControlAuthority`, `WebSocketHub` or `transport.js`. | The connector has no build reference to AOW internals beyond the published wire contract (§3.2); where something cannot be done through the public interfaces, §6 names it and says why. |
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
- **Writing** (a field imported from the cloud, or a new version made in the web app) happens only
  to a field that is **not open**. The connector knows which field is open from the observer
  connection (§3.2) and never writes into an open field's folder; it holds the new version back and
  applies it once the field is closed. It writes by the same temp-file-and-rename rule and only the
  files the format document defines. AOW picks the change up the next time the field is opened, as it
  would any file on disk. AOW itself shows no "new version, load it?" prompt for this.
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
- **Watching a driver's screen.** The connector relays the frames, one way, to the cloud. The
  watcher's browser runs the real AOW web client, served by ASL from a copy of the client files that
  the connector took from the host's own HTTP endpoints for that AOW version. Each watching session
  gets **its own origin** (a per-session host name), so the client's root-relative asset paths and
  its `/ws` connection work **unmodified**; that is how `transport.js` stays untouched. Whatever the
  watcher does in the page is dropped by the relay: nothing flows back to AOW.
- **Consent and the cab indicator** are shown in the connector's web card (§3.3). If no card is open
  there is no one to ask, so watching is refused unless the driver has set *always allow members of
  this organisation* in the connector, in which case the indicator appears the next time a card is
  open.
- The relay is rate limited and bounded; it drops frames rather than queueing them when the link is
  slow.

### 3.3 UI: a web card

- Everything the operator needs is a **web card** (`Plans/ADDON_WEB_CARDS_PLAN.md`): a page served
  by the connector (pairing and unpairing, status, consent, who is watching, planned orders, the
  group map of other machines). The user adds it with the existing bookmark list. AOW does not
  proxy it and does not know what it is.
- As for every web card, a browser **without control** sees it covered. Pairing, consent and
  settings therefore need the operator who holds control.
- **The card pane and its drawer** (`Plans/CARD_PANE_SPLIT_PLAN.md`, #513 and the drawer step). In a
  split layout the card sits in a pane that can be **Closed**: it is hidden, not unloaded, so the
  connector page keeps running, but the driver cannot see it. A card that is hidden is not an
  indicator. The connector page therefore reports whether it is visible (page visibility), and
  watching is only allowed while it is visible, or while *always allow* is set and the driver has
  accepted that the indicator can be hidden. Switching between Floating and Split reloads the page
  once, so the page must restore its state from the connector, not keep it in the page.
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
| iOS | **No second process is possible.** Options for the maintainers in §9: the connector runs on another device on the machine network, or ASL is not offered on iOS |

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

## 6. What cannot be done through public interfaces

Named, with the reason, as the review asked.

1. **Starting a job from a cloud work order.** AOW has no *Planned* job status, and this plan does
   not add one. Orders are shown in the connector's web card with the field, work type, notes and a
   suggested task name; the operator starts the job in AOW's normal New Job form, and the connector
   links the resulting job to the order by the task name, in its sidecar. A small standalone AOW
   feature, "start a job from a template file", would make this smoother and is useful without ASL;
   it would be proposed on its own (§7), only if the maintainers want it.
2. **Other machines on AOW's main map, and section control counting their coverage.** Needs core
   changes, so it is **not** part of this plan. The group view (positions, coverage of the other
   machines) is in the connector's web card. Whether AOW could load another job's coverage tiles as
   a read-only layer on its own is a question for the maintainers (§9).
3. **Reading the frames safely over time.** The wire format is not a documented interface today. The
   connector ships a matching reader per AOW version and degrades to "live view unavailable" on a
   version it does not know; it never reports an error into AOW. A documented, versioned wire format
   would help every add-on and the web client (§7).
4. **iOS** has no second process (§3.4).
5. **Watching without an open card** cannot show the cab indicator (§3.2); the rule there is the
   safe default.

## 7. Changes in AOW: a list, each standalone

The ASL work needs **no change to core AOW**. Wherever a public interface could be better for
add-ons, that is proposed as its own change, with its own benefit and no dependence on ASL. These
are suggestions for the maintainers, any of which can be declined without affecting this plan:

| Proposal | Useful without ASL because |
|---|---|
| A page for add-on authors (`Docs/`): the field folders and the data root, the observer endpoint, web cards, what an add-on must not do. (This is step 3 of `ADDON_WEB_CARDS_PLAN.md`.) | The VT gateway, rate controllers and cameras are add-ons too |
| A version number for the wire format, in the handshake or the config frame, with a compatibility policy | A cached or mismatched web client against a newer host is a problem today |
| A test that the hot-path budgets hold with an observer attached and a file reader running | It guards against any slow or extra client |
| A check that a stalled or slow observer cannot affect the host | A slow browser is the same case |
| A CI check that nothing in `Shared/` or `Platforms/` references an add-on (§8) | It keeps the core independent of every add-on |
| Optional: start a job from a template file; publish AOW's host, port and data root so add-ons need not guess | Repeating jobs; easier add-on setup |

## 8. Keeping it true: checks

- **No reference from core.** A CI check in AOW fails if any project, package or source file under
  `Shared/` or `Platforms/` refers to ASL or the connector. (The connector is not in this
  repository; the check keeps it that way.)
- **Runs without it.** The existing tests and the `--headless` run need no connector.
- **Budgets unchanged.** The hot-path budget tests (`Tests/AgOpenWeb.Services.Tests/Performance`)
  run also with an observer client attached and a file watcher reading the data root; the counts do
  not change.
- **Stalled observer.** A test that a connected observer that stops reading does not slow the host.
- **The add-on's side** (in the private repository): an unpaired connector makes no network call and
  opens no socket; unpairing removes everything in §4; a cloud outage and a malformed reply produce
  only entries in the connector's status; the connector never sends a command or a control claim.

## 9. Open questions for the maintainers

- [ ] **Wire format:** a documented, versioned format with a compatibility policy (§7), or the
  connector pins to each AOW release?
- [ ] **Add-on author page** and the other proposals in §7: which do you want, and in which order?
- [ ] **iOS:** the connector on another device on the machine network, or no ASL on iOS?
- [ ] **Second job's coverage as a read-only layer:** could AOW load it without core changes (§6.2)?
- [ ] **Start a job from a template file** (§6.1): wanted as a standalone feature?
- [ ] **Packaging:** the connector as its own download and service; does AOW's deployment (installers,
  the HAT image) want to list it, or stay out of it entirely?
- [ ] **Old AgShare client in AOW:** this plan does not touch it. Removing it later is a decision for
  the maintainers, not part of ASL.
- [ ] **Web card limits:** is a page that needs the operator's control (pairing, consent) acceptable,
  as for other cards?
- [ ] **Hidden card:** with the drawer Closed the card is hidden but loaded. Can a card learn that it
  is hidden (page visibility), so it can refuse watching? Otherwise the generic add-on banner (§7)
  is the only reliable indicator.

## 10. Phases (the connector and the cloud)

All of this is outside this repository; the AOW side is only §7. Each phase starts when the test of
the previous one has passed.

| Phase | Content | Test |
|---|---|---|
| 0 | The connector: pairing, the identity file, upload of one field folder (read only), the web card | A field folder goes to ASL and back, files byte for byte |
| 1 | Moving the account and importing from the existing AgShare; the connector writes closed field folders | All fields of a test account arrive |
| 2 | Status and the fleet map (the observer client) | An unpaired connector is silent; the fleet shows the last state |
| 3 | Watching (relay, per-session origin, consent in the card) | A watcher can never change anything |
| 4 onward | Master data, orders in the card, the group view, field editing, products and application records, fleet management, retention (see the private plan) | Per phase in the private plan |

Retention in the cloud is five years with advance notices, an archive and an export made by the
user. Organisations with roles and rights, platform administration and entitlements (Live Mode
starts as a trial for everyone) are on the service side.
