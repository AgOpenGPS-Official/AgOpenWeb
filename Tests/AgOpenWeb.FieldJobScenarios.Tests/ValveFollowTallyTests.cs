// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using NUnit.Framework;

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>
/// The valve check's tally without a rig (it runs in milliseconds): a mismatch beyond a second
/// is AgOpenWeb's, unless the emulated module missed frames in it (2026-11-14 nightly, J19: the
/// rig runner delivered 4.5 % fewer frames and a 1.1 s mismatch was reported as an app bug).
/// </summary>
[TestFixture]
[Category("Unit")]
public class ValveFollowTallyTests
{
    private static void Fixes(ValveFollowTally t, int n, bool screen, bool valve, int sent = 10, int? received = 10)
    {
        for (int i = 0; i < n; i++)
        {
            t.BeginFix(sent, received);
            t.Section(0, screen, valve, 0.1);
        }
    }

    [Test]
    public void A_change_within_the_grace_counts_nothing()
    {
        var t = new ValveFollowTally();
        Fixes(t, 10, screen: true, valve: false);
        Fixes(t, 5, screen: true, valve: true);
        Assert.That(t.Persistent[0], Is.Zero);
        Assert.That(t.DuringLostFrames[0], Is.Zero);
    }

    [Test]
    public void A_long_mismatch_with_every_frame_delivered_is_the_apps()
    {
        var t = new ValveFollowTally();
        Fixes(t, 21, screen: true, valve: false);
        Assert.That(t.Persistent[0], Is.EqualTo(1.1).Within(1e-9));
        Assert.That(t.DuringLostFrames[0], Is.Zero);
        Assert.That(t.FramesMissed, Is.Zero);
    }

    [Test]
    public void A_long_mismatch_with_frames_lost_in_it_is_the_harness()
    {
        var t = new ValveFollowTally();
        Fixes(t, 3, screen: true, valve: false);
        Fixes(t, 4, screen: true, valve: false, received: 0);   // the runner dropped them
        Fixes(t, 14, screen: true, valve: false);
        Assert.That(t.Persistent[0], Is.Zero);
        Assert.That(t.DuringLostFrames[0], Is.EqualTo(1.1).Within(1e-9));
        Assert.That(t.FramesMissed, Is.EqualTo(40));
        Assert.That(t.FramesExpected, Is.EqualTo(210));
    }

    [Test]
    public void Frames_lost_before_a_mismatch_do_not_excuse_it()
    {
        var t = new ValveFollowTally();
        Fixes(t, 5, screen: false, valve: false, received: 3);
        Fixes(t, 21, screen: true, valve: false);
        Assert.That(t.Persistent[0], Is.EqualTo(1.1).Within(1e-9));
        Assert.That(t.DuringLostFrames[0], Is.Zero);
    }

    [Test]
    public void A_module_off_or_offline_loses_frames_on_purpose_and_that_stays_the_apps()
    {
        var t = new ValveFollowTally();
        Fixes(t, 21, screen: true, valve: false, received: null);
        Assert.That(t.Persistent[0], Is.EqualTo(1.1).Within(1e-9));
        Assert.That(t.FramesExpected, Is.Zero);
        Assert.That(t.FramesMissed, Is.Zero);
    }
}
