// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services.Track;
using TrackModel = AgOpenWeb.Models.Track.Track;

namespace AgOpenWeb.Services.Tests.Track;

/// <summary>
/// #694: the end of an open curve, as AgOpenGPS (CABCurve.GetCurrentCurveLine, "past end of
/// curve"): with AutoSteer on and going forward, a goal point at the curve's end in the direction
/// of travel flags the end of the track (the pipeline turns AutoSteer off when no U-turn takes
/// over). Past the end the goal point keeps ahead of the tractor: walking from the closest point,
/// already beyond the last point, back to it put the goal behind the tractor, which turned round
/// to it (the reporter's loop back).
/// </summary>
[TestFixture]
public class CurveEndOfTrackTests
{
    private readonly TrackGuidanceService _service = new();

    // A straight curve north, a point a metre, from (0,0) to (0,100).
    private static TrackModel North(bool closed = false)
    {
        var pts = Enumerable.Range(0, 101).Select(i => new Vec3(0, i, 0)).ToList();
        return TrackModel.FromCurve("N", pts, closed);
    }

    private TrackGuidanceOutput At(TrackModel track, double n, double headingRad, bool autoSteer = true,
        bool reverse = false, bool stanley = false, bool youTurn = false, double lookAhead = 5)
    {
        var pivot = new Vec3(0.2, n, headingRad);
        return _service.CalculateGuidance(new TrackGuidanceInput
        {
            Track = track,
            PivotPosition = pivot,
            SteerPosition = new Vec3(0.2 + Math.Sin(headingRad) * 2.5, n + Math.Cos(headingRad) * 2.5, headingRad),
            UseStanley = stanley,
            Wheelbase = 2.5,
            MaxSteerAngle = 35,
            GoalPointDistance = lookAhead,
            FixHeading = headingRad,
            AvgSpeed = 10,
            IsAutoSteerOn = autoSteer,
            IsReverse = reverse,
            IsYouTurnTriggered = youTurn,
            FindGlobalNearest = true,
        });
    }

    [Test]
    public void Mid_curve_is_not_the_end()
    {
        Assert.That(At(North(), 50, 0).IsAtEndOfTrack, Is.False);
        Assert.That(At(North(), 50, Math.PI).IsAtEndOfTrack, Is.False, "the other way");
    }

    [TestCase(95.0)]   // the goal point (5 m ahead) at the end
    [TestCase(97.0)]   // beyond it on the straight past it
    [TestCase(100.5)]  // the tractor past the last point
    [TestCase(130.0)]  // well past: a fix stepped over the 0.5 m window
    public void Driving_north_the_last_point_is_the_end(double n)
        => Assert.That(At(North(), n, 0).IsAtEndOfTrack, Is.True);

    [TestCase(5.0)]
    [TestCase(-20.0)]
    public void Driving_south_the_first_point_is_the_end(double n)
        => Assert.That(At(North(), n, Math.PI).IsAtEndOfTrack, Is.True);

    [Test]
    public void Driving_south_the_far_end_behind_is_not_the_end()
        => Assert.That(At(North(), 97, Math.PI).IsAtEndOfTrack, Is.False);

    [TestCase(101.0)]
    [TestCase(110.0)]
    [TestCase(130.0)]
    public void Past_the_end_the_goal_point_is_ahead_of_the_tractor(double n)
    {
        var o = At(North(), n, 0);
        Assert.That(o.GoalPoint.Northing, Is.EqualTo(n + 5).Within(1e-6), "a look-ahead on along the straight past the end");
        Assert.That(Math.Abs(o.SteerAngle), Is.LessThan(5), "no turn back");
    }

    [Test]
    public void Stanley_past_the_end_is_the_end()
    {
        Assert.That(At(North(), 50, 0, stanley: true).IsAtEndOfTrack, Is.False);
        Assert.That(At(North(), 101, 0, stanley: true).IsAtEndOfTrack, Is.True);
    }

    [Test]
    public void Not_with_AutoSteer_off_reversing_or_in_a_U_turn()
    {
        Assert.That(At(North(), 97, 0, autoSteer: false).IsAtEndOfTrack, Is.False, "AutoSteer off");
        Assert.That(At(North(), 97, 0, reverse: true).IsAtEndOfTrack, Is.False, "reversing");
        Assert.That(At(North(), 97, 0, youTurn: true).IsAtEndOfTrack, Is.False, "a U-turn steers");
    }

    [Test]
    public void A_closed_loop_has_no_end()
    {
        // A 50 m square, closed: driving past its first/last point carries on round.
        var pts = new List<Vec3>();
        foreach (var (e0, n0, e1, n1) in new[] { (0.0, 0.0, 0.0, 50.0), (0, 50, 50, 50), (50, 50, 50, 0), (50, 0, 0, 0) })
            for (int k = 0; k < 50; k++)
                pts.Add(new Vec3(e0 + (e1 - e0) * k / 50, n0 + (n1 - n0) * k / 50, Math.Atan2(e1 - e0, n1 - n0)));
        pts.Add(pts[0]);
        var loop = TrackModel.FromCurve("Loop", pts, isClosed: true);
        // Heading west along the south side, the seam just ahead.
        Assert.That(At(loop, 0, 0).IsAtEndOfTrack, Is.False);
        var o = _service.CalculateGuidance(new TrackGuidanceInput
        {
            Track = loop,
            PivotPosition = new Vec3(3, 0.2, -Math.PI / 2),
            SteerPosition = new Vec3(0.5, 0.2, -Math.PI / 2),
            Wheelbase = 2.5, MaxSteerAngle = 35, GoalPointDistance = 5,
            FixHeading = -Math.PI / 2 + 2 * Math.PI, AvgSpeed = 10, IsAutoSteerOn = true, FindGlobalNearest = true,
        });
        Assert.That(o.IsAtEndOfTrack, Is.False);
    }

    [Test]
    public void An_AB_line_has_no_end()
    {
        var ab = TrackModel.FromABLine("AB", new Vec3(0, 0, 0), new Vec3(0, 100, 0));
        Assert.That(At(ab, 98, 0).IsAtEndOfTrack, Is.False);
        Assert.That(At(ab, 150, 0).IsAtEndOfTrack, Is.False);
    }
}
