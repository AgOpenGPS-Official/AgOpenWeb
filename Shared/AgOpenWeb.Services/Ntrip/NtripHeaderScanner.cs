// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Services;

/// <summary>
/// Finds the end of a caster's reply header in the bytes read so far. Normally that is the
/// blank line. RTKLIB's caster (RTKBase) answers "ICY 200 OK" and one line break, then the
/// RTCM stream with no blank line, also to an NTRIP 2.0 request; a parser that waits for the
/// blank line waits until its timeout. Pure logic, no I/O.
/// </summary>
internal static class NtripHeaderScanner
{
    /// <summary>
    /// True when the header is complete. <paramref name="headerLength"/> is the header text
    /// without its closing line break(s); <paramref name="bodyOffset"/> is where the body starts.
    /// </summary>
    public static bool TryFindEnd(ReadOnlySpan<byte> data, out int headerLength, out int bodyOffset)
    {
        headerLength = bodyOffset = -1;

        int firstEnd = LineEnd(data, out int firstNext);
        if (firstEnd >= 0 && data.StartsWith("ICY "u8) && firstNext < data.Length && !LooksLikeHeaderText(data[firstNext]))
        {
            // "ICY 200 OK" and a line break, and the next byte is not text: the stream began.
            headerLength = firstEnd;
            bodyOffset = firstNext;
            return true;
        }

        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] != (byte)'\n') continue;
            // A blank line is "\n" followed by "\n" or "\r\n".
            if (i + 1 < data.Length && data[i + 1] == (byte)'\n')
            {
                headerLength = i;
                bodyOffset = i + 2;
                return true;
            }
            if (i + 2 < data.Length && data[i + 1] == (byte)'\r' && data[i + 2] == (byte)'\n')
            {
                headerLength = i > 0 && data[i - 1] == (byte)'\r' ? i - 1 : i;
                bodyOffset = i + 3;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The data is exactly an "ICY ..." status line and its line break. RTKLIB sends that and
    /// then waits for the next epoch, so after a short pause it counts as the whole header.
    /// </summary>
    public static bool IsIcyStatusLineOnly(ReadOnlySpan<byte> data) =>
        data.StartsWith("ICY "u8) && LineEnd(data, out int next) >= 0 && next == data.Length;

    // Index where the first line's text ends (its line break starts there), or -1 without a line break.
    private static int LineEnd(ReadOnlySpan<byte> data, out int next)
    {
        int lf = data.IndexOf((byte)'\n');
        if (lf < 0) { next = -1; return -1; }
        next = lf + 1;
        return lf > 0 && data[lf - 1] == (byte)'\r' ? lf - 1 : lf;
    }

    // A header line starts with a letter and a blank line with a line break: both are printable or CR/LF.
    private static bool LooksLikeHeaderText(byte b) => b is (byte)'\r' or (byte)'\n' || (b >= 0x20 && b < 0x7F);
}
