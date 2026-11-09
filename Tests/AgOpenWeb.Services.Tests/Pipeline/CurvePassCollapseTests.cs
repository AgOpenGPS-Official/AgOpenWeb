// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.Track;
using TrackModel = AgOpenWeb.Models.Track.Track;
using AgOpenWeb.Services.Pipeline;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgOpenWeb.Services.Tests.Pipeline;

/// <summary>
/// #694: Boundary AB Curve tracks (Quick AB along the outer fence) on a 21 m tool, in a field
/// 203 m across (the fence and both dumps' curves are in Fixtures/issue694_boundary_curves.json).
/// An inward pass the offset removed altogether became a chord between its offset ends, 200 m
/// added at each end: long straight lines across the field and out of it. One that lost the
/// reference's end had a 200 m tail aimed along a bent fragment, across the field. In AgOpenGPS
/// the first pass is empty (CABCurve.BuildNewOffsetList) and the second has no tail there (a
/// pass is the offset of the extended reference). The passes are named as in the report: pass
/// k right is pass k - 1, pass k left is pass -k (the first pass either side is half a width
/// off the reference).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class CurvePassCollapseTests
{
    private const double Width = 21, Radius = 8;

    private static readonly Lazy<(List<Vec2> Fence, TrackModel D1, TrackModel D2)> Field = new(() =>
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Pipeline", "Fixtures", "issue694_boundary_curves.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        List<(double E, double N)> Read(string name) => doc.RootElement.GetProperty(name).EnumerateArray()
            .Select(p => (p[0].GetDouble(), p[1].GetDouble())).ToList();
        TrackModel Curve(string name) => TrackModel.FromCurve(name,
            CurveProcessing.CalculateHeadings(Read(name).Select(p => new Vec3(p.E, p.N, 0)).ToList()));
        return (Read("fence").Select(p => new Vec2(p.E, p.N)).ToList(), Curve("d1"), Curve("d2"));
    });

    private static TrackModel Dump(string name) => name == "d1" ? Field.Value.D1 : Field.Value.D2;
    private static double Offset(TrackModel t, int pass) => PassGeometry.Offset(t, pass, Width);

    // The line the tractor steers and the map draws (GpsPipelineService: OffsetPassCache, then
    // run on straight past the ends the offset kept).
    private static (List<Vec3> Pass, List<Vec3> Line, bool StartKept, bool EndKept) Steered(TrackModel t, int pass)
    {
        var made = new OffsetPassCache().Get(t, Offset(t, pass), Radius);
        return (made.Points, CurveProcessing.ExtendPass(made), made.StartKept, made.EndKept);
    }

    // The pass a U-turn lands on (YouTurnPathingService / YouTurnCreationService: no radius limit).
    private static List<Vec3> TurnTarget(TrackModel t, int pass) =>
        CurveProcessing.ExtendPass(CurveProcessing.CurvePass(t.Points, Offset(t, pass)));

    private static double Dist(Vec3 a, Vec3 b) => Math.Sqrt((a.Easting - b.Easting) * (a.Easting - b.Easting) + (a.Northing - b.Northing) * (a.Northing - b.Northing));

    [TestCase("d1", -6)] // 6L
    [TestCase("d1", -7)] // 7L
    [TestCase("d1", -8)] // 8L
    [TestCase("d2", 5)]  // 6R
    [TestCase("d2", 6)]  // 7R
    [TestCase("d2", 7)]  // 8R
    [TestCase("d2", 8)]  // 9R
    public void A_pass_the_offset_removes_altogether_has_no_line(string dump, int pass)
    {
        var t = Dump(dump);
        var info = CurveProcessing.CreateOffsetCurveWithInfo(t.Points, Offset(t, pass));
        var (points, line, _, _) = Steered(t, pass);

        Assert.Multiple(() =>
        {
            Assert.That(info.PercentRemoved, Is.EqualTo(100), "every point of it removed");
            Assert.That(info.Points, Is.Empty, "no chord between the offset ends");
            Assert.That(points, Is.Empty, "no pass");
            Assert.That(line, Is.Empty, "nothing steered or drawn");
            Assert.That(TurnTarget(t, pass), Is.Empty, "nothing for a U-turn to land on");
        });
    }

    [Test]
    public void A_pass_that_lost_the_references_end_has_no_tail_there()
    {
        // 5R of the second dump: 82 % of it removed, the reference's last point among them.
        var t = Dump("d2");
        var (pass, line, startKept, endKept) = Steered(t, 4);
        var fence = Field.Value.Fence;
        const double Margin = 5;
        double minE = fence.Min(p => p.Easting) - Margin, maxE = fence.Max(p => p.Easting) + Margin;
        double minN = fence.Min(p => p.Northing) - Margin, maxN = fence.Max(p => p.Northing) + Margin;

        Assert.Multiple(() =>
        {
            Assert.That(pass, Has.Count.GreaterThan(2), "there is a pass");
            Assert.That(startKept, Is.True);
            Assert.That(endKept, Is.False, "the reference's last point removed");
            Assert.That(Dist(line[^1], pass[^1]), Is.LessThan(1e-9), "no tail at the end the offset removed");
            Assert.That(line.All(p => p.Easting >= minE && p.Easting <= maxE && p.Northing >= minN && p.Northing <= maxN),
                "the line stays within the field's extent");
            var target = TurnTarget(t, 4);
            Assert.That(target.All(p => p.Easting >= minE && p.Easting <= maxE && p.Northing >= minN && p.Northing <= maxN),
                "and so does the pass a U-turn lands on");
        });
    }

    [TestCase("d1", 0)] [TestCase("d1", 1)] [TestCase("d1", 2)]    // 1R-3R
    [TestCase("d1", -1)] [TestCase("d1", -2)] [TestCase("d1", -3)] // 1L-3L
    public void The_first_passes_keep_their_tails(string dump, int pass)
    {
        var (points, line, startKept, endKept) = Steered(Dump(dump), pass);
        Assert.Multiple(() =>
        {
            Assert.That(startKept && endKept, Is.True, "both ends of the reference kept");
            Assert.That(Dist(line[0], points[0]), Is.GreaterThan(5), "a tail before the start");
            Assert.That(Dist(line[^1], points[^1]), Is.GreaterThan(5), "a tail past the end");
        });
    }

    // The second dump's curve goes the long way round: its ends face each other across what it
    // leaves out, and there they are the line's ends (#694): no tail at either.
    [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(-1)] [TestCase(-2)] [TestCase(-3)]
    public void Passes_of_a_curve_whose_ends_face_each_other_end_at_A_and_B(int pass)
    {
        var (points, line, _, _) = Steered(Dump("d2"), pass);
        Assert.Multiple(() =>
        {
            Assert.That(Dist(line[0], points[0]), Is.LessThan(1e-9), "no tail before A");
            Assert.That(Dist(line[^1], points[^1]), Is.LessThan(1e-9), "no tail past B");
        });
    }

    [Test]
    public void No_U_turn_lands_on_a_pass_that_is_not_there()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, (int)(Width * 100));
        var pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);
        var fence = Field.Value.Fence;
        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon { Points = fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList() },
        };
        var cultivated = fence.Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList();
        var t = Dump("d2");
        var onPass4 = CurveProcessing.CreateOffsetCurve(t.Points, Offset(t, 4));
        var at = new Position { Easting = onPass4[onPass4.Count / 2].Easting, Northing = onPass4[onPass4.Count / 2].Northing };

        Assert.Multiple(() =>
        {
            Assert.That(pathing.IsPathInsideCultivated(t, 0, 4, boundary, cultivated), Is.True, "5R is there");
            Assert.That(pathing.IsPathInsideCultivated(t, 0, 5, boundary, cultivated), Is.False, "6R is not");
            Assert.That(pathing.IsPathLandable(t, 0, 5, boundary, cultivated, at, true, 10, 4), Is.False);
            Assert.That(pathing.LandingLength(t, 0, 5, boundary, cultivated, at, true, 10), Is.Zero);
        });
    }

    // ── The extension, each end on its own ──────────────────────────────

    // Points 1 m apart from (0,0) north to (0,len).
    private static List<Vec3> North(int len) =>
        CurveProcessing.CalculateHeadings(Enumerable.Range(0, len + 1).Select(i => new Vec3(0, i, 0)).ToList());

    [Test]
    public void Each_end_is_extended_by_its_own_length()
    {
        var line = North(10);
        var endOnly = CurveProcessing.ExtendCurveEnds(line, 0, 50, 1.0);
        var startOnly = CurveProcessing.ExtendCurveEnds(line, 30, 0, 1.0);
        var both = CurveProcessing.ExtendCurveEnds(line, 30, 50, 1.0);
        Assert.Multiple(() =>
        {
            Assert.That(endOnly[0].Northing, Is.EqualTo(0).Within(1e-9), "no extension before the start");
            Assert.That(endOnly[^1].Northing, Is.EqualTo(60).Within(1e-9));
            Assert.That(endOnly, Has.Count.EqualTo(line.Count + 50));
            Assert.That(startOnly[0].Northing, Is.EqualTo(-30).Within(1e-9));
            Assert.That(startOnly[^1].Northing, Is.EqualTo(10).Within(1e-9), "no extension past the end");
            Assert.That(both[0].Northing, Is.EqualTo(-30).Within(1e-9));
            Assert.That(both[^1].Northing, Is.EqualTo(60).Within(1e-9));
            Assert.That(CurveProcessing.ExtendCurveEnds(line, 0, 0, 1.0), Is.EqualTo(line), "neither: the line as it is");
            Assert.That(CurveProcessing.ExtendCurveEnds(line, 200), Has.Count.EqualTo(line.Count + 400), "one length: both ends, as before");
        });
    }

    [Test]
    public void ExtendPass_extends_only_the_ends_the_offset_kept()
    {
        var line = North(10);
        Assert.Multiple(() =>
        {
            Assert.That(CurveProcessing.ExtendPass((line, 0, true, false), 20)[^1].Northing, Is.EqualTo(10).Within(1e-9));
            Assert.That(CurveProcessing.ExtendPass((line, 0, true, false), 20)[0].Northing, Is.EqualTo(-20).Within(1e-9));
            Assert.That(CurveProcessing.ExtendPass((line, 0, false, true), 20)[0].Northing, Is.EqualTo(0).Within(1e-9));
            Assert.That(CurveProcessing.ExtendPass((line, 0, false, false), 20), Is.EqualTo(line));
            Assert.That(CurveProcessing.ExtendPass((new List<Vec3>(), 100, false, false)), Is.Empty, "no pass, nothing to extend");
        });
    }

    [Test]
    public void A_kept_end_facing_the_other_end_is_not_extended()
    {
        // Round a 100 m square the long way, from (30,0) west, round, back along the south side
        // to (70,0): the ends face each other across 40 m (#699). Only the end asked for: it is
        // the line's end, and gets none either (#694).
        var pts = new List<Vec3>();
        void Leg(double e0, double n0, double e1, double n1)
        {
            double len = Math.Sqrt((e1 - e0) * (e1 - e0) + (n1 - n0) * (n1 - n0));
            for (int k = 0; k < (int)Math.Round(len); k++)
                pts.Add(new Vec3(e0 + (e1 - e0) * k / len, n0 + (n1 - n0) * k / len, 0));
        }
        Leg(30, 0, 0, 0); Leg(0, 0, 0, 100); Leg(0, 100, 100, 100); Leg(100, 100, 100, 0); Leg(100, 0, 70, 0);
        pts.Add(new Vec3(70, 0, 0));
        var curve = CurveProcessing.CalculateHeadings(pts);

        var ext = CurveProcessing.ExtendCurveEnds(curve, 0, 200, 1.0);
        Assert.Multiple(() =>
        {
            Assert.That(ext[0].Easting, Is.EqualTo(30).Within(1e-9), "the start as it is");
            Assert.That(ext[^1].Easting, Is.EqualTo(70).Within(1e-9), "the end as it is");
        });
    }
}
