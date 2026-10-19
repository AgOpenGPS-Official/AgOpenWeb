// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.IO;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// #345: the Vehicle &amp; Tool picker's tool overview shows the width the sections add
/// up to, not the stored Tool.Width default the section editor never updates.
/// </summary>
[TestFixture]
public class ToolProfilePreviewTests
{
    [Test]
    public void ActiveToolPreview_ShowsSectionTotal()
    {
        var store = new ConfigurationStore { ActiveToolProfileName = "Test" };
        store.Tool.Width = 6.0;
        store.NumSections = 1;
        store.Tool.SetSectionWidth(0, 300); // cm

        var settings = Substitute.For<ISettingsService>();
        settings.Settings.Returns(new AppSettings { IsMetric = true });
        var dir = Path.GetTempPath();
        var service = new ConfigurationService(
            new TestVehicleProfileService(dir), new TestToolProfileService(dir),
            settings, store);

        Assert.That(service.GetToolProfilePreview("Test"), Does.StartWith("Width: 3.00 m\n"));
    }

    private sealed class TestVehicleProfileService : VehicleProfileService
    {
        public TestVehicleProfileService(string dir)
            : base(NullLogger<VehicleProfileService>.Instance, dir) { }
    }

    private sealed class TestToolProfileService : ToolProfileService
    {
        public TestToolProfileService(string dir)
            : base(NullLogger<ToolProfileService>.Instance, dir) { }
    }
}
