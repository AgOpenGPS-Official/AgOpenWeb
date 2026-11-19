// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using NUnit.Framework;

namespace AgOpenWeb.RemoteServer.Tests;

/// <summary>
/// The steer wizard the host closed under the screens (the screen in control went,
/// MainViewModel.SteerWizardClosedByHost) is said in the Wizard frame, so a screen that comes back
/// with the wizard on it shows that, not buttons that reach no wizard (a tester's tractor,
/// 18 Nov 2026).
/// </summary>
[TestFixture]
public class WizardFrameTests
{
    private static readonly WizardDto Live = WizardDto.ClosedByHost with { Closed = false, TotalSteps = 15, StepKind = "welcome" };

    [Test]
    public void The_closed_flag_is_the_frame_s_last_byte()
    {
        Assert.Multiple(() =>
        {
            Assert.That(WireCodec.EncodeWizard(WizardDto.ClosedByHost)[^1], Is.EqualTo(1), "closed");
            Assert.That(WireCodec.EncodeWizard(Live)[^1], Is.EqualTo(0), "a live wizard");
        });
    }

    [Test]
    public void A_live_wizard_goes_out_every_tick_and_a_closed_one_once()
    {
        bool closedSent = false;
        WizardDto? Tick(WizardDto? fromHost) => MapBroadcaster.WizardToSend(fromHost, ref closedSent);

        Assert.Multiple(() =>
        {
            Assert.That(Tick(Live), Is.SameAs(Live));
            Assert.That(Tick(Live), Is.SameAs(Live), "live: every tick");
            Assert.That(Tick(WizardDto.ClosedByHost), Is.SameAs(WizardDto.ClosedByHost), "closed: said");
            Assert.That(Tick(WizardDto.ClosedByHost), Is.Null, "once");
            Assert.That(Tick(WizardDto.ClosedByHost), Is.Null, "once");
            Assert.That(Tick(null), Is.Null, "no wizard");
            Assert.That(Tick(Live), Is.SameAs(Live), "opened again");
            Assert.That(Tick(WizardDto.ClosedByHost), Is.SameAs(WizardDto.ClosedByHost), "closed again: said again");
        });
    }
}
