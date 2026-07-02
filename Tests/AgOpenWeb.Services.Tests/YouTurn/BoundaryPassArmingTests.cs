using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// Auto-U-turn arming on a fence-hugging boundary-follow pass in a field with NO real
/// headland. The turn line is the AgOpen model: fence inset by uturnDistanceFromBoundary
/// only (NOT turn-radius + distance). A pass offset ~half a tool width inside the fence
/// therefore sits INSIDE the turn line → the tractor reads as "in the cultivated / in-turn
/// area" → it arms through the ordinary path, no special-casing. This is the regression
/// guard for the old bug where the synthetic line was inset by turnRadius + distance (~10 m),
/// which put the boundary pass permanently OUTSIDE the band so it never armed.
/// </summary>
[TestFixture]
[NonParallelizable]
public class BoundaryPassArmingTests
{
    private const double ToolWidth = 6.0;
    private const double HalfTool = ToolWidth / 2.0;
    private const double DistFromBoundary = 2.0;

    private YouTurnStateMachine _sm = null!;
    private Boundary _boundary = null!;
    private List<Vec3> _turnLine = null!;
    private Models.Track.Track _track = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = 2.5;
        c.Vehicle.MaxSteerAngle = 35;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, (int)(ToolWidth * 100)); // 6 m
        c.Guidance.UTurnRadius = 8.0;
        c.Guidance.UTurnExtension = 16.0;
        c.Guidance.UTurnDistanceFromBoundary = DistFromBoundary;
        c.Guidance.UTurnStyle = 0;

        var offset = new PolygonOffsetService();
        var creation = new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, offset, c);
        var pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);
        _sm = new YouTurnStateMachine(creation, pathing, NullLogger<YouTurnStateMachine>.Instance, c);

        // 100 x 100 m field, fence at ±50.
        var outer = new List<Vec2> { new(-50, -50), new(50, -50), new(50, 50), new(-50, 50) };
        _boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = outer.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            },
        };

        // Turn line as the pipeline now builds it: fence inset by uturnDistanceFromBoundary
        // = 2 m → square at ±48. (Pre-fix it was inset by 10 m → ±40, which excluded the pass.)
        _turnLine = offset.CalculatePointHeadings(
            offset.CreateInwardOffset(outer, DistFromBoundary)!);

        // Fence-hugging boundary-follow pass along the WEST fence: x = -50 + halfTool = -47,
        // heading due north. Inside the ±48 turn line (x = -47 > -48).
        var curve = new List<Vec3>();
        for (double y = -46; y <= 46 + 1e-9; y += 2.0)
            curve.Add(new Vec3(-50 + HalfTool, y, 0));
        _track = Models.Track.Track.FromCurve("bnd pass", curve);
        _track.NoPassOffset = true;
    }

    private YouTurnStateMachine.TickContext MakeContext(double easting, double northing, double headingDeg)
    {
        var pos = new Position
        {
            Easting = easting,
            Northing = northing,
            Heading = headingDeg,
            Speed = 2.78,
        };
        return new YouTurnStateMachine.TickContext(
            pos, _track, _boundary, _turnLine,
            UTurnSkipRows: 0, IsSkipWorkedMode: false,
            HeadlandCalculatedWidth: DistFromBoundary, HeadlandDistance: 5.0);
    }

    [Test]
    public void FenceHuggingPass_IsInsideTurnLine_AndArms()
    {
        var gState = new GuidanceWorkingState();
        var tState = new YouTurnWorkingState { IsEnabled = true };

        // Mid-pass, heading north along the west fence.
        var ctx = MakeContext(-47, -20, 0);

        List<Vec3>? armed = null;
        for (int i = 0; i < 8 && armed == null; i++)
        {
            tState.YouTurnCounter++;
            _sm.Tick(in ctx, gState, tState);
            armed = tState.TurnPath;
        }

        Assert.That(tState.CurrentZone, Is.EqualTo(TractorZone.InCultivatedArea),
            "the fence-hugging pass must read as inside the turn line (in-turn-bounds)");
        Assert.That(armed, Is.Not.Null, "turn must arm through the normal cultivated-zone path");
        Assert.That(armed!.Count, Is.GreaterThan(10));

        // The next pass must be INWARD (east of the fence pass, toward the field).
        Assert.That(tState.NextTrack, Is.Not.Null);
        double avgNextEasting = tState.NextTrack!.Points.Average(p => p.Easting);
        Assert.That(avgNextEasting, Is.GreaterThan(-47.0 + 1.0),
            "next track must land on the inward side of the boundary pass");
    }
}
