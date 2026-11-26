// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Job;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;
using AgOpenWeb.Services.Interfaces;
using CommunityToolkit.Mvvm.Input;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Two fields whose names differ only in capitals: Windows and macOS keep them in one folder,
/// Linux in two. A new field must not take such a name, and the Fields and Jobs list must
/// still work when two such folders are already there (it used to throw while building its
/// list, which left open, start, resume and delete all dead).
/// </summary>
[TestFixture]
public class FieldNameTwinTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "aow-twin-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(_root, "Vinha");
        Directory.CreateDirectory(dir);
        GeoJsonFieldService.Save(new Field { Name = "Vinha", DirectoryPath = dir, Origin = new Position { Latitude = 52, Longitude = 5 } }, tracks: null);
    }

    [TearDown]
    public void TearDown() { try { Directory.Delete(_root, true); } catch { } }

    [Test]
    public void ExistingTwin_FindsTheFolderWhoseNameDiffersOnlyInCapitals()
    {
        Assert.Multiple(() =>
        {
            Assert.That(FieldNames.ExistingTwin(_root, "vinha"), Is.EqualTo("Vinha"));
            Assert.That(FieldNames.ExistingTwin(_root, "VINHA"), Is.EqualTo("Vinha"));
            Assert.That(FieldNames.ExistingTwin(_root, "Vinha"), Is.EqualTo("Vinha"));
            Assert.That(FieldNames.ExistingTwin(_root, "Olival"), Is.Null);
            Assert.That(FieldNames.ExistingTwin(Path.Combine(_root, "nowhere"), "Vinha"), Is.Null);
            Assert.That(FieldNames.ExistingTwin(null, "Vinha"), Is.Null);
            Assert.That(FieldNames.ExistsMessage("Vinha", "Vinha"), Is.EqualTo("Field 'Vinha' already exists"));
            Assert.That(FieldNames.ExistsMessage("vinha", "Vinha"), Does.Contain("'Vinha' already exists").And.Contain("capitals"));
        });
    }

    [Test]
    public async Task NewField_RefusesANameThatExistsInOtherCapitals()
    {
        var b = new MainViewModelBuilder();
        b.SettingsService.Settings.Returns(new AppSettings { FieldsDirectory = _root });
        var vm = b.Build();
        string? failure = null;
        vm.FailureReported += m => failure = m;
        vm.NewFieldName = "vinha";
        vm.NewFieldLatitude = 52;
        vm.NewFieldLongitude = 5;

        await ((IAsyncRelayCommand)vm.ConfirmNewFieldDialogCommand!).ExecuteAsync(null);

        Assert.That(failure, Does.Contain("'Vinha' already exists"));
        Assert.That(Directory.GetDirectories(_root), Has.Length.EqualTo(1), "no second folder was made");
        Assert.That(vm.IsFieldOpen, Is.False);
    }

    [Test]
    public void FieldsAndJobs_ListsTwoFieldsThatDifferOnlyInCapitals()
    {
        var fields = Substitute.For<IFieldService>();
        var jobs = Substitute.For<IJobService>();
        var settings = Substitute.For<ISettingsService>();
        settings.Settings.Returns(new AppSettings { FieldsDirectory = _root });
        jobs.SuggestWorkTypes().Returns(JobWorkTypeSuggestions.Seed);
        jobs.ListJobs(Arg.Any<string>()).Returns(Array.Empty<JobSummary>());
        fields.GetAvailableFields(Arg.Any<string>()).Returns(new List<string> { "Vinha", "vinha" });
        fields.FindFieldsNear(Arg.Any<string>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<double>())
            .Returns(new List<NearbyField>
            {
                new("Vinha", Path.Combine(_root, "Vinha"), 1.0, 0),
                new("vinha", Path.Combine(_root, "vinha"), 2.0, 0),
            });
        var appState = new ApplicationState();
        appState.Vehicle.Latitude = 52;
        appState.Vehicle.Longitude = 5;
        var vm = new StartWorkSessionDialogViewModel(fields, jobs, settings, appState,
            close: () => { }, openField: (_, _) => { }, openFieldStartingNewJob: (_, _, _, _, _) => { },
            openFieldResumingJob: (_, _, _) => { }, confirm: (_, _) => { }, confirmWithOption: (_, _, _, _, _) => { });

        Assert.DoesNotThrow(vm.Refresh);

        Assert.That(vm.Fields.Select(f => f.Name), Is.EqualTo(new[] { "Vinha", "vinha" }), "both folders are listed, nearest first");
        Assert.That(vm.Fields.Select(f => f.DistanceKm), Is.EqualTo(new[] { 1.0, 2.0 }), "each row keeps its own distance");
    }
}
