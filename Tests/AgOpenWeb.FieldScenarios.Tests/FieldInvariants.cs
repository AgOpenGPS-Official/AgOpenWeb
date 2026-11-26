// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace AgOpenWeb.FieldScenarios.Tests;

public enum Severity
{
    /// <summary>A crash, a hang, lost or corrupted field data, or geometry that would steer or spray wrong.</summary>
    Bug,
    /// <summary>Two parts of the app disagree (memory vs file, list vs map, a flag vs the data).</summary>
    Inconsistency,
    /// <summary>Invalid input accepted and kept (it may not hurt today, but nothing stops it).</summary>
    Robustness,
    /// <summary>Works, but the operator is left guessing (no message, odd default).</summary>
    Improvement,
}

/// <summary>One thing wrong. <see cref="Code"/> is stable, so the report can count repeats.</summary>
public sealed record Finding(string Code, Severity Severity, string Message);

/// <summary>
/// The rules every state must keep, whatever the operator did. Checked after every step.
/// They are deliberately about outcomes (what is stored, what the tractor would follow), not
/// about how the code gets there.
/// </summary>
public static class FieldInvariants
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string F(FormattableString s) => s.ToString(Inv);

    public static List<Finding> Check(FieldSnapshot s)
    {
        var f = new List<Finding>();

        // --- the field itself
        if (s.IsFieldOpen != s.ActiveFieldSet)
            f.Add(new("FIELD-OPEN-FLAG", Severity.Inconsistency,
                F($"IsFieldOpen={s.IsFieldOpen} but the active field is {(s.ActiveFieldSet ? "set" : "not set")}")));
        if (s.IsFieldOpen && s.FieldDirectory is { } dir && !Directory.Exists(dir))
            f.Add(new("FIELD-DIR-GONE", Severity.Bug, $"the open field's folder does not exist: {dir}"));
        if (!s.IsFieldOpen)
        {
            if (s.Outer != null || s.Inner.Count > 0)
                f.Add(new("NOFIELD-BOUNDARY", Severity.Inconsistency, "no field open, but a boundary is still loaded"));
            if (s.Headland != null)
                f.Add(new("NOFIELD-HEADLAND", Severity.Inconsistency, "no field open, but a headland line is still loaded"));
            if (s.Tracks.Count > 0)
                f.Add(new("NOFIELD-TRACKS", Severity.Inconsistency, F($"no field open, but {s.Tracks.Count} tracks are still listed")));
            if (s.TramSystems.Count > 0)
                f.Add(new("NOFIELD-TRAM", Severity.Inconsistency, F($"no field open, but {s.TramSystems.Count} tram systems are still kept (they would be saved into the next field)")));
            if (s.TramLines.Count > 0 || s.TramOuterTrack.Count > 0)
                f.Add(new("NOFIELD-TRAMLINES", Severity.Inconsistency, "no field open, but tram lines are still built"));
            if (s.Flags.Count > 0)
                f.Add(new("NOFIELD-FLAGS", Severity.Inconsistency, F($"no field open, but {s.Flags.Count} flags are kept")));
        }
        if (s.IsFieldOpen && (s.OriginLat is < -90 or > 90 || s.OriginLon is < -180 or > 180 || (s.OriginLat == 0 && s.OriginLon == 0)))
            f.Add(new("FIELD-ORIGIN", Severity.Bug, F($"field origin is invalid: {s.OriginLat}, {s.OriginLon}")));

        // --- outer boundary
        var outer = s.Outer;
        if (outer != null)
        {
            if (outer.Any(p => !Poly.Finite(p)))
                f.Add(new("BND-NAN", Severity.Bug, "outer boundary has a NaN/infinite point"));
            else
            {
                int distinct = Poly.DistinctCount(outer);
                double area = Poly.Area(outer);
                if (distinct < 3)
                    f.Add(new("BND-DEGENERATE", Severity.Robustness, F($"outer boundary kept with {distinct} distinct points")));
                else if (area < 100)
                    f.Add(new("BND-TINY", Severity.Robustness, F($"outer boundary kept with an area of {area:F1} m²")));
                if (Poly.SelfCrossing(outer) is { } x)
                    f.Add(new("BND-SELFX", Severity.Robustness,
                        F($"outer boundary crosses itself (edges {x.I} and {x.J}, near {x.At.E:F1},{x.At.N:F1}); area and inside/outside tests are wrong for it")));
                if (outer.Any(p => Math.Abs(p.E) > 50000 || Math.Abs(p.N) > 50000))
                    f.Add(new("BND-FAR", Severity.Robustness, "outer boundary has a point more than 50 km from the field origin"));
            }
        }

        // --- inner boundaries
        for (int i = 0; i < s.Inner.Count; i++)
        {
            var ring = s.Inner[i];
            if (ring.Any(p => !Poly.Finite(p)))
            {
                f.Add(new("INNER-NAN", Severity.Bug, F($"inner boundary {i + 1} has a NaN/infinite point")));
                continue;
            }
            if (ring.Count < 3 || Poly.DistinctCount(ring) < 3)
                f.Add(new("INNER-DEGENERATE", Severity.Robustness, F($"inner boundary {i + 1} kept with {ring.Count} points")));
            if (Poly.SelfCrossing(ring) is { } x)
                f.Add(new("INNER-SELFX", Severity.Robustness, F($"inner boundary {i + 1} crosses itself (edges {x.I}/{x.J})")));
            if (outer is { Count: >= 3 } && outer.All(Poly.Finite))
            {
                int outside = ring.Count(p => !Poly.Inside(p, outer));
                if (outside == ring.Count)
                    f.Add(new("INNER-OUTSIDE", Severity.Robustness, F($"inner boundary {i + 1} lies completely outside the outer boundary")));
                else if (outside > 0 || Poly.RingsCross(ring, outer))
                    f.Add(new("INNER-CROSSES-OUTER", Severity.Robustness, F($"inner boundary {i + 1} sticks out of the outer boundary ({outside} of {ring.Count} points outside)")));
            }
            if (outer == null)
                f.Add(new("INNER-NO-OUTER", Severity.Robustness, F($"inner boundary {i + 1} exists but the field has no outer boundary")));
        }

        // --- headland
        var hl = s.Headland;
        if (s.HasHeadlandFlag != (hl != null))
            f.Add(new("HL-FLAG", Severity.Inconsistency, F($"HasHeadland={s.HasHeadlandFlag} but the headland line has {hl?.Count ?? 0} points")));
        if (hl != null)
        {
            if (hl.Any(p => !Poly.Finite(p)))
                f.Add(new("HL-NAN", Severity.Bug, "headland line has a NaN/infinite point"));
            else if (outer is { Count: >= 3 } && outer.All(Poly.Finite) && Poly.SelfCrossing(outer) == null)
            {
                int outside = hl.Count(p => !Poly.Inside(p, outer) && Poly.DistToRing(p, outer) > 0.05);
                if (outside > 0)
                    f.Add(new("HL-OUTSIDE", Severity.Bug, F($"{outside} of {hl.Count} headland points lie outside the outer boundary")));
                double ha = Poly.Area(hl), oa = Poly.Area(outer);
                if (hl.Count >= 3 && ha > oa * 1.001)
                    f.Add(new("HL-BIGGER", Severity.Bug, F($"headland area {ha / 10000:F2} ha is bigger than the field {oa / 10000:F2} ha")));
                if (hl.Count >= 3 && ha < 1)
                    f.Add(new("HL-EMPTY", Severity.Robustness, F($"headland line kept with an area of {ha:F2} m² (offset bigger than the field?)")));
                if (Poly.SelfCrossing(hl) is { } x)
                    f.Add(new("HL-SELFX", Severity.Bug, F($"headland line crosses itself (edges {x.I}/{x.J})")));
                // A whole-boundary headland lies at its offset from the boundary everywhere.
                if (s.HeadlandSegments.Count == 1 && s.HeadlandSegments[0].Type == "Boundary" && hl.Count >= 3)
                {
                    double want = s.HeadlandSegments[0].Offset;
                    double min = hl.Min(p => Poly.DistToRing(p, outer));
                    if (min < want - 0.5)
                        f.Add(new("HL-TOO-CLOSE", Severity.Bug, F($"whole-boundary headland comes {min:F2} m from the boundary; offset is {want:F2} m")));
                }
            }
            if (outer == null)
                f.Add(new("HL-NO-BOUNDARY", Severity.Inconsistency, "a headland line exists but the field has no outer boundary"));
        }
        foreach (var seg in s.HeadlandSegments)
            if (!double.IsFinite(seg.Offset) || seg.Offset <= 0)
                f.Add(new("HL-SEG-OFFSET", Severity.Robustness, F($"headland segment '{seg.Name}' has offset {seg.Offset}")));
        if (s.HeadlandSegments.Count > 0 && hl == null)
            f.Add(new("HL-SEGS-NO-LINE", Severity.Inconsistency, F($"{s.HeadlandSegments.Count} headland segments listed but no headland line built")));

        // --- tracks
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in s.Tracks)
        {
            if (t.Points.Any(p => !double.IsFinite(p.E) || !double.IsFinite(p.N)))
                f.Add(new("TRK-NAN", Severity.Bug, $"track '{t.Name}' ({t.Type}) has a NaN/infinite point"));
            else if (t.Points.Any(p => !double.IsFinite(p.H)))
                f.Add(new("TRK-NAN-HEADING", Severity.Bug, $"track '{t.Name}' ({t.Type}) has a NaN heading"));
            else
            {
                var pts = t.Points.Select(p => (p.E, p.N)).ToList();
                if (t.Type == "ABLine")
                {
                    if (pts.Count != 2)
                        f.Add(new("TRK-AB-POINTS", Severity.Inconsistency, F($"AB line '{t.Name}' has {pts.Count} points")));
                    else if (Poly.Length(pts) < 1.0)
                        f.Add(new("TRK-AB-ZERO", Severity.Robustness, F($"AB line '{t.Name}' is {Poly.Length(pts):F2} m long (A = B: no heading)")));
                }
                else if (t.Type is "Curve" or "BoundaryCurve" or "BoundaryOuter")
                {
                    if (pts.Count < 3 && t.Type != "ABLine")
                        f.Add(new("TRK-CURVE-SHORT", Severity.Robustness, F($"curve '{t.Name}' ({t.Type}) kept with {pts.Count} points")));
                    else if (Poly.Length(pts) < 2.0)
                        f.Add(new("TRK-CURVE-ZERO", Severity.Robustness, F($"curve '{t.Name}' is {Poly.Length(pts):F2} m long")));
                }
                if (pts.Any(p => Math.Abs(p.E) > 50000 || Math.Abs(p.N) > 50000))
                    f.Add(new("TRK-FAR", Severity.Robustness, $"track '{t.Name}' has a point more than 50 km from the field origin"));
            }
            if (string.IsNullOrWhiteSpace(t.Name))
                f.Add(new("TRK-NONAME", Severity.Robustness, $"a {t.Type} track has an empty name"));
            else if (!names.Add(t.Name))
                f.Add(new("TRK-DUPNAME", Severity.Improvement, $"two tracks are both called '{t.Name}' (tram systems and the track list pick tracks by name)"));
            if (!double.IsFinite(t.Nudge))
                f.Add(new("TRK-NUDGE-NAN", Severity.Bug, $"track '{t.Name}' nudge is {t.Nudge}"));
        }
        if (s.StateTrackNames.Count != s.Tracks.Count || !s.StateTrackNames.SequenceEqual(s.Tracks.Select(t => t.Name)))
            f.Add(new("TRK-LISTS", Severity.Inconsistency,
                F($"the track list ({s.Tracks.Count}) and the field's tracks ({s.StateTrackNames.Count}) differ")));
        if (s.ActiveTrack != null && !s.Tracks.Any(t => t.Name == s.ActiveTrack))
            f.Add(new("TRK-ACTIVE-GONE", Severity.Bug, $"the active (steered) track '{s.ActiveTrack}' is no longer in the track list"));

        // --- tram
        foreach (var ts in s.TramSystems)
        {
            if (!double.IsFinite(ts.Width) || ts.Width <= 0)
                f.Add(new("TRAM-WIDTH", Severity.Robustness, F($"tram system '{ts.Name}' width is {ts.Width}")));
            else if (s.ToolWidth > 0 && ts.Width > 0 && ts.Width < s.ToolWidth * 0.5)
                f.Add(new("TRAM-WIDTH-SMALL", Severity.Improvement, F($"tram system '{ts.Name}' width {ts.Width} m is less than half the tool ({s.ToolWidth} m)")));
            if (!double.IsFinite(ts.Offset))
                f.Add(new("TRAM-OFFSET-NAN", Severity.Bug, F($"tram system '{ts.Name}' offset is {ts.Offset}")));
            if (ts.Passes < 0)
                f.Add(new("TRAM-PASSES", Severity.Robustness, F($"tram system '{ts.Name}' passes {ts.Passes}")));
            if (ts.RefTrack != null && !s.Tracks.Any(t => t.Name == ts.RefTrack))
                f.Add(new("TRAM-REF-GONE", Severity.Inconsistency, $"tram system '{ts.Name}' refers to track '{ts.RefTrack}', which does not exist"));
        }
        var allTram = s.TramLines.Concat(s.TramExtraLines).Append(s.TramOuterTrack).Append(s.TramInnerTrack).ToList();
        if (allTram.Any(l => l.Any(p => !Poly.Finite(p))))
            f.Add(new("TRAM-NAN", Severity.Bug, "a tram line has a NaN/infinite point"));
        else if (outer is { Count: >= 3 } && outer.All(Poly.Finite) && Poly.SelfCrossing(outer) == null)
        {
            int outside = 0, total = 0;
            foreach (var l in allTram)
                foreach (var p in l)
                {
                    total++;
                    if (!Poly.Inside(p, outer) && Poly.DistToRing(p, outer) > 0.5) outside++;
                }
            if (outside > 0)
                f.Add(new("TRAM-OUTSIDE", Severity.Bug, F($"{outside} of {total} tram line points lie outside the outer boundary")));
        }
        if (s.TramSystems.Count > 0 && s.TramSystems.All(t => !t.Enabled) && (s.TramLines.Count > 0 || s.TramOuterTrack.Count > 0))
            f.Add(new("TRAM-DISABLED-LINES", Severity.Inconsistency, "every tram system is off, but tram lines are still built"));

        // --- flags
        foreach (var fl in s.Flags)
            if (!double.IsFinite(fl.E) || !double.IsFinite(fl.N))
                f.Add(new("FLAG-NAN", Severity.Bug, $"flag '{fl.Name}' has a NaN/infinite position"));

        return f;
    }
}
