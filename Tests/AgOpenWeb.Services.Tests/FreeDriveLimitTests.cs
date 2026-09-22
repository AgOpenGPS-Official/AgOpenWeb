// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.ViewModels;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>#106 / #112: free-drive steering is limited by the vehicle max steer angle, not a fixed ±40°.</summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore singleton.
public class FreeDriveLimitTests
{
    [TestCase(25, 40, 25)]
    [TestCase(25, -40, -25)]
    [TestCase(50, 45, 45)] // above the old ±40 cap, within the vehicle limit
    public void FreeDriveAngle_IsClampedToVehicleMaxSteerAngle(double max, double requested, double expected)
    {
        var store = new ConfigurationStore();
        ConfigurationStore.SetInstance(store);
        store.Vehicle.MaxSteerAngle = max;
        var cfg = Substitute.For<IConfigurationService>();
        cfg.Store.Returns(store);
        var vm = new AutoSteerConfigViewModel(cfg);

        vm.FreeDriveSteerAngle = requested;

        Assert.That(vm.FreeDriveSteerAngle, Is.EqualTo(expected));
    }
}
