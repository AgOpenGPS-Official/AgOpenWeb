// Projects the live ApplicationState (the pipeline's single source of truth)
// into the wire contract. This is the litmus test in action: the data the map
// needs is filled from the pipeline/state, never from view-state.

using AgValoniaGPS.Models;
using AgValoniaGPS.Models.State;

namespace AgValoniaGPS.RemoteServer;

public sealed class SceneProjector
{
    private readonly ApplicationState _state;

    public SceneProjector(ApplicationState state) => _state = state;

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

        return new SceneDto(
            version,
            f.OriginLatitude,
            f.OriginLongitude,
            f.FieldName,
            f.ActiveField != null,
            boundaries,
            tracks,
            headland,
            guidanceLine);
    }

    private static void AddRing(List<IReadOnlyList<Vec2Dto>> rings, BoundaryPolygon? poly)
    {
        if (poly is { Points.Count: >= 3 })
            rings.Add(poly.Points.Select(p => new Vec2Dto(p.Easting, p.Northing)).ToList());
    }

    public TickDto BuildTick(long sceneVersion)
    {
        var v = _state.Vehicle;
        var s = _state.Sections;
        int n = Math.Clamp(s.NumberOfSections, 0, SectionState.MaxSections);
        var sections = new bool[n];
        for (int i = 0; i < n; i++) sections[i] = s.GetSectionActive(i);

        var g = _state.Guidance;

        return new TickDto(
            sceneVersion,
            new PoseDto(v.Easting, v.Northing, v.Heading, v.Speed),
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
            _state.Field.ActiveTrack?.Name);
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
        return h;
    }
}
