// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Free drive took ±40° at most (AgOpenGPS's free drive buttons), so the max steer angle test,
/// which commands full lock and reads where the wheels stop, read 40° for any lock past it: a
/// 38° lock reads 42° on the left with Ackermann 90. The limit is now past any tractor's lock,
/// and the test commands exactly that.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore singleton.
public class AutoSteerFreeDriveLimitTests
{
    private AutoSteerService _service = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _service = new AutoSteerService(Substitute.For<ITrackGuidanceService>(),
            Substitute.For<IUdpCommunicationService>(), Substitute.For<IGpsService>(), ConfigurationStore.Instance);
    }

    [Test]
    public void The_limit_is_past_any_lock_and_a_command_at_it_goes_through()
    {
        Assert.That(IAutoSteerService.FreeDriveLimitDeg, Is.GreaterThanOrEqualTo(60));

        _service.EnableFreeDrive();
        _service.SetFreeDriveAngle(IAutoSteerService.FreeDriveLimitDeg);

        Assert.That(_service.FreeDriveSteerAngle, Is.EqualTo(IAutoSteerService.FreeDriveLimitDeg));
    }

    [Test]
    public void A_lock_past_40_degrees_is_commanded_as_asked()
    {
        _service.EnableFreeDrive();
        _service.SetFreeDriveAngle(45);

        Assert.That(_service.FreeDriveSteerAngle, Is.EqualTo(45), "40° was the most it took");
    }

    [Test]
    public void Beyond_the_limit_is_held_to_it_both_ways()
    {
        _service.EnableFreeDrive();

        _service.SetFreeDriveAngle(300);
        Assert.That(_service.FreeDriveSteerAngle, Is.EqualTo(IAutoSteerService.FreeDriveLimitDeg));
        _service.SetFreeDriveAngle(-300);
        Assert.That(_service.FreeDriveSteerAngle, Is.EqualTo(-IAutoSteerService.FreeDriveLimitDeg));
    }
}
