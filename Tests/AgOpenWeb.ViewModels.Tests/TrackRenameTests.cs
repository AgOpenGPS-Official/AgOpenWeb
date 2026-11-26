// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Checklist 1.14: renaming a track in the tracks manager. AgOpenGPS (FormBuildTracks
/// btnSaveEditName) sets the name trimmed and keeps the track; the field's file carries it,
/// and the track the field was closed on is still found by its new name (#148).
/// </summary>
[TestFixture]
public class TrackRenameTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aow-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        GeoJsonFieldService.Save(new Field { Name = "F", DirectoryPath = _dir, Origin = new Position { Latitude = 52, Longitude = 5 } }, tracks: null);
    }

    [TearDown]
    public void TearDown() { try { Directory.Delete(_dir, true); } catch { } }

    private static Track Line(string name, double e) => new()
    {
        Name = name, Type = TrackType.ABLine, IsVisible = true,
        Points = new() { new Vec3(e, 0, 0), new Vec3(e, 100, 0) },
    };

    private MainViewModel Vm()
    {
        var b = new MainViewModelBuilder { FieldService = new FieldService() };
        b.SettingsService.Settings.Returns(new AppSettings { FieldsDirectory = Path.GetDirectoryName(_dir)! });
        var vm = b.Build();
        var field = new Field { Name = "F", DirectoryPath = _dir };
        vm.ActiveField = field;
        ((FieldService)b.FieldService).SetActiveField(field);
        vm.IsFieldOpen = true;
        vm.SavedTracks.Add(Line("AB 1", 0));
        vm.SavedTracks.Add(Line("AB 2", 20));
        return vm;
    }

    [Test]
    public void Rename_ChangesTheName_SavesIt_AndKeepsTheTrack()
    {
        var vm = Vm();
        vm.SelectTrackAt(1);

        vm.RenameTrackAt(1, "  Headland pass  ");

        Assert.That(vm.SavedTracks[1].Name, Is.EqualTo("Headland pass"), "trimmed, as AgOpenGPS");
        Assert.That(vm.SavedTracks[1].Points, Has.Count.EqualTo(2), "the geometry is untouched");
        Assert.That(vm.SelectedTrack, Is.SameAs(vm.SavedTracks[1]), "still the active track");
        var saved = GeoJsonFieldService.LoadTracks(_dir);
        Assert.That(saved.Select(t => t.Name), Is.EqualTo(new[] { "AB 1", "Headland pass" }).AsCollection, "in the field's file");
        Assert.That(File.ReadAllText(Path.Combine(_dir, "ActiveTrack.txt")).Trim(), Is.EqualTo("Headland pass"), "the remembered active track follows the rename (#148)");
        Assert.That(vm.State.Field.Tracks.Select(t => t.Name), Does.Contain("Headland pass"), "the list the web shows");
    }

    [Test]
    public void ABlankName_OrABadIndex_ChangesNothing()
    {
        var vm = Vm();
        vm.RenameTrackAt(0, "   ");
        vm.RenameTrackAt(5, "X");
        Assert.That(vm.SavedTracks.Select(t => t.Name), Is.EqualTo(new[] { "AB 1", "AB 2" }).AsCollection);
    }

    // #695: a hidden track can't be made active (AgOpenGPS), and the operator is told why
    // instead of the tap doing nothing.
    [Test]
    public void Selecting_a_hidden_track_is_refused_with_a_reason()
    {
        var vm = Vm();
        vm.SelectTrackAt(0);
        vm.SavedTracks[1].IsVisible = false;
        string? reported = null;
        vm.FailureReported += m => reported = m;

        vm.SelectTrackAt(1);

        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("AB 1"), "the active track stays");
        Assert.That(reported, Does.Contain("hidden").IgnoreCase);
    }
}
