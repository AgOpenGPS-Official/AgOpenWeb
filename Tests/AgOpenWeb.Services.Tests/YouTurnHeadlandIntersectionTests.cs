// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Collections.Generic;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Services.YouTurn;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Repro for issue #354. The U-turn arc lands on cyan NextTrack only if the
/// arc start is on the current pass. The arc start comes from
/// FindABTurnPoint, which extends a ray from ABReferencePoint, which comes
/// from FindTrackHeadlandIntersectionAhead. So the latter must always
/// return a point on the AB line — independent of the vehicle's
/// perpendicular distance from it.
///
/// Before the fix: the AB-line branch built a vehicle-anchored line and
/// intersected it with the headland, so a vehicle perpendicular-offset from
/// AB produced an intersection perpendicular-offset from AB by the same
/// amount, cascading into the U-turn arc landing perpendicular-offset from
/// NextTrack. This was sequence-dependent — the perpendicular offset is
/// non-zero when activation happens before the auto-pass-detect catches up.
/// </summary>
[TestFixture]
public class YouTurnHeadlandIntersectionTests
{
    [Test]
    public void Returns_PointOnAbLine_WhenVehicleOnAbLine()
    {
        // Sanity check baseline.
        var track = MakeAbLine(0, -100, 0, 100); // vertical AB at E=0
        var headland = MakeRectangularHeadland(southN: 50, northN: 100);
        var vehicle = new Vec3(0, 0, 0);

        var result = YouTurnCreationService.FindTrackHeadlandIntersectionAhead(
            track, vehicle, headland, headingSameWay: true);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Easting, Is.EqualTo(0).Within(0.001));
        Assert.That(result.Value.Northing, Is.EqualTo(50).Within(0.001));
    }

    [Test]
    public void Returns_PointOnAbLine_WhenVehiclePerpendicularOffsetFromAbLine()
    {
        // Issue #354 repro: vehicle 10 m perpendicular-offset from the AB line.
        // Pre-fix this returned (10, 50) — the intersection of the
        // vehicle-anchored ray with the headland. Post-fix returns (0, 50)
        // — the intersection of the AB line with the headland.
        var track = MakeAbLine(0, -100, 0, 100);
        var headland = MakeRectangularHeadland(southN: 50, northN: 100);
        var vehicle = new Vec3(10, 0, 0); // 10 m east of AB

        var result = YouTurnCreationService.FindTrackHeadlandIntersectionAhead(
            track, vehicle, headland, headingSameWay: true);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Easting, Is.EqualTo(0).Within(0.001),
            "Intersection must be on the AB line, not on a vehicle-anchored ray");
        Assert.That(result.Value.Northing, Is.EqualTo(50).Within(0.001));
    }

    [Test]
    public void Returns_PointOnAbLine_WhenAbLineIsNotPerpendicularToHeadland()
    {
        // Diagonal AB + horizontal headland; vehicle off-line.
        // AB from (-50,-100) to (50,100) crosses N=0 at E=0.
        var track = MakeAbLine(-50, -100, 50, 100);
        var headland = MakeRectangularHeadland(southN: 0, northN: 50);
        var vehicle = new Vec3(20, -50, 0);

        var result = YouTurnCreationService.FindTrackHeadlandIntersectionAhead(
            track, vehicle, headland, headingSameWay: true);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Easting, Is.EqualTo(0).Within(0.001),
            "Intersection must be on the AB line, not biased by vehicle E=20");
        Assert.That(result.Value.Northing, Is.EqualTo(0).Within(0.001));
    }

    [Test]
    public void HeadingOpposite_StillReturnsPointOnAbLine()
    {
        // headingSameWay=false (vehicle going B→A). Same property must hold.
        var track = MakeAbLine(0, -100, 0, 100);
        // Headland behind the vehicle (south). South edge is the close one.
        var headland = MakeRectangularHeadland(southN: -100, northN: -50);
        var vehicle = new Vec3(10, 0, 0);

        var result = YouTurnCreationService.FindTrackHeadlandIntersectionAhead(
            track, vehicle, headland, headingSameWay: false);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Easting, Is.EqualTo(0).Within(0.001));
        Assert.That(result.Value.Northing, Is.EqualTo(-50).Within(0.001));
    }

    private static AgOpenWeb.Models.Track.Track MakeAbLine(double aE, double aN, double bE, double bN) =>
        AgOpenWeb.Models.Track.Track.FromABLine("test", new Vec3(aE, aN, 0), new Vec3(bE, bN, 0));

    private static List<Vec3> MakeRectangularHeadland(double southN, double northN) => new()
    {
        new Vec3(-200, southN, 0),
        new Vec3(200, southN, 0),
        new Vec3(200, northN, 0),
        new Vec3(-200, northN, 0),
    };
}
