# Coverage Persistence: Tiled, Incremental, Atomic

**Status:** Plan. Not started.
**Decision context:** [GEOPACKAGE_STORAGE_ANALYSIS.md](GEOPACKAGE_STORAGE_ANALYSIS.md) §8.1 —
GeoPackage adoption was rejected; this is the incremental fix to the existing
file handling that the analysis recommended doing regardless.
**Owner file:** `Shared/AgOpenWeb.Services/Coverage/CoverageMapService.cs`

---

## 1. Problem

`MainViewModel.Autosave.cs` calls `_coverageMapService.SaveToFile(path, task)`
**every 30 s** for the whole active job. That call rewrites both coverage
files from scratch. Three distinct problems, in order of severity:

### 1.1 `SaveSectionDisplay` rescans the entire detection grid every save

This is the dominant cost and it is not the RLE. `SaveSectionDisplay`
(`CoverageMapService.cs:1841`) does the following *on every autosave*:

1. Allocates `new byte[pixels.Length]` — up to **25 MB** (`MAX_DISPLAY_PIXELS`
   is 25 M), straight onto the LOH.
2. Iterates **every byte** of `_detectionBits` — 65 MB of bytes for a 520 ha
   field — and for each non-zero byte does 8 bit tests, coordinate math, and a
   `Dictionary<ushort,byte>` lookup per set bit.

On a well-covered 520 ha field that inner loop runs on the order of 5·10⁸
iterations with a dictionary probe each, and allocates 25 MB, **every 30
seconds**. This is what the code comment in `TryAutosaveCoverageAsync` is
describing when it says *"RLE compression can take seconds on a large field."*
The RLE is not the expensive part; the derive-indices-from-detection rescan is.

The rescan exists only because the palette-index representation is never
maintained — it is reconstructed from scratch at save time.

### 1.2 Whole-file rewrite regardless of what changed

Both `SaveDetectionBits` and `SaveSectionDisplay` serialise the complete grid.
A 12 m tool at 10 km/h newly covers roughly 84 m × 12 m in a 30 s window —
about **0.1 %** of a 520 ha field — yet we rewrite 100 % of both files. On the
SD-card-backed Pi target this is also gratuitous write wear.

### 1.3 Writes are not atomic, and two latent bugs

- `FileMode.Create` truncate-then-write: a power cut mid-write leaves a
  truncated or empty coverage file and the job's work is gone. The JSON paths
  already solved this (`Storage/AtomicJsonFile.cs`); the coverage paths did not.
- **Race:** the save runs on a thread-pool thread via `Task.Run` and reads
  `_detectionBits` / `_displayPixels` **without holding `_coverageLock`**,
  while the GPS/simulator thread is calling `MarkCellCovered`. `SetFieldBounds`
  and `CheckAndExpandBounds` *reallocate* both arrays.
- **Silent data loss:** `SaveSectionDisplay` compares `pixels.Length` against
  `_displayWidth * _displayHeight` read separately. If `CheckAndExpandBounds`
  fires between those two reads the check fails and the method **logs
  "Pixel count mismatch" and returns without saving** — a silently skipped
  autosave, exactly when the operator is driving near the field edge.

### 1.4 Secondary: the RLE can expand

The encoding is `[runLength:ushort][value:byte]` — **3 bytes per run**, with no
raw fallback. Uniform regions compress hard, but a speckled region (partial
section overlap, boundary feathering) degenerates to 3 bytes per byte — a **3x
expansion** over the raw array. There is no per-block choice of raw vs RLE.

---

## 2. Non-goals

- No new package dependencies. `AgOpenWeb.Services` stays fully managed
  (this is the whole reason GeoPackage/SQLite was rejected).
- No change to `ICoverageMapService`'s public surface
  (`SaveToFile`/`LoadFromFile` keep their signatures) — this is a storage-layer
  change, not an architecture change.
- No change to the in-memory representation, the GL renderer's
  `GetDisplayPixels`/`ConsumeDirtyRect` contract, or `CoverageProjector`'s
  `_newCellsServer` drain.
- Not touching the detection resolution ladder or `ComputeDisplayCellSize`.

---

## 3. Design

### 3.1 A world-anchored tile grid

Tiles are defined in **world space**, anchored at the plane origin, not at
`_fieldMinE`/`_fieldMinN`:

```
TILE_METERS = 102.4                       // 1024 cells at BITMAP_CELL_SIZE (0.1 m)
tileX = (int)Math.Floor(worldE / TILE_METERS)
tileY = (int)Math.Floor(worldN / TILE_METERS)
```

Why world-anchored rather than buffer-relative: `CheckAndExpandBounds` moves
`_bitmapOriginE`/`_fieldMinE` mid-job (it adds 250 m when coverage comes within
50 m of an edge). A buffer-relative tile grid would renumber every tile on
expansion and force a full rewrite — the exact thing we are removing. A
world-anchored grid makes expansion a no-op for already-written tiles.

One tile key addresses **both layers**:

| Layer | Cells per tile | Raw bytes per tile |
|---|---|---|
| Detection @ 0.1 m fixed | 1024 x 1024 | 131 072 (1 bit/cell) |
| Display @ `_displayCellSize` (0.1–1.0 m) | 1024 down to ~102 per side | 1 048 576 down to ~10 000 (1 byte index/px) |

Tile size is the write-amplification knob: a 30 s window at 12 m / 10 km/h
touches roughly 2–6 tiles, so an autosave writes ~0.3–1 MB instead of ~65 MB.
1024 also keeps the file count sane — a 520 ha field is ~500 detection tiles,
a typical 20 ha field is ~20.

### 3.2 On-disk layout

```
jobs/<task>/coverage/
├─ manifest.json          schemaVersion, displayCellSize, palette, worked area,
│                         field bounds at save time, tile list
├─ d/<tileX>_<tileY>.tile detection bits for that tile
└─ s/<tileX>_<tileY>.tile display palette indices for that tile
```

One file per tile rather than a single container with an internal tile
directory. A container would need free-space management and compaction when a
recompressed tile no longer fits its old slot — that is re-implementing a
database, which is what we just decided not to do. Per-tile files give atomic
replace for free (`write .tmp` → `File.Move(..., overwrite: true)`), make a
corrupt tile cost one tile rather than the job, and let a partial write be
detected and discarded per tile.

Tile header (both layers), 16 bytes:

```
magic      u32   'COVT'
version    u8    1
layer      u8    0 = detection, 1 = display
encoding   u8    0 = raw, 1 = RLE          <- fixes §1.4
reserved   u8
cellSize   f32   metres per cell in this tile
payloadLen u32
crc32      u32   over payload             <- detects torn/truncated tiles
```

`encoding` is chosen per tile by encoding both ways and keeping the smaller —
the tile is at most 1 MB, so this is cheap and removes the 3x pathological
case permanently.

`manifest.json` is written **last**, after every dirty tile has landed, and is
written through `AtomicJsonFile`. It is the commit point: a manifest that
references a tile is a promise that the tile is on disk and CRC-valid. A tile
present on disk but absent from the manifest is ignored and cleaned up on next
save.

### 3.3 Dirty-tile tracking — needs new state

**Correction to an assumption in the GeoPackage analysis:** neither existing
piece of dirty state is reusable here.

- `_dirtyMinX/_dirtyMaxX/...` is the **display-layer rect drained by the
  renderer** — `ConsumeDirtyRect()` resets it every frame. The saver cannot
  share it without starving the renderer or vice versa.
- `_minCellE/_maxCellE/...` are **cumulative coverage bounds** (the extent of
  all coverage ever), not "changed since last save".

So add a third, independent stream, matching the existing precedent of
`_newCells` (renderer) and `_newCellsServer` (web projector) being separate
drains:

```csharp
// Tiles touched since the last successful save. Independent of the renderer's
// ConsumeDirtyRect drain and the server's _newCellsServer drain — a save must
// not steal dirty state from either. Mutated under _coverageLock.
private readonly HashSet<long> _dirtyTiles = new();   // key = (tileX << 32) | (uint)tileY
```

Set in `MarkCellCovered` (which already runs under the lock and already
computes the cell coordinates), cleared **only after the manifest commits**.
A failed save therefore leaves the tiles dirty and the next autosave retries
them — matching the "a failed autosave is a warning, not a fatal" comment in
`TryAutosaveCoverageAsync`.

### 3.4 Killing the rescan (the actual win)

`SaveSectionDisplay`'s full-grid rescan disappears. Per dirty tile:

1. Copy that tile's slice of `_displayPixels` (RGB565) into a **reused scratch
   buffer** under `_coverageLock`, then release the lock.
2. Convert RGB565 → palette index over the scratch buffer only.
3. Encode + write.

Cost per save becomes O(dirty tile area), not O(whole detection grid), and the
25 MB per-save LOH allocation becomes one reused scratch buffer of at most
~1 MB.

The palette stops being discovered by scanning. Build it deterministically from
config — `tool.GetSectionColor(0..15)` plus `tool.SingleCoverageColor`, which is
already the first thing `SaveSectionDisplay` does — and keep
`FindClosestColorIndex` as the fallback for any colour outside that set. Store
it once in `manifest.json` instead of per file.

### 3.5 Locking

The scratch-copy in 3.4 fixes §1.3's race properly: short lock holds (one tile
memcpy), compression and I/O outside the lock, and the length-mismatch check
becomes impossible because tile geometry is derived from world coordinates
inside the same lock that reads the pixels. Delete the silent
`return`-on-mismatch path.

### 3.6 Loading and back-compat

`LoadFromFile` gains one preceding branch, and the existing fallback chain
stays intact:

```
coverage/manifest.json present?          → tiled load (new)
else coverage_detect.bin / _disp.bin?    → legacy binary load, then rewrite as tiles
else Sections.txt?                       → existing AgOpenGPS import path
```

Reusing the one-way-import convention already used for `Sections.txt` and by
`LegacyFieldMigrationService`: read the old format once, write the new one,
leave the old file in place for one release, delete it in the release after.

On tiled load, tiles whose CRC fails are skipped with a warning rather than
failing the whole load — a torn tile costs ~1 ha of display coverage, not the
job.

---

## 4. Work breakdown

Each step is independently shippable and independently revertable.

| # | Step | Files | Risk |
|---|---|---|---|
| 1 | **Measure first.** Add a stopwatch + byte-count log around `SaveDetectionBits`/`SaveSectionDisplay`; capture numbers on Pi and Tab S7 at ~20 ha, ~200 ha, ~520 ha. | `CoverageMapService.cs` | none |
| 2 | Atomic writes for the two existing files (temp + `File.Move` overwrite). Ships the §1.3 durability fix immediately, independent of everything below. | `CoverageMapService.cs` | low |
| 3 | Fix the silent skip: remove the mismatch `return`, take the pixel buffer and its dimensions under one lock. | `CoverageMapService.cs` | low |
| 4 | Tile grid + `_dirtyTiles` set + `manifest.json` writer/reader. No behaviour change yet — write tiles *in addition to* the legacy files, compare on load in tests. | `CoverageMapService.cs`, new `Coverage/CoverageTileStore.cs` | med |
| 5 | Per-tile encoder with raw-vs-RLE choice + CRC32. | `Coverage/CoverageTileStore.cs` | low |
| 6 | Switch `SaveToFile` to dirty-tiles-only; delete the full-grid rescan from the display path; palette from config into the manifest. | `CoverageMapService.cs` | **high** — the hot path |
| 7 | `LoadFromFile` branch order + one-way import from `coverage_*.bin`. | `CoverageMapService.cs` | med |
| 8 | Stop writing the legacy `.bin` files. | `CoverageMapService.cs` | low |

Steps 2 and 3 are worth landing on their own even if the rest slips — they are
small and they fix real data-loss paths.

---

## 5. Testing

Existing coverage tests are the regression harness and must keep passing
unchanged through step 7:

- `Tests/AgOpenWeb.Services.Tests/CoverageMapServicePerJobTests.cs` —
  per-job isolation (`SaveToFile_TwoJobsSameField_DoNotShareCoverage`,
  `LoadFromFile_PerJob_DoesNotPickUpFieldRootCoverage`).
- `Tests/AgOpenWeb.Services.Tests/LegacyCoverageLoadTests.cs` —
  `Sections.txt` import, `LoadFromFile_PrefersBinaryOverLegacy`, corrupt-file
  tolerance.

New tests:

1. **Round-trip fidelity** — cover a known pattern, save, clear, load, assert
   `IsPointCovered` matches cell-for-cell and `TotalWorkedArea` matches.
2. **Only dirty tiles are written** — save, record tile mtimes, cover one
   small area, save again, assert exactly the expected tile files changed.
   This is the test that proves the whole plan works.
3. **Expansion does not renumber tiles** — cover, save, drive past the edge to
   trigger `CheckAndExpandBounds`, save, assert previously written tiles are
   byte-identical and still referenced by the manifest.
4. **Crash safety** — write a manifest referencing a tile, truncate that tile,
   assert load skips it with a warning and keeps the rest.
5. **Encoding choice** — a speckled tile picks `raw`, a uniform tile picks
   `RLE`, and both round-trip.
6. **Legacy import** — a `coverage_detect.bin` + `coverage_disp.bin` pair loads
   and is rewritten as tiles; a second load reads the tiles.
7. **Concurrency** — hammer `MarkCellCovered` on one thread while saving on
   another; assert no exception and no lost coverage. This one is the reason
   §3.5 exists.

---

## 6. Expected outcome

| Metric | Now | Target |
|---|---|---|
| Autosave wall-clock, 520 ha | "seconds" (to be measured, step 1) | < 100 ms |
| Bytes written per 30 s autosave | full grid (tens of MB) | ~0.3–1 MB |
| Per-save LOH allocation | up to 25 MB | one reused ≤1 MB scratch |
| Power-cut mid-write | job coverage lost | last committed manifest survives |
| Save skipped during bounds expansion | silently, yes | no |
| Worst-case encoded size | 3x raw | 1.0x raw |

---

## 7. Open questions

1. **Step 1 gates the rest.** If the measured 520 ha autosave is already, say,
   80 ms, steps 2–3 are still worth it for durability but 4–8 are not worth the
   risk to the hot path. Measure before building.
2. Is 102.4 m the right tile edge? Cross-check against the measured per-save
   dirty area for a typical tool width and speed before committing to it — the
   number should come from step 1's data, not from this document.
3. Should `manifest.json` carry a monotonically increasing generation counter so
   a stale tile from an interrupted save is detectable, or is CRC + "referenced
   by manifest" sufficient? Leaning sufficient, but worth 10 minutes' thought
   before step 4.
4. Do we keep writing the legacy `.bin` files for one full release (step 8
   deferred) to allow downgrade, given field data loss is unrecoverable?
   Leaning yes.
