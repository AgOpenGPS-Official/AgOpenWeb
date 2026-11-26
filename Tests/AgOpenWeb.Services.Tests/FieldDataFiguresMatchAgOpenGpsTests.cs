// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Coverage;
using AgOpenWeb.Services.GeoJson;
using NUnit.Framework;
using static AgOpenWeb.Services.Tests.AgOpenGpsFieldDataOracle;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Checklist 4.11 / 7.12 / 7.8: the field data figures (worked, actual, overlap, total, remaining,
/// rate, time) and the field list's distance and area, held to AgOpenGPS's. The worked area is
/// the sum AgOpenGPS adds per fix; the actual area and the overlap are what it reads off the pixels
/// of the whole field drawn once (ours counts the detection grid's cells instead, the same idea at
/// 0.1 m); the rest is CFieldData's arithmetic and FormFieldExisting's columns.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class FieldDataFiguresMatchAgOpenGpsTests
{
    private ConfigurationStore _store = null!;
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new ConfigurationStore();
        ConfigurationStore.SetInstance(_store);
        // The coverage margin widens what is painted by a few centimetres (no seams between
        // sections); AgOpenGPS paints the section's own edges, so none here.
        _store.Tool.CoverageMargin = 0;
        _dir = Path.Combine(Path.GetTempPath(), "aow-fielddata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch { }
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    private const double FieldSide = 120; // the field: 0..120 m both ways

    private CoverageMapService Ours()
    {
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(0, FieldSide, 0, FieldSide);
        return svc;
    }

    /// <summary>A pass of the tool: its left and right edge at every fix, fed to ours and to AgOpenGPS's patch alike.</summary>
    private static void Drive(CoverageMapService ours, List<CPatches> theirs, IReadOnlyList<(Vec2 l, Vec2 r)> edges)
    {
        var patch = new CPatches { workedAreaTotal = TheirWorked(theirs) }; // the running total carries on
        theirs.Add(patch);
        ours.StartMapping(0, edges[0].l, edges[0].r);
        patch.TurnMappingOn(edges[0].l, edges[0].r);
        for (int i = 1; i < edges.Count; i++)
        {
            ours.AddCoveragePoint(0, edges[i].l, edges[i].r);
            patch.AddMappingPoint(edges[i].l, edges[i].r);
        }
        ours.StopMapping(0);
    }

    private static double TheirWorked(List<CPatches> theirs) => theirs.Count == 0 ? 0 : theirs[^1].workedAreaTotal;

    /// <summary>A straight pass north, x from e0 to e1, every metre from n0 to n1.</summary>
    private static List<(Vec2, Vec2)> Straight(double e0, double e1, double n0, double n1)
        => Enumerable.Range(0, (int)(n1 - n0) + 1).Select(i => (new Vec2(e0, n0 + i), new Vec2(e1, n0 + i))).ToList();

    /// <summary>A 6 m tool along a gentle S, a fix every metre of travel.</summary>
    private static List<(Vec2, Vec2)> Bend()
    {
        var list = new List<(Vec2, Vec2)>();
        for (int i = 0; i <= 80; i++)
        {
            double n = 20 + i;
            double e = 60 + 8 * Math.Sin(i * 0.08);
            double heading = Math.Atan2(8 * 0.08 * Math.Cos(i * 0.08), 1); // d e / d n
            double se = Math.Cos(heading) * 3, sn = -Math.Sin(heading) * 3; // half the tool, square to the travel
            list.Add((new Vec2(e - se, n - sn), new Vec2(e + se, n + sn)));
        }
        return list;
    }

    [Test]
    public void WorkedArea_IsTheStripAreaAgOpenGpsAdds()
    {
        var ours = Ours();
        var theirs = new List<CPatches>();
        Drive(ours, theirs, Straight(10, 16, 10, 50));
        Assert.That(ours.TotalWorkedArea, Is.EqualTo(TheirWorked(theirs)).Within(1e-6), "a straight pass");
        Assert.That(ours.TotalWorkedArea, Is.EqualTo(240).Within(1e-6), "6 m x 40 m");

        Drive(ours, theirs, Bend());
        Assert.That(ours.TotalWorkedArea, Is.EqualTo(TheirWorked(theirs)).Within(1e-6), "a bend: the same two triangles per fix");
        Assert.That(ours.TotalWorkedAreaUser, Is.EqualTo(ours.TotalWorkedArea), "the trip area counts the same strips");
    }

    [Test]
    public void ActualAreaAndOverlap_AreWhatAgOpenGpsReadsOffItsPixels()
    {
        // Three passes: [10,16], then [13,19] twice. Ground: 3 m once, 3 m twice, 3 m thrice,
        // 40 m long; worked 720 m², actual 360 m², overlap 50 %.
        var ours = Ours();
        var theirs = new List<CPatches>();
        Drive(ours, theirs, Straight(10, 16, 10, 50));
        Drive(ours, theirs, Straight(13, 19, 10, 50));
        Drive(ours, theirs, Straight(13, 19, 10, 50));

        var (theirActual, theirOverlap) = OglZoomPaint(theirs, 0, FieldSide, 0, FieldSide, TheirWorked(theirs));
        double ourWorked = ours.TotalWorkedArea, ourActual = Math.Min(ours.ActualAreaCovered, ourWorked);
        double ourOverlap = (1 - ourActual / ourWorked) * 100; // the field data card's arithmetic (overlays.js)
        TestContext.Out.WriteLine($"worked {ourWorked:F1} | actual ours {ourActual:F1} theirs {theirActual:F1} | overlap ours {ourOverlap:F2}% theirs {theirOverlap:F2}%");
        Assert.Multiple(() =>
        {
            Assert.That(ourWorked, Is.EqualTo(TheirWorked(theirs)).Within(1e-6));
            Assert.That(theirActual, Is.EqualTo(360).Within(360 * 0.03), "AgOpenGPS's pixels, at 0.3 m, read the ground about right");
            Assert.That(ourActual, Is.EqualTo(theirActual).Within(360 * 0.03), "actual area: ours is theirs within the pixel's worth");
            Assert.That(ourOverlap, Is.EqualTo(theirOverlap).Within(1.5), "overlap %");
        });
    }

    [Test]
    public void NoOverlap_IsZeroInBoth_AndADoublePass_IsHalf()
    {
        var ours = Ours();
        var theirs = new List<CPatches>();
        Drive(ours, theirs, Straight(10, 16, 10, 50));
        var (a1, o1) = OglZoomPaint(theirs, 0, FieldSide, 0, FieldSide, TheirWorked(theirs));
        Assert.That(o1, Is.Zero);
        Assert.That((1 - Math.Min(ours.ActualAreaCovered, ours.TotalWorkedArea) / ours.TotalWorkedArea) * 100, Is.EqualTo(0).Within(1.5));

        Drive(ours, theirs, Straight(10, 16, 10, 50));
        var (a2, o2) = OglZoomPaint(theirs, 0, FieldSide, 0, FieldSide, TheirWorked(theirs));
        Assert.That(o2, Is.EqualTo(50).Within(0.01), "AgOpenGPS: the same ground twice is 50 %");
        Assert.That((1 - Math.Min(ours.ActualAreaCovered, ours.TotalWorkedArea) / ours.TotalWorkedArea) * 100, Is.EqualTo(50).Within(1.5));
        Assert.That(a2, Is.EqualTo(240).Within(240 * 0.03));
        Assert.That(ours.ActualAreaCovered, Is.EqualTo(a2).Within(240 * 0.03));
    }

    [Test]
    public void TotalArea_IsTheOuterBoundaryLessTheInnerOnes()
    {
        static BoundaryPolygon Ring(double e0, double n0, double side)
        {
            var p = new BoundaryPolygon();
            foreach (var (e, n) in new[] { (e0, n0), (e0 + side, n0), (e0 + side, n0 + side), (e0, n0 + side) })
                p.Points.Add(new BoundaryPoint(e, n, 0));
            p.UpdateBounds();
            return p;
        }
        var boundary = new Boundary { OuterBoundary = Ring(0, 0, 120) };
        boundary.InnerBoundaries.Add(Ring(20, 20, 10));
        boundary.InnerBoundaries.Add(Ring(60, 60, 20));

        static List<Vec3> Fence(BoundaryPolygon p) => p.Points.Select(q => new Vec3(q.Easting, q.Northing, 0)).ToList();
        var areas = new List<double> { FenceArea(Fence(boundary.OuterBoundary)) };
        areas.AddRange(boundary.InnerBoundaries.Select(i => FenceArea(Fence(i))));
        var (outer, lessInner) = UpdateBoundaryAreas(areas);

        Assert.That(boundary.OuterBoundary.AreaHectares * 10000, Is.EqualTo(outer).Within(1e-6));
        Assert.That(boundary.AreaHectares * 10000, Is.EqualTo(lessInner).Within(1e-6), "the card's Total (MainViewModel.FieldBoundaryAreaSqM)");
        Assert.That(lessInner, Is.EqualTo(14400 - 100 - 400).Within(1e-9));
    }

    [TestCase(14400, 3000, 6, 8.0)]
    [TestCase(14400, 3000, 6, 1.5)]   // below 2 km/h: no time estimate
    [TestCase(14400, 14400, 6, 8.0)]  // done
    [TestCase(5, 0, 6, 8.0)]          // a field under 10 m²: no percentage
    public void RemainingRateAndTime_AreCFieldDatas(double areaLessInner, double worked, double toolWidth, double kmh)
    {
        // The host's FieldStatistics (the model behind the card's numbers, AgOpenGPS CFieldData);
        // the web card does the same arithmetic in overlays.js from the frames' m² and the tick's speed.
        var fs = new FieldStatistics { AreaBoundaryOuterLessInner = areaLessInner, WorkedAreaTotal = worked };
        Assert.Multiple(() =>
        {
            Assert.That(fs.WorkedAreaRemainHectares.ToString("N2", System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(WorkedAreaRemainHectares(areaLessInner, worked)), "remaining ha");
            string theirPct = WorkedAreaRemainPercentage(areaLessInner, worked);
            Assert.That(fs.CalculateRemainPercentage().ToString("N1", System.Globalization.CultureInfo.InvariantCulture) + "%",
                Is.EqualTo(theirPct == "0%" ? "0.0%" : theirPct), "remaining %");
            Assert.That(fs.CalculateWorkRateHectares(toolWidth, kmh).ToString("N1", System.Globalization.CultureInfo.InvariantCulture) + " ha/hr",
                Is.EqualTo(WorkRateHectares(toolWidth, kmh)), "rate");
            string theirTime = TimeTillFinished(areaLessInner, worked, toolWidth, kmh);
            var ourTime = fs.CalculateTimeTillFinished(toolWidth, kmh);
            if (theirTime == "∞ Hrs")
                Assert.That(ourTime, Is.EqualTo(TimeSpan.MaxValue), "no estimate below 2 km/h");
            else
                Assert.That(ourTime.Hours.ToString("00:") + ourTime.Minutes.ToString("00") + '"', Is.EqualTo(theirTime), "time till finished");
        });
    }

    [Test]
    public void FieldList_DistanceAndArea_AreFormFieldExistings()
    {
        // Three fields on disk, as AgOpenWeb writes them: one 2 km north with a 100 m square, one
        // 500 m east with a 200 m square, one with no boundary. The tractor at 52 N 5 E.
        var here = new Wgs84(52.0, 5.0);
        static BoundaryPolygon Square(double side)
        {
            var p = new BoundaryPolygon();
            foreach (var (e, n) in new[] { (0.0, 0.0), (side, 0.0), (side, side), (0.0, side), (side / 2, side + 1), (side / 2, -1) }) // 6 points: AgOpenGPS wants more than 5
                p.Points.Add(new BoundaryPoint(e, n, 0));
            p.UpdateBounds();
            return p;
        }
        var fields = new (string name, Wgs84 origin, BoundaryPolygon? ring)[]
        {
            ("North", here.CalculateNewPostionFromBearingDistance(0, 2000), Square(100)),
            ("East", here.CalculateNewPostionFromBearingDistance(Math.PI / 2, 500), Square(200)),
            ("Bare", here.CalculateNewPostionFromBearingDistance(Math.PI, 1200), null),
        };
        foreach (var (name, origin, ring) in fields)
            GeoJsonFieldService.Save(new Field
            {
                Name = name, DirectoryPath = Path.Combine(_dir, name),
                Origin = new Position { Latitude = origin.Latitude, Longitude = origin.Longitude },
                Boundary = ring == null ? null : new Boundary { OuterBoundary = ring },
            }, tracks: null);

        var ours = new FieldService().FindFieldsNear(_dir, here.Latitude, here.Longitude, double.MaxValue);
        var theirs = fields.Select(f => FieldListRow(f.name, f.origin, here,
            f.ring?.Points.Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList(), isMetric: true)).ToList();

        Assert.That(ours.Select(f => f.Name), Is.EqualTo(Sorted(theirs, 1).Select(r => r.name)).AsCollection, "nearest first, AgOpenGPS's distance order");
        foreach (var o in ours)
        {
            var t = theirs.Single(r => r.name == o.Name);
            Assert.That(o.DistanceKm.ToString("N2", System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(t.distance.Trim()), $"{o.Name}: distance, km");
            if (t.area == "No Bndry")
                Assert.That(o.BoundaryAreaHectares, Is.Zero, $"{o.Name}: no boundary");
            else
                Assert.That(o.BoundaryAreaHectares.ToString("N1", System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(t.area.Trim()), $"{o.Name}: area, ha");
        }
    }

    [Test]
    public void FieldList_AreaWithAnIsland_IsTheWorkableArea()
    {
        // AgOpenGPS's list reads only the first ring of Boundary.txt, so a field with a pond lists its
        // gross area; its own field data card subtracts the pond. AgOpenWeb's list shows the same
        // figure as the card. Kept.
        static BoundaryPolygon Ring(double e0, double n0, double side)
        {
            var p = new BoundaryPolygon();
            foreach (var (e, n) in new[] { (e0, n0), (e0 + side, n0), (e0 + side, n0 + side), (e0, n0 + side), (e0 + side / 2, n0 + side + 1), (e0 + side / 2, n0 - 1) })
                p.Points.Add(new BoundaryPoint(e, n, 0));
            p.UpdateBounds();
            return p;
        }
        var boundary = new Boundary { OuterBoundary = Ring(0, 0, 200) };
        boundary.InnerBoundaries.Add(Ring(50, 50, 40));
        GeoJsonFieldService.Save(new Field
        {
            Name = "Pond", DirectoryPath = Path.Combine(_dir, "Pond"),
            Origin = new Position { Latitude = 52, Longitude = 5 }, Boundary = boundary,
        }, tracks: null);

        var ours = new FieldService().FindFieldsNear(_dir, 52.01, 5, double.MaxValue).Single();
        double gross = FenceArea(boundary.OuterBoundary.Points.Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList()) * 0.0001;
        double net = boundary.AreaHectares;
        Assert.That(net, Is.LessThan(gross));
        Assert.That(ours.BoundaryAreaHectares, Is.EqualTo(net).Within(1e-6), "the card's Total, not AgOpenGPS's first ring");
    }
}
