using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AgOpenWeb.Services;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// RTCM forwarding plan, Phase 2: a caster's chunked reply is decoded before the stream is
/// framed, however TCP slices it.
/// </summary>
[TestFixture]
public class ChunkedDecoderTests
{
    private static byte[] Chunk(byte[] data, string extension = "") =>
        Encoding.ASCII.GetBytes(data.Length.ToString("x") + extension + "\r\n").Concat(data).Concat("\r\n"u8.ToArray()).ToArray();

    private static byte[] Decode(ChunkedDecoder decoder, params byte[][] slices)
    {
        var body = new List<byte>();
        foreach (var s in slices) decoder.Feed(s, d => body.AddRange(d.ToArray()));
        return body.ToArray();
    }

    private static byte[] Bytes(int n, int seed = 0) => Enumerable.Range(0, n).Select(i => (byte)(i * 7 + seed)).ToArray();

    [Test]
    public void Chunks_AreJoinedIntoTheBody()
    {
        byte[] a = Bytes(500), b = Bytes(31, 5), c = Bytes(4096, 9);
        var decoder = new ChunkedDecoder();

        byte[] body = Decode(decoder, Chunk(a).Concat(Chunk(b)).Concat(Chunk(c)).ToArray());

        Assert.That(body, Is.EqualTo(a.Concat(b).Concat(c).ToArray()));
        Assert.That(decoder.IsBroken, Is.False);
    }

    [Test]
    public void AStreamSplitAtAnyByte_DecodesTheSame()
    {
        byte[] a = Bytes(300), b = Bytes(18, 3);
        byte[] stream = Chunk(a).Concat(Chunk(b, ";name=value")).ToArray();
        for (int cut = 1; cut < stream.Length; cut++)
        {
            var decoder = new ChunkedDecoder();
            Assert.That(Decode(decoder, stream[..cut], stream[cut..]), Is.EqualTo(a.Concat(b).ToArray()), $"cut at {cut}");
            Assert.That(decoder.IsBroken, Is.False, $"cut at {cut}");
        }
    }

    [Test]
    public void BodyBytesThatLookLikeASizeLine_AreData()
    {
        byte[] tricky = Encoding.ASCII.GetBytes("1f4\r\n\r\n0\r\n\r\n");
        var decoder = new ChunkedDecoder();
        Assert.That(Decode(decoder, Chunk(tricky), Chunk(Bytes(10))), Is.EqualTo(tricky.Concat(Bytes(10)).ToArray()));
        Assert.That(decoder.IsDone, Is.False);
    }

    [Test]
    public void TheZeroChunk_EndsTheBody()
    {
        var decoder = new ChunkedDecoder();
        byte[] body = Decode(decoder, Chunk(Bytes(20)), "0\r\n\r\n"u8.ToArray(), Bytes(50));
        Assert.That(body, Is.EqualTo(Bytes(20)));
        Assert.That(decoder.IsDone, Is.True);
    }

    [Test]
    public void AStreamThatIsNotChunked_IsPassedThrough_FromWhereItBreaks()
    {
        // The header said chunked; the body is plain RTCM.
        byte[] rtcm = RtcmFramerTests.Message(1077, 200).Concat(RtcmFramerTests.Message(1087, 100)).ToArray();
        var decoder = new ChunkedDecoder();

        byte[] body = Decode(decoder, rtcm[..90], rtcm[90..]);

        Assert.That(decoder.IsBroken, Is.True);
        Assert.That(body, Is.EqualTo(rtcm));
    }

    [Test]
    public void AChunkedRtcmStream_FramesCleanly()
    {
        // Chunk boundaries inside messages: what broke one message per epoch before decoding.
        byte[] rtcm = Enumerable.Range(0, 6).SelectMany(i => RtcmFramerTests.Message(1077 + 10 * (i % 3), 350, i)).ToArray();
        byte[] stream = Chunk(rtcm[..500]).Concat(Chunk(rtcm[500..1300])).Concat(Chunk(rtcm[1300..])).ToArray();
        var decoder = new ChunkedDecoder();
        var framer = new RtcmFramer();

        foreach (var slice in stream.Chunk(97))
            decoder.Feed(slice, d => framer.Feed(d));

        Assert.That(framer.Messages, Is.EqualTo(6));
        Assert.That(framer.BytesSkipped + framer.ChecksumFailures, Is.Zero);
    }
}
