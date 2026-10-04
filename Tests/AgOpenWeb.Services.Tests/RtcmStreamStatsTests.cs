using System;
using System.Linq;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Services;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// RTCM forwarding plan, Phase 1: per-message counts of what the caster sent, for the NTRIP
/// health line and the bug report dump. Driven by a TestClock.
/// </summary>
[TestFixture]
public class RtcmStreamStatsTests
{
    private TestClock _clock = null!;
    private RtcmStreamStats _stats = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new TestClock();
        _stats = new RtcmStreamStats(_clock);
        _stats.Reset();
    }

    private static byte[] Message(int type, int payload = 20, int seed = 1) => RtcmFramerTests.Message(type, payload, seed);

    [Test]
    public void MessagesAreCountedByType_WithTheirSpacingAndAge()
    {
        for (int epoch = 0; epoch < 5; epoch++)
        {
            _stats.Feed(Message(1077, 300, epoch));
            _stats.Feed(Message(1087, 200, epoch));
            if (epoch == 0) _stats.Feed(Message(1005, 19));
            _clock.AdvanceMs(1000);
        }

        var snap = _stats.Snapshot();

        Assert.That(snap.Messages, Is.EqualTo(11));
        Assert.That(snap.Types.Select(t => t.Type), Is.EqualTo(new[] { 1005, 1077, 1087 }));
        var gps = snap.Types.Single(t => t.Type == 1077);
        Assert.Multiple(() =>
        {
            Assert.That(gps.Count, Is.EqualTo(5));
            Assert.That(gps.Bytes, Is.EqualTo(5 * 306));
            Assert.That(gps.MeanIntervalSeconds, Is.EqualTo(1.0).Within(1e-6));
            Assert.That(gps.SecondsSinceLast, Is.EqualTo(1.0).Within(1e-6));
            Assert.That(snap.Types.Single(t => t.Type == 1005).MeanIntervalSeconds, Is.NaN);
            Assert.That(snap.Types.Single(t => t.Type == 1005).SecondsSinceLast, Is.EqualTo(5.0).Within(1e-6));
            Assert.That(snap.SessionSeconds, Is.EqualTo(5.0).Within(1e-6));
        });
    }

    [Test]
    public void TheSnapshotSays_WhetherTheBasePositionAndObservationsArrived()
    {
        Assert.That(_stats.Snapshot().HasStationPosition, Is.False);
        Assert.That(_stats.Snapshot().HasObservations, Is.False);

        _stats.Feed(Message(1074, 100));
        Assert.That(_stats.Snapshot().HasObservations, Is.True);
        Assert.That(_stats.Snapshot().HasStationPosition, Is.False);

        _stats.Feed(Message(1006, 21));
        Assert.That(_stats.Snapshot().HasStationPosition, Is.True);
    }

    [Test]
    public void BadBytes_ShowAsChecksumFailuresAndSkippedBytes()
    {
        byte[] bad = Message(1077, 100);
        bad[30] ^= 0x40;
        _stats.Feed(bad);
        _stats.Feed(Message(1087, 100));

        var snap = _stats.Snapshot();

        Assert.That(snap.Messages, Is.EqualTo(1));
        Assert.That(snap.ChecksumFailures, Is.GreaterThanOrEqualTo(1));
        Assert.That(snap.BytesSkipped, Is.EqualTo(bad.Length));
    }

    [Test]
    public void Reset_StartsANewSession()
    {
        _stats.Feed(Message(1005, 19));
        _stats.Feed(Message(1077, 100)[..50]);   // a partial message from the old connection
        _clock.AdvanceMs(3000);

        _stats.Reset(chunkedReply: true);
        _stats.Feed(Message(1087, 100));

        var snap = _stats.Snapshot();
        Assert.Multiple(() =>
        {
            Assert.That(snap.Types.Select(t => t.Type), Is.EqualTo(new[] { 1087 }));
            Assert.That(snap.BytesSkipped, Is.Zero, "the old partial message is forgotten, not blamed on the new session");
            Assert.That(snap.ChunkedReply, Is.True);
            Assert.That(snap.SessionSeconds, Is.Zero.Within(1e-6));
        });
    }

    [Test]
    public void TheReport_NamesTheMessagesAndTheReceiverState()
    {
        _stats.Feed(Message(1005, 19));
        _stats.Feed(Message(1077, 300));
        _stats.Feed(Message(1127, 300));
        _stats.Feed(Message(1230, 12));

        string report = _stats.Snapshot().ToReport(differentialAgeSeconds: 1.25, fixQuality: 4);

        Assert.That(report, Does.Contain("base position (1005/1006): received"));
        Assert.That(report, Does.Contain("fix quality 4, differential age 1.3 s").Or.Contain("fix quality 4, differential age 1.2 s"));
        Assert.That(report, Does.Contain("GPS observations (MSM7)"));
        Assert.That(report, Does.Contain("BeiDou observations (MSM7)"));
        Assert.That(report, Does.Contain("GLONASS code-phase biases"));
    }

    [TestCase(1004, true)]
    [TestCase(1012, true)]
    [TestCase(1074, true)]
    [TestCase(1077, true)]
    [TestCase(1137, true)]
    [TestCase(1005, false)]
    [TestCase(1019, false)]
    [TestCase(1078, false)]
    [TestCase(1230, false)]
    [TestCase(4072, false)]
    public void ObservationMessages_AreRecognised(int type, bool expected) =>
        Assert.That(RtcmMessages.IsObservation(type), Is.EqualTo(expected));

    [TestCase("HTTP/1.1 200 OK\r\nNtrip-Version: Ntrip/2.0\r\nTransfer-Encoding: chunked\r\nContent-Type: gnss/data", true)]
    [TestCase("HTTP/1.1 200 OK\r\ntransfer-encoding:  Chunked", true)]
    [TestCase("HTTP/1.1 200 OK\r\nNtrip-Version: Ntrip/2.0\r\nContent-Type: gnss/data", false)]
    [TestCase("ICY 200 OK", false)]
    [TestCase("", false)]
    public void AChunkedReply_IsDetected(string header, bool expected) =>
        Assert.That(NtripResponse.IsChunked(header), Is.EqualTo(expected));
}
