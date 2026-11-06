// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using AgOpenWeb.ViewModels;

namespace AgOpenWeb.FieldScenarios.Tests;

/// <summary>A copy of everything the field features own, taken on the host loop in one go so
/// the checks see one consistent moment. Field-local metres (E/N).</summary>
public sealed class FieldSnapshot
{
    public bool IsFieldOpen;
    public string FieldName = "";
    public string? FieldDirectory;
    public bool ActiveFieldSet;
    public bool IsBusy;
    public double OriginLat, OriginLon;

    public List<(double E, double N)>? Outer;
    public List<List<(double E, double N)>> Inner = new();
    public List<(bool DriveThrough, bool Hard)> InnerFlags = new();

    public List<(double E, double N)>? Headland;
    public bool HasHeadlandFlag;
    public List<HeadlandSegmentInfo> HeadlandSegments = new();

    public List<TrackInfo> Tracks = new();
    public List<string> StateTrackNames = new();
    public string? SelectedTrack;
    public string? ActiveTrack;

    public List<TramSystemInfo> TramSystems = new();
    public List<List<(double E, double N)>> TramLines = new();
    public List<(double E, double N)> TramOuterTrack = new();
    public List<(double E, double N)> TramInnerTrack = new();
    public List<List<(double E, double N)>> TramExtraLines = new();

    public List<(double E, double N, string Name)> Flags = new();

    public bool IsContourOn;
    public bool IsBoundaryRecording;
    public int BoundaryRecordedPoints;
    public bool IsPromptPending;
    public string PromptText = "";
    public double ToolWidth;

    public sealed record HeadlandSegmentInfo(string Name, string Type, double Offset, int OffsetPoints);

    public sealed record TrackInfo(string Name, string Type, List<(double E, double N, double H)> Points,
        bool Visible, double Nudge, bool IsClosed);

    public sealed record TramSystemInfo(string Name, string? RefTrack, int RefBoundary, double Width,
        double Offset, int Passes, bool Enabled, string Mode, string Direction);

    /// <summary>Must run on the host loop (<see cref="FieldScenarioRig.ReadAsync{T}"/>).</summary>
    public static FieldSnapshot Take(FieldScenarioRig rig)
    {
        var vm = rig.Vm;
        var st = rig.State;
        var s = new FieldSnapshot
        {
            IsFieldOpen = vm.IsFieldOpen,
            FieldName = vm.CurrentFieldName ?? "",
            ActiveFieldSet = st.Field.ActiveField != null,
            FieldDirectory = st.Field.ActiveField?.DirectoryPath,
            IsBusy = st.UI.IsBusy,
            OriginLat = st.Field.OriginLatitude,
            OriginLon = st.Field.OriginLongitude,
            HasHeadlandFlag = vm.HasHeadland,
            IsContourOn = vm.IsContourModeOn,
            IsBoundaryRecording = vm.IsBoundaryRecording,
            BoundaryRecordedPoints = vm.BoundaryPointCount,
            IsPromptPending = vm.IsPromptPending,
            PromptText = vm.IsPromptPending ? (vm.ConfirmationDialogTitle ?? vm.ErrorDialogTitle ?? "") + ": " + (vm.ConfirmationDialogMessage ?? vm.ErrorDialogMessage ?? "") : "",
            ToolWidth = AgOpenWeb.Models.Configuration.ConfigurationStore.Instance.ActualToolWidth,
        };
        var b = st.Field.CurrentBoundary;
        if (b?.OuterBoundary is { } ob && ob.Points.Count > 0)
            s.Outer = ob.Points.Select(p => (p.Easting, p.Northing)).ToList();
        if (b != null)
            foreach (var ib in b.InnerBoundaries)
            {
                s.Inner.Add(ib.Points.Select(p => (p.Easting, p.Northing)).ToList());
                s.InnerFlags.Add((ib.IsDriveThrough, ib.IsHard));
            }
        if (st.Field.HeadlandLine is { Count: > 0 } hl)
            s.Headland = hl.Select(p => (p.Easting, p.Northing)).ToList();
        foreach (var seg in vm.HeadlandSegments)
            s.HeadlandSegments.Add(new HeadlandSegmentInfo(seg.Name, seg.Type.ToString(), seg.Offset, seg.OffsetPoints.Count));
        foreach (var t in vm.SavedTracks)
            s.Tracks.Add(new TrackInfo(t.Name, t.Type.ToString(),
                t.Points.Select(p => (p.Easting, p.Northing, p.Heading)).ToList(), t.IsVisible, t.NudgeDistance, t.IsClosed));
        s.StateTrackNames = st.Field.Tracks.Select(t => t.Name).ToList();
        s.SelectedTrack = vm.SelectedTrack?.Name;
        s.ActiveTrack = st.Field.ActiveTrack?.Name;
        foreach (var ts in vm.TramSystems)
            s.TramSystems.Add(new TramSystemInfo(ts.Name, ts.ReferenceTrackName, ts.ReferenceBoundaryIndex, ts.TramWidth,
                ts.Offset, ts.PassCount, ts.IsEnabled, ts.Mode.ToString(), ts.Direction.ToString()));
        foreach (var line in rig.Tram.ParallelTramLines)
            s.TramLines.Add(line.Select(p => (p.Easting, p.Northing)).ToList());
        s.TramOuterTrack = rig.Tram.OuterBoundaryTrack.Select(p => (p.Easting, p.Northing)).ToList();
        s.TramInnerTrack = rig.Tram.InnerBoundaryTrack.Select(p => (p.Easting, p.Northing)).ToList();
        foreach (var line in rig.Tram.BoundaryExtraLines)
            s.TramExtraLines.Add(line.Select(p => (p.Easting, p.Northing)).ToList());
        foreach (var f in st.Field.Flags)
            s.Flags.Add((f.Easting, f.Northing, f.Name));
        return s;
    }

    /// <summary>A short line for the report.</summary>
    public string Summary()
    {
        var inv = CultureInfo.InvariantCulture;
        if (!IsFieldOpen) return "no field open";
        string area = Outer is { Count: >= 3 } ? (Poly.Area(Outer) / 10000).ToString("F2", inv) + " ha" : "none";
        string hl = Headland is { Count: >= 3 } ? (Poly.Area(Headland) / 10000).ToString("F2", inv) + " ha" : "none";
        return string.Create(inv,
            $"field '{FieldName}': outer {Outer?.Count ?? 0} pts ({area}), {Inner.Count} inner, headland {hl} ({HeadlandSegments.Count} seg), {Tracks.Count} tracks, {TramSystems.Count} tram sys / {TramLines.Count} lines, {Flags.Count} flags");
    }
}

/// <summary>Plane polygon helpers on (E, N) metres.</summary>
public static class Poly
{
    public static double Area(IReadOnlyList<(double E, double N)> p)
    {
        double a = 0;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
            a += (p[j].E + p[i].E) * (p[j].N - p[i].N);
        return Math.Abs(a / 2);
    }

    public static bool Inside((double E, double N) q, IReadOnlyList<(double E, double N)> p)
    {
        bool inside = false;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
            if ((p[i].N > q.N) != (p[j].N > q.N)
                && q.E < (p[j].E - p[i].E) * (q.N - p[i].N) / (p[j].N - p[i].N) + p[i].E)
                inside = !inside;
        return inside;
    }

    public static double DistToSegment((double E, double N) q, (double E, double N) a, (double E, double N) b)
    {
        double dx = b.E - a.E, dy = b.N - a.N;
        double l2 = dx * dx + dy * dy;
        double t = l2 < 1e-12 ? 0 : Math.Clamp(((q.E - a.E) * dx + (q.N - a.N) * dy) / l2, 0, 1);
        double ex = a.E + t * dx - q.E, ey = a.N + t * dy - q.N;
        return Math.Sqrt(ex * ex + ey * ey);
    }

    public static double DistToRing((double E, double N) q, IReadOnlyList<(double E, double N)> ring)
    {
        double best = double.MaxValue;
        for (int i = 0; i < ring.Count; i++)
            best = Math.Min(best, DistToSegment(q, ring[i], ring[(i + 1) % ring.Count]));
        return best;
    }

    private static double Cross((double E, double N) o, (double E, double N) a, (double E, double N) b) =>
        (a.E - o.E) * (b.N - o.N) - (a.N - o.N) * (b.E - o.E);

    /// <summary>Proper crossing of two segments (touching at an end does not count).</summary>
    public static bool Cross((double E, double N) a, (double E, double N) b, (double E, double N) c, (double E, double N) d)
    {
        double d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
        return ((d1 > 1e-9 && d2 < -1e-9) || (d1 < -1e-9 && d2 > 1e-9))
            && ((d3 > 1e-9 && d4 < -1e-9) || (d3 < -1e-9 && d4 > 1e-9));
    }

    /// <summary>First pair of non-adjacent edges of a closed ring that cross, or null.</summary>
    public static (int I, int J, (double E, double N) At)? SelfCrossing(IReadOnlyList<(double E, double N)> p)
    {
        int n = p.Count;
        if (n < 4) return null;
        for (int i = 0; i < n; i++)
        {
            var a = p[i]; var b = p[(i + 1) % n];
            for (int j = i + 2; j < n; j++)
            {
                if (i == 0 && j == n - 1) continue; // adjacent through the closing edge
                var c = p[j]; var d = p[(j + 1) % n];
                if (Cross(a, b, c, d)) return (i, j, ((a.E + b.E) / 2, (a.N + b.N) / 2));
            }
        }
        return null;
    }

    /// <summary>Does any edge of ring A cross any edge of ring B?</summary>
    public static bool RingsCross(IReadOnlyList<(double E, double N)> a, IReadOnlyList<(double E, double N)> b)
    {
        for (int i = 0; i < a.Count; i++)
            for (int j = 0; j < b.Count; j++)
                if (Cross(a[i], a[(i + 1) % a.Count], b[j], b[(j + 1) % b.Count])) return true;
        return false;
    }

    public static bool Finite((double E, double N) p) => double.IsFinite(p.E) && double.IsFinite(p.N);

    public static int DistinctCount(IReadOnlyList<(double E, double N)> p, double tol = 0.01)
    {
        var kept = new List<(double E, double N)>();
        foreach (var q in p)
            if (!kept.Any(k => Math.Abs(k.E - q.E) < tol && Math.Abs(k.N - q.N) < tol)) kept.Add(q);
        return kept.Count;
    }

    public static double Length(IReadOnlyList<(double E, double N)> p)
    {
        double s = 0;
        for (int i = 1; i < p.Count; i++) s += Math.Sqrt(Math.Pow(p[i].E - p[i - 1].E, 2) + Math.Pow(p[i].N - p[i - 1].N, 2));
        return s;
    }
}
