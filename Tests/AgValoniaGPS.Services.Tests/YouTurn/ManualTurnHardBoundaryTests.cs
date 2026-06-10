using System.Collections.Generic;
using System.Linq;

using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Models.Pipeline;
using AgValoniaGPS.Services.Geometry;
using AgValoniaGPS.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace AgValoniaGPS.Services.Tests.YouTurn;

/// <summary>
/// Regression: a manual U-turn must not plot the implement through a HARD outer
/// boundary. The manual arc generator (CreateManualArcPath) originally had no
/// implement clearance — only an arc-endpoint-inside check — so a wide tool
/// would swing past the fence even with the boundary marked hard.
/// </summary>
[TestFixture]
public class ManualTurnHardBoundaryTests
{
    private YouTurnCreationService _creation = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.Tool.Width = 16;          // stored width (fallback only)
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, 1600); // 16 m via sections -> ActualToolWidth = 16
        config.Tool.Overlap = 0;
        config.Tool.HitchLength = 2;
        config.Tool.IsToolRearFixed = true;
        config.Tool.IsToolTrailing = false;
        config.Tool.IsToolTBT = false;
        config.Tool.IsToolFrontFixed = false;
        config.Guidance.UTurnDistanceFromBoundary = 1.0; // clearance margin

        _creation = new YouTurnCreationService(
            NullLogger<YouTurnCreationService>.Instance, new PolygonOffsetService());
    }

    // 100×100 m square boundary centred on origin (edges at ±50).
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

    private List<Vec3> ManualTurn(Boundary boundary, double startN)
    {
        var pos = new Position { Easting = 0, Northing = startN };
        var guidance = new GuidanceWorkingState { IsHeadingSameWay = true };
        // abHeading 0 = north; left turn. Radius = (16/2) = 8 m, so an arc started
        // at N=45 sweeps the pivot to ~N=53 — past the +50 edge.
        return _creation.CreateManualArcPath(pos, abHeading: 0, turnLeft: true,
            boundary, guidance, uTurnSkipRows: 0);
    }

    [Test]
    public void SoftBoundary_NearEdge_PlotsTurn()
    {
        var path = ManualTurn(Square(hard: false), startN: 45);
        Assert.That(path.Count, Is.GreaterThan(2), "soft boundary should still plot the manual turn");
    }

    [Test]
    public void HardBoundary_NearEdge_RefusesTurn()
    {
        var path = ManualTurn(Square(hard: true), startN: 45);
        Assert.That(path.Count, Is.LessThanOrEqualTo(2),
            "hard boundary must refuse a manual turn that swings the implement past the fence");
    }

    [Test]
    public void HardBoundary_WellInside_StillPlotsTurn()
    {
        // Centred at origin: the whole turn + implement stays well inside ±50.
        var path = ManualTurn(Square(hard: true), startN: 0);
        Assert.That(path.Count, Is.GreaterThan(2),
            "a clear turn far from the hard boundary must not be falsely refused");
    }
}
