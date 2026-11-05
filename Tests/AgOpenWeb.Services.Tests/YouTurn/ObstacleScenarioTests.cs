// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services.GeoJson;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// The obstacle scenarios on "Quinta Torta" (<see cref="ObstacleTestField"/>): every U-turn
/// obstacle, approached from both ends along AB lines at three headings and a curve, with a
/// rear, a trailing and a tow-between implement, by an operator who does nothing wrong and
/// by one who does everything wrong (<see cref="BadOperator"/>). Two rules hold in every run:
/// while AutoSteer is on, neither the tractor's wheels nor (at a Hard obstacle) the implement
/// enter a U-turn obstacle; and when the host takes AutoSteer off for an obstacle there are
/// 10 m left to stop in, unless the operator gave it less to begin with.
/// Set AGOPENWEB_SCENARIO_REPORT to a file path to get the matrix as a Markdown report, and
/// AGOPENWEB_SCENARIO_ONLY to a piece of a leg's name (an obstacle, a track, a seed) to run only those legs.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
[Category("Scenario")]
public class ObstacleScenarioTests
{
    private const double DefaultWidth = 6;
    private static readonly double[] Widths = { 3, 6, 12 };
    private const double ApproachLength = 130; // m along the pass before the obstacle
    private const double MinRoom = 40;
    private const double StopRoom = 10;
    private static readonly string[] Tools = { "rear", "front", "trailing", "tbt" };
    private static readonly List<LegResult> Results = new();

    private static bool Wanted(Leg leg)
    {
        string? only = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_ONLY");
        return string.IsNullOrEmpty(only) || leg.ToString().Contains(only, StringComparison.OrdinalIgnoreCase);
    }

    public sealed record Leg(string Obstacle, string Track, bool Forward, string Tool, double Width, double Offset, int Seed)
    {
        public override string ToString() =>
            $"{Obstacle}, {Track}, {(Forward ? "forward" : "back")}, {Tool} {Width.ToString("0.#", CultureInfo.InvariantCulture)} m"
            + (Offset != 0 ? $" offset {Offset.ToString("+0.#;-0.#", CultureInfo.InvariantCulture)} m" : "")
            + (Seed >= 0 ? $", seed {Seed}" : "");
    }

    public sealed record LegResult(Leg Leg, string Outcome, string? Skipped, double WorstClearance, string? WorstObstacle,
        double WheelsAtSoft, double PathError, string? Kickout, double? AheadAtKickout, double RoomAtLastArm, double Time,
        IReadOnlyList<string> Mischief, IReadOnlyList<string> Log, string? Failure);

    public static IEnumerable<TestCaseData> NormalLegs()
    {
        var field = ObstacleTestField.Build();
        foreach (var o in field.UTurnObstacles)
            foreach (var kind in ObstacleTestField.TrackKinds)
                foreach (bool forward in new[] { true, false })
                    foreach (var tool in Tools)
                        foreach (double width in Widths)
                        {
                            // Every implement at every width on the straight north-south line; every implement 6 m
                            // wide on the curve; the 6 m rear one on the angled lines.
                            if (kind != "AB 0°" && width != DefaultWidth) continue;
                            if (kind != "AB 0°" && kind != "Curve" && tool != "rear") continue;
                            // The thirty lone trees get a shorter list: the 6 m rear and trailed implements and the
                            // 3 m front one on the straight line, the 6 m rear one on the curve.
                            if (IsLoneTree(o) && !(kind == "AB 0°" && (tool == "rear" || tool == "trailing") && width == DefaultWidth
                                || kind == "AB 0°" && tool == "front" && width == 3
                                || kind == "Curve" && tool == "rear")) continue;
                            var leg = new Leg(o.Name, kind, forward, tool, width, 0, -1);
                            if (Wanted(leg)) yield return new TestCaseData(leg).SetName($"Normal({leg})");
                        }
        // Implements running beside the tractor (a mower conditioner): rigid and trailed, 3 m, 2.5 m to either
        // side. (Half the width to one side makes the tractor's return line the line it came on, a turn with
        // no lateral span, which the generator does not build: kickout at every obstacle, see the report.)
        foreach (var o in field.UTurnObstacles.Where(o => !IsLoneTree(o)))
            foreach (bool forward in new[] { true, false })
                foreach (var tool in new[] { "rear", "trailing" })
                    foreach (double offset in new[] { -2.5, 2.5 })
                    {
                        var leg = new Leg(o.Name, "AB 0°", forward, tool, 3, offset, -1);
                        if (Wanted(leg)) yield return new TestCaseData(leg).SetName($"Normal({leg})");
                    }
    }

    private static bool IsLoneTree(ObstacleTestField.Obstacle o) => o.Name.StartsWith("Tree circle", StringComparison.Ordinal);

    public static IEnumerable<TestCaseData> BadLegs()
    {
        var field = ObstacleTestField.Build();
        int seed = 100;
        foreach (var o in field.UTurnObstacles)
            foreach (var kind in ObstacleTestField.TrackKinds)
                for (int k = 0; k < 2; k++)
                {
                    if (IsLoneTree(o) && kind != "AB 0°") continue; // one seed each way for the lone trees
                    var leg = new Leg(o.Name, kind, k == 0, "rear", DefaultWidth, 0, seed++);
                    if (Wanted(leg)) yield return new TestCaseData(leg).SetName($"Bad({leg})");
                }
    }

    [TestCaseSource(nameof(NormalLegs))]
    public void TheOperatorWhoDoesNothingWrong(Leg leg)
    {
        var r = Run(leg);
        Assert.That(r.Outcome, Is.Not.EqualTo("passed it").And.Not.EqualTo("timeout"), "the pass never met the obstacle");
    }

    [TestCaseSource(nameof(BadLegs))]
    public void TheOperatorWhoDoesEverythingWrong(Leg leg) => Run(leg);

    [Test]
    public void TheField_IsSane()
    {
        var field = ObstacleTestField.Build();
        Assert.That(field.Outer.IsValid, Is.True);
        Assert.That(field.Outer.AreaHectares, Is.InRange(10, 20));
        Assert.That(field.Boundary.HeadlandPolygon, Is.Not.Null);
        Assert.That(field.UTurnObstacles.Count(), Is.EqualTo(39));
        foreach (var o in field.Obstacles)
        {
            Assert.That(o.Polygon.IsValid, Is.True, o.Name);
            foreach (var p in o.Polygon.Points)
                Assert.That(field.Outer.IsPointInside(p.Easting, p.Northing), Is.True, $"{o.Name} sticks out of the field");
        }
        TestContext.Out.WriteLine($"{field.Outer.AreaHectares:F1} ha, {field.Obstacles.Count} obstacles, " +
            $"{field.UTurnObstacles.Count()} of them U-turn");
    }

    /// <summary>
    /// Writes the field as an AgOpenWeb field folder (field.geojson with the AB lines and the
    /// curve) into AGOPENWEB_EXPORT_FIELD, to open on a real host.
    /// </summary>
    [Test]
    public void ExportTheField()
    {
        string? dir = Environment.GetEnvironmentVariable("AGOPENWEB_EXPORT_FIELD");
        if (string.IsNullOrEmpty(dir)) Assert.Ignore("set AGOPENWEB_EXPORT_FIELD to a folder to export the field");
        var built = ObstacleTestField.Build();
        Directory.CreateDirectory(dir!);
        var field = new Models.Field
        {
            Name = Path.GetFileName(dir!.TrimEnd(Path.DirectorySeparatorChar)),
            DirectoryPath = dir,
            Origin = new Position { Latitude = 39.4225, Longitude = -7.5319 },
            Boundary = built.Boundary,
        };
        var tracks = ObstacleTestField.TrackKinds.Select(ObstacleTestField.Track).ToList();
        GeoJsonFieldService.Save(field, tracks);
        Assert.That(File.Exists(Path.Combine(dir, "field.geojson")), Is.True);
        TestContext.Out.WriteLine($"exported to {dir}");
    }

    [OneTimeTearDown]
    public void WriteReport()
    {
        string? path = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_REPORT");
        if (string.IsNullOrEmpty(path) || Results.Count == 0) return;
        File.WriteAllText(path, ScenarioReport.Build(Results));
    }

    // ── the leg ─────────────────────────────────────────────────────────────────────────────

    private static LegResult Run(Leg leg)
    {
        var field = ObstacleTestField.Build();
        var target = field.Obstacles.First(o => o.Name == leg.Obstacle);
        var track = ObstacleTestField.Track(leg.Track);
        using var rig = new ObstacleScenarioRig(field, leg.Tool, leg.Width, leg.Offset);
        rig.UseTrack(track, 0);

        var approach = PlanApproach(field, track, target, leg.Forward, leg.Width, leg.Offset);
        string? skipped = approach.First == null ? "the pass misses it"
            : approach.Room < MinRoom ? $"no room to approach it from there ({approach.Room:F0} m)" : null;
        if (skipped != null)
        {
            Record(new LegResult(leg, "skipped", skipped, double.NaN, null, double.NaN, double.NaN, null, null, double.NaN, 0,
                Array.Empty<string>(), Array.Empty<string>(), null));
            Assert.Ignore(skipped);
        }
        var first = approach.First!;
        rig.Place(approach.E, approach.N, approach.Heading);
        rig.Fix(); // AutoSteer off: the host picks the pass under the tractor
        rig.Note($"start on pass {rig.Pass}, heading {approach.Heading * 180 / Math.PI:F0}°, {first.Name} {approach.Room:F0} m along the pass");

        BadOperator? op = leg.Seed >= 0 ? new BadOperator(rig, first, leg.Seed) : null;
        if (op != null) rig.Note("operator plans: " + string.Join(", ", op.Chosen));
        if (op == null || !op.StartsDisengaged) rig.Engage();

        double extent = first.Polygon.Points.Max(p => Math.Sqrt((p.Easting - first.CentreE) * (p.Easting - first.CentreE)
            + (p.Northing - first.CentreN) * (p.Northing - first.CentreN)));
        double se = Math.Sin(approach.Heading), ce = Math.Cos(approach.Heading);
        bool Passed() => (rig.E - first.CentreE) * se + (rig.N - first.CentreN) * ce > extent + 15;
        bool Done() => rig.KickoutReason != null || (rig.TurnEndedAt is { } t && rig.Time > t + 8) || Passed();

        while (!Done() && rig.Time < 240)
        {
            op?.Tick();
            rig.Fix();
        }

        string outcome =
            rig.KickoutReason != null ? "kickout" :
            rig.TurnedAtObstacle && rig.TurnEndedAt != null ? "turned at the obstacle" :
            rig.TurnEndedAt != null ? "turned at the field's edge" :
            Passed() && first.Polygon.AtObstacle == ObstacleAction.DriveThrough ? "driven through (operator's choice)" :
            Passed() ? "passed it" :
            "timeout";

        // The two rules.
        string? failure = null;
        // A pass laid beside an obstacle can run the implement's edge into it before any turn (the
        // line, not the turn, puts it there; the log says "on the pass itself"); the turn is judged
        // by what happened from the moment it was driven.
        if (rig.WorstClearanceInTurn < 0)
            failure = $"AutoSteer took the tractor or the implement {-rig.WorstClearanceInTurn:F2} m into {rig.WorstObstacleInTurn}";
        else if (rig.KickoutReason != null && rig.ObstacleAheadAtKickout is { } ahead && !double.IsInfinity(ahead))
        {
            double required = Math.Min(StopRoom, rig.RoomAtLastArm - 1.5);
            if (ahead < required)
                failure = $"AutoSteer went off {ahead:F1} m before the obstacle; {required:F1} m were expected";
        }

        var result = new LegResult(leg, outcome, null, rig.WorstClearance, rig.WorstObstacle, rig.WorstWheelsAtSoft,
            rig.MaxPathError, rig.KickoutReason, rig.ObstacleAheadAtKickout, rig.RoomAtLastArm, rig.Time,
            op?.Done ?? (IReadOnlyList<string>)Array.Empty<string>(), rig.Log.ToList(), failure);
        Record(result);

        TestContext.Out.WriteLine(ScenarioReport.Line(result));
        foreach (var line in rig.Log) TestContext.Out.WriteLine("  " + line);
        Assert.That(failure, Is.Null);
        return result;
    }

    private static void Record(LegResult r)
    {
        lock (Results) Results.Add(r);
    }

    private sealed record Approach(double E, double N, double Heading, ObstacleTestField.Obstacle? First, double Room);

    /// <summary>
    /// The pass through the obstacle's centre, as the host lays it out (the pass nearest the
    /// centre, the track offset by its distance), and the tractor's start on it: about
    /// <see cref="ApproachLength"/> m before the obstacle along the pass, moved on until it is in the
    /// workable area with the implement's room behind it clear of every U-turn obstacle. Also the
    /// first U-turn obstacle the pass meets from there and how far along the pass it is.
    /// </summary>
    private static Approach PlanApproach(ObstacleTestField.Built field, Models.Track.Track track,
        ObstacleTestField.Obstacle target, bool forward, double toolWidth, double toolOffset)
    {
        // The reference line, dense, and the pass's own line.
        List<Vec3> reference = track.IsABLine ? CurveProcessing.CalculateHeadings(Densified(track.Points[0], track.Points[1], 2)) : track.Points;
        int near = Nearest(reference, target.CentreE, target.CentreN);
        var (te, tn) = Tangent(reference, near);
        double rightOf = (target.CentreE - reference[near].Easting) * tn - (target.CentreN - reference[near].Northing) * te;
        // The tool runs on the pass; the tractor steers beside it by the tool offset (#446).
        int pass = PassGeometry.Nearest(track, rightOf, toolWidth);
        double offset = PassGeometry.Offset(track, pass, toolWidth, 0, PassGeometry.ToolShift(toolOffset, forward));
        var path = Math.Abs(offset) < 0.01 ? new List<Vec3>(reference) : CurveProcessing.CreateOffsetCurve(reference, offset);
        if (!forward) path.Reverse(); // walked in the direction of travel

        // Back from the obstacle along the pass.
        int at = Nearest(path, target.CentreE, target.CentreN);
        int start = at;
        for (double back = 0; start > 0 && back < ApproachLength; start--) back += Step(path, start - 1, start);

        // On into the field: inside the headland line, with the implement's 12 m behind the tractor and the
        // host's 20 m of look-ahead in front of it clear of every U-turn obstacle's turn line (3 m from the fence).
        var headland = field.Boundary.HeadlandPolygon!;
        bool Clear(int i)
        {
            if (!headland.IsPointInside(path[i].Easting, path[i].Northing)) return false;
            int behind = i, ahead = i;
            for (double d = 0; behind > 0 && d < 12; behind--) d += Step(path, behind - 1, behind);
            for (double d = 0; ahead < path.Count - 1 && d < 20; ahead++) d += Step(path, ahead, ahead + 1);
            for (int k = behind; k <= ahead; k++)
                foreach (var o in field.UTurnObstacles)
                    if (ObstacleTestField.SignedDistance(o.Polygon, path[k].Easting, path[k].Northing) <= 3) return false;
            return true;
        }
        while (start < path.Count - 1 && !Clear(start)) start++;

        // The first U-turn obstacle from there, and the room before it.
        ObstacleTestField.Obstacle? first = null;
        double room = double.PositiveInfinity, walked = 0;
        for (int i = start + 1; i < path.Count && walked < 200 && first == null; i++)
        {
            walked += Step(path, i - 1, i);
            foreach (var o in field.UTurnObstacles)
                if (o.Polygon.IsPointInside(path[i].Easting, path[i].Northing)) { first = o; room = walked; break; }
        }
        var (he, hn) = Tangent(path, start);
        return new Approach(path[start].Easting, path[start].Northing, Math.Atan2(he, hn), first, room);
    }

    private static List<Vec3> Densified(Vec3 a, Vec3 b, double step)
    {
        double len = Math.Sqrt(Dist2(a, b.Easting, b.Northing));
        int n = Math.Max(1, (int)Math.Ceiling(len / step));
        return Enumerable.Range(0, n + 1)
            .Select(i => new Vec3(a.Easting + (b.Easting - a.Easting) * i / n, a.Northing + (b.Northing - a.Northing) * i / n, 0)).ToList();
    }

    private static int Nearest(List<Vec3> pts, double e, double n)
    {
        int best = 0;
        for (int i = 1; i < pts.Count; i++)
            if (Dist2(pts[i], e, n) < Dist2(pts[best], e, n)) best = i;
        return best;
    }

    private static double Step(List<Vec3> pts, int i, int j) => Math.Sqrt(Dist2(pts[i], pts[j].Easting, pts[j].Northing));

    private static double Dist2(Vec3 p, double e, double n) => (p.Easting - e) * (p.Easting - e) + (p.Northing - n) * (p.Northing - n);

    private static (double E, double N) Tangent(List<Vec3> pts, int i)
    {
        var a = pts[Math.Max(0, i - 1)];
        var b = pts[Math.Min(pts.Count - 1, i + 1)];
        double de = b.Easting - a.Easting, dn = b.Northing - a.Northing, len = Math.Sqrt(de * de + dn * dn);
        return (de / len, dn / len);
    }
}

/// <summary>The scenario matrix as Markdown: a table of every leg, then the log of each one that failed or ended in a kickout.</summary>
internal static class ScenarioReport
{
    public static string Line(ObstacleScenarioTests.LegResult r)
    {
        var ci = CultureInfo.InvariantCulture;
        string clearance = double.IsNaN(r.WorstClearance) || r.WorstClearance == double.MaxValue ? "-" : r.WorstClearance.ToString("F2", ci) + " m";
        if (!double.IsNaN(r.WheelsAtSoft) && r.WheelsAtSoft < 3) clearance += $", wheels at the Soft one {r.WheelsAtSoft.ToString("F2", ci)} m";
        string pathError = double.IsNaN(r.PathError) ? "-" : r.PathError.ToString("F2", ci) + " m";
        string kick = r.Kickout == null ? "-" : $"{(r.AheadAtKickout is { } a && !double.IsInfinity(a) ? a.ToString("F1", ci) + " m before: " : "")}{r.Kickout}";
        string mischief = r.Mischief.Count == 0 ? "-" : string.Join("; ", r.Mischief);
        return $"| {r.Leg} | {(r.Failure != null ? "**FAIL** " : "")}{r.Outcome}{(r.Skipped != null ? ": " + r.Skipped : "")} | {clearance}{(r.WorstObstacle != null ? " (" + r.WorstObstacle + ")" : "")} | {pathError} | {kick} | {r.Time.ToString("F0", ci)} s | {mischief} |";
    }

    public static string Build(IReadOnlyList<ObstacleScenarioTests.LegResult> results)
    {
        var sb = new StringBuilder();
        var ordered = results.OrderBy(r => r.Leg.Seed >= 0).ThenBy(r => r.Leg.Obstacle).ThenBy(r => r.Leg.Track).ThenByDescending(r => r.Leg.Forward).ThenBy(r => r.Leg.Tool).ThenBy(r => r.Leg.Width).ThenBy(r => r.Leg.Offset).ThenBy(r => r.Leg.Seed).ToList();
        int failed = ordered.Count(r => r.Failure != null), skipped = ordered.Count(r => r.Skipped != null);
        sb.AppendLine("# Obstacle scenarios on Quinta Torta");
        sb.AppendLine();
        sb.AppendLine($"{ordered.Count} legs: {ordered.Count - failed - skipped} passed, {failed} failed, {skipped} skipped.");
        sb.AppendLine();
        foreach (var group in ordered.GroupBy(r => r.Leg.Seed >= 0 ? "The operator who does everything wrong" : "The operator who does nothing wrong"))
        {
            sb.AppendLine($"## {group.Key}");
            sb.AppendLine();
            foreach (var byOutcome in group.GroupBy(r => r.Outcome).OrderByDescending(g => g.Count()))
                sb.AppendLine($"- {byOutcome.Key}: {byOutcome.Count()}");
            sb.AppendLine();
            sb.AppendLine("| Leg | Outcome | Nearest while engaged | Off the turn path | AutoSteer off | Time | What the operator did |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var r in group) sb.AppendLine(Line(r));
            sb.AppendLine();
        }
        var detailed = ordered.Where(r => r.Failure != null || r.Outcome == "kickout" || r.Outcome == "passed it" || r.Outcome == "timeout").ToList();
        if (detailed.Count > 0)
        {
            sb.AppendLine("## Logs");
            sb.AppendLine();
            foreach (var r in detailed)
            {
                sb.AppendLine($"### {r.Leg}: {r.Outcome}{(r.Failure != null ? " — " + r.Failure : "")}");
                sb.AppendLine();
                sb.AppendLine("```");
                foreach (var line in r.Log) sb.AppendLine(line);
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }
}
