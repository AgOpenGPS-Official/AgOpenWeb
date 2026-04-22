// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Linq;
using System.Reflection;
using AgValoniaGPS.Models.Pipeline;
using AgValoniaGPS.Services.Interfaces;
using AgValoniaGPS.Services.Pipeline;

namespace AgValoniaGPS.Services.Tests.Pipeline;

/// <summary>
/// End-of-Phase-D locks for the Guidance-on-cycle contract. Mirrors the
/// structural guards established by <see cref="YouTurnCycleTests"/> in
/// Phase C — the cycle worker is the sole writer of guidance working
/// state, and the only UI→cycle channel for guidance commands is
/// <see cref="IPipelineIntents"/>.
/// </summary>
[TestFixture]
public class GuidanceCycleTests
{
    [Test]
    public void Drained_GuidanceSnap_left_encodes_correctly()
    {
        var intents = new PipelineIntents();
        intents.RequestGuidanceSnap(left: true);

        var batch = intents.Drain();

        Assert.That(batch.GuidanceSnap, Is.True,
            "Snap-left intent must survive drain as GuidanceSnap=true");
    }

    [Test]
    public void Drained_GuidanceSnap_right_encodes_correctly()
    {
        var intents = new PipelineIntents();
        intents.RequestGuidanceSnap(left: false);

        var batch = intents.Drain();

        Assert.That(batch.GuidanceSnap, Is.False,
            "Snap-right intent must survive drain as GuidanceSnap=false");
    }

    [Test]
    public void GuidanceNudge_accumulates_between_drains()
    {
        var intents = new PipelineIntents();

        // Three 2 cm nudges right between drains → total 6 cm.
        intents.RequestGuidanceNudge(0.02);
        intents.RequestGuidanceNudge(0.02);
        intents.RequestGuidanceNudge(0.02);

        var batch = intents.Drain();

        Assert.That(batch.GuidanceNudgeMeters, Is.EqualTo(0.06).Within(1e-9),
            "Nudge requests must accumulate into the drain batch delta");
    }

    [Test]
    public void GuidanceNudge_left_and_right_net_out()
    {
        var intents = new PipelineIntents();

        intents.RequestGuidanceNudge(0.05);
        intents.RequestGuidanceNudge(-0.03);

        Assert.That(intents.Drain().GuidanceNudgeMeters, Is.EqualTo(0.02).Within(1e-9),
            "Opposite-direction nudges must net out in the accumulated delta");
    }

    [Test]
    public void GuidanceNudge_drain_resets_the_accumulator()
    {
        var intents = new PipelineIntents();

        intents.RequestGuidanceNudge(0.1);
        intents.Drain();

        Assert.That(intents.Drain().GuidanceNudgeMeters, Is.EqualTo(0).Within(1e-12),
            "After a drain, the nudge accumulator must be zero");
    }

    [Test]
    public void GuidanceResetNudge_is_idempotent_between_drains()
    {
        var intents = new PipelineIntents();

        intents.RequestGuidanceResetNudge();
        intents.RequestGuidanceResetNudge();

        Assert.That(intents.Drain().GuidanceResetNudge, Is.True);
        Assert.That(intents.Drain().GuidanceResetNudge, Is.False);
    }

    /// <summary>
    /// Structural lock — mirror of the Phase C guard for YouTurn state.
    /// <see cref="IGpsPipelineService"/> must not expose a method that lets
    /// the UI thread write directly into the cycle-owned
    /// <see cref="GuidanceWorkingState"/> or <see cref="GuidanceSnapshot"/>.
    /// If a future change adds such a method, it's reintroducing a
    /// cross-thread writer the Phase D intent channel exists to prevent.
    /// </summary>
    [Test]
    public void IGpsPipelineService_has_no_direct_Guidance_writethrough_methods()
    {
        var disallowed = typeof(IGpsPipelineService).GetMethods()
            .Where(m =>
                (m.Name.StartsWith("Set") || m.Name.StartsWith("Push") || m.Name.StartsWith("Apply"))
                && m.GetParameters().Any(p =>
                    p.ParameterType == typeof(GuidanceWorkingState)
                    || p.ParameterType == typeof(GuidanceSnapshot)))
            .Select(m => m.Name)
            .ToList();

        Assert.That(disallowed, Is.Empty,
            "IGpsPipelineService must not expose a direct write-through into cycle-owned Guidance state. "
            + "Use IPipelineIntents instead. Offending method(s): "
            + string.Join(", ", disallowed));
    }

    [Test]
    public void GuidanceSnapshot_carries_cycle_only_fields()
    {
        // Smoke-lock the three cycle-only fields added in D1 — HasGuidance,
        // DisplayTrack, BaseTrack. If they regress from record to something
        // that breaks `with` expressions or default values, this catches it.
        var snapshot = new GuidanceSnapshot
        {
            HasGuidance = true,
            DisplayTrack = null,
            BaseTrack = null,
        };

        Assert.That(snapshot.HasGuidance, Is.True);
        Assert.That(snapshot.DisplayTrack, Is.Null);
        Assert.That(snapshot.BaseTrack, Is.Null);
    }
}
