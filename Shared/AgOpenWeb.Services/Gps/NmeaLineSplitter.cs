// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using AgOpenWeb.Models.GPS;

namespace AgOpenWeb.Services.Gps;

/// <summary>
/// Cuts GPS datagrams into whole lines for the parser. The bridge contract
/// (<c>Plans/GPS_RECEIVER_SENTENCES_PLAN.md</c>) says a datagram holds one or more whole
/// lines, so the common case — one <c>$…*hh</c> per datagram, with or without a line end —
/// is handed out as a span into the receive buffer, no copy. A bridge that batches an
/// epoch's burst into one datagram gets each line in turn; one that cuts a line across two
/// datagrams gets it joined from a bounded tail kept per <see cref="GpsSource"/>, and the
/// repair is counted so Network IO can point at the bridge.
///
/// <para>A line starts at <c>$</c> or <c>#</c> and ends at CR, LF, the next line start, or
/// the datagram end when the text ends with its <c>*hh</c> checksum. Bytes between lines
/// (stray line ends, binary) are skipped. Lines starting with <c>#</c> are handed out too:
/// the parser reports them as unknown until the <c>#</c> framer exists, so the System Data
/// card shows what the receiver prints.</para>
///
/// <para>Single writer: the owner serialises calls (one receive thread per port, one lock
/// around the parse). Nothing is allocated per datagram.</para>
/// </summary>
public sealed class NmeaLineSplitter
{
    /// <summary>Longest partial line kept between datagrams. A longer one is garbage.</summary>
    public const int MaxTail = 512;

    private const int SourceCount = 3; // GpsSource values

    private readonly byte[][] _tails = new byte[SourceCount][];
    private readonly int[] _tailLength = new int[SourceCount];

    /// <summary>Lines that arrived cut across two datagrams and were joined (a bridge bug, tolerated).</summary>
    public long JoinedLines { get; private set; }

    /// <summary>Bytes thrown away: a tail that never completed, grew past <see cref="MaxTail"/>, or bytes that belong to no line.</summary>
    public long DroppedBytes { get; private set; }

    public NmeaLineSplitter()
    {
        for (int i = 0; i < SourceCount; i++) _tails[i] = new byte[MaxTail];
    }

    /// <summary>
    /// Whether a datagram can be GPS text at all: a line start, a line end, or printable
    /// ASCII (the continuation of a line cut by a bridge). Binary datagrams from other
    /// devices on the LAN that broadcast to the module port are not fed to the splitter,
    /// so <see cref="DroppedBytes"/> speaks only of GPS text (seen on the bench: an
    /// unrelated host sending 18-byte binary datagrams to 9999 every few seconds).
    /// </summary>
    public static bool IsTextDatagram(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length == 0) return false;
        byte b = datagram[0];
        return IsLineStart(b) || IsLineEnd(b) || (b >= 0x20 && b < 0x7F);
    }

    /// <summary>The whole lines in <paramref name="datagram"/>, in order, after any line joined with the source's tail.</summary>
    public LineEnumerator Lines(ReadOnlySpan<byte> datagram, GpsSource source) => new(this, datagram, (int)source);

    private static bool IsLineStart(byte b) => b == (byte)'$' || b == (byte)'#';
    private static bool IsLineEnd(byte b) => b == (byte)'\r' || b == (byte)'\n';

    private static bool IsHex(byte c) =>
        (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');

    /// <summary>Text that ends with its <c>*hh</c> checksum is a complete line even without a line end.</summary>
    private static bool EndsWithChecksum(ReadOnlySpan<byte> text) =>
        text.Length >= 3 && text[^3] == (byte)'*' && IsHex(text[^2]) && IsHex(text[^1]);

    public ref struct LineEnumerator
    {
        private readonly NmeaLineSplitter _owner;
        private readonly ReadOnlySpan<byte> _data;
        private readonly int _source;
        private int _pos;
        private bool _tailChecked;
        private ReadOnlySpan<byte> _current;

        internal LineEnumerator(NmeaLineSplitter owner, ReadOnlySpan<byte> data, int source)
        {
            _owner = owner;
            _data = data;
            _source = source;
            _pos = 0;
            _tailChecked = false;
            _current = default;
        }

        public readonly ReadOnlySpan<byte> Current => _current;
        public readonly LineEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            if (!_tailChecked)
            {
                _tailChecked = true;
                if (_owner._tailLength[_source] > 0 && TryContinueTail()) return true;
            }

            while (_pos < _data.Length)
            {
                // Skip to the next line start; stray line ends cost nothing, other bytes are garbage.
                if (!IsLineStart(_data[_pos]))
                {
                    if (!IsLineEnd(_data[_pos])) _owner.DroppedBytes++;
                    _pos++;
                    continue;
                }

                int start = _pos;
                int end = start + 1;
                while (end < _data.Length && !IsLineEnd(_data[end]) && !IsLineStart(_data[end])) end++;

                var line = _data.Slice(start, end - start);
                if (end < _data.Length || EndsWithChecksum(line))
                {
                    _current = line;
                    _pos = end;
                    return true;
                }

                // The datagram ended mid-line: keep it for the next datagram from this source.
                _pos = end;
                if (line.Length <= MaxTail)
                {
                    line.CopyTo(_owner._tails[_source]);
                    _owner._tailLength[_source] = line.Length;
                }
                else
                {
                    _owner.DroppedBytes += line.Length;
                }
                return false;
            }
            return false;
        }

        /// <summary>
        /// A tail is pending from this source. The datagram's leading text (up to a line end or
        /// the next line start) completes it; a datagram that opens a new line means the tail
        /// never completed.
        /// </summary>
        private bool TryContinueTail()
        {
            int tailLength = _owner._tailLength[_source];
            var tail = _owner._tails[_source];

            if (_data.Length == 0) return false; // nothing arrived; keep waiting
            if (IsLineStart(_data[0]))
            {
                _owner.DroppedBytes += tailLength;
                _owner._tailLength[_source] = 0;
                return false;
            }

            int end = 0;
            while (end < _data.Length && !IsLineEnd(_data[end]) && !IsLineStart(_data[end])) end++;
            var rest = _data.Slice(0, end);
            _pos = end;

            if (tailLength + rest.Length > MaxTail)
            {
                _owner.DroppedBytes += tailLength + rest.Length;
                _owner._tailLength[_source] = 0;
                return false;
            }

            rest.CopyTo(tail.AsSpan(tailLength));
            int joinedLength = tailLength + rest.Length;

            if (end == _data.Length && !EndsWithChecksum(tail.AsSpan(0, joinedLength)))
            {
                // Still not whole: keep waiting.
                _owner._tailLength[_source] = joinedLength;
                return false;
            }

            _owner._tailLength[_source] = 0;
            _owner.JoinedLines++;
            _current = tail.AsSpan(0, joinedLength);
            return true;
        }
    }
}
