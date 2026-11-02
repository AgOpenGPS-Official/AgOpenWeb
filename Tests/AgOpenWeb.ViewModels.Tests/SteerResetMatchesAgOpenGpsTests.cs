// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Checklist 10.7: Reset all to defaults on the AutoSteer panel puts AgOpenGPS's defaults back
/// (its Settings.cs values, the ones a fresh AgOpenGPS runs with), and a fresh AgOpenWeb starts
/// with the same. FormSteer's own reset button also sets max steer 45 and acquire 0.85, which
/// its defaults don't; those two are left to its defaults.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class SteerResetMatchesAgOpenGpsTests
{
    [TearDown]
    public void TearDown() => ConfigurationStore.SetInstance(new ConfigurationStore());

    private static void AssertAgOpenGpsDefaults(ConfigurationStore store, string when)
    {
        var a = store.AutoSteer; var g = store.Guidance;
        Assert.Multiple(() =>
        {
            Assert.That(a.WasOffset, Is.EqualTo(3), $"{when}: setAS_wasOffset");
            Assert.That(a.CountsPerDegree, Is.EqualTo(110), $"{when}: setAS_countsPerDegree");
            Assert.That(a.Ackermann, Is.EqualTo(100), $"{when}: setAS_ackerman");
            Assert.That(a.ProportionalGain, Is.EqualTo(50), $"{when}: setAS_Kp");
            Assert.That(a.MaxPwm, Is.EqualTo(180), $"{when}: setAS_highSteerPWM");
            Assert.That(a.MinPwm, Is.EqualTo(25), $"{when}: setAS_minSteerPWM");
            Assert.That(a.AcquireFactor, Is.EqualTo(0.9), $"{when}: setVehicle_goalPointAcquireFactor");
            Assert.That(g.GoalPointLookAheadHold, Is.EqualTo(3.0), $"{when}: setVehicle_goalPointLookAheadHold");
            Assert.That(g.GoalPointLookAheadMult, Is.EqualTo(1.5), $"{when}: setVehicle_goalPointLookAheadMult");
            Assert.That(g.PurePursuitIntegralGain, Is.EqualTo(0.0), $"{when}: purePursuitIntegralGainAB");
            Assert.That(g.StanleyHeadingErrorGain, Is.EqualTo(1.0), $"{when}: stanleyHeadingErrorGain");
            Assert.That(g.StanleyDistanceErrorGain, Is.EqualTo(1.0), $"{when}: stanleyDistanceErrorGain");
            Assert.That(g.IsPurePursuit, Is.True, $"{when}: setVehicle_isStanleyUsed false");
            Assert.That(g.UTurnCompensation, Is.EqualTo(1.0), $"{when}: setAS_uTurnCompensation");
            Assert.That(a.SideHillCompensation, Is.EqualTo(0.0), $"{when}: setAS_sideHillComp");
        });
    }

    [Test]
    public void AFreshStore_HasAgOpenGpssDefaults() => AssertAgOpenGpsDefaults(new ConfigurationStore(), "fresh");

    [Test]
    public void ResetAll_PutsAgOpenGpssDefaultsBack()
    {
        var store = new ConfigurationStore();
        ConfigurationStore.SetInstance(store);
        store.AutoSteer.WasOffset = -40; store.AutoSteer.CountsPerDegree = 92; store.AutoSteer.Ackermann = 115;
        store.AutoSteer.ProportionalGain = 33; store.AutoSteer.MaxPwm = 220; store.AutoSteer.MinPwm = 12; store.AutoSteer.AcquireFactor = 0.7;
        store.Guidance.GoalPointLookAheadHold = 5; store.Guidance.GoalPointLookAheadMult = 2.2; store.Guidance.PurePursuitIntegralGain = 0.4;
        store.Guidance.StanleyHeadingErrorGain = 1.7; store.Guidance.StanleyDistanceErrorGain = 0.4; store.Guidance.IsPurePursuit = false;
        store.Guidance.UTurnCompensation = 1.3; store.AutoSteer.SideHillCompensation = 0.5;
        var cfg = Substitute.For<IConfigurationService>();
        cfg.Store.Returns(store);
        var panel = new AutoSteerConfigViewModel(cfg);

        panel.ConfirmResetCommand.Execute(null);

        AssertAgOpenGpsDefaults(store, "after Reset all");
    }
}
