// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Field Builder › Tracks › Add › All Edges and Longest Edge on a field drawn on the map
/// (#933, 23 Nov 2026): four taps, a point at each corner. All Edges made no track and said
/// nothing; Longest Edge answered as for a round field ("Boundary Edge 1").
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class AllEdgesDrawnFieldTests
{
    private MainViewModel _vm = null!;
    private readonly List<string> _told = new();

    // The dump's outer boundary, in metres from the field's origin.
    private static readonly (double E, double N)[] Drawn = { (-69.5, -151.7), (100.9, -33.1), (212.7, -208.4), (39.1, -322.3) };

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _vm = new MainViewModelBuilder().Build();
        _told.Clear();
        _vm.FailureReported += _told.Add;
    }

    private void Boundary(IEnumerable<(double E, double N)> points) =>
        _vm.State.Field.CurrentBoundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon { Points = points.Select(p => new BoundaryPoint(p.E, p.N, 0)).ToList() },
        };

    [Test]
    public void AllEdges_OnAFieldDrawnWithFourPoints_MakesFourLines()
    {
        Boundary(Drawn);

        _vm.CreateTracksFromAllEdgesCommand!.Execute(null);

        Assert.That(_vm.SavedTracks, Has.Count.EqualTo(4));
        Assert.That(_vm.SavedTracks.All(t => t.IsABLine), Is.True);
        Assert.That(_told, Is.Empty);
    }

    [Test]
    public void LongestEdge_OnAFieldDrawnWithFourPoints_IsItsLongestSide()
    {
        Boundary(Drawn);

        _vm.CreateTrackFromBoundaryCommand!.Execute(null);

        Assert.That(_vm.SavedTracks, Has.Count.EqualTo(1));
        Assert.That(_vm.SavedTracks[0].Name, Is.EqualTo("Longest Edge (208m)"));
    }

    [Test]
    public void AllEdges_OnARoundField_TellsTheOperator()
    {
        Boundary(Enumerable.Range(0, 360).Select(i => (100 * Math.Cos(i * Math.PI / 180), 100 * Math.Sin(i * Math.PI / 180))));

        _vm.CreateTracksFromAllEdgesCommand!.Execute(null);

        Assert.That(_vm.SavedTracks, Is.Empty);
        Assert.That(_told, Has.Count.EqualTo(1), "the tap looked as if it had done nothing");
        Assert.That(_told[0], Does.StartWith("No edges found"));
    }
}
