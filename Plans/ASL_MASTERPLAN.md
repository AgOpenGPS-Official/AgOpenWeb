# AgShareLive (ASL) master plan: the AgOpenWeb side

**Status:** proposed 2026-11-01, for maintainer approval. Nothing is built.

**Scope of this document.** AgShareLive is a separate, **closed-source, free-to-use** cloud
service (private repository, owned by AgOpenGPS-Official). This repository is public, so this plan
contains what the AgOpenWeb maintainers need: what AOW does, the behaviour AOW relies on, the
changes to AOW, and the open decisions. Server internals (storage schema, limits, administration
and support tooling, hardening, deployment) live in the private ASL repository on purpose.

**Supersedes:** [AGSHARE_JOBS_SYNC_PLAN.md](AGSHARE_JOBS_SYNC_PLAN.md) (its content is §8–§9) and
brings [FIELD_SELECTION_AND_CLIENTS_PLAN.md](FIELD_SELECTION_AND_CLIENTS_PLAN.md) in line with §7.

## 1. Goal

AgShareLive (ASL) is the cloud side of AgOpenWeb (AOW), built for it from scratch:

- AOW signs in with OAuth; only then is a **machine ID** created for that terminal.
- Fields, jobs, coverage and profiles travel **file-faithfully** in both directions; nothing is lost.
- In the web app: field management, creating and following jobs, a live fleet map, **watching a
  driver's screen**, several machines on one field, public fields, and shared vehicle and
  implement profiles.
- AOW's current AgShare client is **replaced** by the ASL link.
- Existing AgShare users **move their account** and bring their fields over **once**.

Why not extend the existing AgShare: its field model carries only name, origin, outer boundary
with holes, and lines. Headland, flags, jobs, coverage, elevation log and settings do not fit, and
what comes back is less than what AOW sent.

## 2. Agreed starting points

| # | Starting point | Consequence |
|---|---|---|
| 1 | The **organisation** owns the data; users are members with a role. | A user with no colleagues never sees the organisation (§5). |
| 2 | **OAuth first, then the machine ID.** | No machine ID exists before pairing. |
| 3 | The **identity file on the client is encrypted** and bound to that device. | §5. |
| 4 | **File-faithful.** The file as AOW writes it is the source of truth. | New AOW properties need no server migration. |
| 5 | **One-time move** of account and fields from the existing AgShare. | §11. |
| 6 | **John Deere moves to ASL later**, with a new developer app on the JD side. | Core model stays independent of it (§13). |
| 7 | **Existing AgShare and its REST API stay** for AgOpenGPS and the JD link. | A one-way bridge keeps exports and JD working until JD has moved. |
| 8 | **AOW only.** AgOpenGPS WinForms stays on the old API. | |
| 9 | **Live Mode starts as Demo/Trial for everyone** and stays on until the feature is switched off for all; only then do we decide on paid. | Features check an entitlement per organisation (§5). |
| 10 | **Platform administrators** stand above the organisations, with full administration of the site but no general view of organisations' data. | Support runs through a user-initiated, code-based session (§5). |
| 11 | **New repository, from scratch**: `AgShareLive`, private, All Rights Reserved. | No code from earlier AgShare branches; the field editor is rebuilt. |
| 12 | **Viewers can watch a driver's screen live**, read only, with the driver's consent. | §9. |
| 13 | **Fields can be made public** by their owner; **vehicle and implement profiles can be shared.** | §10a, §10b. |

## 3. Sources and what happens to them

| Source | In this plan |
|---|---|
| AgShare Realtime plan (23 Oct) | **Adopted** (terminal WebSocket, demand-driven sync, watching), with the changes below |
| AgShare customers-and-farms design | **Adopted** (§7); the AgOpenGPS part drops out |
| AgShare mapping-data design (`FieldOperation`, coverage) | **Partly adopted**: definition vs operational data, external links. Replaced: patch blob (ASL stores AOW tiles) |
| AgShare field-editing plan | **Adopted** as a phase (§10), **rebuilt from scratch** |
| AOW `Docs/FIELD_FILE_FORMATS.md` | **The data contract** |
| AOW `JOB_FINISH_LOCK_IMPLEMENT_PLAN.md` | **Stays**; ASL receives it as `job.json` |

## 4. Architecture

```
AOW host (cab) ──── one outbound WSS ────►  AgShareLive (cloud)
  CloudLinkService                              │
Browser (ASL web app) ◄── watch, manage ────────┤
                                                └──► bridge (one way) ──► existing AgShare (JD, exports)
```

- **AgShareLive:** its own service, database and deployment. If it fails, the existing AgShare
  keeps running.
- **Accounts:** ASL has its own accounts and is its own OAuth server. A user of the existing
  AgShare moves their account (§11); nothing is shared at database level.
- **AOW host:** a `CloudLinkService` in `Shared/AgOpenWeb.Services` (connection, queue, interest,
  status), hooked onto `INetworkWatcher` like NTRIP and the bug report outbox.
  `Shared/AgOpenWeb.Services/AgShare/` goes away after the switch-over.
- **Bridge (one way, ASL → existing AgShare):** while JD and exports live in the existing AgShare,
  finished field versions are written there. A retry never makes a duplicate field.

## 5. Identity, organisations, roles, pairing

### Organisations, members, roles and rights

An organisation owns fields, customers, farms, work orders, jobs, profiles and terminals. Every
user who signs up gets a personal organisation of one (role *Owner*); the interface shows nothing
of it until they invite someone. A user can belong to several organisations, so a contractor can
work in a farmer's organisation without fields being copied. The OAuth token carries the
organisation; AOW and the web app offer a "work as…" choice.

| Role | For |
|---|---|
| **Owner** | Created the organisation: everything, including members and transfer |
| **Admin** | Everything except deleting or transferring the organisation |
| **Planner** | Office or manager: fields, master data, work orders, sees the fleet |
| **Driver** | Works on a terminal: runs work orders, pairs their own terminal, edits fields on their own terminal |
| **Viewer** | Read only (adviser, customer, accountant); may watch a driver's screen |

The five roles are built-in templates; Owner or Admin can make **custom roles** (a named set of
rights). The server checks rights, not role names.

| Right | Owner | Admin | Planner | Driver | Viewer |
|---|---|---|---|---|---|
| View fields | ✓ | ✓ | ✓ | ✓ | ✓ |
| Create and edit fields | ✓ | ✓ | ✓ | own terminal | – |
| Delete fields | ✓ | ✓ | – | – | – |
| Publish fields publicly | ✓ | ✓ | – | – | – |
| Manage customers and farms | ✓ | ✓ | ✓ | – | – |
| Create and change work orders | ✓ | ✓ | ✓ | – | – |
| Start and run work (jobs) | ✓ | ✓ | – | ✓ | – |
| View and export jobs | ✓ | ✓ | ✓ | own | ✓ |
| Unlock or delete a finished job | ✓ | ✓ | – | – | – |
| Manage profiles; publish them to the library | ✓ | ✓ | – | – | – |
| Pair a terminal | ✓ | ✓ | – | own | – |
| Revoke or rename terminals | ✓ | ✓ | – | – | – |
| See fleet map and live positions | ✓ | ✓ | ✓ | own and peers | – |
| Watch a driver's screen (live, read only, with consent) | ✓ | ✓ | ✓ | – | ✓ |
| Invite members, change roles | ✓ | ✓ (not Owner) | – | – | – |
| Settings | ✓ | ✓ | – | – | – |

- Rights are enforced on the server; AOW and the web app only hide what is not allowed.
- A terminal has its own limited scopes, independent of who paired it, and writes only into its own
  organisation.
- Drivers' location is personal data; who may see it is a right, and a driver can put a terminal in
  private mode (§9).

### Platform administration and entitlements

Platform administrators stand above the organisations, set up per user, starting with one. They
have full administration of the service but **no general view of organisations' data**. Support
runs through a **user-initiated session**: the user creates a support code in their own
environment, the administrator enters it in a support tool and sees what the user sees, for a
limited time, which the user can stop at any time. Everything is logged.

Each organisation has **entitlements** (feature flags). Live Mode and realtime sync (peer view,
fleet, remote watching) are **Demo/Trial and on for everyone**, and stay so until the feature is
switched off for all. The connection and screens ask the entitlement, so making it paid later is a
change of data, not of code. Sync, import and field management are outside the flag. AOW shows the
trial state.

### Pairing and the identity file

1. In AOW: *AgShare → Pair*; the host opens the ASL login in the system browser (OAuth with PKCE).
2. The user enters a short-lived pairing code from the web app.
3. ASL creates the **terminal** and its **machine ID** and returns a token bound to that terminal.
4. The terminal has a **name**, a **colour**, and the active vehicle and implement profile.

AOW keeps the machine ID, refresh token and a device key in a file only that client can read. The
client generates a device key pair at pairing and proves possession of the key when it asks for
tokens, so a copied file does not work on another machine. The file is encrypted with platform
secure storage: DPAPI (Windows), Keychain (macOS, iOS), Keystore (Android); on Linux it is a file
with restricted permissions, which is weaker (the docs say so). Revoking a terminal in the web app
closes its connection at once. Headless installs pair through a connected phone or laptop.

## 6. Data semantics

- Every file AOW writes goes to ASL **unchanged** (per file: content hash, version, base version).
  ASL also reads field and job out for its map, search and overviews; that read model can always be
  rebuilt from the files.
- Field, job, customer, farm and profile get a **GUID** from whoever creates them and keep it
  everywhere. Renaming changes a property, not the identity.
- A wrong base version keeps both copies and flags the conflict. Nothing is silently overwritten.
- Detection tiles go to the cloud too (recovery, continuing on another machine). Retention is
  five years, then archive and deletion with advance notice (§10d).

## 7. Customers and farms

Hierarchy **customer → farm → field**. A field has only a `farmId`; the customer follows from the
farm; a farm may have no customer. No farm: **No Farm**; no customer: **No Customer**.

- A customer or farm with fields cannot be deleted (the message gives the count); a farm without
  fields may be; a customer with farms may not. A farm can be renamed or moved to another customer
  at any time; its fields follow. Names are unique per customer.
- Values follow ISOXML (`CTR`, `FRM`): customer: last name or company name, first name, address,
  phone, e-mail, notes; farm: designator, address, notes.
- In AOW, existing fields without owner data show *No Farm* / *No Customer* and write that with the
  next save; reading never writes. The list is the organisation's master data, one file AOW uses
  offline and syncs. A job records the names when it starts, so a finished job does not change when
  its field later moves.
- ISOXML import reads `CTR`, `FRM` and the `PFD` references; export writes them back
  (*No Farm* / *No Customer* write nothing).

## 8. Work orders and executions

| | **Work order** | **Execution** (`job`) |
|---|---|---|
| What | What must be done: field, work type, notes, planned date, farm and customer, implement hint | What a machine did: `job.json` and coverage |
| Created | In the web app (or AOW) | In AOW, on a terminal |
| Status | `Planned`, `Started`, `Done`, `Cancelled` | `InProgress`, `Done`, `Abandoned` (done = locked) |

- An order arrives in AOW as a file and shows in the job list as **Planned**; *Start* creates an
  execution that points to it. One order can have several executions (two machines).
- A finished execution is never overwritten by a download; unlocking stays as in the Finish/Lock plan.
- An execution without an order (started on the spot) stays possible.

## 9. Sync, live view and several machines

- **Live:** while someone watches (live map, viewer, peer), realtime sync refreshes **every 10
  seconds**. This is a named constant with a test that asserts it.
- **Idle:** with no active user in ASL, the terminal does not push every 10 s; it sends its changes
  and status **every 5 minutes**, plus small start/stop messages for the fleet list, and on field
  or job close.
- **On demand:** when a user opens the live map, ASL asks the terminals shown for their latest
  data. Offline terminals show the last known data with its time.
- One outbound WebSocket per terminal; the terminal is never reachable from outside.

**Remote watching.** The web app shows the real AOW screen, read only, with the driver's consent
(per terminal: *ask every time*, the default, or *always allow members of this organisation*). The
cab shows who is watching, with a stop button. A remote client can **never** get control; the host
enforces this in `ControlAuthority`.

**Several machines on one field.**
- **One job per machine**, so coverage never conflicts: a job's tiles come from one terminal.
- Terminals of one organisation that open the same field form a **field group**; they follow each
  other at the live cadence (position, heading, speed, width, section state, ha/h, gear, active
  order and track) and receive each other's new tiles.
- The other's coverage is a **read-only layer** in AOW; the display shows the union. Section
  control may count it as "already done" only if the driver turns that on. Data from another
  machine never controls anything.
- The other machine shows on the map with its colour and name, and "last seen" when the link drops.
- **Private mode** per terminal switches off peer status and remote watching.

## 10. Field management in the web app

A field is a version of files; a web change is a new version sent to the terminal, which asks the
driver *"New version of this field, load it?"* if the field is open. Edits: rename, offset a line,
line from a boundary part, derive a boundary from lines, replace a boundary with an external one
(RVO/PDOK). A field with jobs never changes origin (coverage is anchored to it), so **merging or
splitting makes new fields** that point to the originals; the old fields and their jobs remain.

### 10a. Public fields

An owner can make a field public and withdraw it again; fields are private by default. Public: name,
origin, boundary, and guidance lines, searchable by location, downloadable as a copy into another
organisation. **Never public:** coverage, jobs, orders, customer and farm, profiles, drivers and
live positions. Publishing warns that the field's location becomes visible. Administrators can
unpublish abuse.

### 10b. Vehicle and implement profiles

Profiles belong to the organisation and sync to its terminals. A user can publish a profile to a
**library** (searchable by make, model, implement type); others **import a copy**, with no live
link. Because profiles include settings that affect steering, **an imported profile is never applied
automatically**: AOW shows what changes and asks for confirmation, and geometry (dimensions) is
kept apart from steering tuning, which is marked "user-provided, not verified". NTRIP passwords and
driver names never leave the device.

### 10c. Fleet management scope

All of this is in scope; the order is in §15. Items marked **(format)** change the AOW file formats
and are done early, because they are hard to add later. "Guidance line" always means the AB line or
curve, never a breadcrumb trail.

**Fields and guidance lines**
- **(format)** A guidance line records the **RTK correction source and reference frame** it was
  made in (base station or network, datum, epoch), and a field has a **correction offset** for the
  whole field. Lines made with another base, network or year then stay usable across machines.
- **(format)** **Parcel identity:** official parcel number or RVO/BRP reference, crop per season
  (with the structured crop year), lessor or owner, measured and official area.
- Obstacles, hazards, access points, no-go and buffer zones on the field, with the driver's notes
  and photos.
- Line sets per working width, with who made them and when; lines shared by the machines of a
  field group.

**Jobs**
- **Products and applied rates:** a product catalogue (ISOXML `PDT`), dose, batch number, weather
  conditions, buffer zones and operator, as the application record chemicals require. Prescription
  (task) maps are imported for variable rate; as-applied maps are exported (ISOXML TimeLog,
  shapefile). Connects to `AOG-TaskController`.
- **Coverage reports:** overlap, missed area and accuracy per job.
- **Time per job:** work, transport, idle and set-up, with hours per driver and per order; the basis
  for invoicing per customer (hectares × rate) and for export to accounting.
- **Planning:** a calendar and assignment view, priorities, and a notification to the driver.

**Machines**
- Vehicles and implements as **assets**: serial number, hours, and which implement was coupled to
  which tractor (ISOBUS identity), not only a profile.
- **Maintenance:** engine hours, service intervals and reminders.
- **Driver sign-in** on the terminal.
- **Telemetry** where the machine provides it over CAN/ISOBUS (fuel, load), fuel per hectare.
- **Geofences and alerts:** a machine leaves or enters a field, is offline, idle too long, or moves
  outside working hours; push and e-mail.
- An optional **transport/route log** (breadcrumb), off by default, per organisation, with a
  bounded retention. Strike it if it is not wanted.

**Platform**
- An **open API and webhooks** per organisation (accounting, other farm software).
- **Export and deletion** of an organisation's data (GDPR).
- Notifications (push, e-mail) and quotas per organisation.

### 10d. Retention, archive and simplifying finished jobs

- **Five years.** Data is kept for five years. Ahead of that the organisation gets **active
  notices** (in the app and by e-mail; proposed 12 months, 3 months and 30 days before) that its data
  is moving to the archive and will be deleted, and that it must make its own **export**. The
  export format is still to be decided (PDF or Excel for reports, plus the field and job files in
  their native formats).
- **Archive, then deletion.** At five years data moves to a read-only archive (export only);
  deletion follows after a grace period (proposed 12 months after the final notice). The policy
  must not be shorter than what users have to keep: application records for chemicals have legal
  minimums, and accounting records are kept longer in many countries. Both are checked and named in
  the notices.
- **Simplifying a finished job.** A finished job is locked, so it can be reduced. After a grace
  period (proposed 30 days after Finish) ASL **compacts** it: it keeps the summary values (area,
  overlap, missed area, time, products), the display layer of the coverage and the job file; it
  drops the fine detection layer, thins the logs and simplifies polygons. After that a job can be
  viewed and exported but **can no longer be unlocked for painting**; before that, unlocking works
  as in the Finish/Lock plan. The terminal's own copy is untouched.
- Cold storage for old data and quotas per organisation keep a free service affordable.

## 11. Moving from the existing AgShare

- In the existing AgShare the user chooses *Move to AgShareLive*. It gives a short-lived,
  single-use transfer token; ASL creates an account with the same e-mail address and the user
  **chooses a new password**. No password hashes are transferred. The old account stays for
  AgOpenGPS.
- The same token lets ASL read the user's data **read only** through a small export endpoint added
  to the existing AgShare. No API key needs to be typed.
- **Same GUIDs:** the ASL field keeps the existing field's id, so `agshare.txt` in AOW field folders
  stays valid.
- **Comes across:** name, origin, outer boundary with holes, lines, the JD references, public
  status, and jobs with coverage where they exist. **Cannot:** anything AgOpenGPS never uploaded
  (headland, flags, …).
- After the move ASL is the source for the field; the bridge only writes ASL → old.

## 12. Replacing AOW's AgShare client

- `CloudLinkService` and a pairing screen replace the AgShare menu items (Upload, Download, API)
  and `Shared/AgOpenWeb.Services/AgShare/`. Field folders stay the same.
- **Switching over:** pair, then *Move my account / import my fields* (§11).
- **Test environment:** ASL has a test service next to the live one. AOW gets a server choice in
  its developer settings (live by default) and registers a terminal per environment, so a test
  build never touches live data.
- **Terms:** new users accept the terms of use and privacy statement when they sign up in ASL (a
  versioned acceptance); AOW shows nothing new, the OAuth login does.
- **Changes in AOW for watching** (need the maintainers' approval): `ControlAuthority` refuses
  remote clients; `WebSocketHub` gets a virtual client for the live channel; `transport.js` gets a
  configurable URL and relative asset paths.
- New AOW features that need the cloud: the **Planned** job status and a reference to the order in
  `job.json`; customer/farm master data; profile import with confirmation; share-field toggle.

## 13. John Deere later

JD is finished in the existing AgShare and stays there. It moves to ASL later with a new developer
app on the JD side, which is a **new OAuth client**: users reconnect with JD, so the move gets its
own phase and a user message. Provision now: external references (source, external id, organisation
id) on field, farm and customer, kept out of the core model; a JD organisation can later map to a
farm or customer. Until then JD runs through the bridge. The existing AgShare's JD code and docs are
the source of knowledge.

## 14. Security and privacy (principles)

- Only encrypted transport; every request is checked against organisation, role and right, or the
  terminal's scopes; a terminal never writes into another organisation.
- Input from terminals is validated (formats, sizes, paths); the AOW side keeps the findings of
  `Docs/SECURITY_THREAT_MODEL.md` (F7 path traversal, F8 plain-text NTRIP passwords) in mind.
- NTRIP passwords never travel. A remote client can never steer; Tier-2 commands are never allowed.
- **Customer data and live location are personal data (GDPR).** Live location has a private mode, a
  cab indicator, and consent per watching session.
- **Closed source, free to use:** a private repository with All Rights Reserved. That is a
  copyright notice and says nothing about patents; terms of use and a privacy statement are the
  real instruments, and dependency licences need an audit. Anything that might be patented should be
  filed before it is published here. This is not legal advice.

## 15. Phases

Each phase starts only when the previous test passes. Server work happens in the private ASL
repository; the AOW column is what lands here.

| Phase | AOW work | Test |
|---|---|---|
| 0 · Slice | `CloudLinkService`, pairing screen, encrypted identity file; the **(format)** additions of §10c (line reference frame and offset, parcel identity) in `Docs/FIELD_FILE_FORMATS.md`; one field there and back | A full AOW field folder goes to ASL and back: every file equal, coordinates within 1e-9°, files byte for byte |
| 1 · Move | *Move my account / import my fields*; AOW menu replaced | All fields of a test account arrive; `agshare.txt` stays valid |
| 2 · Status and fleet | Status at the live and idle cadences | A quiet link stays quiet |
| 3 · Watching | Virtual remote client, consent prompt and cab indicator, `ControlAuthority` rule, `transport.js` | A remote client never gets control |
| 4 · Master data, fields, profiles | Customer/farm, ISOXML in/out, share-field toggle, profile import | Delete blocking, *No Farm*/*No Customer*, round trips |
| 5 · Orders and executions | **Planned** status, order reference, tiles | An order from the web app is started in AOW and comes back as an execution |
| 6 · Collaboration | Field group, others' coverage layer, optional section control | Two simulated terminals see each other; link loss is safe |
| 7 · Field editing | Applying new field versions, merge/split provenance | A field with jobs stays intact when split |
| 8 · John Deere | (mostly server side) | Existing JD users sync from ASL |
| 9 · Products and reports | Product catalogue, application records, prescription import, as-applied export, coverage reports, time per job | A sprayed job produces a complete application record and an as-applied export |
| 10 · Machines and alerts | Assets and combinations, driver sign-in, hours and maintenance, telemetry, geofences and alerts, optional route log | An alert reaches the user when a machine leaves its field |
| 11 · Planning and platform | Calendar and assignment, driver notifications, open API and webhooks, invoicing export, retention notices and export | An organisation exports its data before the archive date |

AOW tests: unit tests for the messages and the 10 s / 5 min cadence asserts; a round-trip test of a
full field folder; integration with the headless host and `Simulators/AgOpenWeb.VirtualModules`
against a local ASL (a simulated working day, two hosts on one field); network cuts and restarts;
a remote client cannot take control. Design target: **1,500 paired terminals, 150 active and
watched at the same time.**

## 16. Decisions and open questions

**Decided:** everything in §2; custom roles; plain WebSocket; retention of five years with archive, notices and an owner-made export (§10d); simplifying finished jobs; the fleet management scope (§10c); Driver can edit
fields on their own terminal; structured crop year (start and end date); headless pairing through a
phone; detection tiles to the cloud; the cadence in §9; the design target; JD later; account move
and the small export endpoint on the existing AgShare; public fields; profile library.

**Waiting:** the AOW maintainers' approval of the changes to `ControlAuthority`, `WebSocketHub` and
`transport.js` (§12).

**Open**
- [ ] Coverage visibility (the worked-area map of a job): inherit from the field (proposed); for a
  public field coverage is never public.
- [ ] Public fields: moderation policy, and whether the location should be blurred.
- [ ] Profiles: which parts may be shared publicly, and whether a review step is needed.
- [ ] Trial rules: what happens when Live Mode is switched off for all (open during development).
- [ ] Sharing single fields between organisations without membership: later.
- [ ] Retention: the export format; when deletion follows archiving (12 months proposed); the
  notice schedule; the legal minimums for application records; the grace period before a finished
  job is compacted (30 days proposed).
- [ ] The optional transport/route log (§10c): wanted, and with what retention?
- [ ] Legal: terms of use, privacy statement, dependency licence audit, patent question (§14).

## 17. What this means for the other AOW plans (PR 518)

- `FIELD_SELECTION_AND_CLIENTS_PLAN.md`: hierarchy field → farm → customer; master data as an
  organisation file; *No Farm* / *No Customer*; delete blocking; ISOXML references from the farm.
- `AGSHARE_JOBS_SYNC_PLAN.md`: superseded by §8 and §9.
