// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Timing;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Isobus;
using NSubstitute;

namespace AgOpenWeb.Services.Tests.Isobus;

/// <summary>#312: the ISOBUS task controller link, PGN 240 in and PGN 241 out
/// (AOG-TaskController docs/PROTOCOL.md, AgOpenGPS CISOBUS).</summary>
[TestFixture]
[NonParallelizable] // sets the global clock
public class IsobusTaskControllerServiceTests
{
    private IUdpCommunicationService _udp = null!;
    private IsobusTaskControllerService _service = null!;
    private TestClock _clock = null!;
    private List<byte[]> _sent = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new TestClock(new DateTime(2026, 10, 11, 12, 0, 0));
        Clock.Set(_clock);
        _udp = Substitute.For<IUdpCommunicationService>();
        _sent = new List<byte[]>();
        _udp.When(u => u.SendToModules(Arg.Any<byte[]>())).Do(c => _sent.Add(c.Arg<byte[]>()));
        _service = new IsobusTaskControllerService(_udp);
    }

    [TearDown]
    public void TearDown() => Clock.Reset();

    // A PGN 240 frame as the task controller builds it (CRC = sum of source..last payload byte).
    private static byte[] Heartbeat(bool enabled, int sections, ulong states = 0, int clients = 0, byte source = 0x80)
    {
        int stateBytes = sections == 0 ? 0 : (sections + 7) / 8;
        var f = new byte[5 + 2 + stateBytes + 1];
        f[0] = 0x80; f[1] = 0x81; f[2] = source; f[3] = 0xF0; f[4] = (byte)(2 + stateBytes);
        f[5] = (byte)((enabled ? 1 : 0) | (clients << 1));
        f[6] = (byte)sections;
        for (int i = 0; i < stateBytes; i++) f[7 + i] = (byte)(states >> (8 * i));
        int sum = 0;
        for (int i = 2; i < f.Length - 1; i++) sum += f[i];
        f[^1] = (byte)sum;
        return f;
    }

    private void Receive(byte[] frame) =>
        _udp.DataReceived += Raise.EventWith(new UdpDataReceivedEventArgs { Data = frame, PGN = frame[3] });

    // Heartbeats every 100 ms for the given time.
    private void Heartbeats(double seconds, bool enabled, int sections = 4, ulong states = 0)
    {
        for (int i = 0; i < (int)Math.Round(seconds * 10); i++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(100));
            Receive(Heartbeat(enabled, sections, states));
        }
    }

    // ── PGN 240 ──────────────────────────────────────────────────────────

    [Test]
    public void Heartbeat_WithAnImplement_ReportsModeCountAndSections()
    {
        Receive(Heartbeat(enabled: true, sections: 12, states: 0b1000_0000_0101, clients: 1));

        var s = _service.Status;
        Assert.That(s.IsAlive, Is.True);
        Assert.That(s.SectionControlEnabled, Is.True);
        Assert.That(s.SectionCount, Is.EqualTo(12));
        Assert.That(s.ClientCount, Is.EqualTo(1));
        Assert.That(s.ReportedSections, Is.EqualTo(0b1000_0000_0101UL), "section 1 is bit 0 of the first state byte");
    }

    [Test]
    public void Heartbeat_WithNoImplement_IsAliveWithNoSections()
    {
        Receive(Heartbeat(enabled: false, sections: 0));

        var s = _service.Status;
        Assert.That(s.IsAlive, Is.True);
        Assert.That(s.SectionCount, Is.Zero);
        Assert.That(s.ReportedSections, Is.Zero);
    }

    [Test]
    public void Heartbeat_SixtyFourSections_AllBitsRead()
    {
        Receive(Heartbeat(enabled: true, sections: 64, states: 0x8000_0000_0000_0001));
        Assert.That(_service.Status.ReportedSections, Is.EqualTo(0x8000_0000_0000_0001UL));
    }

    [Test]
    public void NoTaskController_IsNotAlive()
    {
        Assert.That(_service.Status, Is.EqualTo(default(IsobusStatus)));
    }

    [Test]
    public void AliveForOneSecondAfterTheLastHeartbeat()
    {
        Receive(Heartbeat(enabled: true, sections: 4, states: 0b1111));

        _clock.Advance(TimeSpan.FromMilliseconds(900));
        Assert.That(_service.Status.IsAlive, Is.True);

        _clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.That(_service.Status, Is.EqualTo(default(IsobusStatus)), "nothing stale is shown once it goes quiet");
    }

    [Test]
    public void MalformedHeartbeats_AreIgnored()
    {
        var wrongLength = Heartbeat(enabled: true, sections: 12);
        wrongLength[6] = 20;                                   // says 20 sections, carries bytes for 12
        Receive(wrongLength);
        Receive(Heartbeat(enabled: true, sections: 4, source: 0x7F)); // not from the task controller
        Receive(new byte[] { 0x80, 0x81, 0x80, 0xF0, 1, 1, 0 });      // too short to hold the count

        Assert.That(_service.Status.IsAlive, Is.False);
    }

    // ── PGN 241 ──────────────────────────────────────────────────────────

    [Test]
    public void Request_SendsPgn241()
    {
        _service.RequestSectionControl(true);
        Assert.That(_sent.Single(), Is.EqualTo(new byte[] { 0x80, 0x81, 0x7F, 0xF1, 1, 1, 0x72 }),
            "CRC = 0x7F + 0xF1 + 1 + 1");

        _sent.Clear();
        _service.RequestSectionControl(false);
        Assert.That(_sent.Single(), Is.EqualTo(new byte[] { 0x80, 0x81, 0x7F, 0xF1, 1, 0, 0x71 }));
    }

    [Test]
    public void Toggle_AsksForTheOppositeOfWhatIsReported()
    {
        Receive(Heartbeat(enabled: true, sections: 4));
        _service.ToggleSectionControl();
        Assert.That(_sent.Single()[5], Is.EqualTo(0));
    }

    [Test]
    public void Toggle_WithNoTaskController_SendsNothing()
    {
        _service.ToggleSectionControl();
        Assert.That(_sent, Is.Empty);
    }

    [Test]
    public void Request_IsRepeatedUntilTheHeartbeatShowsIt()
    {
        Heartbeats(1, enabled: true);
        _service.RequestSectionControl(false);          // the first datagram is lost
        Heartbeats(1.2, enabled: true);
        Assert.That(_sent.Count, Is.EqualTo(3), "sent, then again at 0.5 s and 1.0 s");

        Heartbeats(2, enabled: false);                  // taken
        Assert.That(_sent.Count, Is.EqualTo(3));
        Assert.That(_service.Status.SectionControlEnabled, Is.False);
    }

    [Test]
    public void Request_NotTaken_StopsAfterThreeSeconds()
    {
        Heartbeats(1, enabled: true);
        _service.RequestSectionControl(false);
        Heartbeats(10, enabled: true);

        Assert.That(_sent.Count, Is.InRange(6, 7), "every 0.5 s for 3 s, then it gives up");
        Assert.That(_service.Status.SectionControlEnabled, Is.True, "the button shows what the task controller reports");
    }

    [Test]
    public void RestartedTaskController_GetsTheLastRequestAgain()
    {
        Heartbeats(1, enabled: true);
        _service.RequestSectionControl(false);
        Heartbeats(1, enabled: false);
        _sent.Clear();

        // The task controller restarts and comes back with its default, enabled.
        _clock.Advance(TimeSpan.FromSeconds(5));
        Heartbeats(0.1, enabled: true);
        Assert.That(_sent.Single()[5], Is.EqualTo(0), "manual is asked for again");

        Heartbeats(1, enabled: false);
        Assert.That(_sent.Count, Is.EqualTo(1));
    }

    [Test]
    public void ModeChangedSomewhereElse_IsFollowed_NotUndone()
    {
        Heartbeats(1, enabled: true);
        _service.RequestSectionControl(false);
        Heartbeats(1, enabled: false);
        _sent.Clear();

        Heartbeats(5, enabled: true);                   // switched on at the terminal, say
        Assert.That(_sent, Is.Empty);

        // The setting made there is now the one a restart is brought back to.
        _clock.Advance(TimeSpan.FromSeconds(5));
        Heartbeats(0.1, enabled: false);
        Assert.That(_sent.Single()[5], Is.EqualTo(1), "automatic, as last set; not the old request for manual");
    }

    [Test]
    public void BeforeAnyRequest_TheTaskControllerIsLeftAlone()
    {
        Heartbeats(2, enabled: true);
        _clock.Advance(TimeSpan.FromSeconds(5));
        Heartbeats(2, enabled: false);
        Assert.That(_sent, Is.Empty);
    }
}
