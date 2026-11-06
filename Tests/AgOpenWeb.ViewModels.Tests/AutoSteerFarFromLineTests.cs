// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.State;
using AgOpenWeb.Models.Track;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// AutoSteer doesn't engage far from its line. In the Vehicle &amp; Tool scenarios section
/// widths typed in millimetres (1000 = 10 m each, an 80 m boom) put the line 36 m to the
/// side, and AutoSteer swung the tractor across to it. A pass is never more than half a pass
/// away, so only a very wide implement, or a wrong width, gets this far.
/// </summary>
[TestFixture, NonParallelizable] // ConfigurationStore singleton.
public class AutoSteerFarFromLineTests
{
    private MainViewModel _vm = null!;
    private string? _said;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _vm = new MainViewModelBuilder().Build();
        _vm.IsAutoSteerAvailable = true;
        _said = null;
        _vm.FailureReported += m => _said = m;
    }

    private static readonly Track Line = new()
    {
        Name = "AB",
        Points = new List<Vec3> { new(0, 0, 0), new(0, 100, 0) },
    };

    // Off AutoSteer the cycle measures the distance to the nearest pass for the light bar
    // (HasGuidance is set only while steering) and shows that pass's line.
    private void Cycle(double xte, bool steering = false, bool line = true) => _vm.ApplyGpsCycleResult(new GpsCycleResult
    {
        Guidance = new GuidanceSnapshot
        {
            HasGuidance = steering,
            DisplayTrack = line ? Line : null,
            CrossTrackError = xte,
        },
    });

    private void Engage() => _vm.ToggleAutoSteerCommand!.Execute(null);

    [TestCase(36.0)]
    [TestCase(-5.2)]
    public void Far_from_the_line_it_does_not_engage_and_says_how_far(double xte)
    {
        Cycle(xte);

        Engage();

        Assert.That(_vm.IsAutoSteerEngaged, Is.False);
        Assert.That(_said, Is.EqualTo($"AutoSteer not available - the line is {System.Math.Abs(xte):F1} m away: drive within 5 m of it"));
    }

    [TestCase(0.4)]
    [TestCase(-4.9)]
    public void Near_the_line_it_engages(double xte)
    {
        Cycle(xte);

        Engage();

        Assert.That(_vm.IsAutoSteerEngaged, Is.True);
        Assert.That(_said, Is.Null);
    }

    [Test]
    public void Engaged_it_keeps_on_when_the_line_moves_away_and_still_switches_off()
    {
        Cycle(0.2);
        Engage();
        Cycle(8.0);   // the operator moved to the next pass, or nudged the line
        Assume.That(_vm.IsAutoSteerEngaged, Is.True);

        Engage();

        Assert.That(_vm.IsAutoSteerEngaged, Is.False);
    }

    [Test]
    public void Steering_guidance_counts_too()
    {
        Cycle(36.0, steering: true);

        Engage();

        Assert.That(_vm.IsAutoSteerEngaged, Is.False);
    }

    [Test]
    public void With_no_line_there_is_no_distance_to_judge()
    {
        Cycle(36.0, line: false);

        Engage();

        Assert.That(_vm.IsAutoSteerEngaged, Is.True);
    }
}
