// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Services;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Coverage;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Pipeline;
using AgOpenWeb.Services.Section;
using AgOpenWeb.Services.Tool;
using AgOpenWeb.Services.Track;
using NSubstitute;

namespace AgOpenWeb.Services.Tests.Pipeline;

/// <summary>
/// The work of one 100 Hz control tick (<see cref="ControlTick"/>, moved out of the view model,
/// Plans/MAINVIEWMODEL_REFACTOR_PLAN.md item 7): its order, its start before the first fix, a
/// section button reaching the PGNs on the next tick (#311), one tick at a time, and what a tick
/// allocates on the real services: the ground a real-time loop builds on.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore singleton.
public class ControlTickTests
{
    private IToolPositionService _tool = null!;
    private ISectionControlService _sections = null!;
    private IAutoSteerService _autoSteer = null!;
    private IPositionEstimator _estimator = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _tool = Substitute.For<IToolPositionService>();
        _sections = Substitute.For<ISectionControlService>();
        _autoSteer = Substitute.For<IAutoSteerService>();
        _estimator = Substitute.For<IPositionEstimator>();
    }

    [TearDown]
    public void TearDown() => ConfigurationStore.SetInstance(new ConfigurationStore());

    [Test]
    public void A_tick_moves_the_tool_then_the_sections_then_hands_the_bits_then_sends()
    {
        _estimator.GetLatestSnapshot().Returns(new PoseSnapshot(new Vec2(10, 20), 0.5, 3, 0, 0, 0));
        _estimator.GetPose(42).Returns(new InterpolatedPose(new Vec2(11, 21), 0.6, 3.5, 0));
        _tool.ToolPosition.Returns(new Vec3(9, 19, 0.6));
        _tool.ToolHeading.Returns(0.55);
        _sections.GetSectionBits64().Returns(0b101UL);

        new ControlTick(_tool, _sections, _autoSteer, _estimator).Run(42);

        Received.InOrder(() =>
        {
            _tool.Update(new Vec3(11, 21, 0.6), 0.6);
            _sections.Update(new Vec3(9, 19, 0.6), 0.55, 0.6, 3.5);
            _autoSteer.SetSectionBits(0b101UL);
            _autoSteer.SendPgnsForControlTick();
        });
    }

    [Test]
    public void Before_the_first_fix_the_tool_and_sections_wait_but_the_pgns_go()
    {
        _estimator.GetLatestSnapshot().Returns((PoseSnapshot?)null);

        new ControlTick(_tool, _sections, _autoSteer, _estimator).Run(1);

        _tool.DidNotReceiveWithAnyArgs().Update(default, default);
        _sections.DidNotReceiveWithAnyArgs().Update(default, default, default, default);
        _autoSteer.Received(1).SetSectionBits(Arg.Any<ulong>());
        _autoSteer.Received(1).SendPgnsForControlTick();
    }

    [Test]
    public void With_no_estimator_the_pgns_still_go()
    {
        new ControlTick(_tool, _sections, _autoSteer).Run(1);

        _tool.DidNotReceiveWithAnyArgs().Update(default, default);
        _autoSteer.Received(1).SendPgnsForControlTick();
    }

    [Test]
    public void A_section_switched_on_between_fixes_is_in_the_next_ticks_bits()
    {
        var config = ConfigurationStore.Instance;
        config.NumSections = 4;
        var tool = new ToolPositionService(config);
        var sections = new SectionControlService(tool, new CoverageMapService(config), config);
        var tick = new ControlTick(tool, sections, _autoSteer, _estimator);
        _estimator.GetLatestSnapshot().Returns((PoseSnapshot?)null);

        tick.Run(1);
        _autoSteer.Received(1).SetSectionBits(0UL);

        sections.SetSectionState(2, SectionButtonState.On); // a tap on the host loop, no fix since
        _autoSteer.ClearReceivedCalls();
        tick.Run(2);

        _autoSteer.Received(1).SetSectionBits(Arg.Is<ulong>(b => (b & 0b100UL) != 0));
    }

#if DEBUG
    [Test]
    public void A_tick_entered_while_one_runs_fails_in_debug_builds()
    {
        ControlTick tick = null!;
        Exception? inner = null;
        _autoSteer.When(a => a.SendPgnsForControlTick()).Do(_ =>
        {
            try { tick.Run(2); } catch (Exception e) { inner = e; }
        });
        tick = new ControlTick(_tool, _sections, _autoSteer, _estimator);

        tick.Run(1);

        Assert.That(inner, Is.InstanceOf<InvalidOperationException>(), "the second tick ran alongside the first");
        Assert.DoesNotThrow(() => tick.Run(3), "the tick after them was refused: the first did not let go");
    }
#endif

    /// <summary>
    /// What a tick allocates on the real services, painting with every section on: the PGN
    /// arrays, the section event's args, the coverage strips. Held to today's figure so it can
    /// only fall: a real-time tick has to get to none (ControlTick's header lists the sources).
    /// The UDP port is not open, so the sends stop at the connected check; with no view model
    /// listening, the section bar's post to the host loop is not in it either.
    /// </summary>
    [Test]
    public void A_tick_painting_on_the_real_services_allocates_no_more_than_today()
    {
        var config = ConfigurationStore.Instance;
        config.NumSections = 4;
        for (int i = 0; i < 4; i++) config.Tool.SetSectionWidth(i, 150);
        var tool = new ToolPositionService(config);
        var coverage = new CoverageMapService(config);
        coverage.SetFieldBounds(-100, 100, -100, 400);
        var sections = new SectionControlService(tool, coverage, config) { TickHz = 100 };
        sections.MasterState = SectionMasterState.Auto;
        sections.SetAllSections(SectionButtonState.On);
        var gps = new GpsService();
        gps.Start();
        var udp = new UdpCommunicationService(Substitute.For<ILocalNetworkInfoProvider>());
        var autoSteer = new AutoSteerService(new TrackGuidanceService(), udp, gps, config);
        autoSteer.Start();
        var estimator = new PositionEstimator();
        var tick = new ControlTick(tool, sections, autoSteer, estimator);

        // 3 m/s north for 20 s: a fix every 10 ticks, the bytes counted over the last 15 s.
        long perTick = Clock.Current.Frequency / 100;
        const double speed = 3.0;
        long counted = 0, bytes = 0;
        for (int i = 0; i < 2000; i++)
        {
            long ts = i * perTick;
            if (i % 10 == 0)
                estimator.UpdateFromGps(new PoseSnapshot(new Vec2(0, speed * i / 100.0), 0, speed, 0, 0, ts));
            long before = GC.GetAllocatedBytesForCurrentThread();
            tick.Run(ts);
            if (i >= 500) { bytes += GC.GetAllocatedBytesForCurrentThread() - before; counted++; }
        }
        autoSteer.Stop();
        gps.Stop();

        double perTickBytes = (double)bytes / counted;
        TestContext.Out.WriteLine($"control tick on the real services: {perTickBytes:F0} bytes a tick ({bytes:N0} over {counted} ticks)");
        Assert.That(coverage.TotalWorkedArea, Is.GreaterThan(100), "nothing was painted: the measurement is not of a working tick");
        Assert.That(perTickBytes, Is.LessThanOrEqualTo(TickBytesToday),
            "A control tick allocates more than it did: the 100 Hz path does no new work (refactor rule, " +
            "and a real-time tick has to get to none). See ControlTick's header.");
    }

    // 723 bytes a tick at the move, Debug and Release; the ceiling leaves 10 % for runtime
    // differences, not for new work.
    private const double TickBytesToday = 800;
}
