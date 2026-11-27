// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Ntrip;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// A field's NTRIP switch is work started for that field: it disconnects, then connects the
/// field's caster. When another field is opened before the disconnect is done, the first switch
/// must not connect the first field's caster over the second's (Plans/MAINVIEWMODEL_REFACTOR_PLAN.md,
/// item 6: a result can't land in another field). The same for the return to the default
/// profile when a field is closed and another opened at once.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class NtripFieldSessionTests
{
    private string _root = null!;
    private MainViewModelBuilder _b = null!;
    private TaskCompletionSource _slowDisconnect = null!;
    private readonly List<string> _connected = new();

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        ConfigurationStore.Instance.Connections.NtripEnabled = true;
        ConfigurationStore.Instance.Connections.NtripProfilePerField = true;
        _root = Path.Combine(Path.GetTempPath(), "aow-ntrip-session-" + Guid.NewGuid().ToString("N"));
        foreach (var (name, lat) in new[] { ("Session A", 47.51), ("Session B", 47.52) })
        {
            var dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            GeoJsonFieldService.Save(new Field
            {
                Name = name, DirectoryPath = dir, Origin = new Position { Latitude = lat, Longitude = 8.5 },
                Boundary = new Boundary { OuterBoundary = Square() },
            }, new[] { Track.FromABLine("AB", new Vec3(0, -40, 0), new Vec3(0, 40, 0)) });
        }

        _b = new MainViewModelBuilder { FieldService = new FieldService() };
        _b.SettingsService.Settings.Returns(new AppSettings { FieldsDirectory = _root });
        var profiles = new Dictionary<string, NtripProfile>
        {
            ["Session A"] = new() { Name = "A", CasterHost = "a.example", MountPoint = "A", AutoConnectOnFieldLoad = true },
            ["Session B"] = new() { Name = "B", CasterHost = "b.example", MountPoint = "B", AutoConnectOnFieldLoad = true },
        };
        _b.NtripProfileService.GetProfileForField(Arg.Any<string>()).Returns(ci => profiles.GetValueOrDefault(ci.Arg<string>()));
        _b.NtripProfileService.DefaultProfile.Returns(new NtripProfile { Name = "Base", CasterHost = "base.example", MountPoint = "BASE", AutoConnectOnFieldLoad = true, IsDefault = true });

        // Connected to something at the start, so each switch disconnects first. A disconnect
        // the test holds waits until the test lets it finish; the others finish at once.
        var ntrip = _b.NtripService;
        ntrip.IsActive.Returns(true);
        ntrip.IsConnected.Returns(true);
        ntrip.DisconnectAsync().Returns(_ =>
        {
            var t = _held?.Task ?? Task.CompletedTask;
            _held = null;
            return t;
        });
        ntrip.ConnectAsync(Arg.Any<NtripConfiguration>()).Returns(ci =>
        {
            _connected.Add(ci.Arg<NtripConfiguration>().CasterAddress);
            return Task.CompletedTask;
        });
    }

    private TaskCompletionSource? _held;

    /// <summary>The next disconnect waits until the returned source is set.</summary>
    private TaskCompletionSource HoldNextDisconnect() => _held = new TaskCompletionSource();

    private MainViewModel Build()
    {
        var vm = _b.Build();
        _connected.Clear(); // what the start connected is not this test's
        return vm;
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); } catch { }
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    [Test]
    public async Task A_field_left_while_its_caster_is_switched_in_does_not_connect_it_over_the_next()
    {
        var vm = Build();
        var aDisconnect = HoldNextDisconnect();
        await Open(vm, "Session A"); // A's switch waits in its disconnect
        Assume.That(_connected, Is.Empty, "A's switch connected before its disconnect was done");

        await Open(vm, "Session B"); // B's own switch disconnects at once and connects B
        Assume.That(_connected, Is.EqualTo(new[] { "b.example" }), "B's caster was not connected");

        aDisconnect.SetResult(); // A's disconnect is done now
        await Task.Yield();
        Assert.That(_connected.Last(), Is.EqualTo("b.example"), "field A's switch connected A's caster with B open");
    }

    [Test]
    public async Task The_default_caster_restored_after_a_close_does_not_land_on_the_next_field()
    {
        var vm = Build();
        await Open(vm, "Session A");
        Assume.That(_connected.Last(), Is.EqualTo("a.example"));

        // Close A: its return to the default caster waits in the disconnect; B is opened.
        var restoreDisconnect = HoldNextDisconnect();
        var close = ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.CloseFieldCommand).ExecuteAsync(null);
        await Open(vm, "Session B");
        Assume.That(_connected.Last(), Is.EqualTo("b.example"), "B's caster was not connected");

        restoreDisconnect.SetResult();
        await close;
        Assert.That(_connected.Last(), Is.EqualTo("b.example"), "the default caster, restored for A's close, was connected with B open");
    }

    private Task Open(MainViewModel vm, string name) => vm.OpenFieldAsync(Path.Combine(_root, name), name);

    private static BoundaryPolygon Square()
    {
        var p = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (-50.0, -50.0), (50.0, -50.0), (50.0, 50.0), (-50.0, 50.0) })
            p.Points.Add(new BoundaryPoint(e, n, 0));
        p.UpdateBounds();
        return p;
    }
}
