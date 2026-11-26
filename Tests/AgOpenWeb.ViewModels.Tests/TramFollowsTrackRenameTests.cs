// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Models.Tram;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;
using AgOpenWeb.Services.Tram;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// A tram system follows its track by name. Renaming the track used to cut the system off
/// (its lines vanished without a word); deleting the track left the system pointing at
/// nothing. Now the system follows a rename, and goes with the track when the track goes.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class TramFollowsTrackRenameTests
{
    private string _dir = null!;
    private MainViewModel _vm = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.Vehicle.TrackWidth = 1.8;
        _dir = Path.Combine(Path.GetTempPath(), "aow-tramrename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var field = new Field { Name = "F", DirectoryPath = _dir, Origin = new Position { Latitude = 52, Longitude = 5 } };
        GeoJsonFieldService.Save(field, tracks: null);
        var tram = new TramLineService(new TramLineOffsetService(), NullLogger<TramLineService>.Instance, config);
        _vm = new MainViewModelBuilder { FieldService = new FieldService(), TramLineService = tram }.Build();
        _vm.ActiveField = field;
        _vm.IsFieldOpen = true;
        _vm.SavedTracks.Add(Line("AB1"));
        _vm.SavedTracks.Add(Line("AB2"));
        _vm.TramSystems.Add(new TramSystem { Name = "Tram 1 (AB1)", ReferenceTrackName = "AB1", ReferenceBoundaryIndex = -1, TramWidth = 12 });
        _vm.TramSystems.Add(new TramSystem { Name = "Tram 2 (Boundary)", ReferenceTrackName = null, ReferenceBoundaryIndex = 0, TramWidth = 12 });
    }

    private static Track Line(string name) => new()
    {
        Name = name, Type = TrackType.ABLine, IsVisible = true,
        Points = new() { new Vec3(0, -100, 0), new Vec3(0, 100, 0) },
    };

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch { }
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    [Test]
    public void RenamingTheTrack_CarriesItsTramSystemAlong_AndSaysSo()
    {
        _vm.RenameTrackAt(0, "Main line");

        Assert.Multiple(() =>
        {
            Assert.That(_vm.TramSystems[0].ReferenceTrackName, Is.EqualTo("Main line"));
            Assert.That(_vm.TramSystems[0].Name, Is.EqualTo("Tram 1 (Main line)"));
            Assert.That(_vm.TramSystems[1].ReferenceBoundaryIndex, Is.Zero, "the boundary system is untouched");
            Assert.That(_vm.StatusMessage, Does.Contain("tram system"));
            Assert.That(File.ReadAllText(Path.Combine(_dir, "TramSystems.json")), Does.Contain("Main line"), "saved with the field");
        });
    }

    [Test]
    public void RenamingATrackNoTramSystemFollows_SaysNothingAboutTrams()
    {
        _vm.RenameTrackAt(1, "Second line");
        Assert.That(_vm.TramSystems[0].ReferenceTrackName, Is.EqualTo("AB1"));
        Assert.That(_vm.StatusMessage ?? "", Does.Not.Contain("tram"));
    }

    [Test]
    public void DeletingTheTrack_RemovesTheTramSystemThatFollowedIt_AndSaysSo()
    {
        _vm.DeleteTrackAt(0);

        Assert.Multiple(() =>
        {
            Assert.That(_vm.TramSystems, Has.Count.EqualTo(1));
            Assert.That(_vm.TramSystems[0].ReferenceBoundaryIndex, Is.Zero, "the boundary system stays");
            Assert.That(_vm.StatusMessage, Does.Contain("Tram 1 (AB1)"));
        });
    }

    [Test]
    public void DeletingATrackWhoseNameAnotherTrackStillHas_KeepsTheTramSystem()
    {
        _vm.SavedTracks.Add(Line("AB1")); // a second track called AB1
        _vm.DeleteTrackAt(0);
        Assert.That(_vm.TramSystems, Has.Count.EqualTo(2), "the system can still find a track of that name");
    }

    [Test]
    public void DeletingAllTracks_RemovesTheTramSystemsThatFollowedThem_KeepsBoundaryOnes()
    {
        _vm.DeleteAllTracksConfirmed();

        Assert.That(_vm.TramSystems.Select(s => s.Name), Is.EqualTo(new[] { "Tram 2 (Boundary)" }));
        Assert.That(_vm.StatusMessage, Does.Contain("1 tram system"));
    }
}
