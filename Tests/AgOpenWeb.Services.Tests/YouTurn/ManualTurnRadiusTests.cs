using System;
using System.Collections.Generic;
using System.Linq;

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// #156: a manual U-turn is driven at the configured U-turn radius (AgOpenGPS
/// BuildManualYouTurn: Dubins at youTurnRadius), not at half the pass offset. With a
/// 6 m tool the old semicircle had a 3 m radius; the tractor fell outside it and the turn
/// was abandoned halfway, square to the next pass.
/// </summary>
[TestFixture]
public class ManualTurnRadiusTests
{
    private YouTurnCreationService _creation = null!;
    private ConfigurationStore _config = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _config = ConfigurationStore.Instance;
        _config.NumSections = 1;
        _config.Tool.Overlap = 0;
        _config.Tool.HitchLength = 2;
        _config.Tool.IsToolRearFixed = true;
        _config.Tool.IsToolTrailing = false;
        _config.Tool.IsToolTBT = false;
        _config.Tool.IsToolFrontFixed = false;
        _config.Guidance.UTurnRadius = 8.0;
        _creation = new YouTurnCreationService(
            NullLogger<YouTurnCreationService>.Instance, new PolygonOffsetService(), _config);
    }

    private List<Vec3> Turn(double toolWidthM, bool turnLeft, double abHeadingDeg = 0, bool sameWay = true)
    {
        _config.Tool.Width = toolWidthM;
        _config.Tool.SetSectionWidth(0, toolWidthM * 100);
        var pos = new Position { Easting = 0, Northing = 0 };
        var guidance = new GuidanceWorkingState { IsHeadingSameWay = sameWay };
        return _creation.CreateManualArcPath(pos, abHeadingDeg * Math.PI / 180, turnLeft,
            boundary: null, guidance, uTurnSkipRows: 0);
    }

    /// <summary>
    /// Tightest turning radius along the path, from heading change per metre. The last two
    /// samples are skipped: the Dubins service appends the exact goal after its 0.25 m
    /// subsample, so the final sample's heading points at it rather than along the arc.
    /// That is 25 cm from the end, inside the 4 m early hand-over.
    /// </summary>
    private static double MinRadius(List<Vec3> path)
    {
        double min = double.MaxValue;
        for (int i = 1; i < path.Count - 3; i++)
        {
            double ds = GeometryMath.Distance(path[i], path[i + 1]);
            double dh = Math.Abs(path[i + 1].Heading - path[i].Heading);
            if (dh > Math.PI) dh = 2 * Math.PI - dh;
            if (dh < 1e-4 || ds < 1e-6) continue;
            min = Math.Min(min, ds / dh);
        }
        return min;
    }

    private static double Deg(double rad) => ((rad * 180 / Math.PI) % 360 + 360) % 360;

    /// <summary>Smallest absolute difference between two headings, degrees.</summary>
    private static double HeadingError(double rad, double expectedDeg)
    {
        double d = Math.Abs(Deg(rad) - expectedDeg) % 360;
        return d > 180 ? 360 - d : d;
    }

    [TestCase(6.0, true, TestName = "ManualTurn_6m_tool_left_keeps_the_8m_radius")]
    [TestCase(6.0, false, TestName = "ManualTurn_6m_tool_right_keeps_the_8m_radius")]
    [TestCase(12.0, true, TestName = "ManualTurn_12m_pass_left_keeps_the_8m_radius")]
    [TestCase(20.0, true, TestName = "ManualTurn_20m_pass_left_keeps_the_8m_radius")]
    public void ManualTurn_keeps_the_configured_radius_and_lands_on_the_next_pass(double toolWidth, bool turnLeft)
    {
        var path = Turn(toolWidth, turnLeft);
        Assume.That(path.Count, Is.GreaterThan(10), "a path was built");

        var first = path[0];
        var last = path[^1];
        // Heading north from the origin: the next pass is at E = ∓ offset (left = west).
        double expectedE = turnLeft ? -toolWidth : toolWidth;
        Assert.Multiple(() =>
        {
            Assert.That((first.Easting, first.Northing), Is.EqualTo((0.0, 0.0)), "the path starts at the tractor");
            Assert.That(MinRadius(path), Is.GreaterThanOrEqualTo(7.5),
                $"a {toolWidth} m pass must not shrink the turn below the configured 8 m radius");
            Assert.That(last.Easting, Is.EqualTo(expectedE).Within(0.1), "the turn ends on the next pass");
            Assert.That(HeadingError(last.Heading, 180), Is.LessThan(1.5), "heading back the other way");
            Assert.That(last.Northing, Is.LessThan(toolWidth + 8),
                "the exit lands near the entry row, not far down the field");
        });
    }

    [Test]
    public void ManualTurn_heading_the_opposite_way_turns_back_toward_the_tractor_side()
    {
        // Track heading north, tractor driving south on it (opposite way), left turn:
        // left of south is east.
        var path = Turn(12.0, turnLeft: true, sameWay: false);
        Assume.That(path.Count, Is.GreaterThan(10));
        Assert.Multiple(() =>
        {
            Assert.That(HeadingError(path[0].Heading, 180), Is.LessThan(1.5));
            Assert.That(path[^1].Easting, Is.EqualTo(12.0).Within(0.1));
            Assert.That(HeadingError(path[^1].Heading, 0), Is.LessThan(1.5));
            Assert.That(MinRadius(path), Is.GreaterThanOrEqualTo(7.5));
        });
    }

    [Test]
    public void ManualTurn_wider_than_the_radius_still_lands_on_the_pass()
    {
        // 20 m pass, 8 m radius: arc – straight – arc, no omega.
        var path = Turn(20.0, turnLeft: false, abHeadingDeg: 90);
        Assume.That(path.Count, Is.GreaterThan(10));
        var last = path[^1];
        Assert.Multiple(() =>
        {
            // Heading east, right turn: the next pass is 20 m south.
            Assert.That(last.Northing, Is.EqualTo(-20.0).Within(0.1));
            Assert.That(HeadingError(last.Heading, 270), Is.LessThan(1.5));
        });
    }
}
