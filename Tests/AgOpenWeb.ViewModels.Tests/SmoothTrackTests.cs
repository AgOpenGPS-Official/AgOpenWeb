// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services.GeoJson;
using NSubstitute;
using NUnit.Framework;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The Smooth Curve panel (#929), as AgOpenGPS's FormSmoothAB: − and + set how much the selected
/// curve is smoothed, live on the map; Save writes it, Cancel puts the curve back. The curve keeps
/// its number of points. The button it replaces left four points for each one on every press: two
/// presses on a boundary curve made 17,377 points 7 cm apart and the GPS cycle took over half a
/// second.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class SmoothTrackTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aow-smooth-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { System.IO.Directory.Delete(_dir, true); } catch { }
    }

    private MainViewModel WithSelected(params Track[] tracks)
    {
        var b = new MainViewModelBuilder();
        var field = new Field { Name = "F", DirectoryPath = _dir };
        GeoJsonFieldService.Save(field, null); // the field file the tracks are saved into
        b.FieldService.ActiveField.Returns(field);
        var vm = b.Build();
        foreach (var t in tracks) vm.SavedTracks.Add(t);
        vm.SelectedTrack = vm.SavedTracks[0];
        vm.SaveTracksToFile();
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

    private static Track Line() => Track.FromABLine("AB", new Vec3(50, 0, 0), new Vec3(50, 100, 0));

    private static double MaxOff(IEnumerable<Vec3> pts) => pts.Max(p => Math.Abs(p.Easting - 5));

    private List<Vec3> Saved(string name) => GeoJsonFieldService.LoadTracks(_dir).Single(t => t.Name == name).Points;

    private static void More(MainViewModel vm, int presses)
    {
        for (int i = 0; i < presses; i++) vm.SmoothMoreCommand!.Execute(null);
    }

    [Test]
    public void The_panel_opens_at_no_smoothing_and_changes_nothing()
    {
        var vm = WithSelected(Wobble());
        var before = vm.SelectedTrack!.Points;

        vm.SmoothBeginCommand!.Execute(null);

        Assert.Multiple(() =>
        {
            Assert.That(vm.State.Operation.SmoothOpen, Is.True);
            Assert.That(vm.State.Operation.SmoothCount, Is.EqualTo(0));
            Assert.That(vm.SelectedTrack!.Points, Is.SameAs(before));
        });
    }

    [Test]
    public void More_smooths_the_curve_on_the_map_but_does_not_save_it()
    {
        var vm = WithSelected(Wobble());
        vm.SmoothBeginCommand!.Execute(null);

        More(vm, 2);

        var pts = vm.SelectedTrack!.Points;
        Assert.Multiple(() =>
        {
            Assert.That(vm.State.Operation.SmoothCount, Is.EqualTo(2));
            Assert.That(MaxOff(pts.Skip(2).Take(pts.Count - 4)), Is.LessThan(0.05), "the wobble is evened out");
            Assert.That(MaxOff(Saved("Wobble")), Is.EqualTo(0.2).Within(1e-6), "the file keeps the curve as it was");
        });
    }

    [Test]
    public void The_curve_keeps_its_number_of_points_at_any_amount()
    {
        var vm = WithSelected(BoundaryCurve());
        int before = vm.SelectedTrack!.Points.Count;
        vm.SmoothBeginCommand!.Execute(null);

        More(vm, 150);

        Assert.Multiple(() =>
        {
            Assert.That(vm.State.Operation.SmoothCount, Is.EqualTo(MainViewModel.SmoothCountMax), "stops at the most");
            Assert.That(vm.SelectedTrack!.Points.Count, Is.EqualTo(before));
        });
    }

    [Test]
    public void Less_goes_back_exactly_and_stops_at_the_curve_as_saved()
    {
        var vm = WithSelected(Wobble());
        var before = vm.SelectedTrack!.Points;
        vm.SmoothBeginCommand!.Execute(null);
        More(vm, 1);
        var atOne = vm.SelectedTrack!.Points.ToList();
        More(vm, 4);

        for (int i = 0; i < 4; i++) vm.SmoothLessCommand!.Execute(null);
        Assert.That(vm.SelectedTrack!.Points, Is.EqualTo(atOne), "each amount is made from the saved curve");

        for (int i = 0; i < 3; i++) vm.SmoothLessCommand!.Execute(null);
        Assert.Multiple(() =>
        {
            Assert.That(vm.State.Operation.SmoothCount, Is.EqualTo(0));
            Assert.That(vm.SelectedTrack!.Points, Is.SameAs(before));
        });
    }

    [Test]
    public void Cancel_puts_the_curve_back_and_closes_the_panel()
    {
        var vm = WithSelected(Wobble());
        var before = vm.SelectedTrack!.Points;
        vm.SmoothBeginCommand!.Execute(null);
        More(vm, 10);

        vm.SmoothCancelCommand!.Execute(null);

        Assert.Multiple(() =>
        {
            Assert.That(vm.SelectedTrack!.Points, Is.SameAs(before));
            Assert.That(vm.State.Operation.SmoothOpen, Is.False);
            Assert.That(vm.State.Operation.SmoothCount, Is.EqualTo(0));
        });
    }

    [Test]
    public void Save_writes_the_smoothed_curve_and_closes_the_panel()
    {
        var vm = WithSelected(Wobble());
        vm.SmoothBeginCommand!.Execute(null);
        More(vm, 2);

        vm.SmoothSaveCommand!.Execute(null);

        var saved = Saved("Wobble");
        Assert.Multiple(() =>
        {
            Assert.That(vm.State.Operation.SmoothOpen, Is.False);
            Assert.That(saved, Has.Count.EqualTo(60));
            Assert.That(MaxOff(saved.Skip(2).Take(saved.Count - 4)), Is.LessThan(0.05));
        });
    }

    [Test]
    public void Selecting_another_track_puts_the_curve_back()
    {
        var vm = WithSelected(Wobble(), Line());
        var wobble = vm.SavedTracks[0];
        var before = wobble.Points;
        vm.SmoothBeginCommand!.Execute(null);
        More(vm, 10);

        vm.SelectedTrack = vm.SavedTracks[1];

        Assert.Multiple(() =>
        {
            Assert.That(wobble.Points, Is.SameAs(before));
            Assert.That(vm.State.Operation.SmoothOpen, Is.False);
        });
    }

    [Test]
    public void An_AB_line_does_not_open_the_panel()
    {
        var vm = WithSelected(Line());

        vm.SmoothBeginCommand!.Execute(null);

        Assert.That(vm.State.Operation.SmoothOpen, Is.False);
    }

    [Test]
    public void A_reference_nudge_and_a_smoothing_wait_for_each_other()
    {
        var open = Track.FromCurve("Open", CurveProcessing.CalculateHeadings(
            Enumerable.Range(0, 60).Select(i => new Vec3(5 + Math.Sin(i / 8.0), i * 1.0, 0)).ToList()));
        var vm = WithSelected(open);
        vm.State.Guidance.IsHeadingSameWay = true;

        vm.RefNudgeRightCommand!.Execute(null);
        vm.SmoothBeginCommand!.Execute(null);
        Assert.That(vm.State.Operation.SmoothOpen, Is.False, "not with an unsaved reference move");

        vm.RefNudgeUndoCommand!.Execute(null);
        vm.SmoothBeginCommand!.Execute(null);
        More(vm, 3);
        var smoothed = vm.SelectedTrack!.Points;
        vm.RefNudgeRightCommand!.Execute(null);
        Assert.That(vm.SelectedTrack!.Points, Is.SameAs(smoothed), "not moved while the panel is open");
    }

    [Test]
    public void A_closed_curve_stays_closed_and_its_seam_corner_is_rounded_as_the_others()
    {
        var vm = WithSelected(BoundaryCurve());
        vm.SmoothBeginCommand!.Execute(null);

        More(vm, 2);

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
