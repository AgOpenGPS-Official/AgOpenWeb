// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Section;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// With "Off outside boundary" on, a section coming to an obstacle is switched off the look-ahead
/// time before it, so the valve, which takes that long, closes at the obstacle's edge. Where the
/// section is now and where it is about to be are judged by one rule (95 % in, both ends in);
/// the look-ahead used a weaker one (50 %, no ends) on a single line, so an obstacle that touched
/// only a section's end, or one shorter than the look-ahead distance that the line had already
/// passed, was found on arrival and the valve sprayed into it for its closing time (field job
/// J2/J6, 2026-11-09: 96 sections switched off on arrival in J6, 39 by the end test, 57 short
/// obstacles).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class ObstacleEdgeLookAheadTests
{
    private const double Speed = 2.0;     // m/s
    private const double TickHz = 100.0;  // the control loop
    private const double LookOff = 0.5;   // s: the module is told off Speed × LookOff = 1 m early
    private SectionControlService _service = null!;

    private static BoundaryPolygon Rect(double x0, double y0, double x1, double y1)
    {
        var poly = new BoundaryPolygon();
        poly.Points.Add(new BoundaryPoint(x0, y0, 0));
        poly.Points.Add(new BoundaryPoint(x1, y0, 0));
        poly.Points.Add(new BoundaryPoint(x1, y1, 0));
        poly.Points.Add(new BoundaryPoint(x0, y1, 0));
        poly.UpdateBounds();
        return poly;
    }

    /// <summary>One 2 m section (5 cm coverage margin by default), the field 0..200, one obstacle.</summary>
    private void Field(BoundaryPolygon obstacle, bool offWhenOut = true)
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, 200);
        config.Tool.Offset = 0;
        config.Tool.IsSectionOffWhenOut = offWhenOut;
        config.Tool.LookAheadOnSetting = 1.0;
        config.Tool.LookAheadOffSetting = LookOff;

        var boundary = new Boundary { OuterBoundary = Rect(0, 0, 200, 200) };
        boundary.InnerBoundaries.Add(obstacle);
        _service = new SectionControlService(Substitute.For<IToolPositionService>(),
            Substitute.For<ICoverageMapService>(), config) { TickHz = TickHz };
        _service.SetFieldContext(new AgOpenWeb.Models.Sections.SectionFieldContext(boundary, null, true, false));
        _service.SetAllAuto();
        _service.MasterState = SectionMasterState.Auto;
    }

    /// <summary>Drive north at <paramref name="e"/>; the northings where the module bit and the
    /// map's IsOn change.</summary>
    private (List<(double N, bool On)> Bit, List<(double N, bool On)> Map) DriveNorth(double e, double fromN, double toN)
    {
        var bit = new List<(double, bool)>();
        var map = new List<(double, bool)>();
        bool b = false, m = false;
        for (double n = fromN; n <= toN; n += Speed / TickHz)
        {
            _service.Update(new Vec3(e, n, 0), 0, 0, Speed);
            bool nb = (_service.GetSectionBits64() & 1) != 0, nm = _service.SectionStates[0].IsOn;
            if (nb != b) bit.Add((n, nb));
            if (nm != m) map.Add((n, nm));
            b = nb; m = nm;
        }
        return (bit, map);
    }

    private static string Show(List<(double N, bool On)> l) => string.Join(", ", l.Select(x => $"{(x.On ? "on" : "off")}@{x.N:F2}"));

    /// <summary>The module is told off about Speed × LookOff before the obstacle (<paramref name="edgeN"/>
    /// to <paramref name="farN"/>) and not told on again until the section is past it (a valve told
    /// on again before it has closed never closes), and the map stops at the obstacle.</summary>
    private static void ToldOffBefore(List<(double N, bool On)> bit, List<(double N, bool On)> map, double edgeN, double farN)
    {
        var bitOff = bit.First(x => !x.On && x.N > 50);
        var mapOff = map.First(x => !x.On && x.N > 50);
        Assert.Multiple(() =>
        {
            Assert.That(bitOff.N, Is.EqualTo(edgeN - Speed * LookOff).Within(0.3), "module told off before the obstacle: " + Show(bit));
            Assert.That(bit.Any(x => x.On && x.N > bitOff.N && x.N < farN), Is.False, "module kept off until past the obstacle: " + Show(bit));
            Assert.That(mapOff.N, Is.EqualTo(edgeN).Within(0.3), "map stops at the obstacle: " + Show(map));
        });
    }

    [Test]
    public void An_obstacle_over_15_percent_of_the_section()
    {
        Field(Rect(90, 100, 110, 120));          // the section at 110.7 spans 109.7..111.7: 0.3 m over
        var (bit, map) = DriveNorth(110.7, 60, 150);
        ToldOffBefore(bit, map, 100, 120);
        Assert.That(bit.Last().On, Is.True, "on again past it: " + Show(bit));
    }

    [Test]
    public void An_obstacle_shorter_than_the_look_ahead_distance()
    {
        Field(Rect(90, 100, 110, 100.6));        // 0.6 m long; the look-off reaches 1 m ahead
        var (bit, map) = DriveNorth(100, 60, 150);
        ToldOffBefore(bit, map, 100, 100.6);
        Assert.That(bit.Last().On, Is.True, "on again past it: " + Show(bit));
    }

    [Test]
    public void An_obstacle_touching_only_the_end_of_the_section()
    {
        // The section with its 5 cm margin spans 108.97..111.07: 3 cm over the obstacle's east
        // side (1.4 % of it, so the 95 % rule alone passes), its west end inside.
        Field(Rect(90, 100, 110, 120));
        var (bit, map) = DriveNorth(110.02, 60, 150);
        ToldOffBefore(bit, map, 100, 120);
    }

    [Test]
    public void Without_off_outside_boundary_a_section_mostly_clear_of_the_obstacle_stays_on()
    {
        Field(Rect(90, 100, 110, 120), offWhenOut: false);
        var (bit, map) = DriveNorth(110.7, 60, 150);
        Assert.Multiple(() =>
        {
            Assert.That(bit.Select(x => x.On), Is.EqualTo(new[] { true }), "module: " + Show(bit));
            Assert.That(map.Select(x => x.On), Is.EqualTo(new[] { true }), "map: " + Show(map));
        });
    }
    /// <summary>
    /// A lap with the boom's end on the boundary: the section's real ends are judged, not the
    /// map's 5 cm coverage margin, with <see cref="SectionControlService.EdgeLeewayM"/> of leeway
    /// for the receiver's jitter. With the margin in the test the outer section never opened on
    /// the first lap (path job scenarios EDGE-NOT-SPRAYED, a 1 m strip round the field).
    /// </summary>
    [TestCase(199.00, true, TestName = "The section's end on the boundary sprays")]
    [TestCase(199.04, true, TestName = "The section's end 4 cm over the boundary sprays")]
    [TestCase(199.10, false, TestName = "The section's end 10 cm over the boundary stays shut")]
    public void A_lap_along_the_boundary(double e, bool sprays)
    {
        Field(Rect(10, 10, 12, 12));             // an obstacle far off; the 2 m section spans e ± 1
        var (bit, _) = DriveNorth(e, 60, 150);
        bool openAt100 = bit.LastOrDefault(x => x.N <= 100).On;
        Assert.That(openAt100, Is.EqualTo(sprays), "module: " + Show(bit));
    }
}
