# Coverage Persistence: Tiled, Incremental, Atomic

**Status:** Completed 2026-10-01. Steps 1–3a in #200, the CPU fixes in #202 (§5b),
steps 4–8 in #203 (§5c, which lists where the build differs from §3). Re-checked against `develop` @ `e8c8f444` on 2026-10-01 — see §0.
**Decision context:** [GEOPACKAGE_STORAGE_ANALYSIS.md](../GEOPACKAGE_STORAGE_ANALYSIS.md) §8.1 —
GeoPackage adoption was rejected; this is the incremental fix to the existing
file handling that the analysis recommended doing regardless.
**Owner file:** `Shared/AgOpenWeb.Services/Coverage/CoverageMapService.cs`

---

## 0. Re-check 2026-10-01

No commit since this plan (#74) has touched the save path, so every problem in
§1 still exists as written. Line numbers have moved: `SaveDetectionBits` is now
`CoverageMapService.cs:1705`, `SaveSectionDisplay` `:1868`, `LoadSectionDisplay`
`:2035`, `MarkCellCovered` `:675`. Code that landed since, and corrections to
the plan that came out of the re-read:

1. **Display tiles are not world-aligned (corrects §3.1).** Detection cells are
   absolute (`_bitmapOriginE = floor(minE / 0.1)`), so world-anchored detection
   tiles work as described. The display grid does not: `PaintDisplayPixel` puts
   its origin at `_fieldMinE` (any double), with cell sizes of 0.35 or 0.75 m that
   don't divide 102.4 m. `CheckAndExpandBounds` → `SetFieldBounds` also re-runs
   `ComputeDisplayCellSize`, so an expansion can change the display cell size
   too. "Expansion is a no-op for already-written tiles" is therefore only true
   for the **detection** layer. Resolved in §3.1a.
2. **More events must invalidate tiles (extends §3.3).**
   - `ClearAll` (Delete Applied Area, #189) → next save writes a manifest with an
     empty tile list and deletes the tile files. Without this the tiles come back
     on reopen, which is the "deleted data returns" bug class we don't copy.
   - `RebuildDisplayForResolutionChange` (#175) → every display tile is dirty
     (new cell size). Detection tiles are unchanged.
   - Display cell-size change during expansion → every display tile is dirty.
   - Legacy import (`Sections.txt` via `LoadLegacySections`, `coverage_*.bin`) →
     every tile is dirty. The import paints through `MarkCellCovered`, so this
     happens on its own if load doesn't clear `_dirtyTiles` afterwards.
   All painting (`AddCoveragePoint`, `MarkRectangleCovered`, legacy import) goes
   through `MarkCellCovered`, so §3.3's single hook point holds.
3. **Suspected load bug: the saved origin is ignored.** `LoadDetectionBits` reads
   `originE/originN` from the header and then never uses them. It resizes
   `_detectionBits` to the saved width/height but keeps the `_bitmapOriginE/N`
   from the current `SetFieldBounds`. `LoadSectionDisplay` scales from (0,0) the
   same way. If a job was saved after `CheckAndExpandBounds` grew the bounds
   (driving outside the boundary, or a no-boundary field), reopening it would
   decode the bits against the wrong origin and stride: shifted or sheared
   coverage. **Confirmed (step 3a)**, in two forms:
   - Bounds grown **west or south** (the min corner moves): coverage reloads 250 m
     off. Growing east or north happens to work, because the origin is unchanged.
   - **Fields with no boundary**: reopening loads before any bounds exist, then
     the first GPS fix sets bounds and wipes the loaded cells. The job reopens
     with an empty map, and the next save writes that empty map to disk.
     Reproduced in the headless app on `develop`.
   Fixed: `LoadFromFile` first grows the bounds to hold the saved files' extent
   (from their headers), and both loaders decode through the saved origin. The tiled format fixes it by design (tiles carry world keys), but the
   legacy importer in step 7 must honour the saved origin or it will carry the
   bug into the new files.

---

## 1. Problem

`MainViewModel.Autosave.cs` calls `_coverageMapService.SaveToFile(path, task)`
**every 30 s** for the whole active job. That call rewrites both coverage
files from scratch. Three distinct problems, in order of severity:

### 1.1 `SaveSectionDisplay` rescans the entire detection grid every save

This is the dominant cost and it is not the RLE. `SaveSectionDisplay`
(`CoverageMapService.cs:1868`) does the following *on every autosave*:

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

### 3.1a Display tiles: snap the display origin

To give display tiles the same stability as detection tiles, snap the display
grid's origin to a world multiple of the display cell size in `SetFieldBounds`
(`_displayOriginE = floor(_fieldMinE / cell) * cell`, same for N). Expose it
through `DisplayBoundsWorld`, which the web renderer already uses, so the
renderer needs no change beyond reading the snapped origin. Then:

- An absolute display pixel index is `floor(world / cell)`, independent of field
  bounds, so expansion without a cell-size change leaves display tiles valid.
- Display tiles are sized in **pixels** (256 × 256) rather than metres, keyed
  `(tileX, tileY)` with the cell size stored in the manifest. Slicing a tile out
  of `_displayPixels` is a row-wise memcpy, with no resampling on save.
- A cell-size change (resolution change, or an expansion that crosses a
  `ComputeDisplayCellSize` step) rewrites all display tiles once. That is rare
  and costs the same as today's every-30-s save.

The alternative, resampling each tile through world coordinates on save and
load, avoids touching `SetFieldBounds` but brings back per-pixel coordinate math
on the hot path. Not recommended.

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
| 3a | Test for §0.3: cover outside the original bounds to force an expansion, save, reopen with the original bounds, assert `IsPointCovered` cell-for-cell. If it fails, fix both legacy loaders to honour the saved origin (grow bounds to the saved extent before decoding). | `CoverageMapService.cs`, new test | low |
| 4 | Snap the display origin (§3.1a), then the tile grid + `_dirtyTiles` set + the invalidation events in §0.2 + `manifest.json` writer/reader. No behaviour change yet — write tiles *in addition to* the legacy files, compare on load in tests. | `CoverageMapService.cs`, new `Coverage/CoverageTileStore.cs` | med |
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
7. **Delete Applied Area** — cover, save, `ClearAll`, save, reopen: no
   coverage. Extend `CoverageClearWhilePaintingTests`.
8. **Resolution change** — cover, save, `RebuildDisplayForResolutionChange`,
   save: all display tiles rewritten at the new cell size, detection tiles
   untouched, and reload matches. Extend `CoverageResolutionChangeTests`.
9. **Concurrency** — hammer `MarkCellCovered` on one thread while saving on
   another; assert no exception and no lost coverage. This one is the reason
   §3.5 exists.

---

## 5a. Step 1 measurements (2026-10-01)

Measured with the step 1 timing log on an Apple-silicon Mac. These are not the Pi
or Tab S7 numbers the plan asks for; expect those to be several times slower.
12 m passes with 1 m gaps over half of a square field:

| Field | Detection file | Display file | Save total |
|---|---|---|---|
| 20 ha | — | — | 120–370 ms |
| 200 ha | 5.9 MB, ~65 ms | 1.1 MB, ~1 000 ms | ~1.05 s |
| 520 ha | 16 MB, ~490 ms | 2.1 MB, ~3 000 ms | ~3.5 s |

The display path (the §1.1 rescan) is ~90 % of the time, so open question 1 is
answered: steps 4–8 are worth doing. §3.4 on its own, maintaining palette
indices instead of rescanning, would remove most of the cost before any tiling.

## 5b. CPU fixes ahead of tiling, measured on a CM4 (2026-10-01)

The §1.1 rescan through the detection bits existed to filter background-image
pixels out of the display buffer. The native map control that composited them is
gone, and the only writers of `_displayPixels` now are `PaintDisplayPixel`, the
expansion copy and the loader, so every non-zero pixel is coverage. Three changes,
with output byte-identical to the old encoder (detection) and pixel-identical
(display) on the fixtures below:

1. One pass over the display pixels, with one palette lookup per run instead of
   per pixel, and no 25 MB index buffer.
2. Both RLE encoders find runs with `Span.IndexOfAnyExcept` (vectorised) into a
   reused buffer.
3. A change counter. A save with nothing new since the last save to the same job
   (stationary, headland turns, sections off) does no work.

Benchmarked on a CM4 (4 × A72, 2 GB, SD card, PREEMPT_RT kernel) with the same
fixtures (12 m passes, 1 m gaps, half the field):

| Field | Before | After, SD | After, tmpfs (CPU only) | Written per save |
|---|---|---|---|---|
| 20 ha | ~0.55 s | 0.06–0.11 s | — | 0.7 MB |
| 200 ha | ~5.9 s | 0.36–0.79 s | ~0.12 s | 7 MB |
| 520 ha | ~16 s | 0.87–2.6 s | ~0.30 s | 18 MB |

Peak RSS at 520 ha: 322 → 231 MB. What remains on SD is writing and `fsync`ing the
whole files, so the reason left for steps 4–8 is write volume and SD wear, not
CPU. These fixtures are a worst case for the detection file: a 1 m gap between
every pass makes it speckled. Real overlapping passes compress much better.

## 5c. Tiles as built (2026-10-01)

Where the build differs from §3:

- **Detection grid byte-aligned.** `SetFieldBounds` rounds the detection origin and
  width out to multiples of 8 cells. Every tile row is then a plain byte copy, with
  no bit shifting.
- **Display origin snapped (§3.1a).** `_displayOriginX/Y` are absolute display-pixel
  indices; `DisplayBoundsWorld` reports the snapped extent, so `CoverageProjector`
  needed no change.
- **Display tiles store RGB565, not palette indices.** RLE is `[run:u16][value:u16]`.
  This drops the palette, its discovery and `FindClosestColorIndex`, at about 4/3 the
  size per run. Display tiles are ~15 % of the bytes.
- **The folder is the index, not a manifest tile list.** A tile with no coverage has
  no file. `manifest.json` (via `AtomicJsonFile`) holds the display cell size, worked
  area and field bounds. If it's unreadable, it is rebuilt from the tiles.
- **One display folder per cell size** (`s<µm>/`). A quality change writes a new
  folder; the manifest switches to it, and then the old folder is removed. So a crash
  mid-regrid never mixes cell sizes. This answers open question 3: no generation
  counter needed.
- **Batched flush.** All tiles are written to `.tmp`, then flushed, then renamed, then
  the manifest is written. On ext4 the first flush commits the journal for all of them,
  which cut a full save at 520 ha from ~8 s to ~2 s on the CM4.
- **Legacy files deleted after migration** (open question 4, decided 2026-10-01): a
  job's `coverage_*.bin` are imported once and removed after its first tiled save. The
  old loaders stay, for import only. The old writers are gone.
- **Third dirty stream** as §3.3: `_dirtyDetectTiles`/`_dirtyDisplayTiles`, marked in
  `MarkCellCovered`/`PaintDisplayPixel` with a last-key cache. Swapped out under the lock
  at save time and put back if the save fails. Full saves (new field or job, Delete
  Applied Area, regrid, import) write every non-empty tile and delete the rest.

CM4 + SD, same fixtures as §5a/§5b, each save after one 12 × 84 m strip:

| Field | Incremental save | Bytes per save | Full save | Load (cold cache) |
|---|---|---|---|---|
| 20 ha | 53–69 ms | 44–58 KB | 0.5–0.6 s | — |
| 200 ha | 20–24 ms | ~47 KB | 0.8–1.8 s | 1.4 s |
| 520 ha | 17–20 ms | ~42 KB | 1.8–2.2 s | 2.2 s (old files: 9 s) |

Small fields at Ultra quality have more display tiles: at 0.1 m one is 25.6 m square,
so a 20 ha field has ~150 files. Kept on purpose (decided 2026-10-01): Ultra is mainly
for desktop-class hardware, which handles the file count easily. On a CM4, where SD
writes are what matter, small tiles keep each save small. Don't raise
`DisplayTileShift` for this.

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
