// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services.GeoJson;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>#489: the Ref Nudge panel moves the selected track itself (driver's left/right) and
/// keeps a total for the panel. A move is live but only written to field.geojson by Save; Undo
/// puts the line back as it was.</summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore.Instance is shared
public class RefNudgeTests
{
    private string _dir = null!;
    private int _step;

    [SetUp]
    public void SetUp()
    {
        _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aow-refnudge-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_dir);
        _step = ConfigurationStore.Instance.AutoSteer.RefNudgeDistance;
        ConfigurationStore.Instance.AutoSteer.RefNudgeDistance = 20; // cm
    }

    [TearDown]
    public void TearDown()
    {
        ConfigurationStore.Instance.AutoSteer.RefNudgeDistance = _step;
        try { System.IO.Directory.Delete(_dir, true); } catch { }
    }

    // A→B runs north from (0,0), so its right is east (+Easting). Saved to the field file first.
    private MainViewModel NewVm(bool headingSameWay = true)
    {
        var b = new MainViewModelBuilder();
        var field = new Field { Name = "F", DirectoryPath = _dir };
        GeoJsonFieldService.Save(field, null); // the field file the tracks are saved into
        b.FieldService.ActiveField.Returns(field);
        var vm = b.Build();
        vm.SavedTracks.Add(Track.FromABLine("A", new Vec3(0, 0, 0), new Vec3(0, 100, 0)));
        vm.SavedTracks.Add(Track.FromABLine("B", new Vec3(50, 0, 0), new Vec3(50, 100, 0)));
        vm.SelectedTrack = vm.SavedTracks[0];
        vm.SaveTracksToFile();
        vm.State.Guidance.IsHeadingSameWay = headingSameWay;
        return vm;
    }

    private double SavedEasting(string name) =>
        GeoJsonFieldService.LoadTracks(_dir).Single(t => t.Name == name).Points[0].Easting;

    [Test]
    public void RefNudgeRight_MovesTheTrackByTheStepToTheRight_ButDoesNotSaveIt()
    {
        var vm = NewVm();

        vm.RefNudgeRightCommand!.Execute(null);

        Assert.That(vm.SavedTracks[0].Points[0].Easting, Is.EqualTo(0.20).Within(1e-9));
        Assert.That(vm.SavedTracks[0].Points[1].Easting, Is.EqualTo(0.20).Within(1e-9));
        Assert.That(vm.SavedTracks[0].Points[1].Northing, Is.EqualTo(100).Within(1e-9));
        Assert.That(vm.SavedTracks[1].Points[0].Easting, Is.EqualTo(50), "other tracks stay");
        Assert.That(vm.State.Operation.RefNudgePending, Is.True);
        Assert.That(SavedEasting("A"), Is.EqualTo(0).Within(1e-6), "the file keeps the saved line until Save");
    }

    [Test]
    public void Save_WritesTheMovedLine()
    {
        var vm = NewVm();
        vm.RefNudgeRightCommand!.Execute(null);

        vm.RefNudgeSaveCommand!.Execute(null);

        Assert.That(SavedEasting("A"), Is.EqualTo(0.20).Within(1e-6));
        Assert.That(vm.State.Operation.RefNudgePending, Is.False);
        Assert.That(vm.State.Operation.RefNudgeMoved, Is.EqualTo(0));
    }

    [Test]
    public void Undo_PutsTheSavedLineBack()
    {
        var vm = NewVm();
        vm.RefNudgeRightCommand!.Execute(null);
        vm.RefNudgeRightCommand.Execute(null);

        vm.RefNudgeUndoCommand!.Execute(null);

        Assert.That(vm.SavedTracks[0].Points[0].Easting, Is.EqualTo(0).Within(1e-9));
        Assert.That(vm.State.Operation.RefNudgePending, Is.False);
        Assert.That(vm.State.Operation.RefNudgeMoved, Is.EqualTo(0));
        Assert.That(SavedEasting("A"), Is.EqualTo(0).Within(1e-6));
    }

    [Test]
    public void Undo_GoesBackToTheLastSave_NotToTheStart()
    {
        var vm = NewVm();
        vm.RefNudgeRightCommand!.Execute(null);
        vm.RefNudgeSaveCommand!.Execute(null);
        vm.RefNudgeRightCommand.Execute(null);

        vm.RefNudgeUndoCommand!.Execute(null);

        Assert.That(vm.SavedTracks[0].Points[0].Easting, Is.EqualTo(0.20).Within(1e-9));
    }

    [Test]
    public void AnotherSaveOfTheTracks_NeverWritesAnUnsavedMove()
    {
        var vm = NewVm();
        vm.RefNudgeRightCommand!.Execute(null);

        vm.SaveTracksToFile(); // what selecting a track, closing the field, ... do

        Assert.That(SavedEasting("A"), Is.EqualTo(0).Within(1e-6));
        Assert.That(vm.SavedTracks[0].Points[0].Easting, Is.EqualTo(0.20).Within(1e-9), "still moved on screen");
    }

    [Test]
    public void SelectingAnotherTrack_UndoesTheUnsavedMove_AndMovesTheNextOne()
    {
        var vm = NewVm();
        vm.RefNudgeRightCommand!.Execute(null);

        vm.SelectedTrack = vm.SavedTracks[1];
        vm.RefNudgeRightCommand.Execute(null);

        Assert.That(vm.SavedTracks[0].Points[0].Easting, Is.EqualTo(0).Within(1e-9), "the unsaved move was undone");
        Assert.That(vm.SavedTracks[1].Points[0].Easting, Is.EqualTo(50.20).Within(1e-9));
        Assert.That(vm.State.Operation.RefNudgeMoved, Is.EqualTo(0.20).Within(1e-9), "total starts at zero for the next track");
        vm.RefNudgeSaveCommand!.Execute(null);
        Assert.That(SavedEasting("B"), Is.EqualTo(50.20).Within(1e-6));
        Assert.That(SavedEasting("A"), Is.EqualTo(0).Within(1e-6));
    }

    [Test]
    public void SaveThenNext_KeepsTheSavedMove()
    {
        var vm = NewVm();
        vm.RefNudgeRightCommand!.Execute(null);
        vm.RefNudgeSaveCommand!.Execute(null);

        vm.SelectedTrack = vm.SavedTracks[1];

        Assert.That(vm.SavedTracks[0].Points[0].Easting, Is.EqualTo(0.20).Within(1e-9));
        Assert.That(SavedEasting("A"), Is.EqualTo(0.20).Within(1e-6));
    }

    [Test]
    public void RefNudgeLeft_AgainstTheTrackDirection_IsTheDriversLeft()
    {
        var vm = NewVm(headingSameWay: false);

        vm.RefNudgeLeftCommand!.Execute(null); // driving south: the driver's left is east

        Assert.That(vm.SavedTracks[0].Points[0].Easting, Is.EqualTo(0.20).Within(1e-9));
    }

    [Test]
    public void RefHalfNudge_MovesByHalfAWorkingWidth()
    {
        var vm = NewVm();
        double half = (ConfigurationStore.Instance.ActualToolWidth - ConfigurationStore.Instance.Tool.Overlap) * 0.5;

        vm.RefHalfNudgeLeftCommand!.Execute(null);

        Assert.That(vm.SavedTracks[0].Points[0].Easting, Is.EqualTo(-half).Within(1e-9));
    }

    [Test]
    public void TheMovedTotal_AddsUp_AndStartsAgainWhenThePanelOpensWithNothingPending()
    {
        var vm = NewVm();

        vm.RefNudgeRightCommand!.Execute(null);
        vm.RefNudgeRightCommand.Execute(null);
        vm.RefNudgeLeftCommand!.Execute(null);
        Assert.That(vm.State.Operation.RefNudgeMoved, Is.EqualTo(0.20).Within(1e-9));

        vm.RefNudgeSaveCommand!.Execute(null);
        vm.RefNudgeBeginCommand!.Execute(null);
        Assert.That(vm.State.Operation.RefNudgeMoved, Is.EqualTo(0));
    }

    [Test]
    public void ACurve_IsMovedWithTheOffsetThePassesUse_AndSavedBySave()
    {
        var vm = NewVm();
        var pts = new List<Vec3>();
        for (int i = 0; i <= 20; i++) pts.Add(new Vec3(0, i * 5.0, 0)); // a straight "curve" north
        vm.SavedTracks.Add(Track.FromCurve("C", pts));
        vm.SelectedTrack = vm.SavedTracks[2];

        vm.RefNudgeRightCommand!.Execute(null);

        var moved = vm.SavedTracks[2].Points;
        Assert.That(moved.Count, Is.GreaterThanOrEqualTo(2));
        Assert.That(moved.All(p => Math.Abs(p.Easting - 0.20) < 1e-6), Is.True);
        vm.RefNudgeSaveCommand!.Execute(null);
        Assert.That(SavedEasting("C"), Is.EqualTo(0.20).Within(1e-6));
    }

    [Test]
    public void ABoundaryLoop_CantBeMoved()
    {
        var vm = NewVm();
        vm.SavedTracks[0].IsClosed = true;

        vm.RefNudgeRightCommand!.Execute(null);

        Assert.That(vm.SavedTracks[0].Points[0].Easting, Is.EqualTo(0));
        Assert.That(vm.State.Operation.RefNudgeMoved, Is.EqualTo(0));
        Assert.That(vm.State.Operation.RefNudgePending, Is.False);
    }
}
