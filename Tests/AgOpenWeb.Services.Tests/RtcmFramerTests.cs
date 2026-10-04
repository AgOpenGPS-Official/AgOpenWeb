using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Services;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// RTCM forwarding plan, Phase 1: the caster's stream is framed into RTCM 3 messages
/// (preamble, length, CRC-24Q) however TCP slices it, and anything else is skipped and counted.
/// </summary>
[TestFixture]
public class RtcmFramerTests
{
    /// <summary>A valid RTCM 3 message of the given type with <paramref name="payloadLength"/> payload bytes.</summary>
    internal static byte[] Message(int type, int payloadLength = 20, int seed = 1)
    {
        var m = new byte[payloadLength + RtcmFramer.Overhead];
        m[0] = RtcmFramer.Preamble;
        m[1] = (byte)(payloadLength >> 8);
        m[2] = (byte)payloadLength;
        for (int i = 0; i < payloadLength; i++) m[3 + i] = (byte)(seed * 31 + i * 7);
        if (payloadLength >= 2)
        {
            m[3] = (byte)(type >> 4);
            m[4] = (byte)((type << 4) | (m[4] & 0x0F));
        }
        uint crc = RtcmFramer.Crc24Q(m.AsSpan(0, m.Length - 3));
        m[^3] = (byte)(crc >> 16);
        m[^2] = (byte)(crc >> 8);
        m[^1] = (byte)crc;
        return m;
    }

    private static List<(int Type, byte[] Bytes)> Parse(RtcmFramer framer, params byte[][] slices)
    {
        var found = new List<(int, byte[])>();
        foreach (var s in slices)
            framer.Feed(s, (type, message) => found.Add((type, message.ToArray())));
        return found;
    }

    [Test]
    public void APublishedMessage_PassesTheChecksum()
    {
        // The 1005 example that circulates in RTCM decoder documentation (station 2003).
        byte[] sample = Convert.FromHexString("D300133ED7D30202980EDEEF34B4BD62AC0941986F33360B98");
        var framer = new RtcmFramer();

        var found = Parse(framer, sample);

        Assert.That(found.Select(f => f.Type), Is.EqualTo(new[] { 1005 }));
        Assert.That(framer.ChecksumFailures, Is.Zero);
    }

    [Test]
    public void AMessage_IsReturnedWhole_WithItsType()
    {
        var framer = new RtcmFramer();
        byte[] m = Message(1077, 300);

        var found = Parse(framer, m);

        Assert.That(found, Has.Count.EqualTo(1));
        Assert.That(found[0].Type, Is.EqualTo(1077));
        Assert.That(found[0].Bytes, Is.EqualTo(m));
        Assert.That(framer.Messages, Is.EqualTo(1));
        Assert.That(framer.BytesSkipped, Is.Zero);
        Assert.That(framer.Pending, Is.Zero);
    }

    [Test]
    public void MessagesSplitAtAnyByte_AreStillFound()
    {
        byte[] stream = Message(1005, 19).Concat(Message(1077, 250, 2)).Concat(Message(1230, 12, 3)).ToArray();
        for (int cut = 1; cut < stream.Length; cut++)
        {
            var framer = new RtcmFramer();
            var found = Parse(framer, stream[..cut], stream[cut..]);
            Assert.That(found.Select(f => f.Type), Is.EqualTo(new[] { 1005, 1077, 1230 }), $"cut at {cut}");
            Assert.That(framer.BytesSkipped + framer.ChecksumFailures, Is.Zero, $"cut at {cut}");
        }
    }

    [Test]
    public void AStreamFedOneByteAtATime_IsStillFramed()
    {
        var framer = new RtcmFramer();
        byte[] stream = Enumerable.Range(0, 12).SelectMany(i => Message(1074 + i % 4, 40 + i * 37, i)).ToArray();
        var types = new List<int>();
        foreach (byte b in stream)
            framer.Feed(new[] { b }, (type, _) => types.Add(type));
        Assert.That(types, Has.Count.EqualTo(12));
        Assert.That(framer.BytesSkipped, Is.Zero);
    }

    [Test]
    public void TheLargestMessage_AndAnEmptyOne_AreAccepted()
    {
        var framer = new RtcmFramer();
        var found = Parse(framer, Message(1127, RtcmFramer.MaxPayload), Message(0, 0));
        Assert.That(found.Select(f => f.Bytes.Length), Is.EqualTo(new[] { RtcmFramer.MaxMessage, RtcmFramer.Overhead }));
        Assert.That(found[1].Type, Is.EqualTo(-1));
    }

    [Test]
    public void BytesBetweenMessages_AreSkippedAndCounted()
    {
        var framer = new RtcmFramer();
        byte[] junk = System.Text.Encoding.ASCII.GetBytes("\r\n1f4\r\n");

        var found = Parse(framer, Message(1005, 19), junk, Message(1077, 100));

        Assert.That(found.Select(f => f.Type), Is.EqualTo(new[] { 1005, 1077 }));
        Assert.That(framer.BytesSkipped, Is.EqualTo(junk.Length));
        Assert.That(framer.ChecksumFailures, Is.Zero);
    }

    [Test]
    public void ACorruptedMessage_FailsTheChecksum_AndTheNextOneIsFound()
    {
        var framer = new RtcmFramer();
        byte[] bad = Message(1077, 100);
        bad[50] ^= 0x01;

        var found = Parse(framer, bad, Message(1087, 80, 2));

        Assert.That(found.Select(f => f.Type), Is.EqualTo(new[] { 1087 }));
        Assert.That(framer.ChecksumFailures, Is.GreaterThanOrEqualTo(1));
        Assert.That(framer.BytesSkipped, Is.EqualTo(bad.Length), "every byte of the bad message is skipped");
    }

    [Test]
    public void AMessageCutShort_DoesNotHideTheOnesAfterIt()
    {
        // What a mid-message drop does to the stream: the head of one message, then whole ones.
        var framer = new RtcmFramer();
        byte[] head = Message(1077, 400)[..120];
        var rest = Enumerable.Range(0, 6).Select(i => Message(1087, 200, i)).ToArray();

        var found = Parse(framer, new[] { head }.Concat(rest).ToArray());

        Assert.That(found, Has.Count.EqualTo(6));
        Assert.That(framer.ChecksumFailures, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void AStrayPreambleInGarbage_DoesNotHideARealMessage()
    {
        // 0xD3 followed by a plausible header claims the next 1029 bytes. The real messages
        // inside that span are found once its checksum fails.
        var framer = new RtcmFramer();
        byte[] stray = { RtcmFramer.Preamble, 0x03, 0xFF };
        var real = Enumerable.Range(0, 8).Select(i => Message(1097, 150, i)).ToArray();

        var found = Parse(framer, new[] { stray }.Concat(real).ToArray());

        Assert.That(found, Has.Count.EqualTo(8));
        Assert.That(framer.BytesSkipped, Is.EqualTo(stray.Length));
    }

    [Test]
    public void APreambleWithReservedBitsSet_IsNotAMessageStart()
    {
        var framer = new RtcmFramer();
        byte[] notAHeader = { RtcmFramer.Preamble, 0xFF, 0xFF };

        var found = Parse(framer, notAHeader, Message(1005, 19));

        Assert.That(found.Select(f => f.Type), Is.EqualTo(new[] { 1005 }));
        Assert.That(framer.ChecksumFailures, Is.Zero, "rejected on the header, no checksum computed");
    }

    [Test]
    public void LongGarbage_IsDiscarded_WithoutGrowingTheBuffer()
    {
        var framer = new RtcmFramer();
        var junk = new byte[50_000];   // no preamble in it
        framer.Feed(junk);
        Assert.That(framer.Pending, Is.Zero);
        Assert.That(framer.BytesSkipped, Is.EqualTo(junk.Length));

        Assert.That(Parse(framer, Message(1006, 21)), Has.Count.EqualTo(1));
    }

    [Test]
    public void Reset_ForgetsAPartialMessageAndTheCounts()
    {
        var framer = new RtcmFramer();
        framer.Feed(Message(1077, 100)[..40]);
        Assert.That(framer.Pending, Is.EqualTo(40));

        framer.Reset();

        Assert.That(framer.Pending, Is.Zero);
        Assert.That(Parse(framer, Message(1005, 19)).Select(f => f.Type), Is.EqualTo(new[] { 1005 }));
        Assert.That(framer.BytesSkipped, Is.Zero);
    }
}
