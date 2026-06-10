using System.Collections.Generic;
using System.Linq;

using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Models.Pipeline;
using AgValoniaGPS.Models.Tool;
using AgValoniaGPS.Services.Geometry;
using AgValoniaGPS.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace AgValoniaGPS.Services.Tests.YouTurn;

/// <summary>
/// Reproduces the field report: a 3-point mounted 16 m sprayer auto-turning
/// next to a HARD outer boundary must not plot the implement through the fence.
/// Calls the auto generator (CreateTurnPath) directly with the operator's rig.
/// </summary>
[TestFixture]
public class AutoTurnHardBoundaryTests
{
    private YouTurnCreationService _creation = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Tool.Width = 16;               // 16 m wide
        c.Tool.Overlap = 0;
        c.Tool.HitchLength = 4.5;        // working-centre distance
        c.Tool.Length = 3.5;             // overall implement length
        c.Tool.IsToolRearFixed = true;   // 3-point mounted
        c.Tool.IsToolTrailing = false;
        c.Tool.IsToolTBT = false;
        c.Tool.IsToolFrontFixed = false;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, 1600);
        c.Guidance.UTurnDistanceFromBoundary = 1.0;
        c.Guidance.UTurnRadius = 8.0;
        c.Guidance.UTurnSmoothing = 3;

        _creation = new YouTurnCreationService(
            NullLogger<YouTurnCreationService>.Instance, new PolygonOffsetService());
    }

    private static Boundary Square(bool hard)
    {
        var pts = new List<Vec2> { new(-50, -50), new(50, -50), new(50, 50), new(-50, 50) };
        return new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = pts.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                IsHard = hard
            }
        };
    }

    private static List<Vec3> HeadlandSquare() => new()
    {
        new(-40, -40, 0), new(40, -40, 0), new(40, 40, 0), new(-40, 40, 0)
    };

    private YouTurnCreationService.TurnPathResult Run(Boundary boundary)
    {
        var pos = new Position { Easting = 0, Northing = 38 }; // approaching north headland (y=40)
        var track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
        var guidance = new GuidanceWorkingState { IsHeadingSameWay = true, HowManyPathsAway = 0 };
        var turn = new YouTurnWorkingState { IsTurnLeft = true };

        return _creation.CreateTurnPath(
            pos, track, headingRadians: 0, abHeading: 0,
            boundary, HeadlandSquare(), guidance, turn,
            uTurnSkipRows: 0, headlandCalculatedWidth: 10, headlandDistance: 5);
    }

    private static ToolGeometry Rig() => new(
        ToolMount.RearFixed, Width: 16, Offset: 0,
        VehicleHitchLength: ConfigurationStore.Instance.Vehicle.HitchLength,
        ToolHitchLength: 4.5, TrailingHitchLength: 0, TrailingToolToPivotLength: 0,
        TankTrailingHitchLength: 0, Length: 3.5);

    [Test]
    public void SoftBoundary_ProducesATurn_SoSetupIsValid()
    {
        var result = Run(Square(hard: false));
        Assert.That(result.Path, Is.Not.Null.And.Count.GreaterThan(10),
            "soft boundary should yield a normal turn — confirms the test setup is valid");
    }

    [Test]
    public void HardBoundary_ImplementNeverCrossesTheFence()
    {
        var outer = Square(hard: true);
        var result = Run(outer);

        // Acceptable outcomes: blocked (no path), OR a path whose implement
        // footprint stays inside the fence. NOT acceptable: a plotted turn whose
        // implement crosses the boundary.
        if (result.Path == null)
        {
            Assert.That(result.ClearanceBlocked, Is.True, "no path should mean clearance-blocked");
            return;
        }

        var poly = outer.OuterBoundary!.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList();
        var swept = ImplementSweptPath.Compute(result.Path, Rig());

        var corners = swept.FrontLeft.Concat(swept.FrontRight)
            .Concat(swept.RearLeft).Concat(swept.RearRight);
        foreach (var corner in corners)
        {
            bool inside = GeometryMath.IsPointInPolygon(poly, new Vec2(corner.Easting, corner.Northing));
            Assert.That(inside, Is.True,
                $"implement corner ({corner.Easting:F1},{corner.Northing:F1}) crossed the hard boundary");
        }
    }
}
