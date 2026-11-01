# Field Selection screen, minimap, and ISOXML Client / Farm per field

**Status:** proposed 2026-11-01, for maintainer approval. Nothing is built.
**Hierarchy:** customer → farm → field, as in ISOXML (`CTR`, `FRM`). A field carries only a
`farmId`; its customer is the farm's customer (a farm may have none). Where this plan says
`customerId` on the field, read: derived from the farm. This plan stands on its own and does not
depend on any cloud service.
**Prompted by:** the Fields and Jobs dialog is crowded, and a contractor who works for several
farms or customers cannot record or filter on who a field belongs to.
**Builds on:** [Completed/FIELDS_AND_JOBS_PLAN.md](Completed/FIELDS_AND_JOBS_PLAN.md) (Field/Job
split), [JOB_FINISH_LOCK_IMPLEMENT_PLAN.md](JOB_FINISH_LOCK_IMPLEMENT_PLAN.md) (job snapshots,
locked jobs). Format consumers: [Docs/FIELD_FILE_FORMATS.md](../Docs/FIELD_FILE_FORMATS.md).

## Problem

1. **One dialog does too much.** `fieldsandjobs` (`app.js` `renderFieldsAndJobs`, `index.html`
   `#fj-*`) shows the field list, field actions, the job list, the new-job form, and four
   creation buttons (New, From Existing, ISO-XML, KML) at once. Picking a field is mixed up with
   working on a job.
2. **You cannot see which field you are picking.** The list shows name, distance and area only.
   Fields with similar names look alike.
3. **There is no Rename.** The only field actions are Open Only and Delete.
4. **No owner on a field.** ISOXML (ISO 11783-10) has Customer (`CTR`) and Farm (`FRM`), and a
   Partfield (`PFD`) refers to both (`PFD` attributes `E` CustomerIdRef, `F` FarmIdRef). We read
   the `PFD` on import and drop the references, and write a `PFD` with neither on export
   (`IsoXmlExporter.AddPartfield`). A contractor with 80 fields for 6 customers has one flat list.

## Decisions to approve

1. **Two steps, not one dialog.** *Field Selection* (choose and manage a field) → *Jobs* (as
   today, for the chosen field). No wizard: both are plain screens with a Back arrow, as
   `JOB_FINISH_LOCK_IMPLEMENT_PLAN.md` decision 1 requires.
2. **Customers and Farms are a shared list, referenced by id from the field.** Entered once,
   picked per field. Not typed into every field.
3. **Order of work: data first, UI once.** Phase 1 adds the ISOXML values (model, storage,
   import, export, wire, tests, docs) with no new screens. Phase 2 builds both screens in one
   change. Between the two the dialog looks as it does today.
4. **Existing fields fall back to *No Customer* / *No Farm*.** Fields saved before this change
   have no owner data and show those two built-in entries. It is not a migration: nothing is
   rewritten on disk until the field is saved for any reason, and then the built-in ids are
   written with it. Existing fields stay assignable to a real customer and farm at any time.
5. **Jobs snapshot the customer and farm** when they start, as the implement plan does for the
   implement. A finished job keeps the names it was done for even if the field changes owner.

## Phase 1: Customer / Farm data (no UI)

### Model (`Shared/AgOpenWeb.Models`)

Names follow ISOXML so import and export are a straight mapping. Only what a contractor needs;
the rest of the `CTR`/`FRM` attributes can follow.

```
Customer  { Id (GUID), LastName*, FirstName, Street, PostalCode, City, Country,
            Phone, Email, Notes }
Farm      { Id (GUID), Designator*, Street, PostalCode, City, Country, CustomerId? }
```

`*` required. `LastName` holds a company name too (ISOXML has no separate company field). A farm
may belong to a customer (`FRM` attribute `I`, CustomerIdRef), or stand alone.

### Built-in *No Customer* / *No Farm*

- Two fixed entries, ids `no-customer` and `no-farm`, always available, never stored in
  `clients.json`, not editable or deletable. Shown translated (`tr('No Customer')`,
  `tr('No Farm')`); `customerName`/`farmName` in the files are written in English.
- A field with no owner properties reads as these. The first time its `field.geojson` metadata is
  saved (any save, not only an owner change), the properties are written with the built-in ids.
  Reading never writes.
- They are real filter choices in Field Selection, so "fields without a customer" is one click.
- A real customer or farm can be assigned later and removed again (back to the built-ins).
  A customer or farm that has fields **cannot be deleted**: move or reassign those fields first.
  The delete button is disabled with the field count ("Used by 4 fields"), and the host checks
  again (every field on disk, open or closed) and refuses with a message. A customer whose farms
  hold fields is blocked the same way; a customer with only unused farms asks first and deletes
  them with it. Jobs keep their own copy of the names, so they never block a delete.
- ISOXML: the built-ins write no `CTR`/`FRM` and no `PFD` reference. A `PFD` imported without
  references gets the built-ins.

### Storage

- **Registry:** one `clients.json` (camelCase, invariant culture) beside the other profiles,
  behind a new `IClientRegistryService` (CRUD, same shape as `IVehicleProfileService` /
  `INtripProfileService`). Exact folder follows those services; confirm in implementation.
- **On the field:** `field.geojson` `metadata` gains `farmId` and, as copies, `farmName` and
  `customerName` (the customer comes from the farm). The names are a copy so a field folder is readable alone (copied to another
  machine) without the registry. The registry wins when both are present.
- **On opening** a field whose ids are not in the registry (copied from another machine), the
  entities are added from the copied names. No dialog. The built-in ids are never added.
- **On the job:** `job.json` gets the same four properties, written when the job starts and not
  changed afterwards. `schemaVersion` stays 1 (no installed base).
- Unknown metadata properties are already preserved by the field writer; keep that.

### ISOXML

- **Import** (`MainViewModel.Commands.Fields.cs`, `ConfirmIsoXmlImportDialog`): read `CTR` and
  `FRM` from `TASKDATA.XML`, follow the `PFD`'s `E`/`F` references, and match into the registry
  by name (case-insensitive; farm also by customer) or add. Never overwrite an existing entry's
  details. Today only the first `PFD` is imported (`SelectSingleNode("//PFD")`); see open
  question 3.
- **Export** (`IsoXmlExporter`): write `CTR` and `FRM` for the field's customer/farm and set
  `PartfieldCustomerIdRef` / `PartfieldFarmIdRef`. ISOXML ids (`CTR1`, `FRM1`) are assigned at
  export; our GUIDs stay internal. Fields with neither export as today.
- The `Dev4Agriculture.ISO11783` library should already model `ISOCustomer` / `ISOFarm`; verify
  before relying on it. If a property is missing, add the attribute by hand and say so in the PR.

### Wire and wiring

- `FieldEntryDto` gains `CustomerId`, `FarmId`, `LastOpened` (the client needs them to list and
  filter without a second request).
- New `Clients` frame: customers and farms, sent on connect and when the registry changes.
  Encode in `WireCodec`, decode in `transport.js` in the same order.
- New commands (all need control): `client.save`, `client.delete`, `farm.save`, `farm.delete`,
  `field.setOwner|field \t farmId` (moving a farm to another customer is `farm.save`). `client.delete` and `farm.delete` are
  refused while any field uses the customer or farm (see the built-ins section); only an unused
  one is deleted, after a browser confirm as in `field.deleteField`.
  `field.setOwner` works on a closed field (writes `field.geojson` metadata) and on the open one.

### Tests and docs

- `Services.Tests`: registry CRUD and persistence; ISOXML round trip (export, re-import, same
  customer/farm, no duplicates on a second import); import with unresolved reference; import
  without `CTR`/`FRM`; a field without owner data reads as the built-ins, is not changed by
  opening, and gets the built-in ids on its first save; assigning a real customer to such a field
  and removing it again; opening a field with unknown ids adds them; job snapshot is not changed by
  a later `field.setOwner`; deleting a customer or farm is refused while any field (open or
  closed) uses it, allowed once it is unused, and a customer with only unused farms deletes them too.
- `Docs/FIELD_FILE_FORMATS.md`: new `metadata` properties (section 3) and `job.json` properties
  (section 4), and a note that readers ignore properties they do not know (already the rule there). Update in the same PR as the code.
- `Tools/i18n-extract.py` is not needed in phase 1 (no UI text).

**Done when:** an ISOXML file with customers and farms imports with the right owner on each
field, the export of that field carries them again, and none of this changes what the user sees.

## Phase 2: Field Selection and Jobs screens (one change)

### Field Selection

```
┌ Field Selection ───────────────────────────────────── ✕ ┐
│ [search…]   Customer ▾   Farm ▾   Sort: name ▾    [＋ ▾] │
│ ┌──────────────────────┐ ┌────────────────────────────┐ │
│ │ North 40      12.3 ha│ │        ┌────────┐          │ │
│ │ Mill Road      8.1 ha│ │  mini  │ boundary│          │ │
│ │ ▶ Van Dijk – Oost ...│ │  map   └────────┘          │ │
│ │ ...                  │ │ Customer: Van Dijk BV  ✎   │ │
│ │                      │ │ Farm: Oost             ✎   │ │
│ │                      │ │ 12.3 ha · 1.4 km · 3 jobs  │ │
│ │                      │ │ [Rename] [Copy] [Delete]   │ │
│ └──────────────────────┘ │ [        Select field     ]│ │
│                          └────────────────────────────┘ │
└──────────────────────────────────────────────────────────┘
```

- **List:** search by field, customer or farm; Customer and Farm filters (a Farm filter narrows
  to the chosen customer); sort by name, distance, last opened. The list is one flat list, so a
  user who ignores customers sees what they see today.
- **＋ menu** holds the four creation paths (New, From Existing, From ISO-XML, From KML). Their
  panels are unchanged; only the entry moves. New field and the importers gain an optional
  Customer / Farm picker (importers pre-fill it from the file).
- **Detail:** minimap, owner (edit opens a small panel to pick, or add, a customer and farm),
  area, distance, last opened, job count.
- **Actions:** **Rename**, **Copy** (= From Existing with this field as source), **Delete**,
  **Select field**. Select goes to Jobs.
- **Rename (new):** field must be closed. Renames the folder, `metadata.name`, and
  `fieldName` in each `job.json`; rejects empty names, characters illegal in a folder name, and a
  name already taken. Before building, grep for everything that stores a field name (last-opened
  field and job in settings, `agshare.txt` is unaffected, coverage `manifest.json`) and update
  each. Needs tests like `field.deleteField`.
- **Minimap:** the outer boundary, inner boundaries and headland, drawn on a small 2D canvas,
  fitted and north-up, in the field's colours. No CanvasKit instance for it. Data comes from a
  new `field.preview|name` request answered by its own `FieldPreview` frame (simplified polygons,
  at most ~200 points each, plane metres from the origin) so the `FieldOps` frame stays small
  with many fields. Cached per field in the client; `field.preview` changes nothing, so it goes
  in `ObserverIds`. A field without a boundary shows its origin and "No boundary yet".

### Jobs

The current right-hand side of the dialog, as its own screen for the chosen field:
- Header: field name, customer · farm, small minimap, **Back** to Field Selection.
- Job list, new-job form (work type, notes, task name, Date/Time), Resume, Delete, as now.
- **Open Field Only** stays here as a secondary action.
- Job status column and the locked-job behaviour arrive with the
  [Finish/Lock plan](JOB_FINISH_LOCK_IMPLEMENT_PLAN.md) and slot in unchanged.
- Start New Job writes the customer/farm snapshot (phase 1).
- **Resume Job** and **Resume Last Job** in the Field Operations menu stay as they are.
- Main menu: *Fields and Jobs* is renamed *Field Selection*; the old one-dialog layout and
  its `#fj-*` CSS are removed, not kept behind a setting.

### Platform and layout

All of this is `Shared/` (web client and RemoteServer); no head changes. Check the screens in a
split card pane (`--sal`/`--sar`, see `CLAUDE.md`) and at tablet size in landscape, the usual cab
screen. Observers see the screen and change nothing.

### Strings

All UI text in English with `tr('…')`, then `Tools/i18n-extract.py`; CI runs `--check` and
`i18n-limits.py`, so the labels (Rename, Select field, Customer, Farm, …) must fit the layout in
the longest translation.

## Order of work and size

| Step | Content | Rough size |
|---|---|---|
| 1 | Models, `IClientRegistryService`, `field.geojson`/`job.json` properties, tests | small |
| 2 | ISOXML import + export of `CTR`/`FRM`/`PFD` refs, round-trip tests | medium |
| 3 | `Clients` frame, DTO fields, commands, `Docs/FIELD_FILE_FORMATS.md` | small |
| 4 | Field Selection + Jobs screens, minimap, Rename, owner pickers, strings | large |

Steps 1–3 are one PR (or one per step, same branch rules as always); step 4 is one PR. Merge 4
only after 1–3 are on `develop`.

## Not in this plan

- Other ISOXML master data: Workers (`WKR`), Products (`PDT`), crop types, and writing a Task
  (`TSK`) per job. A job-to-`TSK` export is the natural next step and uses the snapshot made here.
- Per-customer settings (default implement, NTRIP profile). Possible later through the same id.
- Any other program reading the owner. The new properties are in `field.geojson`, documented in
  `Docs/FIELD_FILE_FORMATS.md`; nothing else is done for them here.
- Billing, invoices, reports per customer.

## Open questions for the maintainers

1. **Storage of the registry:** `clients.json` beside the profiles (proposed), or inside
   `AppSettings`? A separate file is easier to back up and share between machines.
2. **Copy of names in `field.geojson`:** keep (proposed, self-contained folders) or ids only
   (no duplicate data, but a copied field loses its owner)?
3. **ISOXML files with several `PFD`:** FMIS exports often hold dozens. Today only the first is
   imported. Should the importer list them and import the ones picked? It belongs in the same
   screen as the owner work, but it is extra scope; propose to leave it for a follow-up unless
   someone needs it now.
4. **Minimap:** is a simple 2D canvas with boundary and headland enough, or should it show the
   `background.png` satellite image behind it when the field has one? (Proposed: boundary only
   for v1; the image is a later nicety.)
5. **Fields count:** do we expect 100+ fields per user? If so the list needs virtual scrolling
   in phase 2; if not, plain DOM rows as today are fine.
