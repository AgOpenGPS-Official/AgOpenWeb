// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.Track;
using NUnit.Framework;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The Smooth button on a curve (#929): it evens the line out and leaves its number of points
/// alone, as AgOpenGPS's SmoothAB. It left four points for each one, so two presses on a boundary
/// curve made 17,377 points 7 cm apart and the GPS cycle took over half a second.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class SmoothTrackTests
{
    private static MainViewModel WithSelected(Track track)
    {
        var vm = new MainViewModelBuilder().Build();
        vm.SavedTracks.Add(track);
        vm.ActivateTrackAt(0);
        return vm;
    }

    // A line north with every other point 0.2 m to one side.
    private static Track Wobble() => Track.FromCurve("Wobble", CurveProcessing.CalculateHeadings(
        Enumerable.Range(0, 60).Select(i => new Vec3(5 + (i % 2 == 0 ? 0.2 : -0.2), i * 1.0, 0)).ToList()));

    // The boundary curve of a 300 m square field: a closed loop with a point every 1.1 m.
    private static Track BoundaryCurve() => Track.FromCurve("Boundary Curve", CurveProcessing.DensifyLoop(new[]
    {
        new Vec3(0, 0, 0), new Vec3(300, 0, 0), new Vec3(300, 300, 0), new Vec3(0, 300, 0),
    }), isClosed: true);

    [Test]
    public void Smooth_keeps_the_number_of_points_however_often_it_is_pressed()
    {
        var vm = WithSelected(BoundaryCurve());
        int before = vm.SelectedTrack!.Points.Count;

        for (int i = 0; i < 5; i++) vm.SmoothABLineCommand!.Execute(null);

        Assert.That(vm.SelectedTrack!.Points.Count, Is.EqualTo(before));
    }

    [Test]
    public void Smooth_evens_out_a_wobble()
    {
        var vm = WithSelected(Wobble());

        vm.SmoothABLineCommand!.Execute(null);

        var pts = vm.SelectedTrack!.Points;
        Assert.That(pts.Skip(2).Take(pts.Count - 4).Max(p => Math.Abs(p.Easting - 5)), Is.LessThan(0.05));
    }

    [Test]
    public void A_closed_curve_stays_closed_and_its_seam_corner_is_rounded_as_the_others()
    {
        var vm = WithSelected(BoundaryCurve());

        vm.SmoothABLineCommand!.Execute(null);

        var pts = vm.SelectedTrack!.Points;
        Assert.Multiple(() =>
        {
            Assert.That(pts[^1].Easting, Is.EqualTo(pts[0].Easting).Within(1e-9));
            Assert.That(pts[^1].Northing, Is.EqualTo(pts[0].Northing).Within(1e-9));
            // The seam is the corner at (0, 0): it moves in as the corner at (300, 300) does.
            double seam = Math.Min(pts[0].Easting, pts[0].Northing);
            double other = pts.Min(p => Math.Max(300 - p.Easting, 300 - p.Northing));
            Assert.That(seam, Is.GreaterThan(0.1));
            Assert.That(seam, Is.EqualTo(other).Within(0.05));
        });
    }
}
