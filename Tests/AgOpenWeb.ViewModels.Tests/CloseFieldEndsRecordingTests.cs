// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Closing a field ends what was being recorded in it. A boundary recording left running
/// carried on into the next field, where Stop saved both fields' points as that field's
/// boundary; a line being drawn or recorded did the same.
/// </summary>
[TestFixture]
public class CloseFieldEndsRecordingTests
{
    private string _root = null!;
    private MainViewModelBuilder _b = null!;
    private BoundaryRecordingService _rec = null!;
    private MainViewModel _vm = null!;
    private string? _failure;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _root = Path.Combine(Path.GetTempPath(), "aow-closerec-" + Guid.NewGuid().ToString("N"));
        _rec = new BoundaryRecordingService();
        _b = new MainViewModelBuilder { FieldService = new FieldService(), BoundaryRecordingService = _rec };
        _b.SettingsService.Settings.Returns(new AppSettings { FieldsDirectory = _root });
        _vm = _b.Build();
        _vm.FailureReported += m => _failure = m;
        _vm.IsDrawAtPivot = true;
        Open("F");
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); } catch { }
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    private void Open(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var field = new Field { Name = name, DirectoryPath = dir, Origin = new Position { Latitude = 52, Longitude = 5 } };
        GeoJsonFieldService.Save(field, tracks: null);
        _vm.ActiveField = field;
        _vm.IsFieldOpen = true;
        _failure = null;
    }

    private void Fix(double e, double n, double headingDeg)
    {
        double rad = headingDeg * Math.PI / 180;
        _vm.State.Vehicle.Easting = e;
        _vm.State.Vehicle.Northing = n;
        _vm.State.Vehicle.Heading = headingDeg;
        _b.ToolPositionService.HasPosition.Returns(true);
        _b.ToolPositionService.ToolPosition.Returns(new Vec3(e, n, rad));
        _b.ToolPositionService.ToolHeading.Returns(rad);
        _b.GpsService.GpsDataUpdated += Raise.Event<EventHandler<GpsData>>(_b.GpsService,
            new GpsData { CurrentPosition = new Position { Easting = e, Northing = n, Heading = headingDeg, Latitude = 52, Longitude = 5 } });
    }

    private void DriveNorth(double fromN, double metres)
    {
        for (int i = 0; i <= (int)metres; i++) Fix(0, fromN + i, 0);
    }

    [Test]
    public async Task ClosingTheField_EndsABoundaryRecording_AndTheNextFieldStartsClean()
    {
        _vm.DriveAroundFieldCommand!.Execute(null);
        _vm.ToggleRecordingCommand!.Execute(null);
        DriveNorth(0, 20);
        Assert.That(_rec.PointCount, Is.GreaterThan(3), "the recording was running before the close");

        await _vm.CloseFieldAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_rec.State, Is.EqualTo(BoundaryRecordingState.Idle));
            Assert.That(_rec.PointCount, Is.Zero);
            Assert.That(_vm.IsBoundaryRecording, Is.False);
            Assert.That(_vm.IsBoundaryPlayerPanelVisible, Is.False);
        });

        // The next field: driving records nothing, and Stop has nothing to save into it.
        Open("G");
        DriveNorth(100, 30);
        Assert.That(_rec.PointCount, Is.Zero, "points driven in the next field were recorded for the closed one's boundary");
        _vm.StopBoundaryRecordingCommand!.Execute(null);
        Assert.That(_vm.HasBoundary, Is.False, "the closed field's recording was saved as the next field's boundary");
    }

    [Test]
    public async Task ClosingTheField_CancelsALineBeingRecorded()
    {
        _vm.StartCurveRecordingCommand!.Execute(null);
        DriveNorth(0, 10);
        Assert.That(_vm.CurrentABCreationMode, Is.EqualTo(ABCreationMode.Curve));

        await _vm.CloseFieldAsync();

        Assert.That(_vm.CurrentABCreationMode, Is.EqualTo(ABCreationMode.None));
        Assert.That(_vm.RecordedCurvePointCount, Is.Zero);
    }
}
