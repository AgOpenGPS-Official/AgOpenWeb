// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgOpenWeb.Models;
using AgOpenWeb.Models.State;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// A $KSXT fix has no HDOP and no correction age (that field is reserved), so the
/// readouts must say "—", not 0.0: a stale-corrections age of 0.0 s would read as fresh.
/// </summary>
[TestFixture]
public class KsxtReadoutTests
{
    [Test]
    public void KsxtFix_LeavesHdopAndCorrectionAgeUnreported()
    {
        var vm = new MainViewModelBuilder().Build();

        vm.ApplyGpsCycleResult(new GpsCycleResult
        {
            SentenceType = GpsSentenceType.Ksxt, FixQuality = 4, SatelliteCount = 29, Hdop = 0, DifferentialAge = 0,
        });

        Assert.That(double.IsNaN(vm.State.Vehicle.Hdop), Is.True);
        Assert.That(double.IsNaN(vm.State.Vehicle.Age), Is.True);
        Assert.That(vm.State.Vehicle.FixQuality, Is.EqualTo(4), "the fix itself is reported as before");
    }

    [Test]
    public void PaogiFix_ReportsHdopAndCorrectionAge()
    {
        var vm = new MainViewModelBuilder().Build();

        vm.ApplyGpsCycleResult(new GpsCycleResult
        {
            SentenceType = GpsSentenceType.Paogi, FixQuality = 4, Hdop = 0.8, DifferentialAge = 1.5,
        });

        Assert.That(vm.State.Vehicle.Hdop, Is.EqualTo(0.8));
        Assert.That(vm.State.Vehicle.Age, Is.EqualTo(1.5));
    }
}
