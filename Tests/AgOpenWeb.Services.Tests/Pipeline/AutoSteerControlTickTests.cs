// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Pipeline;
using AgOpenWeb.Services.Track;
using NSubstitute;

namespace AgOpenWeb.Services.Tests.Pipeline;

/// <summary>
/// Verifies commit 4 of #313: SendPgnsForControlTick exists on
/// IAutoSteerService and emits PGN 254 + PGN 239 to the UDP transport
/// when the control loop ticks.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore singleton.
public class AutoSteerControlTickTests
{
    private IUdpCommunicationService _udp = null!;
    private AutoSteerService _autoSteer = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _udp = Substitute.For<IUdpCommunicationService>();
        var gps = new GpsService();
        gps.Start();
        _autoSteer = new AutoSteerService(new TrackGuidanceService(), _udp, gps, ConfigurationStore.Instance);
        _autoSteer.Start();

        // AutoSteerService now emits a baseline PGN 251 + PGN 252 pair on
        // Start() so the module sees current settings without waiting for
        // the operator. These tests only care about the per-tick
        // PGN 254 + PGN 239 traffic, so drop the baseline from the count.
        _udp.ClearReceivedCalls();
    }

    [Test]
    public void ControlLoopTick_TriggersTwoPgnSends()
    {
        var loop = new ManualSteerMachineLoop();
        loop.Ticked += _ => _autoSteer.SendPgnsForControlTick();
        loop.Start();

        loop.Tick(0);

        // PGN 254 (autosteer) + PGN 239 (machine) = 2 sends per tick.
        _udp.Received(2).SendToModules(Arg.Any<byte[]>());
    }

    [Test]
    public void ControlLoopTick_NotEnabled_SuppressesSends()
    {
        var loop = new ManualSteerMachineLoop();
        loop.Ticked += _ => _autoSteer.SendPgnsForControlTick();
        loop.Start();
        _autoSteer.Stop();

        loop.Tick(0);

        _udp.DidNotReceive().SendToModules(Arg.Any<byte[]>());
    }

    [Test]
    public void MultipleTicks_FireProportionalSendCount()
    {
        var loop = new ManualSteerMachineLoop();
        loop.Ticked += _ => _autoSteer.SendPgnsForControlTick();
        loop.Start();

        for (int i = 0; i < 5; i++)
            loop.Tick(i);

        // 5 ticks × 2 PGNs each = 10 sends.
        _udp.Received(10).SendToModules(Arg.Any<byte[]>());
    }

    // #311: the control loop hands over the section bits itself, so they go out with
    // the same tick's PGNs instead of waiting for the next GPS cycle.
    [Test]
    public void SectionBitsSetOnATick_GoOutWithThatTick()
    {
        var sent = new List<byte[]>();
        _udp.When(u => u.SendToModules(Arg.Any<byte[]>()))
            .Do(c => sent.Add((byte[])c.Arg<byte[]>().Clone()));

        _autoSteer.SetMachineState(sectionBits: 0, isInUTurn: true, hydLiftState: 2);
        _autoSteer.SendPgnsForControlTick();
        Assert.That(SectionBits(sent, 0xFE), Is.EqualTo(0));
        Assert.That(SectionBits(sent, 0xEF), Is.EqualTo(0));

        sent.Clear();
        _autoSteer.SetSectionBits(0x8005);
        _autoSteer.SendPgnsForControlTick();
        Assert.That(SectionBits(sent, 0xFE), Is.EqualTo(0x8005), "PGN 254");
        Assert.That(SectionBits(sent, 0xEF), Is.EqualTo(0x8005), "PGN 239");
        var machine = sent.Single(p => p[3] == 0xEF);
        Assert.That(machine[5], Is.EqualTo(1), "U-turn state stays as the GPS cycle left it");
        Assert.That(machine[7], Is.EqualTo(2), "so does the hydraulic lift state");
    }

    [Test]
    public void SectionBitsSetOnATick_ReachPgn229_WithMoreThan16Sections()
    {
        ConfigurationStore.Instance.NumSections = 20;
        var sent = new List<byte[]>();
        _udp.When(u => u.SendToModules(Arg.Any<byte[]>()))
            .Do(c => sent.Add((byte[])c.Arg<byte[]>().Clone()));

        _autoSteer.SetSectionBits(1UL << 18);
        _autoSteer.SendPgnsForControlTick();

        var pgn229 = sent.Single(p => p[3] == 0xE5);
        Assert.That(pgn229[5 + 2], Is.EqualTo(0x04), "section 19 is bit 2 of the third data byte");
    }

    private static int SectionBits(List<byte[]> sent, byte pgn)
    {
        var p = sent.Single(x => x[3] == pgn);
        return p[11] | (p[12] << 8);
    }
}
