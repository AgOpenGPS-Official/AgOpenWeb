// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Collections.Generic;
using System.Linq;

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Services.YouTurn;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// #150: the U-turn approach alarm fires once as the tractor comes within 20 m of the
/// turn. AgOpenGPS tests an 18–20 m band, which a fast approach steps over between fixes
/// — the alarm then sounded on roughly every other turn.
/// </summary>
[TestFixture]
public class YouTurnApproachAlarmTests
{
    [Test]
    public void AFastApproach_ThatSkipsThe18to20mBand_StillAlarms()
    {
        var sm = BuildStateMachine();
        var turn = new YouTurnWorkingState { TurnPath = TurnPath(0, 30) };

        // 24 m out, then 16 m: nothing lands inside 18–20 m (4 m per fix ≈ 40 km/h at 10 Hz).
        var a = sm.Tick(Ctx(0, 6), new GuidanceWorkingState(), turn);
        Assert.That(a.ApproachAlarmSound, Is.False, "24 m out is too far");

        var b = sm.Tick(Ctx(0, 14), new GuidanceWorkingState(), turn);
        Assert.That(b.ApproachAlarmSound, Is.True, "16 m out: the turn is close, so it must sound");
    }

    [Test]
    public void ItAlarmsOnlyOncePerTurn()
    {
        var sm = BuildStateMachine();
        var turn = new YouTurnWorkingState { TurnPath = TurnPath(0, 30) };

        Assert.That(sm.Tick(Ctx(0, 12), new GuidanceWorkingState(), turn).ApproachAlarmSound, Is.True);
        Assert.That(sm.Tick(Ctx(0, 14), new GuidanceWorkingState(), turn).ApproachAlarmSound, Is.False);
        Assert.That(sm.Tick(Ctx(0, 16), new GuidanceWorkingState(), turn).ApproachAlarmSound, Is.False);
    }

    private static YouTurnStateMachine BuildStateMachine()
    {
        var polygonOffset = Substitute.For<Services.Geometry.IPolygonOffsetService>();
        var creation = new YouTurnCreationService(
            NullLogger<YouTurnCreationService>.Instance, polygonOffset, ConfigurationStore.Instance);
        var pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, ConfigurationStore.Instance);
        return new YouTurnStateMachine(creation, pathing, NullLogger<YouTurnStateMachine>.Instance, ConfigurationStore.Instance);
    }

    private static List<Vec3> TurnPath(double e, double n) =>
        new() { new(e, n, 0), new(e + 1, n + 1, 0), new(e + 2, n + 2, 0) };

    private static YouTurnStateMachine.TickContext Ctx(double e, double n)
    {
        var outer = new BoundaryPolygon
        {
            Points = new List<Vec2> { new(-50, -50), new(50, -50), new(50, 50), new(-50, 50) }
                .Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
        };
        return new YouTurnStateMachine.TickContext(
            new Position { Easting = e, Northing = n, Heading = 0 },
            Models.Track.Track.FromABLine("AB-test", new Vec3(0, -100, 0), new Vec3(0, 100, 0)),
            new Boundary { OuterBoundary = outer },
            new List<Vec3> { new(-45, -45, 0), new(45, -45, 0), new(45, 45, 0), new(-45, 45, 0) },
            UTurnSkipRows: 0, IsSkipWorkedMode: false,
            HeadlandCalculatedWidth: 10.0, HeadlandDistance: 5.0);
    }
}
