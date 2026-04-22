// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Linq;
using System.Reflection;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.Pipeline;
using AgValoniaGPS.Models.State;

namespace AgValoniaGPS.Models.Tests.Pipeline;

[TestFixture]
public class YouTurnWorkingStateTests
{
    [Test]
    public void Property_shape_mirrors_YouTurnState()
    {
        var workingProps = DeclaredProps(typeof(YouTurnWorkingState));
        var observableProps = DeclaredProps(typeof(YouTurnState));

        Assert.That(workingProps, Is.EqualTo(observableProps),
            "YouTurnWorkingState must mirror YouTurnState property-for-property. " +
            "If a property is added to one, add it to the other.");
    }

    [Test]
    public void Defaults_match_YouTurnState_defaults()
    {
        var working = new YouTurnWorkingState();
        var observable = new YouTurnState();

        Assert.Multiple(() =>
        {
            Assert.That(working.IsEnabled, Is.EqualTo(observable.IsEnabled));
            Assert.That(working.IsTriggered, Is.EqualTo(observable.IsTriggered));
            Assert.That(working.IsExecuting, Is.EqualTo(observable.IsExecuting));
            Assert.That(working.TurnPath, Is.EqualTo(observable.TurnPath));
            Assert.That(working.PathIndex, Is.EqualTo(observable.PathIndex));
            Assert.That(working.DistanceToHeadland, Is.EqualTo(observable.DistanceToHeadland));
            Assert.That(working.SnakeIndex, Is.EqualTo(observable.SnakeIndex));
            Assert.That(working.CurrentZone, Is.EqualTo(observable.CurrentZone));
        });
    }

    [Test]
    public void Reset_clears_the_same_fields_as_YouTurnState_Reset()
    {
        var working = MakeNonDefaultWorking();
        var observable = MakeNonDefaultObservable();

        working.Reset();
        observable.Reset();

        Assert.Multiple(() =>
        {
            Assert.That(working.IsTriggered, Is.EqualTo(observable.IsTriggered));
            Assert.That(working.IsExecuting, Is.EqualTo(observable.IsExecuting));
            Assert.That(working.TurnPath, Is.EqualTo(observable.TurnPath));
            Assert.That(working.PathIndex, Is.EqualTo(observable.PathIndex));
            Assert.That(working.DistanceToHeadland, Is.EqualTo(observable.DistanceToHeadland));
            Assert.That(working.DistanceToTrigger, Is.EqualTo(observable.DistanceToTrigger));
            Assert.That(working.NextTrack, Is.EqualTo(observable.NextTrack));
            Assert.That(working.HasCompletedFirstTurn, Is.EqualTo(observable.HasCompletedFirstTurn));
            Assert.That(working.YouTurnCounter, Is.EqualTo(observable.YouTurnCounter));
            Assert.That(working.WasHeadingSameWayAtTurnStart, Is.EqualTo(observable.WasHeadingSameWayAtTurnStart));
            Assert.That(working.NextTrackTurnOffset, Is.EqualTo(observable.NextTrackTurnOffset));
            Assert.That(working.ReturnPassTargetPath, Is.EqualTo(observable.ReturnPassTargetPath));
            Assert.That(working.SnakeSequence, Is.EqualTo(observable.SnakeSequence));
            Assert.That(working.SnakeIndex, Is.EqualTo(observable.SnakeIndex));
            Assert.That(working.CurrentZone, Is.EqualTo(observable.CurrentZone));
        });
    }

    [Test]
    public void CompleteTurn_matches_YouTurnState_CompleteTurn()
    {
        var working = MakeNonDefaultWorking();
        var observable = MakeNonDefaultObservable();

        working.CompleteTurn();
        observable.CompleteTurn();

        Assert.Multiple(() =>
        {
            Assert.That(working.IsExecuting, Is.EqualTo(observable.IsExecuting));
            Assert.That(working.IsTriggered, Is.EqualTo(observable.IsTriggered));
            Assert.That(working.TurnPath, Is.EqualTo(observable.TurnPath));
            Assert.That(working.LastTurnWasLeft, Is.EqualTo(observable.LastTurnWasLeft));
            Assert.That(working.HasCompletedFirstTurn, Is.EqualTo(observable.HasCompletedFirstTurn));
            Assert.That(working.YouTurnCounter, Is.EqualTo(observable.YouTurnCounter));
        });
    }

    private static (string Name, System.Type Type)[] DeclaredProps(System.Type t) =>
        t.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance)
         .Select(p => (p.Name, p.PropertyType))
         .OrderBy(x => x.Name)
         .ToArray();

    private static YouTurnWorkingState MakeNonDefaultWorking() => new()
    {
        IsTriggered = true,
        IsExecuting = true,
        TurnPath = new() { new Vec3 { Easting = 1, Northing = 2 } },
        PathIndex = 5,
        IsTurnLeft = true,
        DistanceToHeadland = 12.3,
        DistanceToTrigger = 4.5,
        HasCompletedFirstTurn = true,
        YouTurnCounter = 7,
        WasHeadingSameWayAtTurnStart = true,
        NextTrackTurnOffset = 2.5,
        ReturnPassTargetPath = 3,
        SnakeSequence = new() { 1, 2, 3 },
        SnakeIndex = 1,
        CurrentZone = TractorZone.InCultivatedArea,
    };

    private static YouTurnState MakeNonDefaultObservable() => new()
    {
        IsTriggered = true,
        IsExecuting = true,
        TurnPath = new() { new Vec3 { Easting = 1, Northing = 2 } },
        PathIndex = 5,
        IsTurnLeft = true,
        DistanceToHeadland = 12.3,
        DistanceToTrigger = 4.5,
        HasCompletedFirstTurn = true,
        YouTurnCounter = 7,
        WasHeadingSameWayAtTurnStart = true,
        NextTrackTurnOffset = 2.5,
        ReturnPassTargetPath = 3,
        SnakeSequence = new() { 1, 2, 3 },
        SnakeIndex = 1,
        CurrentZone = TractorZone.InCultivatedArea,
    };
}
