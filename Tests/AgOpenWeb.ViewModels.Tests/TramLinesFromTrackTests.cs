// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Track;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Tram lines without Field Builder tram systems: an active AB line gets lines parallel to it
/// plus the boundary tram track (AgOpenGPS CABLine.BuildTram); a curve, or no track, gets the
/// concentric lanes that follow the boundary.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class TramLinesFromTrackTests
{
    private MainViewModelBuilder _builder = null!;
    private MainViewModel _vm = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _builder = new MainViewModelBuilder();
        _vm = _builder.Build();
        var bnd = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = new List<BoundaryPoint>
                {
                    new(0, 0, 0), new(200, 0, 0), new(200, 200, 0), new(0, 200, 0),
                }
            }
        };
        bnd.OuterBoundary.UpdateBounds();
        _vm.State.Field.CurrentBoundary = bnd;
    }

    private void TurnTramLinesOn(Track track)
    {
        _vm.SavedTracks.Add(track);
        _vm.SelectedTrack = track;
        _builder.TramLineService.ClearReceivedCalls();
        _vm.ToggleTramDisplayCommand!.Execute(null);
    }

    [Test]
    public void AnABLine_GetsLinesParallelToIt()
    {
        var ab = new Track { Name = "AB", Type = TrackType.ABLine,
            Points = new List<Vec3> { new(100, -50, 0), new(100, 250, 0) } };

        TurnTramLinesOn(ab);

        _builder.TramLineService.Received().GenerateParallelTramLines(ab, Arg.Any<double>());
        _builder.TramLineService.Received().GenerateBoundaryTramTracks(
            Arg.Any<IReadOnlyList<Vec3>>(), Arg.Any<int>(), Arg.Any<AgOpenWeb.Models.Tram.TramSystemMode>(), Arg.Any<double>());
        _builder.TramLineService.DidNotReceive().GenerateConcentricTramLanes(Arg.Any<double>());
    }

    [Test]
    public void ACurve_GetsLanesFollowingTheBoundary()
    {
        var curve = new Track { Name = "Curve", Type = TrackType.Curve,
            Points = new List<Vec3> { new(50, 0, 0), new(60, 100, 0), new(50, 200, 0) } };

        TurnTramLinesOn(curve);

        _builder.TramLineService.Received().GenerateConcentricTramLanes(Arg.Any<double>());
        _builder.TramLineService.DidNotReceive().GenerateParallelTramLines(Arg.Any<Track>(), Arg.Any<double>());
    }
}
