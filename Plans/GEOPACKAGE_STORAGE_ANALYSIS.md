# GeoPackage vs. Current File Formats — Storage Analysis

**Status:** Decided — **GeoPackage rejected.** Options B, C and D below were all
declined: marginal value against their implementation cost. §8.1 (fix coverage
writes, no new dependency) was accepted and is planned in
[COVERAGE_TILED_PERSISTENCE_PLAN.md](COVERAGE_TILED_PERSISTENCE_PLAN.md).
The rest of this document is retained as the reasoning behind that call.

**Related:** [FILE_FORMAT_MODERNIZATION_PLAN.md](FILE_FORMAT_MODERNIZATION_PLAN.md),
[Completed/FIELDS_AND_JOBS_PLAN.md](Completed/FIELDS_AND_JOBS_PLAN.md),
[../Docs/COVERAGE_PERFORMANCE_TESTS.md](../Docs/COVERAGE_PERFORMANCE_TESTS.md)

---

## 1. What we store today

A field is a **directory of files**, not a single document. Current inventory as
written by `Shared/AgOpenWeb.Services`:

| Data | File | Format | Writer |
|---|---|---|---|
| Field metadata (id, name, origin, convergence) | `field.json` | JSON, schemaVersion 1 | `Fields/FieldJsonService.cs` |
| Boundary + holes, headland, tracks, bg-image bounds | `field.geojson` | GeoJSON FeatureCollection (WGS84), `role` property per feature | `GeoJson/GeoJsonFieldService.cs` |
| Field metadata (legacy mirror) | `Field.txt` | Fixed-line text | `FieldPlaneFileService.cs` |
| Boundary (legacy mirror) | `Boundary.txt` | Point-list text | `BoundaryFileService.cs` |
| Tracks | `TrackLines.txt` | Multi-line text blocks | `TrackFilesService.cs` |
| Headland | `Headland.Txt`, `Headlines.txt`, `HeadlandSegments.json` | text + JSON | `HeadlandLineSerializer.cs`, `Headland/HeadlandSegmentFileService.cs` |
| Tram lines | `TramLines.txt`, `TramConfig.json`, `TramSystems.json` | text + JSON | `Tram/*FileService.cs` |
| Recorded path | `RecPath.txt` | text | `RecPathFileService.cs` |
| Flags, contour, elevation | `Flags.txt`, `Contour.txt`, `Elevation.txt` | text | various |
| Background imagery | `BackPic.png` + `BackPic.Txt` | PNG + bounds text | `BackgroundImageFileService.cs` |
| **Coverage — detection** | `jobs/<task>/coverage_detect.bin` | `COVD`: header + RLE bit array @ 0.1 m | `Coverage/CoverageMapService.cs:1681` |
| **Coverage — display** | `jobs/<task>/coverage_disp.bin` | `COVS`: header + ≤255-entry RGB565 palette + RLE byte indices | `Coverage/CoverageMapService.cs:1841` |
| Coverage (legacy import) | `Sections.txt` | AgOpenGPS triangle strips | `CoverageMapService.LoadLegacySections` |
| Job metadata | `jobs/<task>/job.json` | JSON, schemaVersion 1 | `Fields/JobJsonService.cs` |

Two things follow from this table and matter for everything below:

1. **GeoJSON is already the canonical vector format.** `FieldService.SaveField`
   writes GeoJSON *and* mirrors legacy text; `LoadField` auto-converts legacy
   fields to GeoJSON on first open. The AgOpenGPS-compat text files are a
   deliberate interop mirror, not the source of truth.
2. **The vector data is tiny; the coverage raster is not.** Boundaries and
   tracks are hundreds to a few thousand points. Coverage detection for a
   520 ha field is ~65 MB of bits at 0.1 m (`COVERAGE_PERFORMANCE_TESTS.md`),
   which is why there is an adaptive resolution ladder (0.1 m at ≤50 ha,
   1.0 m at 2812 ha+).

---

## 2. What GeoPackage actually is

A `.gpkg` is a **SQLite 3 database** with `application_id = 'GPKG'` and a
mandated set of metadata tables (`gpkg_spatial_ref_sys`, `gpkg_contents`,
`gpkg_geometry_columns`). Vector features live in ordinary tables with a
geometry column encoded as *GeoPackageBinary* — a small header (SRS id,
optional envelope, flags) followed by standard WKB. Rasters live as a
PNG/JPEG tile pyramid (`gpkg_tile_matrix_set` / `gpkg_tile_matrix`).
Aspatial tables are legal (`data_type = 'attributes'`), and **arbitrary
private tables are permitted** as long as they are not registered as GPKG
content. Optional extensions add an R-Tree spatial index and, relevantly for
us, **Tiled Gridded Coverage Data** (16-bit PNG or 32-bit float TIFF tiles).

So "adopt GeoPackage" really means "adopt SQLite, plus the OGC table
conventions on top of it." Those two halves have very different cost/benefit
profiles and should be evaluated separately.

---

## 3. Fit, per data class

### 3.1 Vector (boundary, headland, tracks, tram, flags, recpath) — weak case

| Criterion | GeoJSON (today) | GeoPackage |
|---|---|---|
| QGIS / ArcGIS opens it | Yes, natively | Yes, natively |
| Human-readable, greppable, git-diffable | Yes | No (binary) |
| Field support debugging ("send me your field") | Paste a text file | Needs a tool to inspect |
| Parse cost at our sizes | Negligible (<10 ms) | Negligible |
| Typed schema / constraints | No | Yes |
| Partial read / spatial index | No | Yes — **but we load the whole field into RAM anyway** |
| Mixed geometry types in one layer | Yes | No — one geometry type per table |

The classic database wins (indexed queries, partial reads, concurrent access)
are **wasted here**. `MainViewModel` loads the entire field into plane
coordinates and keeps it resident for guidance; nothing ever queries a subset.
Meanwhile we would give up text-file debuggability, which has real support
value for a user in a field whose only tool is a phone.

Verdict: GPKG offers no meaningful advantage over GeoJSON for our vector data.

### 3.2 Coverage raster — the real question, and GPKG answers it awkwardly

This is where the current design genuinely hurts:

- `TryAutosaveCoverageAsync` fires **every 30 s** and rewrites the *entire*
  `coverage_detect.bin` and `coverage_disp.bin`. The code comment is candid:
  *"RLE compression can take seconds on a large field."*
- The RLE is `[runLength:ushort][value:byte]` — **3 bytes per run**. On uniform
  data it compresses hard; on speckled data (partial section overlap, edge
  cells) it can *expand up to 3x* versus the raw bit array. There is no
  fallback to raw or to a real compressor.
- Writes are `FileMode.Create` — truncate-then-write. A power cut mid-write on
  a tractor leaves a corrupt or empty coverage file. (Contrast
  `Storage/AtomicJsonFile.cs`, which the JSON paths already use.)
- There is no "changed since last save" state to scope the write with.
  `_minCellE`/`_maxCellE` are *cumulative* coverage bounds and the
  `_dirtyMinX`/`_dirtyMaxX` rect is drained every frame by the renderer via
  `ConsumeDirtyRect()`, so a saver needs its own dirty stream.
- The dominant cost is not the RLE at all: `SaveSectionDisplay` rebuilds the
  palette-index array from scratch on every save by scanning the entire
  detection grid. See §1.1 of the coverage plan.

A database would fix the incremental-write and durability problems. But
**standard GeoPackage does not** give us a good home for this data:

- *GPKG tiles* are PNG/JPEG images on a defined zoom pyramid in a declared CRS.
  Our detection layer is a 1-bit semantic mask at a metric cell size tied to
  the field's local plane, not a display pyramid. Shoehorning it in means
  inventing a tile matrix set per field and paying PNG encode/decode on the
  30 s autosave path — worse than the RLE we have.
- *Tiled Gridded Coverage* is designed for continuous values (elevation),
  16-bit PNG or float TIFF. Our detection bits are boolean and our display
  layer is a palette index. Neither is what the extension models.
- The honest answer is a **private blob table**
  (`coverage_tiles(job, tx, ty, blob)`) inside the SQLite file — legal in a
  GPKG, but at that point GeoPackage contributes nothing except the file
  extension.

Verdict: the coverage problem is a *chunked, transactional write* problem.
SQLite solves it. GeoPackage-the-standard adds nothing to that solve.

### 3.3 Background imagery — mild case for GPKG tiles

`BackPic.png` + a bounds text file is exactly what `gpkg_tile_matrix` exists to
replace, and it would scale to multi-tile imagery if we ever want more than one
capture. Low priority; the current single-PNG approach is not causing pain.

### 3.4 Metadata / config — no change either way

JSON is correct for `field.json`, `job.json`, `appsettings.json`.

---

## 4. Cost of adoption

### 4.1 New dependency: SQLite on five heads

We currently ship **zero native dependencies** in `AgOpenWeb.Services`
(`Newtonsoft.Json`, `Dev4Agriculture.ISO11783.ISOXML`, `Clipper2`,
`Microsoft.Extensions.Logging.Abstractions` — all managed). Adding
`Microsoft.Data.Sqlite` pulls in `SQLitePCLRaw` and a native `e_sqlite3` per
RID. Consequences:

- **Desktop** (win-x64, linux-x64, linux-arm64, osx): fine, but the
  `deploy/{linux,windows,macos}/package.sh` scripts must carry the extra
  native asset per RID.
- **Android**: must ship bundled `e_sqlite3` (the system SQLite is not usable
  under that name on Android). APK grows.
- **iOS**: works in principle, but this is the head where Release/AOT is
  already fragile — `CLAUDE.md` notes *"iOS Release builds hang in CI; use
  Debug."* Adding a native P/Invoke library to that build is where surprises
  live.
- The `RTREE` and WAL behaviours we would rely on need verifying per bundle,
  not assuming.

### 4.2 No usable managed GeoPackage library

`NetTopologySuite.IO.GeoPackage` — the obvious candidate — is at **2.0.0,
released August 2019**, and is essentially the GeoPackageBinary geometry codec,
not a container manager. `SpatialFocus.GeoPackage` is alpha. GDAL bindings
(`MaxRev.Gdal.Core`) are tens of MB of native code and a non-starter for
iOS/Android here.

So the realistic path is **hand-rolling the GPKG container** over
`Microsoft.Data.Sqlite`: the metadata tables, the SRS rows, the geometry
header, the `gpkg_contents` bookkeeping, and validation against the spec. That
is a few hundred lines of code we own forever, plus the risk that a subtle spec
violation produces files QGIS opens but a customer's FMS rejects.

### 4.3 Migration and churn

The GeoJSON canonical format landed recently and legacy auto-conversion
(`FieldService.LoadField`, `LegacyFieldMigrationService`) already carries two
eras of format. A third canonical format means a third migration path and
another round of "which of these files is authoritative" bugs — the exact class
of bug the Fields/Jobs split (#349) just cleaned up.

### 4.4 Loss of failure transparency

A directory of files degrades gracefully: a corrupt `TramLines.txt` costs you
tram lines, not the field. A corrupt SQLite header costs you *everything* for
that field — boundary, tracks, and every job's coverage. A single-file
container needs an explicit answer here: backup-on-open, `PRAGMA
integrity_check`, or keeping coverage outside the container.

---

## 5. What GeoPackage would genuinely buy

Being fair to the proposal, these are real:

1. **One file per field.** Emailing or USB-sticking a field becomes trivial
   versus zipping a directory of ~12 files. Good UX, and good for
   AgShare-adjacent workflows.
2. **Transactional, incremental writes.** The 30 s coverage autosave becomes a
   handful of dirty-tile UPDATEs inside a transaction instead of a multi-MB
   full rewrite. Crash-safe by construction. This is the single biggest
   technical win — and it comes from *SQLite*, not from *GeoPackage*.
3. **Typed schema with constraints**, replacing per-file ad-hoc parsers.
4. **Direct GIS ingest** with no export step: drop the field on QGIS, get
   boundary/headland/tracks as named layers with attributes.
5. **A natural home for job history.** Many jobs per field accumulating
   as-applied coverage is a database-shaped problem.

---

## 6. Interop reality check

| Consumer | GeoJSON | GeoPackage | Notes |
|---|---|---|---|
| QGIS / ArcGIS / GDAL | yes | yes | Both first-class |
| AgOpenGPS (desktop) | no | no | Needs the legacy `.txt` mirror either way |
| AgShare | yes (JSON API) | no | `AgShareUploaderService` posts JSON |
| ISO 11783 / ISOXML | n/a | n/a | Separate exporter (`IsoXml/IsoXmlExporter.cs`) |
| Farm management systems (Ops Center, FieldView, Trimble) | partial | **verify** | These predominantly ingest shapefile + ISOXML; GPKG support is inconsistent. Confirm before claiming GPKG improves FMS interop. |

Note the shape of this table: **GPKG does not unlock any consumer we cannot
already reach.** Its advantage over GeoJSON is convenience (one file, typed
layers), not reach.

---

## 7. Options

**A. Status quo.** Keep GeoJSON + JSON + `.bin` coverage.
*Cost:* zero. *Leaves unfixed:* the coverage full-rewrite and non-atomic-write
problems, and no single-file field.

**B. GeoPackage as an import/export format only.** Canonical storage unchanged;
add `ExportFieldToGeoPackage` / `ImportFieldFromGeoPackage` alongside the
existing ISOXML exporter.
*Cost:* SQLite dependency + ~400 lines of container code, but **zero migration
risk** and no hot-path exposure. Export can ship Desktop-only at first,
sidestepping the iOS/Android AOT question entirely.
*Gets:* interop convenience, single-file sharing. *Does not get:* the coverage
write fix.

**C. GeoPackage canonical for vector, coverage stays `.bin`.**
*Cost:* full migration path, loses text debuggability.
*Gets:* very little that B does not. **Worst ratio of the four.**

**D. Single SQLite container per field — GPKG-compliant for vector layers,
private tables for coverage.** Boundary/headland/tracks/flags as real GPKG
feature tables (so QGIS just works), coverage as a tiled blob table, metadata
as attributes tables.
*Cost:* highest — SQLite on all five heads, hand-rolled container, full
migration, corruption blast-radius answer required.
*Gets:* everything in §5 at once, including the coverage write fix.

---

## 8. Recommendation

**Split the decision. The coverage problem is urgent and is not a GeoPackage
problem; the GeoPackage question is a nice-to-have and is not urgent.**

1. **Fix coverage writes independently of this decision.** Chunk
   `coverage_detect.bin` / `coverage_disp.bin` into world-anchored tiles, track
   dirty tiles, stop rebuilding the palette-index array from a full-grid scan
   on every save, and write atomically (temp + rename, as `AtomicJsonFile`
   does). While in there, add a raw-vs-RLE choice per tile so speckled tiles
   stop expanding 3x. This removes the "seconds on a large field" autosave
   stall and the power-cut corruption window **with no new dependency**.
   → **Accepted.** Planned in
   [COVERAGE_TILED_PERSISTENCE_PLAN.md](COVERAGE_TILED_PERSISTENCE_PLAN.md).

2. ~~Take Option B for GeoPackage: export/import only.~~
   → **Declined.** The benefit is a QGIS/single-file convenience that no
   consumer in §6 actually requires, against a SQLite native dependency, a
   hand-rolled container, and ongoing spec-compliance risk.

3. ~~Do not make GeoPackage canonical now.~~
   → **Declined outright**, not merely deferred. GeoJSON reaches every consumer
   GPKG reaches and text-file debuggability has real field-support value.

---

## 9. If Option B proceeds — sketch

```
field.gpkg
├─ gpkg_spatial_ref_sys        EPSG:4326
├─ gpkg_contents               one row per layer below
├─ gpkg_geometry_columns
├─ boundary        POLYGON     role, isHard, isDriveThrough, area_ha
├─ headland        POLYGON     source ('offset' | 'drawn')
├─ tracks          LINESTRING  name, mode, isClosed, nudge_m, noPassOffset
├─ tram_lines      LINESTRING  system, index
├─ flags           POINT       name, color, note
└─ field_meta      attributes  id, name, originLat, originLon, convergence,
                               offsetX/Y, created, modified, schemaVersion
```

Coordinates in EPSG:4326, converted via the existing `GeoConversion` on the way
out and back — identical to what `GeoJsonFieldService` already does, so the
conversion code is reusable and only the encoding layer is new.

Coverage is deliberately **excluded** from the export in Option B. If we later
want as-applied maps in the file, the correct standard-compliant answer is a
vectorised coverage polygon layer (Clipper2 is already a dependency and can
union the worked area), *not* the raw detection bitmap — a vector as-applied
map is what an agronomist or FMS actually wants to consume.

---

## 10. Open questions — resolved by the decision

1. What is the *measured* autosave cost today on the Pi and Tab S7 targets at
   520 ha? → Still open, now the **first step** of the coverage plan.
2. How often does the RLE actually expand rather than compress on real field
   data? → Still open, folded into the same measurement step.
3. Do the FMS platforms our users care about ingest GPKG? → Moot; GPKG declined.
4. Does `e_sqlite3` build cleanly under the iOS AOT path? → Moot; no SQLite.
5. Is anyone asking for single-file fields? → Not currently; revisit only if
   that demand materialises.
