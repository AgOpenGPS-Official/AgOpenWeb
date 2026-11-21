// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The recorder window says what it records: the field's outer boundary or an obstacle
/// (<see cref="MainViewModel.IsRecordingObstacle"/>, sent in the Boundary frame for its title). Both
/// opened as "Stop Record Pause Boundary", and on a tester's field (Couto da Ribeira, 20 Nov 2026)
/// three obstacles driven round with the field's button were each saved as the outer boundary.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class BoundaryRecorderKindTests
{
    private string _root = null!;
    private BoundaryRecordingService _rec = null!;
    private MainViewModel _vm = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _root = Path.Combine(Path.GetTempPath(), "aow-reckind-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(_root, "F");
        Directory.CreateDirectory(dir);
        // The field has its outer boundary, so an obstacle may be recorded in it.
        var outer = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (0.0, 0.0), (200.0, 0.0), (200.0, 200.0), (0.0, 200.0) }) outer.Points.Add(new BoundaryPoint(e, n, 0));
        outer.UpdateBounds();
        var field = new Field { Name = "F", DirectoryPath = dir, Origin = new Position { Latitude = 52, Longitude = 5 }, Boundary = new Boundary { OuterBoundary = outer } };
        GeoJsonFieldService.Save(field, tracks: null);

        _rec = new BoundaryRecordingService();
        var b = new MainViewModelBuilder { FieldService = new FieldService(), BoundaryRecordingService = _rec };
        b.SettingsService.Settings.Returns(new AppSettings { FieldsDirectory = _root });
        _vm = b.Build();
        _vm.ActiveField = field;
        _vm.IsFieldOpen = true;
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); } catch { }
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    [Test]
    public void Nothing_recorded_is_no_obstacle()
    {
        Assert.That(_vm.IsRecordingObstacle, Is.False);
    }

    [Test]
    public void Drive_around_field_records_the_field_s_boundary()
    {
        _vm.DriveAroundFieldCommand!.Execute(null);

        Assume.That(_rec.State, Is.EqualTo(BoundaryRecordingState.Paused), "the recorder opens paused");
        Assert.That(_vm.IsRecordingObstacle, Is.False, "paused");
        _vm.ToggleRecordingCommand!.Execute(null);
        Assume.That(_rec.State, Is.EqualTo(BoundaryRecordingState.Recording));
        Assert.That(_vm.IsRecordingObstacle, Is.False, "recording");
    }

    [Test]
    public void Drive_around_obstacle_records_an_obstacle_until_stopped()
    {
        _vm.DriveAroundInnerBoundaryCommand!.Execute(null);

        Assume.That(_rec.State, Is.EqualTo(BoundaryRecordingState.Paused), "the recorder opens paused");
        Assert.That(_vm.IsRecordingObstacle, Is.True, "paused");
        _vm.ToggleRecordingCommand!.Execute(null);
        Assume.That(_rec.State, Is.EqualTo(BoundaryRecordingState.Recording));
        Assert.That(_vm.IsRecordingObstacle, Is.True, "recording");

        _vm.StopBoundaryRecordingCommand!.Execute(null);
        Assert.That(_rec.State, Is.EqualTo(BoundaryRecordingState.Idle));
        Assert.That(_vm.IsRecordingObstacle, Is.False, "stopped: nothing is recorded");
    }

    [Test]
    public void The_field_s_button_after_the_obstacle_s_records_the_field_s_boundary()
    {
        _vm.DriveAroundInnerBoundaryCommand!.Execute(null);
        Assume.That(_vm.IsRecordingObstacle, Is.True);
        _vm.StopBoundaryRecordingCommand!.Execute(null);

        _vm.DriveAroundFieldCommand!.Execute(null);
        Assert.That(_vm.IsRecordingObstacle, Is.False);
    }
}
