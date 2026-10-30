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

/// <summary>#489: the Ref Nudge panel moves the selected track itself (driver's left/right),
/// keeps a total for the panel, and saves the field's tracks.</summary>
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

    // A→B runs north from (0,0), so its right is east (+Easting).
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
        vm.State.Guidance.IsHeadingSameWay = headingSameWay;
        return vm;
    }

    [Test]
    public void RefNudgeRight_MovesTheTrackByTheStepToTheRight_AndSavesIt()
    {
        var vm = NewVm();

        vm.RefNudgeRightCommand!.Execute(null);

        Assert.That(vm.SavedTracks[0].Points[0].Easting, Is.EqualTo(0.20).Within(1e-9));
        Assert.That(vm.SavedTracks[0].Points[1].Easting, Is.EqualTo(0.20).Within(1e-9));
        Assert.That(vm.SavedTracks[0].Points[1].Northing, Is.EqualTo(100).Within(1e-9));
        Assert.That(vm.SavedTracks[1].Points[0].Easting, Is.EqualTo(50), "other tracks stay");
        var saved = GeoJsonFieldService.LoadTracks(_dir);
        Assert.That(saved.Single(t => t.Name == "A").Points[0].Easting, Is.EqualTo(0.20).Within(1e-6));
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
    public void TheMovedTotal_AddsUp_AndStartsAgainWhenThePanelOpens()
    {
        var vm = NewVm();

        vm.RefNudgeRightCommand!.Execute(null);
        vm.RefNudgeRightCommand.Execute(null);
        vm.RefNudgeLeftCommand!.Execute(null);
        Assert.That(vm.State.Operation.RefNudgeMoved, Is.EqualTo(0.20).Within(1e-9));

        vm.RefNudgeBeginCommand!.Execute(null);
        Assert.That(vm.State.Operation.RefNudgeMoved, Is.EqualTo(0));
    }

    [Test]
    public void SelectingTheNextTrack_StartsItsTotalAtZero_AndMovesThatTrack()
    {
        var vm = NewVm();
        vm.RefNudgeRightCommand!.Execute(null);

        vm.SelectedTrack = vm.SavedTracks[1];
        vm.RefNudgeRightCommand.Execute(null);

        Assert.That(vm.State.Operation.RefNudgeMoved, Is.EqualTo(0.20).Within(1e-9));
        Assert.That(vm.SavedTracks[1].Points[0].Easting, Is.EqualTo(50.20).Within(1e-9));
        Assert.That(vm.SavedTracks[0].Points[0].Easting, Is.EqualTo(0.20).Within(1e-9), "the first stays moved");
    }

    [Test]
    public void ABoundaryLoop_CantBeMoved()
    {
        var vm = NewVm();
        vm.SavedTracks[0].IsClosed = true;

        vm.RefNudgeRightCommand!.Execute(null);

        Assert.That(vm.SavedTracks[0].Points[0].Easting, Is.EqualTo(0));
        Assert.That(vm.State.Operation.RefNudgeMoved, Is.EqualTo(0));
    }
}
