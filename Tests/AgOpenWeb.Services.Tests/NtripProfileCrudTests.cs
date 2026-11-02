// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Ntrip;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Checklist 8.1: an NTRIP profile created, edited and deleted, through the real profile service
/// and its files under the data root (AgOpenGPS has one caster in AgIO's settings; profiles are
/// AgOpenWeb's own). What is saved is what comes back after a restart.
/// </summary>
[TestFixture]
[NonParallelizable] // AGOPENWEB_DATA is process-wide
public class NtripProfileCrudTests
{
    private string _root = null!;
    private string? _oldData;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "aow-ntripcrud-" + Guid.NewGuid().ToString("N"));
        _oldData = Environment.GetEnvironmentVariable(AppDataRoot.EnvVar);
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _root);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _oldData);
        try { Directory.Delete(_root, true); } catch { }
    }

    private static NtripProfileService Service()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Settings.Returns(new AppSettings());
        return new NtripProfileService(settings, NullLogger<NtripProfileService>.Instance);
    }

    private static async Task<NtripProfileService> Restarted()
    {
        var svc = Service();
        await svc.LoadProfilesAsync();
        return svc;
    }

    [Test]
    public async Task Create_Edit_Delete_AndWhatARestartFinds()
    {
        var svc = Service();
        await svc.LoadProfilesAsync();
        Assume.That(svc.Profiles, Is.Empty);

        // Create
        var farm = svc.CreateNewProfile("Farm RTK");
        farm.CasterHost = "rtk.example.com"; farm.CasterPort = 2101; farm.MountPoint = "VRS"; farm.Username = "u"; farm.Password = "p";
        farm.IsDefault = true;
        await svc.SaveProfileAsync(farm);
        Assert.That(svc.Profiles.Select(p => p.Name), Is.EqualTo(new[] { "Farm RTK" }).AsCollection);
        Assert.That(svc.DefaultProfile?.Id, Is.EqualTo(farm.Id));
        Assert.That(Directory.GetFiles(svc.ProfilesDirectory, "*.json"), Has.Length.EqualTo(1), "one file per profile");
        var again = await Restarted();
        Assert.That(again.Profiles.Single().CasterHost, Is.EqualTo("rtk.example.com"), "found again after a restart");
        Assert.That(again.DefaultProfile?.Id, Is.EqualTo(farm.Id));

        // Edit: the same profile, the same file
        farm.MountPoint = "NEAR"; farm.CasterPort = 2102;
        await svc.SaveProfileAsync(farm);
        Assert.That(svc.Profiles, Has.Count.EqualTo(1), "an edit is not a second profile");
        Assert.That(Directory.GetFiles(svc.ProfilesDirectory, "*.json"), Has.Length.EqualTo(1));
        again = await Restarted();
        Assert.That(again.Profiles.Single().MountPoint, Is.EqualTo("NEAR"));
        Assert.That(again.Profiles.Single().CasterPort, Is.EqualTo(2102));

        // A second profile made the default takes it from the first, in the files too
        var coop = svc.CreateNewProfile("Co-op");
        coop.CasterHost = "coop.example.com"; coop.IsDefault = true;
        await svc.SaveProfileAsync(coop);
        Assert.That(svc.DefaultProfile?.Id, Is.EqualTo(coop.Id));
        again = await Restarted();
        Assert.That(again.Profiles.Count(p => p.IsDefault), Is.EqualTo(1), "one default");
        Assert.That(again.DefaultProfile?.Name, Is.EqualTo("Co-op"));

        // Delete
        await svc.DeleteProfileAsync(farm.Id);
        Assert.That(svc.Profiles.Select(p => p.Name), Is.EqualTo(new[] { "Co-op" }).AsCollection);
        Assert.That(Directory.GetFiles(svc.ProfilesDirectory, "*.json"), Has.Length.EqualTo(1), "its file is gone");
        again = await Restarted();
        Assert.That(again.Profiles.Select(p => p.Name), Is.EqualTo(new[] { "Co-op" }).AsCollection);

        // Deleting what isn't there does nothing
        await svc.DeleteProfileAsync("no-such-id");
        Assert.That(svc.Profiles, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task AProfileNamedLikeAPath_StaysInTheProfilesFolder()
    {
        var svc = Service();
        await svc.LoadProfilesAsync();
        var p = svc.CreateNewProfile("../escaped/caster: 2101");
        await svc.SaveProfileAsync(p);
        var file = Directory.GetFiles(svc.ProfilesDirectory, "*.json").Single();
        Assert.That(Path.GetDirectoryName(Path.GetFullPath(file)), Is.EqualTo(Path.GetFullPath(svc.ProfilesDirectory).TrimEnd(Path.DirectorySeparatorChar)));
        Assert.That(Directory.Exists(Path.Combine(_root, "escaped")), Is.False);
        var again = await Restarted();
        Assert.That(again.Profiles.Single().Name, Is.EqualTo("../escaped/caster: 2101"), "the name is kept, only the file name is made safe");
    }
}
