// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.GpsNtripScenarios.Tests;

/// <summary>
/// RTCM 3 framing for the fake caster and the emulated receiver, written here on purpose
/// rather than taken from AgOpenWeb, so the receiver judges AgOpenWeb's forwarding with its
/// own rules: preamble 0xD3, 6 reserved bits and a 10-bit length, the payload, CRC-24Q.
/// </summary>
public static class Rtcm
{
    public const int Overhead = 6;
    public const int MaxPayload = 1023;

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i << 16;
            for (int b = 0; b < 8; b++)
                c = (c & 0x800000) != 0 ? (c << 1) ^ 0x1864CFB : c << 1;
            t[i] = c & 0xFFFFFF;
        }
        return t;
    }

    public static uint Crc24Q(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (byte b in data)
            crc = ((crc << 8) & 0xFFFFFF) ^ Table[(byte)(crc >> 16) ^ b];
        return crc;
    }

    /// <summary>
    /// One message: the 12-bit message number, the 12-bit station id and, for observations,
    /// the 30-bit epoch time (here the caster's milliseconds since it started), then filler
    /// up to <paramref name="payloadLength"/> bytes.
    /// </summary>
    public static byte[] Message(int type, int payloadLength, uint epochMs, Random rnd, int station = 2101)
    {
        payloadLength = Math.Clamp(payloadLength, 8, MaxPayload);
        var payload = new byte[payloadLength];
        rnd.NextBytes(payload);
        SetBits(payload, 0, 12, (uint)type);
        SetBits(payload, 12, 12, (uint)station);
        SetBits(payload, 24, 30, epochMs & 0x3FFFFFFF);
        var frame = new byte[payloadLength + Overhead];
        frame[0] = 0xD3;
        frame[1] = (byte)((payloadLength >> 8) & 0x03);
        frame[2] = (byte)payloadLength;
        payload.CopyTo(frame, 3);
        uint crc = Crc24Q(frame.AsSpan(0, payloadLength + 3));
        frame[^3] = (byte)(crc >> 16);
        frame[^2] = (byte)(crc >> 8);
        frame[^1] = (byte)crc;
        return frame;
    }

    public static bool IsObservation(int type) =>
        type is >= 1001 and <= 1004 or >= 1009 and <= 1012 || (type >= 1071 && type <= 1137);

    public static bool IsStation(int type) => type is 1005 or 1006;

    public static uint Bits(ReadOnlySpan<byte> data, int start, int count)
    {
        uint value = 0;
        for (int i = start; i < start + count; i++)
            value = (value << 1) | (uint)((data[i >> 3] >> (7 - (i & 7))) & 1);
        return value;
    }

    private static void SetBits(Span<byte> data, int start, int count, uint value)
    {
        for (int i = 0; i < count; i++)
        {
            int bit = start + i;
            bool one = ((value >> (count - 1 - i)) & 1) != 0;
            if (one) data[bit >> 3] |= (byte)(1 << (7 - (bit & 7)));
            else data[bit >> 3] &= (byte)~(1 << (7 - (bit & 7)));
        }
    }

    /// <summary>
    /// Re-frames a byte stream the way a receiver does: finds whole messages with a good
    /// checksum, counts the bytes it had to skip and the checksums that failed.
    /// </summary>
    public sealed class Framer
    {
        private readonly List<byte> _buf = new();
        public long BytesSkipped, ChecksumFailures, Messages;

        public void Feed(ReadOnlySpan<byte> data, Action<int, byte[]> onMessage)
        {
            foreach (byte b in data) _buf.Add(b);
            while (true)
            {
                int start = _buf.IndexOf(0xD3);
                if (start < 0) { BytesSkipped += _buf.Count; _buf.Clear(); return; }
                if (start > 0) { BytesSkipped += start; _buf.RemoveRange(0, start); }
                if (_buf.Count < 3) return;
                int len = ((_buf[1] & 0x03) << 8) | _buf[2];
                if ((_buf[1] & 0xFC) != 0) { BytesSkipped++; _buf.RemoveAt(0); continue; }
                if (_buf.Count < len + Overhead) return;
                var frame = _buf.GetRange(0, len + Overhead).ToArray();
                uint crc = Crc24Q(frame.AsSpan(0, len + 3));
                uint got = ((uint)frame[^3] << 16) | ((uint)frame[^2] << 8) | frame[^1];
                if (crc != got) { ChecksumFailures++; BytesSkipped++; _buf.RemoveAt(0); continue; }
                _buf.RemoveRange(0, len + Overhead);
                Messages++;
                int type = len >= 2 ? (int)Bits(frame.AsSpan(3), 0, 12) : -1;
                onMessage(type, frame);
            }
        }
    }
}
