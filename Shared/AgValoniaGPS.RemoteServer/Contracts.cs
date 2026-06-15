// Phase 1 wire contract (deliberate, not VM-mirroring). Field-local meters;
// the host sends the field origin once in the Scene, everything else is x=E, y=N.
// Camera is NOT here — it's client-owned (REMOTE_WEB_UI_SPLIT.md §5 rule 3).

namespace AgValoniaGPS.RemoteServer;

/// <summary>A field-local point in meters (x = easting, y = northing).</summary>
public record Vec2Dto(double E, double N);

public record TrackDto(string Id, string Name, int Type, IReadOnlyList<Vec2Dto> Points);

/// <summary>Static-ish geometry: sent on connect and whenever the fingerprint changes.</summary>
public record SceneDto(
    long Version,
    double OriginLat,
    double OriginLon,
    string FieldName,
    bool HasField,
    IReadOnlyList<IReadOnlyList<Vec2Dto>> Boundaries, // each = one outer ring
    IReadOnlyList<TrackDto> Tracks,
    IReadOnlyList<Vec2Dto>? Headland, // inner headland ring (green line), if any
    IReadOnlyList<Vec2Dto>? GuidanceLine, // the followed offset line (magenta), if guiding
    IReadOnlyList<SectionSpanDto> ToolSections, // section spans (static layout); pose is per-Tick
    IReadOnlyList<Vec2Dto>? UTurnPath, // the planned U-turn arc through the headland (green), if active
    IReadOnlyList<Vec2Dto>? NextTrack); // the next pass to pick up after the turn (cyan), if any

/// <summary>One section's signed offsets from the tool centerline (meters; left
/// negative, right positive), perpendicular to the tool heading. Static-ish —
/// changes only with tool/section config.</summary>
public record SectionSpanDto(double Left, double Right);

/// <summary>Vehicle pose. Heading is RADIANS (0 = north, clockwise); Speed is m/s.</summary>
public record PoseDto(double E, double N, double Heading, double Speed);

// --- Coverage (Phase 2). Display layer streamed as RGB cells; init carries the
//     grid geometry, cells carry painted cells (snapshot on connect, deltas after). ---
public record CoverageInitDto(double CellSize, double OriginE, double OriginN, int Width, int Height);

/// <summary>Flat triples: [cellX, cellY, packedRgb, ...] where packedRgb = (R&lt;&lt;16)|(G&lt;&lt;8)|B.</summary>
public record CoverageCellsDto(int[] Cells);

/// <summary>~10 Hz dynamic feed the client extrapolates/draws from.</summary>
public record TickDto(
    long SceneVersion,
    PoseDto Pose,
    int Fix,
    byte[] Sections, // per-section SectionControlState.ColorCode: 0 off(red) 1 manual-on(yellow)
                     // 2 auto-on(green) 3 turning-off(cyan) 4 turning-on(orange) 5 auto-off(gray)
    // Guidance HUD: cross-track error (m, +right of line), whether guidance is
    // engaged, the line label ("3L"), and the name of the followed track so the
    // client can highlight it among the Scene tracks. CrossTrackError is only
    // meaningful when GuidanceActive.
    double CrossTrackError,
    bool GuidanceActive,
    string LineLabel,
    string? ActiveTrackName,
    // Tool pose (the section spans in the Scene are drawn relative to this).
    // Matches SectionControlService.GetSectionWorldPosition's frame: world edge =
    // (ToolE,ToolN) + (sin,cos)(ToolHeading + π/2) × span. ToolReady gates drawing.
    double ToolE,
    double ToolN,
    double ToolHeading,
    bool ToolReady);
