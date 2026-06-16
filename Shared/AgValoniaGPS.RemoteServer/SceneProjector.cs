// Projects the live ApplicationState (the pipeline's single source of truth)
// into the wire contract. This is the litmus test in action: the data the map
// needs is filled from the pipeline/state, never from view-state.

using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Models.State;
using AgValoniaGPS.Services.Interfaces;

namespace AgValoniaGPS.RemoteServer;

public sealed class SceneProjector
{
    private readonly ApplicationState _state;
    private readonly ISectionControlService _sections;
    private readonly IToolPositionService _tool;
    private readonly ConfigurationStore _config;
    private readonly ICoverageMapService _coverage;
    private readonly IJobService _jobs;

    public SceneProjector(ApplicationState state, ISectionControlService sections,
        IToolPositionService tool, ConfigurationStore config,
        ICoverageMapService coverage, IJobService jobs)
    {
        _state = state;
        _sections = sections;
        _tool = tool;
        _config = config;
        _coverage = coverage;
        _jobs = jobs;
    }

    public SceneDto BuildScene(long version)
    {
        var f = _state.Field;

        // The live boundary the UI draws lives on ActiveField.Boundary (a single
        // Boundary with an outer ring + optional inner holes) — NOT the (unused)
        // Field.Boundaries collection.
        var boundaries = new List<IReadOnlyList<Vec2Dto>>();
        var bnd = f.ActiveField?.Boundary;
        if (bnd is not null)
        {
            AddRing(boundaries, bnd.OuterBoundary);
            if (bnd.InnerBoundaries != null)
                foreach (var inner in bnd.InnerBoundaries.ToArray())
                    AddRing(boundaries, inner);
        }

        var tracks = f.Tracks.ToArray()
            .Where(t => t.IsVisible && t.Points.Count >= 2)
            .Select((t, i) => new TrackDto(
                i.ToString(),
                t.Name,
                (int)t.Type,
                t.Points.Select(p => new Vec2Dto(p.Easting, p.Northing)).ToList()))
            .ToList();

        // Headland (the green inner line) — the live one the app draws lives on
        // State.Field.HeadlandLine, set in lockstep with the VM's _currentHeadlandLine
        // (NOT Boundary.HeadlandPolygon, which is only seeded on file load).
        IReadOnlyList<Vec2Dto>? headland = null;
        var hl = f.HeadlandLine;
        if (hl != null && hl.Count >= 3)
            headland = hl.Select(p => new Vec2Dto(p.Easting, p.Northing)).ToList();

        // The followed offset line (DisplayLine) — the line the tractor is
        // steering to, parallel to the active reference track and shifted by the
        // current pass. Snapshot the list (it's mutated on the UI thread).
        IReadOnlyList<Vec2Dto>? guidanceLine = null;
        var dl = _state.Guidance.DisplayLine?.ToArray();
        if (dl is { Length: >= 2 })
            guidanceLine = dl.Select(p => new Vec2Dto(p.Easting, p.Northing)).ToList();

        // Tool/section layout — static spans (PositionLeft/Right, offsets from the
        // tool centerline). The pose is per-Tick; the client transforms these by it.
        int nSec = Math.Clamp(_sections.NumSections, 0, SectionState.MaxSections);
        var states = _sections.SectionStates;
        var toolSections = new List<SectionSpanDto>(nSec);
        for (int i = 0; i < nSec && i < states.Count; i++)
            toolSections.Add(new SectionSpanDto(states[i].PositionLeft, states[i].PositionRight));

        // Planned U-turn path through the headland (when a turn is active).
        IReadOnlyList<Vec2Dto>? uTurnPath = null;
        var tp = _state.YouTurn.TurnPath?.ToArray();
        if (tp is { Length: >= 2 })
            uTurnPath = tp.Select(p => new Vec2Dto(p.Easting, p.Northing)).ToList();

        // The next pass the tractor will pick up after the turn (cyan until it
        // becomes the active line). State.YouTurn.NextTrack is the upcoming Track.
        IReadOnlyList<Vec2Dto>? nextTrack = null;
        var nt = _state.YouTurn.NextTrack?.Points?.ToArray();
        if (nt is { Length: >= 2 })
            nextTrack = nt.Select(p => new Vec2Dto(p.Easting, p.Northing)).ToList();

        // Background imagery: just the world rectangle + a per-image version
        // (the PNG itself is fetched over HTTP at /backpic.png?v=version).
        ImageryDto? imagery = null;
        var im = f.Imagery;
        if (im is not null)
            imagery = new ImageryDto(im.MinE, im.MinN, im.MaxE, im.MaxN, ImageVersion(im.Path));

        return new SceneDto(
            version,
            f.OriginLatitude,
            f.OriginLongitude,
            f.FieldName,
            f.ActiveField != null,
            boundaries,
            tracks,
            headland,
            guidanceLine,
            toolSections,
            uTurnPath,
            nextTrack,
            imagery);
    }

    // Deterministic per-path version (string.GetHashCode is process-randomized).
    private static long ImageVersion(string path)
    {
        long h = 17;
        foreach (char c in path) h = h * 31 + c;
        return h;
    }

    private static void AddRing(List<IReadOnlyList<Vec2Dto>> rings, BoundaryPolygon? poly)
    {
        if (poly is { Points.Count: >= 3 })
            rings.Add(poly.Points.Select(p => new Vec2Dto(p.Easting, p.Northing)).ToList());
    }

    public TickDto BuildTick(long sceneVersion)
    {
        var v = _state.Vehicle;
        // Section on/off comes from SectionControlService (the authoritative source
        // coverage paints from) — NOT State.Sections, whose per-section flags are
        // never written. IsOn = the section is on/laying product.
        // Per-section display state = the canonical 6-state ColorCode (same source
        // as the native section bar / map renderer): 0 off(red) 1 manual-on(yellow)
        // 2 auto-on(green) 3 turning-off(cyan) 4 turning-on(orange) 5 auto-off(gray).
        var secStates = _sections.SectionStates;
        int n = Math.Clamp(_sections.NumSections, 0, SectionState.MaxSections);
        var sections = new byte[n];
        for (int i = 0; i < n && i < secStates.Count; i++)
            sections[i] = (byte)secStates[i].ColorCode;

        var g = _state.Guidance;

        return new TickDto(
            sceneVersion,
            // VehicleState.Heading is DEGREES (0 = north, clockwise); the wire
            // carries RADIANS so the client can ctx.rotate / sin / cos directly
            // (matching the native map control's headingRadians convention).
            // Without this the marker spun ~57× per revolution during turns.
            new PoseDto(v.Easting, v.Northing, v.Heading * System.Math.PI / 180.0, v.Speed),
            v.FixQuality,
            sections,
            g.CrossTrackError,
            // Lightbar gate — mirror the native exactly (MainWindow feeds the
            // LightBarPanel with hasGuidance = HasActiveTrack || contour || rec-path,
            // NOT autosteer-engaged). IsGuidanceActive is dead (never set true).
            _state.Field.ActiveTrack != null
                || g.IsContourMode
                || _state.RecordedPath.IsDrivingRecordedPath,
            g.CurrentLineLabel,
            _state.Field.ActiveTrack?.Name,
            _tool.ToolPosition.Easting,
            _tool.ToolPosition.Northing,
            _tool.ToolHeading,
            _tool.IsToolPositionReady,
            // Operational state (right-nav toolbar). Engaged + contour + U-turn
            // direction/distance come from state; the three mode flags from the
            // VM mirror (ApplicationState.Operation).
            _state.Operation.IsAutoSteerEngaged,
            _state.Operation.IsAutoSteerAvailable,
            _state.Operation.IsContourOn,
            _state.Operation.IsSectionAutoMaster,
            _state.Operation.IsSectionManualAll,
            _state.Operation.IsYouTurnEnabled,
            _state.YouTurn.IsTurnLeft,
            _state.YouTurn.DistanceToTrigger,
            _state.Field.ActiveTrack?.IsClosed == true,
            _state.Vehicle.Roll);
    }

    // Top status-bar readouts (Phase 1). All state-projected: fix/age/sats from
    // VehicleState, module health + IPs from ConnectionState, units from config.
    public StatusDto BuildStatus()
    {
        var v = _state.Vehicle;
        var c = _state.Connections;
        var cfg = _config.Connections;
        return new StatusDto(
            v.FixQuality,
            v.FixQualityText,
            v.Age,
            v.SatelliteCount,
            _config.IsMetric,
            c.IsGpsDataOk,
            c.IsImuDataOk,
            c.IsAutoSteerDataOk,
            c.IsMachineDataOk,
            c.ImuIpAddress ?? "",
            c.AutoSteerIpAddress ?? "",
            c.MachineIpAddress ?? "",
            cfg.IsGpsConfigured,
            cfg.IsImuConfigured,
            cfg.IsAutoSteerConfigured,
            cfg.IsMachineConfigured,
            _jobs.ActiveJob?.TaskName ?? "",
            _coverage.TotalWorkedArea,
            v.Latitude,
            v.Longitude,
            v.Altitude,
            v.Hdop,
            _state.Simulator.IsEnabled,
            _state.Simulator.SpeedKph,
            _state.Simulator.SteerAngle,
            _state.Simulator.Is10x);
    }

    /// <summary>
    /// Cheap change-detector so the broadcaster only re-sends the Scene when the
    /// static-ish geometry actually changes (field swap, boundary/track edits).
    /// </summary>
    public long SceneFingerprint()
    {
        var f = _state.Field;
        long h = 17;
        h = h * 31 + f.FieldName.GetHashCode();
        var bnd = f.ActiveField?.Boundary;
        h = h * 31 + (bnd?.OuterBoundary?.Points.Count ?? 0);
        if (bnd?.InnerBoundaries != null)
            foreach (var inner in bnd.InnerBoundaries.ToArray()) h = h * 31 + inner.Points.Count;
        h = h * 31 + (f.HeadlandLine?.Count ?? 0);
        h = h * 31 + f.Tracks.Count;
        foreach (var t in f.Tracks.ToArray()) h = h * 31 + t.Points.Count;

        // Followed offset line: re-send the Scene when the pass changes. The list
        // is replaced (not mutated) per pass, so count + first-point (rounded to
        // 0.1 m to avoid float jitter) detects the shift without per-tick churn.
        var dl = _state.Guidance.DisplayLine;
        if (dl is { Count: > 0 })
        {
            h = h * 31 + dl.Count;
            var p0 = dl[0];
            h = h * 31 + (long)System.Math.Round(p0.Easting * 10);
            h = h * 31 + (long)System.Math.Round(p0.Northing * 10);
        }

        // Section layout — re-send on count or a span change (tool/section config).
        int nSec = _sections.NumSections;
        h = h * 31 + nSec;
        var states = _sections.SectionStates;
        for (int i = 0; i < nSec && i < states.Count; i++)
            h = h * 31 + (long)System.Math.Round((states[i].PositionLeft + states[i].PositionRight) * 100);

        // U-turn path: appears/changes per turn (replaced, not mutated).
        var tp = _state.YouTurn.TurnPath;
        if (tp is { Count: > 0 })
        {
            h = h * 31 + tp.Count;
            var p0 = tp[0];
            h = h * 31 + (long)System.Math.Round(p0.Easting * 10);
            h = h * 31 + (long)System.Math.Round(p0.Northing * 10);
        }

        // Next track: appears when a turn is set up, clears once picked up.
        var nt = _state.YouTurn.NextTrack?.Points;
        if (nt is { Count: > 0 })
        {
            h = h * 31 + nt.Count;
            var p0 = nt[0];
            h = h * 31 + (long)System.Math.Round(p0.Easting * 10);
            h = h * 31 + (long)System.Math.Round(p0.Northing * 10);
        }

        // Imagery: re-send (client reloads /backpic.png) when it toggles or the
        // field's image changes.
        var im = _state.Field.Imagery;
        h = h * 31 + (im is not null ? ImageVersion(im.Path) : 0);
        return h;
    }
}
