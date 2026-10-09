// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Reads a <c>Tools/nmea-capture.py</c> file: one datagram per line as
/// <c>seconds TAB ip:port TAB payload</c>, the payload with CR, LF, tab and backslash
/// escaped and any other byte as <c>\xhh</c>; a binary datagram is written as
/// <c>other N bytes hh hh …</c> (its first 16 bytes). Fixtures live under
/// <c>Fixtures/nmea/</c> and are copied beside the test assembly.
/// </summary>
internal static class NmeaCapture
{
    public sealed record Datagram(double Seconds, string Source, byte[] Bytes, bool Binary);

    public static string Path(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "nmea", name);

    public static List<Datagram> Read(string name)
    {
        var datagrams = new List<Datagram>();
        foreach (var line in File.ReadLines(Path(name)))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split('\t', 3);
            if (parts.Length < 3) continue;
            double seconds = double.Parse(parts[0], CultureInfo.InvariantCulture);
            if (parts[2].StartsWith("other ", StringComparison.Ordinal))
            {
                var hex = parts[2][(parts[2].IndexOf("bytes ", StringComparison.Ordinal) + 6)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var bytes = new byte[hex.Length];
                for (int i = 0; i < hex.Length; i++) bytes[i] = byte.Parse(hex[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                datagrams.Add(new Datagram(seconds, parts[1], bytes, Binary: true));
            }
            else
            {
                datagrams.Add(new Datagram(seconds, parts[1], Unescape(parts[2]), Binary: false));
            }
        }
        return datagrams;
    }

    private static byte[] Unescape(string text)
    {
        var bytes = new List<byte>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\\' || i + 1 >= text.Length) { bytes.Add((byte)c); continue; }
            char e = text[++i];
            switch (e)
            {
                case 'r': bytes.Add(13); break;
                case 'n': bytes.Add(10); break;
                case 't': bytes.Add(9); break;
                case '\\': bytes.Add((byte)'\\'); break;
                case 'x':
                    bytes.Add(byte.Parse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 2;
                    break;
                default: bytes.Add((byte)'\\'); bytes.Add((byte)e); break;
            }
        }
        return bytes.ToArray();
    }
}
