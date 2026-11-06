// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Windows.Input;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.ViewModels.Wizards.SteerWizard;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// The steer wizard scenarios' Zero WAS and Zero Roll findings: with the module unplugged
/// Zero WAS took the zero from the last angle it had sent, and two quick taps zeroed twice from
/// a reading that didn't show the first zero yet (straight ahead then read 15°, level ground 9°).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore and Clock are singletons
public class SteerZeroGuardTests
{
    private ConfigurationStore _store = null!;
    private IConfigurationService _cfg = null!;
    private IAutoSteerService _auto = null!;
    private TestClock _clock = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new ConfigurationStore();
        ConfigurationStore.SetInstance(_store);
        _cfg = Substitute.For<IConfigurationService>();
        _cfg.Store.Returns(_store);
        _auto = Substitute.For<IAutoSteerService>();
        _clock = new TestClock();
        Clock.Set(_clock);
        _store.AutoSteer.CountsPerDegree = 110;
        _store.AutoSteer.WasOffset = 0;
    }

    [TearDown]
    public void TearDown()
    {
        Clock.Reset();
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    private void Reading(double angle, DateTime at)
    {
        _auto.LastSteerData.Returns(new SteerModuleData(angle, 0, 0, false, false, false, false, 0));
        _auto.LastSteerDataTime.Returns(at);
    }

    [Test]
    public void Zero_WAS_with_no_reading_from_the_module_is_refused_and_says_why()
    {
        var step = new WasCalibrationStepViewModel(_cfg, _auto);
        Reading(15, _clock.Now);
        _clock.Advance(TimeSpan.FromSeconds(5)); // unplugged: the last reading is 5 s old

        ((ICommand)step.ZeroWasCommand).Execute(null);

        Assert.That(step.WasOffset, Is.EqualTo(0), "the offset is not taken from a stale angle");
        Assert.That(step.ValidationMessage, Does.Contain("No reading from the steer module"));
    }

    [Test]
    public void A_quick_second_tap_on_Zero_WAS_is_ignored_and_a_later_one_is_taken()
    {
        var step = new WasCalibrationStepViewModel(_cfg, _auto);
        Reading(15, _clock.Now);
        ((ICommand)step.ZeroWasCommand).Execute(null);
        int first = step.WasOffset;
        Assert.That(first, Is.Not.EqualTo(0));

        // 0.3 s later, the module still shows the old 15° (the new offset is on its way).
        _clock.Advance(TimeSpan.FromMilliseconds(300));
        Reading(15, _clock.Now);
        ((ICommand)step.ZeroWasCommand).Execute(null);
        Assert.That(step.WasOffset, Is.EqualTo(first), "a quick second tap would zero twice");

        // Seconds later, with the new zero applied, the wheels read 2°: that zero is taken.
        _clock.Advance(TimeSpan.FromSeconds(2));
        Reading(2, _clock.Now);
        ((ICommand)step.ZeroWasCommand).Execute(null);
        Assert.That(step.WasOffset, Is.Not.EqualTo(first));
    }

    [Test]
    public void A_quick_second_tap_on_Zero_Roll_is_ignored()
    {
        var step = new RollCalibrationStepViewModel(_cfg);
        step.LiveRoll = 9;
        ((ICommand)step.ZeroRollCommand).Execute(null);
        Assert.That(step.RollZero, Is.EqualTo(9));

        _clock.Advance(TimeSpan.FromMilliseconds(300));
        ((ICommand)step.ZeroRollCommand).Execute(null); // LiveRoll still 9: not yet the new zero

        Assert.That(step.RollZero, Is.EqualTo(9), "two quick taps left level ground reading 9°");
    }
}
