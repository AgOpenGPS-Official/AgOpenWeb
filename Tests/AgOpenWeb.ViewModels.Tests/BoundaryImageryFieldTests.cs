// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// #969: the aerial imagery for a boundary is captured in the background, for seconds. Its result
/// goes to the field it was started in, and to no other: closing that field and opening another
/// before it comes back must not save the first field's imagery into the second.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class BoundaryImageryFieldTests
{
    private string _root = null!, _dirA = null!, _dirB = null!, _png = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _root = Path.Combine(Path.GetTempPath(), "aow-imagery-" + Guid.NewGuid().ToString("N"));
        _dirA = Path.Combine(_root, "Imagery A");
        _dirB = Path.Combine(_root, "Imagery B");
        Directory.CreateDirectory(_dirA);
        Directory.CreateDirectory(_dirB);
        GeoJsonFieldService.Save(new Field
        {
            Name = "Imagery A", DirectoryPath = _dirA, Origin = new Position { Latitude = 47.2911, Longitude = 8.6203 },
        }, Array.Empty<Track>());
        GeoJsonFieldService.Save(new Field
        {
            Name = "Imagery B", DirectoryPath = _dirB, Origin = new Position { Latitude = 47.2988, Longitude = 8.6377 },
        }, Array.Empty<Track>());
        // What the capture leaves: the image is copied, not read, on the way into the field.
        _png = Path.Combine(_root, "captured.png");
        File.WriteAllBytes(_png, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); } catch { }
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    [Test]
    public async Task Imagery_captured_for_a_field_is_saved_into_that_field()
    {
        var vm = Fresh();
        await vm.OpenFieldAsync(_dirA, "Imagery A");
        var startedIn = vm.ActiveField;

        Assert.That(Apply(vm, startedIn), Is.True);
        Assert.That(HasBackground(_dirA), Is.True, "A's imagery in A");
    }

    [Test]
    public async Task Imagery_captured_for_a_field_closed_meanwhile_is_not_saved_into_the_next()
    {
        var vm = Fresh();
        await vm.OpenFieldAsync(_dirA, "Imagery A");
        var startedIn = vm.ActiveField;

        // Closed, and B opened, while the capture runs.
        await vm.CloseFieldAsync();
        await vm.OpenFieldAsync(_dirB, "Imagery B");
        Assert.That(Apply(vm, startedIn), Is.False);
        Assert.That(HasBackground(_dirB), Is.False, "A's imagery saved into B");
        Assert.That(vm.State.Field.Imagery, Is.Null, "A's imagery shown in B");

        // Straight from A to B, without closing first.
        await vm.OpenFieldAsync(_dirA, "Imagery A");
        startedIn = vm.ActiveField;
        await vm.OpenFieldAsync(_dirB, "Imagery B");
        Assert.That(Apply(vm, startedIn), Is.False);
        Assert.That(HasBackground(_dirB), Is.False, "A's imagery saved into B, opened straight after A");
    }

    [Test]
    public async Task Imagery_captured_for_a_field_closed_meanwhile_is_not_saved_with_none_open()
    {
        var vm = Fresh();
        await vm.OpenFieldAsync(_dirA, "Imagery A");
        var startedIn = vm.ActiveField;
        await vm.CloseFieldAsync();

        Assert.That(Apply(vm, startedIn), Is.False);
        Assert.That(HasBackground(_dirA), Is.False);
    }

    private bool Apply(MainViewModel vm, Field? startedIn) =>
        vm.ApplyCapturedBackground(startedIn, _png, 47.30, 8.62, 47.29, 8.63, 959_000, 960_000, 5_990_000, 5_991_000);

    private static bool HasBackground(string dir) => File.Exists(Path.Combine(dir, FieldBackground.DefaultImageFile));

    private MainViewModel Fresh()
    {
        var b = new MainViewModelBuilder { FieldService = new FieldService() };
        b.SettingsService.Settings.Returns(new AppSettings { FieldsDirectory = _root });
        return b.Build();
    }
}
