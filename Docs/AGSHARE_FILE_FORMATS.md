# AgOpenWeb field and coverage file formats (for AgShare)

As of 2026-10-01 (`develop`).

## 1. Purpose

AgOpenWeb no longer reads or writes AgOpenGPS field files. A field is now one `field.geojson` plus a few sidecar files, and a job's worked area (coverage) is stored as tiles. AgShare needs to read and write all of these: a field uploaded from AgOpenWeb and downloaded again must come back with every part intact.

This doc is written for the AgShare developer and for the Claude instance working on AgShare. Hand the whole doc to that Claude session as the spec; section 7 is a checklist written for it directly.

- **One-way import:** AgOpenWeb imports AgOpenGPS files it finds in a field folder once, on open, then deletes them. It never writes AgOpenGPS formats.
- **No compatibility shims:** there is no installed base of older AgOpenWeb builds, so the formats below are the only ones to support.
- **Source of truth:** where this doc and the AgOpenWeb code disagree, the code wins. Section 7 lists the files to read.

## 2. Field folder layout

A field is a folder named after the field, under the user's Fields folder (default `Documents/AgOpenWeb/Fields/<field name>/`). Only `field.geojson` is required; every other file is optional and may be absent.

| File | Holds | Format | AgShare handling |
| --- | --- | --- | --- |
| `field.geojson` | Origin, boundaries, headland polygon, tracks, flags, headland lines, background image placement | GeoJSON, WGS84 | Parse and carry every feature |
| `field.origin` | Copy of the origin, `lat,lon` with 8 decimals, invariant culture (e.g. `32.59058587,-87.18025540`) | Text, one line | Write with every field; AgOpenWeb uses it to recover a lost origin |
| `background.png` | Background (satellite) image | PNG | Carry |
| `contours.geojson` | Contour guidance strips | GeoJSON, WGS84 | Parse and carry |
| `recorded-paths.geojson` | Recorded paths for playback | GeoJSON, WGS84 | Parse and carry |
| `elevation.csv` | Elevation log, one row per GPS sample | CSV | Parse and carry |
| `HeadlandSegments.json` | Headland builder segments | JSON, plane metres | Carry as an opaque file |
| `TramConfig.json`, `TramSystems.json` | Tram line settings | JSON | Carry as opaque files |
| `agshare.txt` | The AgShare field GUID this folder came from or was uploaded to | Text, one GUID | Write on download |
| `jobs/<task>/job.json` | One job (work session) on the field | JSON | Parse and carry |
| `jobs/<task>/coverage/` | That job's worked area | Tiles (section 5) | Carry; decode to display |

A folder may still contain AgOpenGPS files (`Field.txt`, `Boundary.txt`, `TrackLines.txt` and so on) until AgOpenWeb next opens it, at which point they are imported and deleted. AgShare should never write them into an AgOpenWeb field folder.

## 3. field.geojson

`field.geojson` is one GeoJSON `FeatureCollection`. Every feature has a `role` property that says which part of the field it is. Property names are camelCase, and the file is UTF-8, indented JSON.

### Coordinates

- Positions are WGS84 `[longitude, latitude]` in degrees, the GeoJSON order.
- Lines and polygons whose points carry a heading use `[longitude, latitude, heading]`. **The third value is a heading in radians, not an altitude**: 0 = north, clockwise, range 0 to 2π, computed as `atan2(ΔEasting, ΔNorthing)` to the next point. A missing third value reads as heading 0.
- AgOpenWeb works internally in metres on a flat plane centred on the field origin. It converts with the projection below, so a round-trip through AgOpenWeb returns the same WGS84 within float precision. Use the same formulas if AgShare needs plane coordinates (it already does for uploads).

```math
\begin{aligned}
m_{lat} &= 111132.92 - 559.82\cos 2\varphi_0 + 1.175\cos 4\varphi_0 - 0.0023\cos 6\varphi_0 \\
m_{lon}(\varphi) &= 111412.84\cos\varphi - 93.5\cos 3\varphi + 0.118\cos 5\varphi \\
N &= (\varphi - \varphi_0)\, m_{lat} \qquad E = (\lambda - \lambda_0)\, m_{lon}(\varphi)
\end{aligned}
```

φ₀ and λ₀ are the origin's latitude and longitude; φ and λ the point's, in degrees (cosines take radians). Longitude scale uses each point's own latitude, not the origin's.

### Feature roles

| Role | Geometry | Count | Properties |
| --- | --- | --- | --- |
| `metadata` | Point at the origin | Exactly 1, first | `name`, `originLatitude`, `originLongitude`, `convergence`, `areaHectares`, `createdDate`, `lastModifiedDate` |
| `outer-boundary` | Polygon, 1 ring, closed, with headings | 0 or 1 | `isDriveThrough`, `isHard`, `areaHectares` |
| `inner-boundary` | Polygon, as above | 0 or more | `isDriveThrough`, `isHard`, `areaHectares` |
| `headland` | Polygon, as above | 0 or 1 | `isDriveThrough`, `isHard`, `areaHectares` (only geometry is read) |
| `track` | LineString with headings, ≥ 2 points | 0 or more | `name`, `trackType`, `isClosed`, `noPassOffset`, `nudgeDistance`, `isVisible` |
| `flag` | Point `[lon, lat]` | 0 or more | `name`, `color`, `id`, `notes` (optional) |
| `headland-line` | LineString with headings | 0 or more | `name`, `moveDistance`, `mode`, `aPointIndex` |
| `background-image` | Polygon: image corners NW, NE, SE, SW, NW | 0 or 1 | `image` (file name, normally `background.png`); optional `mercatorMinX`, `mercatorMaxX`, `mercatorMinY`, `mercatorMaxY` |

Property details:

- **`originLatitude` / `originLongitude`:** the plane origin. They are authoritative; the Point geometry repeats them. **Never change the origin of an existing field**: coverage tiles (section 5) are stored in plane metres from this origin, so moving it shifts all worked area.
- **`convergence`:** carried over from AgOpenGPS `Field.txt`; normally 0. Preserve it.
- **Dates:** ISO 8601 round-trip format with offset, e.g. `2026-09-22T13:27:47.7408110-05:00`.
- **`areaHectares`:** informational, written by AgOpenWeb, ignored on read.
- **`isHard`:** hard boundary (autosteer stops at it). **`isDriveThrough`:** an inner boundary the tractor may drive through.
- **`trackType`:** integer. `2` AB line (2 points, infinite extension), `4` curve, `8` boundary outer, `16` boundary inner, `32` boundary curve, `64` water pivot, `128` recorded path, `256` contour. Unknown values read as `2`. AgShare's AB and curve lines map to `2` and `4`.
- **`isClosed`:** the curve is a loop. **`noPassOffset`:** don't offset this track by pass width. **`nudgeDistance`:** metres, signed. **`isVisible`:** defaults to `true` when absent.
- **Flag `color`:** `0` red, `1` green, `2` yellow, `3` blue, `4` orange, `5` purple, `6` cyan, `7` pink, `8` white, `9` black. **`id`:** the flag's unique number.
- **`headland-line`:** headland guidance paths built from the boundary. `moveDistance` in metres; `mode` and `aPointIndex` are AgOpenWeb builder state, preserve them as read.
- **Mercator bounds:** EPSG:3857 metres of the image, present only when the image was captured from a web-map tile service.

A reader must ignore roles and properties it doesn't know, and a writer must keep them: AgOpenWeb saves each part on its own and leaves other features untouched.

### Example

The metadata and boundary come from a real field (ring shortened to 3 points); the track and flag are illustrative:

```json
{
  "type": "FeatureCollection",
  "features": [
    {
      "type": "Feature",
      "geometry": { "type": "Point", "coordinates": [-87.1802554, 32.59058587] },
      "properties": {
        "role": "metadata",
        "name": "Test1",
        "originLatitude": 32.59058587,
        "originLongitude": -87.1802554,
        "convergence": 0,
        "areaHectares": 82.80888807535,
        "createdDate": "2026-09-22T13:27:47.7408110-05:00",
        "lastModifiedDate": "2026-09-22T13:27:47.7408110-05:00"
      }
    },
    {
      "type": "Feature",
      "geometry": { "type": "Polygon", "coordinates": [[
        [-87.18839000187572, 32.590196004348584, 1.5751224095689045],
        [-87.1878596212259, 32.59019406190269, 1.5751224095689045],
        [-87.18839000187572, 32.590196004348584, 1.5751224095689045]
      ]] },
      "properties": { "role": "outer-boundary", "isDriveThrough": false, "isHard": false, "areaHectares": 82.80888807535 }
    },
    {
      "type": "Feature",
      "geometry": { "type": "LineString", "coordinates": [
        [-87.1880, 32.5900, 0.0],
        [-87.1880, 32.5950, 0.0]
      ] },
      "properties": { "role": "track", "name": "AB 0", "trackType": 2, "isClosed": false, "noPassOffset": false, "nudgeDistance": 0, "isVisible": true }
    },
    {
      "type": "Feature",
      "geometry": { "type": "Point", "coordinates": [-87.1850, 32.5920] },
      "properties": { "role": "flag", "name": "Rock", "color": 0, "id": 1, "notes": "big one" }
    }
  ]
}
```

## 4. Sidecar files

These sit beside `field.geojson` so that data appended while driving never rewrites the field file. All coordinates convert through the origin in `field.geojson`. Each is optional; a missing file means no data.

- **`contours.geojson`:** a FeatureCollection of LineStrings `[lon, lat, heading]`, each with `"role": "contour"` and no other properties. One feature per finished contour strip, appended as strips finish.
- **`recorded-paths.geojson`:** a FeatureCollection of LineStrings `[lon, lat, heading]`, each with `"role": "recorded-path"`. The path in use has `"current": true` and no name; saved paths have a `name` instead. Per-vertex arrays in properties, one entry per coordinate: `speeds` (km/h, minimum 1.0) and `autoSteer` (booleans: section master on at that point). The file is deleted when it has no features.
- **`elevation.csv`:** UTF-8 without a byte-order mark. Header `latitude,longitude,elevation,fixQuality,easting,northing,heading,roll`, then one row per sample, logged after the vehicle moves at least 2.9 m. Elevation is GPS altitude minus antenna height, in metres. Easting and northing are plane metres; **heading and roll here are degrees**, unlike the radians in the GeoJSON files.
- **`background.png`:** the background image, placed by the `background-image` feature in `field.geojson`. The `image` property names the file, so a reader should use it rather than assume `background.png`.

All GeoJSON headings are radians, as in section 3.

## 5. Coverage tiles

A job's worked area is stored as square binary tiles on the field's plane grid: a 1-bit "covered" layer at 0.1 m that AgOpenWeb uses for section control, plus a coloured display layer. Only tiles that contain coverage exist as files.

```text
<field>/jobs/<task>/
  job.json
  coverage/
    manifest.json         written last; switches the display folder
    d/<tx>_<ty>.tile      detection: 1024 x 1024 cells at 0.1 m (102.4 m square)
    s<µm>/<tx>_<ty>.tile  display: 256 x 256 RGB565 pixels at the display cell size
```

### job.json

CamelCase JSON: `schemaVersion` (1), `id` (GUID), `fieldName`, `taskName` (same as the folder name), `workType`, `notes`, `startedAt`, `endedAt`, `lastOpenedAt` (ISO 8601), `status` (`"InProgress"`, `"Done"` or `"Abandoned"`), `distanceTraveledMeters`, `areaWorkedHectares`, `uTurnCount`. A `jobs/<task>/` folder without `job.json` is not listed as a job.

### manifest.json

PascalCase JSON. A previous good copy is kept as `manifest.json.bak`.

```json
{
  "SchemaVersion": 1,
  "DisplayCellSize": 0.35,
  "TotalWorkedArea": 412345.6,
  "MinE": -812.4,
  "MaxE": 655.0,
  "MinN": -301.7,
  "MaxN": 488.2
}
```

- `DisplayCellSize`: metres per display pixel. It names the display folder: `s` + round(cell × 10⁶), e.g. 0.35 m → `s350000`.
- `TotalWorkedArea`: square metres.
- `MinE` … `MaxN`: the coverage grid's bounds in plane metres. A manifest is valid only if `SchemaVersion` is 1, `DisplayCellSize` > 0, `MaxE` > `MinE` and `MaxN` > `MinN`.

### Tile addressing

All coordinates are plane metres from the field origin (section 3), with E east and N north.

|  | Detection (`d/`) | Display (`s<µm>/`) |
| --- | --- | --- |
| Cell | `cellE = floor(E / 0.1)`, `cellN = floor(N / 0.1)` | `px = floor(E / DisplayCellSize)`, `py` likewise |
| Tile key | `tx = cellE >> 10`, `ty = cellN >> 10` | `tx = px >> 8`, `ty = py >> 8` |
| Cell in tile | `x = cellE - tx*1024`, `y = cellN - ty*1024` | `x = px - tx*256`, `y = py - ty*256` |
| Row layout | Row `y` (0 = south) is 128 bytes; cell `x` is bit `x % 8` (LSB first) of byte `y*128 + x/8` | Row `y` (0 = south) is 256 little-endian u16; pixel at index `y*256 + x` |
| Value | 1 = covered | RGB565 colour; 0 = not covered |
| Raw payload size | 131,072 bytes | 131,072 bytes |

Tile keys can be negative (`-3_2.tile`): `>>` is an arithmetic shift, so use floor division, not truncation.

### Tile file format

Little-endian. A 32-byte header, then the payload.

| Offset | Type | Field |
| --- | --- | --- |
| 0 | u32 | Magic `0x54564F43` (bytes `C O V T`) |
| 4 | u8 | Version, 1 |
| 5 | u8 | Layer: 0 detection, 1 display |
| 6 | u8 | Encoding: 0 raw, 1 RLE |
| 7 | u8 | Reserved, 0 |
| 8 | i32 | `tx`, must match the file name |
| 12 | i32 | `ty`, must match the file name |
| 16 | f64 | Cell size in metres (0.1 for detection) |
| 24 | u32 | Payload length in bytes |
| 28 | u32 | CRC-32 (IEEE 802.3, as zlib) of the payload |

RLE payloads are records of `[run: u16][value]`, where the value is one byte for detection and one u16 for display; runs longer than 65,535 are split. The writer picks whichever of raw or RLE is smaller. The runs must expand to exactly the raw size.

### Reading and writing rules

- **Readers** skip any tile that fails the magic, version, layer, name/header match, length or CRC check. They lose that tile, not the job.
- **No manifest:** AgOpenWeb rebuilds one from the detection tile names. With no display tiles, it redraws the display layer from detection in one colour, so a writer can produce detection tiles only.
- **Writers:** write each tile to `<name>.tmp`, flush, then rename over the target. Write all tiles first and `manifest.json` last. Delete tiles that no longer hold coverage.
- **Cleared job:** an empty coverage is a manifest with no tile files; delete the old tiles rather than leaving them.
- **Display colours:** each section's colour converted with `((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3)`. Pure black would encode as 0 and read as uncovered.

For showing coverage on a web map, decode the detection layer (or the display layer for colour), then project each cell's corners from plane metres to WGS84 with the section 3 formulas.

## 6. Mapping to AgShare and AgOpenGPS

AgOpenWeb's current AgShare client uploads this payload (`AgShareUploaderService.cs`) and builds `field.geojson` from the same shape on download. The table shows where each AgShare field lands.

| AgShare field | field.geojson | Notes |
| --- | --- | --- |
| `name` | `metadata.name` | The folder name is the field name too |
| `origin.latitude`, `origin.longitude` | `metadata.originLatitude`, `originLongitude` | Keep as is; also write `field.origin` |
| `convergence` | `metadata.convergence` | Usually 0 |
| `boundary.outer` | `outer-boundary` ring | Close the ring; compute headings |
| `boundary.holes[]` | `inner-boundary` rings | `isDriveThrough`, `isHard` default false |
| `abLines[]` with `type: "AB"` | `track`, `trackType: 2`, 2 points | Both points carry the line's heading |
| `abLines[]` with `type: "Curve"` | `track`, `trackType: 4`, N points | Heading per point, to the next point |
| Field GUID | `agshare.txt` | One line, the GUID |
| `isPublic`, `sourceId` | Not stored | AgShare-side only |

Not carried by AgShare today, and required (AgShare must carry all of them): headland polygon, flags, headland lines, background image, contours, recorded paths, elevation, jobs and coverage, and the per-track `isClosed`, `noPassOffset`, `nudgeDistance`, `isVisible`.

| Part | Source in the field folder | Parse or carry |
| --- | --- | --- |
| Track settings `isClosed`, `noPassOffset`, `nudgeDistance`, `isVisible`, and track types other than AB and curve | `track` features | Parse |
| Boundary flags `isHard`, `isDriveThrough` | Boundary features | Parse |
| Headland polygon | `headland` feature | Parse |
| Flags | `flag` features | Parse |
| Headland lines | `headland-line` features | Parse |
| Background image | `background-image` feature + the PNG it names | Parse placement, carry image |
| Contours | `contours.geojson` | Parse |
| Recorded paths | `recorded-paths.geojson` | Parse |
| Elevation log | `elevation.csv` | Parse |
| Jobs | `jobs/<task>/job.json` | Parse |
| Coverage | `jobs/<task>/coverage/` | Carry; decode to display |
| Headland builder and tram settings | `HeadlandSegments.json`, `TramConfig.json`, `TramSystems.json` | Carry as opaque files |

The simplest lossless design is to store each uploaded field folder's files as they are, and parse them for AgShare's own map, search and listing. Storing `field.geojson` whole also keeps feature roles and properties that a future AgOpenWeb adds. Ask the developer before choosing between that and a field-by-field API.

AgOpenWeb's AgShare client (`Shared/AgOpenWeb.Services/AgShare/`) sends only the payload above today. It will be updated to send and receive the full field once AgShare's API for it exists.

For reference, the AgOpenGPS files AgOpenWeb imports, and where they go:

| AgOpenGPS file | AgOpenWeb |
| --- | --- |
| `Field.txt` | `metadata` feature (origin, convergence) |
| `Boundary.txt` | `outer-boundary`, `inner-boundary` |
| `Headland.Txt` | `headland` |
| `TrackLines.txt`, `ABLines.txt` | `track` features |
| `Flags.txt` | `flag` features |
| `Headlines.txt` | `headland-line` features |
| `BackPic.txt` + `BackPic.png` | `background-image` feature + `background.png` |
| `Contour.txt` | `contours.geojson` |
| `RecPath.txt`, `*.rec` | `recorded-paths.geojson` |
| `Elevation.txt` | `elevation.csv` |
| `Sections.txt` | Coverage tiles |
| `TramLines.txt` | Dropped (tram lines are regenerated) |

## 7. Instructions for AgShare's Claude

You are updating AgShare to exchange fields with AgOpenWeb. Treat sections 2 to 6 as the spec, and the AgOpenWeb source files below as the reference implementation when anything is unclear. Ask the developer before changing AgShare's own API or storage.

### Checklist

- [ ] Design AgShare's storage and API to carry a whole field: every file in section 2's table, including `jobs/` with its coverage tiles. Agree the shape with the developer first.
- [ ] Export: write a complete field folder: `field.geojson` (`metadata` first, then every feature), `field.origin`, `agshare.txt`, the sidecar files, the background image, the opaque JSON files and `jobs/`.
- [ ] Import: read `field.geojson` by `role` and keep every feature, including roles and properties AgShare doesn't use.
- [ ] Parse and display boundaries, headland polygon, tracks, flags, headland lines, background image, contours, recorded paths and elevation.
- [ ] Decode coverage tiles to show each job's worked area (section 5), and list jobs from `job.json`.
- [ ] Use the section 3 projection wherever AgShare converts between WGS84 and plane metres, so values match AgOpenWeb exactly.
- [ ] Write headings (radians, 0 = north, clockwise) as the third coordinate of boundary rings, tracks, headland lines, contours and recorded paths.
- [ ] Tests: round-trip a full field folder (AgOpenWeb files → AgShare → files) and check every file comes back equivalent, coordinates within 1e-9 degrees and tiles byte-identical once decoded; decode a tile with a hand-built RLE payload and check its CRC.

### Validation and edge cases

- Exactly one `metadata` feature; reject the file without it.
- Polygons: read ring 0 only, need at least 3 points, and drop a closing point that repeats the first (within 1 mm in plane metres). Write rings closed.
- Tracks need at least 2 points. AB lines are exactly 2 points; their extension past the points is implied.
- Treat the third coordinate as a heading, never as altitude. Generic GeoJSON tools will show it as Z.
- Numbers are invariant culture (`.` decimal separator) everywhere, including `field.origin` and `elevation.csv`.
- Never change `originLatitude`/`originLongitude` of a field that has jobs: coverage is anchored to it.
- Don't write AgOpenGPS files into an AgOpenWeb field folder; AgOpenWeb would import and delete them on open, replacing newer data.
- Write files through a temp file and rename, as AgOpenWeb does, so a field being opened never sees a half-written file.

### Reference source (AgOpenWeb, `develop` branch)

Repository: [AgOpenGPS-Official/AgOpenWeb](https://github.com/AgOpenGPS-Official/AgOpenWeb).

| File | What it defines |
| --- | --- |
| `Shared/AgOpenWeb.Models/GeoJson/GeoJsonModels.cs` | Role names and property keys |
| `Shared/AgOpenWeb.Services/GeoJson/GeoJsonFieldService.cs` | `field.geojson` read and write |
| `Shared/AgOpenWeb.Services/GeoJson/GeoJsonFieldService.Sidecars.cs` | `contours.geojson`, `recorded-paths.geojson` |
| `Shared/AgOpenWeb.Models/Base/LocalPlane.cs` | WGS84 ↔ plane projection |
| `Shared/AgOpenWeb.Models/Track/Track.cs` | `TrackType` values |
| `Shared/AgOpenWeb.Models/Field/Flag.cs` | Flag colours |
| `Shared/AgOpenWeb.Services/ElevationLogService.cs` | `elevation.csv` |
| `Shared/AgOpenWeb.Services/Fields/JobJsonService.cs` | `job.json` |
| `Shared/AgOpenWeb.Services/Coverage/CoverageTileStore.cs` | Tile file format, RLE, CRC, manifest |
| `Shared/AgOpenWeb.Services/Coverage/CoverageMapService.cs` | Cell and tile addressing (`MarkCellCovered`, `WriteTiles`, `LoadFromFile`) |
| `Shared/AgOpenWeb.Services/AgShare/` | AgOpenWeb's current AgShare client (upload, download) |
| `Plans/Completed/FILE_FORMAT_MODERNIZATION_PLAN.md`, `COVERAGE_TILED_PERSISTENCE_PLAN.md` | Design history |
