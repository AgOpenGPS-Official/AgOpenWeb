// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Linq;
using System.Reflection;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.Pipeline;
using AgValoniaGPS.Models.State;
using AgValoniaGPS.Models.Track;

namespace AgValoniaGPS.Models.Tests.Pipeline;

[TestFixture]
public class GuidanceWorkingStateTests
{
    [Test]
    public void Property_shape_mirrors_GuidanceState()
    {
        var workingProps = DeclaredProps(typeof(GuidanceWorkingState));
        var observableProps = DeclaredProps(typeof(GuidanceState));

        Assert.That(workingProps, Is.EqualTo(observableProps),
            "GuidanceWorkingState must mirror GuidanceState property-for-property. " +
            "If a property is added to one, add it to the other.");
    }

    [Test]
    public void Defaults_match_GuidanceState_defaults()
    {
        var working = new GuidanceWorkingState();
        var observable = new GuidanceState();

        Assert.Multiple(() =>
        {
            Assert.That(working.IsGuidanceActive, Is.EqualTo(observable.IsGuidanceActive));
            Assert.That(working.CrossTrackError, Is.EqualTo(observable.CrossTrackError));
            Assert.That(working.SteerAngle, Is.EqualTo(observable.SteerAngle));
            Assert.That(working.IsHeadingSameWay, Is.EqualTo(observable.IsHeadingSameWay));
            Assert.That(working.CurrentLineLabel, Is.EqualTo(observable.CurrentLineLabel),
                "CurrentLineLabel has a non-default initializer (\"1L\")");
            Assert.That(working.HowManyPathsAway, Is.EqualTo(observable.HowManyPathsAway));
            Assert.That(working.IsContourMode, Is.EqualTo(observable.IsContourMode));
        });
    }

    [Test]
    public void Reset_clears_the_same_fields_as_GuidanceState_Reset()
    {
        var working = MakeNonDefaultWorking();
        var observable = MakeNonDefaultObservable();

        working.Reset();
        observable.Reset();

        Assert.Multiple(() =>
        {
            Assert.That(working.ActiveTrack, Is.EqualTo(observable.ActiveTrack));
            Assert.That(working.IsGuidanceActive, Is.EqualTo(observable.IsGuidanceActive));
            Assert.That(working.CrossTrackError, Is.EqualTo(observable.CrossTrackError));
            Assert.That(working.HeadingError, Is.EqualTo(observable.HeadingError));
            Assert.That(working.SteerAngle, Is.EqualTo(observable.SteerAngle));
            Assert.That(working.SteerAngleRaw, Is.EqualTo(observable.SteerAngleRaw));
            Assert.That(working.DistanceOffRaw, Is.EqualTo(observable.DistanceOffRaw));
            Assert.That(working.PpIntegral, Is.EqualTo(observable.PpIntegral));
            Assert.That(working.PpPivotDistanceError, Is.EqualTo(observable.PpPivotDistanceError));
            Assert.That(working.PpPivotDistanceErrorLast, Is.EqualTo(observable.PpPivotDistanceErrorLast));
            Assert.That(working.PpCounter, Is.EqualTo(observable.PpCounter));
            Assert.That(working.GoalPoint.Easting, Is.EqualTo(observable.GoalPoint.Easting));
            Assert.That(working.RadiusPoint.Easting, Is.EqualTo(observable.RadiusPoint.Easting));
            Assert.That(working.PurePursuitRadius, Is.EqualTo(observable.PurePursuitRadius));
            Assert.That(working.IsHeadingSameWay, Is.EqualTo(observable.IsHeadingSameWay));
            Assert.That(working.IsReverse, Is.EqualTo(observable.IsReverse));
            Assert.That(working.HowManyPathsAway, Is.EqualTo(observable.HowManyPathsAway));
            Assert.That(working.NudgeOffset, Is.EqualTo(observable.NudgeOffset));
            Assert.That(working.CurrentLineLabel, Is.EqualTo(observable.CurrentLineLabel));
            Assert.That(working.IsContourMode, Is.EqualTo(observable.IsContourMode));
        });
    }

    // Phase D D9 removed UpdateFromGuidance from both GuidanceState and
    // GuidanceWorkingState — its only caller (CalculateContourGuidance) was
    // dead code. Cycle writers target specific fields directly. A parity
    // test for the method no longer makes sense.

    private static (string Name, System.Type Type)[] DeclaredProps(System.Type t) =>
        t.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance)
         .Select(p => (p.Name, p.PropertyType))
         .OrderBy(x => x.Name)
         .ToArray();

    private static GuidanceWorkingState MakeNonDefaultWorking() => new()
    {
        IsGuidanceActive = true,
        CrossTrackError = 0.3,
        HeadingError = 0.1,
        SteerAngle = 2.0,
        SteerAngleRaw = 200,
        DistanceOffRaw = 300,
        PpIntegral = 1.1,
        PpPivotDistanceError = 0.9,
        PpPivotDistanceErrorLast = 0.8,
        PpCounter = 5,
        GoalPoint = new Vec2 { Easting = 10, Northing = 20 },
        RadiusPoint = new Vec2 { Easting = 30, Northing = 40 },
        PurePursuitRadius = 5.5,
        IsHeadingSameWay = false,
        IsReverse = true,
        HowManyPathsAway = 2,
        NudgeOffset = 0.15,
        CurrentLineLabel = "3R",
        IsContourMode = true,
    };

    private static GuidanceState MakeNonDefaultObservable() => new()
    {
        IsGuidanceActive = true,
        CrossTrackError = 0.3,
        HeadingError = 0.1,
        SteerAngle = 2.0,
        SteerAngleRaw = 200,
        DistanceOffRaw = 300,
        PpIntegral = 1.1,
        PpPivotDistanceError = 0.9,
        PpPivotDistanceErrorLast = 0.8,
        PpCounter = 5,
        GoalPoint = new Vec2 { Easting = 10, Northing = 20 },
        RadiusPoint = new Vec2 { Easting = 30, Northing = 40 },
        PurePursuitRadius = 5.5,
        IsHeadingSameWay = false,
        IsReverse = true,
        HowManyPathsAway = 2,
        NudgeOffset = 0.15,
        CurrentLineLabel = "3R",
        IsContourMode = true,
    };
}
