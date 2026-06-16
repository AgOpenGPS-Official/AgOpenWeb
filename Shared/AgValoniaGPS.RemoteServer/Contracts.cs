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
    IReadOnlyList<Vec2Dto>? NextTrack, // the next pass to pick up after the turn (cyan), if any
    ImageryDto? Imagery); // background field image world-rect + version (PNG served over HTTP)

/// <summary>Background-imagery placement: the field-local world rectangle the
/// PNG (fetched from /backpic.png) covers, plus a version that changes per field
/// so the client cache-busts. Null when no enabled imagery.</summary>
public record ImageryDto(double MinE, double MinN, double MaxE, double MaxN, long Version);

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
    bool ToolReady,
    // Operational state for the right-nav toolbar (Phase 3). Lives at Tick rate so
    // the autosteer 3-state, U-turn arming and distance-to-trigger stay live.
    bool IsAutoSteerEngaged,
    bool AutoSteerAvailable,   // a track is active → autosteer may engage (else greyed)
    bool IsContourMode,
    bool IsSectionAutoMaster,
    bool IsSectionManualAll,
    bool IsYouTurnEnabled,
    bool TurnIsLeft,
    double DistanceToTrigger,
    bool IsActiveTrackClosed,
    double Roll); // vehicle roll angle (degrees) for the roll gauge

/// <summary>Top status-bar readouts (Phase 1), sent at a low rate. GPS fix quality
/// + correction age + sat count; the units preference (so the client formats speed
/// itself — speed rides the Tick); and the four module connection states
/// (GPS/IMU/AutoSteer/Machine) with their detected IPs ("" = not detected).</summary>
public record StatusDto(
    int FixQuality,
    string FixQualityText,
    double Age,
    int SatelliteCount,
    bool IsMetric,
    bool GpsOk,
    bool ImuOk,
    bool AutoSteerOk,
    bool MachineOk,
    string ImuIp,
    string AutoSteerIp,
    string MachineIp,
    // Module-configured flags (the aggregate "Modules" dot is green only when every
    // CONFIGURED module is present — mirrors MainViewModel.ModuleStatusKind).
    bool GpsConfigured,
    bool ImuConfigured,
    bool AutoSteerConfigured,
    bool MachineConfigured,
    // Rotating bottom-line inputs the client can't derive from the Scene: the active
    // job's task name and the worked area (m²). Field name, workable area (boundary/
    // headland), tool width and speed the client already has, so it formats + rotates
    // the three pages (Field / Stats / AB-line) itself, matching the native strip.
    string JobName,
    double WorkedAreaSqM,
    // GPS-detail card (Phase 5): lat/lon (degrees), altitude (m) and HDOP for the
    // popup toggled by the strip's fix dot. Rides the ~2 Hz Status — plenty for a
    // readout. (Heading + roll for that card ride the 10 Hz Tick; sats/age/fix are
    // already above.) Lat/lon need f64 precision; alt/hdop go out as f32.
    double Latitude,
    double Longitude,
    double Altitude,
    double Hdop,
    // Simulator panel (Phase 6): enabled + raw speed (kph, before 10×) + steer angle
    // (deg) + 10× toggle. The client applies the 10× multiplier + unit formatting.
    bool SimEnabled,
    double SimSpeedKph,
    double SimSteerAngle,
    bool Sim10x);

/// <summary>Remote-actuation authority state (Phase 2). Broadcast on change; the
/// client compares HolderId to its own id (sent once in the Hello frame) to know
/// whether it is the holder. Held=false means no client has control.</summary>
public record ControlStateDto(bool Held, string HolderId, string HolderName);
