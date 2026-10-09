// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.Text;
using AgOpenWeb.Models.GPS;
using AgOpenWeb.Services.Gps;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// The splitter in front of the parser: whole lines out of datagrams, however a bridge
/// chunked them (Plans/GPS_RECEIVER_SENTENCES_PLAN.md, Phase 1).
/// </summary>
[TestFixture]
public class NmeaLineSplitterTests
{
    private const string Panda = "$PANDA,162255.50,3924.90,N,00731.80,W,4,12,0.7,341.9,1.2,4.8,2217,31,-12,0.5*5A";
    private const string Ksxt = "$KSXT,20261009001935.90,-87.18038977,32.59045030,57.6625,20.54,-1.00,140.70,0.006,0.00,3,3,27,31,-13.911,-11.418,-3.127,0.004,-0.005,0.005,,*24";
    private const string Gga = "$GNGGA,123519.00,4807.038,N,01131.000,E,4,12,0.9,545.4,M,46.9,M,1.0,0000*5B";

    private static byte[] B(string s) => Encoding.ASCII.GetBytes(s);

    /// <summary>Every line the splitter hands out for one datagram, as text.</summary>
    private static List<string> Lines(NmeaLineSplitter splitter, string datagram, GpsSource source = GpsSource.ModulePort)
    {
        var lines = new List<string>();
        foreach (var line in splitter.Lines(B(datagram), source))
            lines.Add(Encoding.ASCII.GetString(line));
        return lines;
    }

    [Test]
    public void One_line_per_datagram_is_the_same_span_no_copy()
    {
        var splitter = new NmeaLineSplitter();
        foreach (var text in new[] { Panda, Panda + "\r\n", Ksxt + "\n" })
        {
            var datagram = B(text);
            int count = 0;
            foreach (var line in splitter.Lines(datagram, GpsSource.ModulePort))
            {
                count++;
                Assert.That(line.Overlaps(datagram), Is.True, "the line is a window on the receive buffer");
                Assert.That(Encoding.ASCII.GetString(line), Is.EqualTo(text.TrimEnd('\r', '\n')));
            }
            Assert.That(count, Is.EqualTo(1));
        }
        Assert.That(splitter.JoinedLines, Is.Zero);
        Assert.That(splitter.DroppedBytes, Is.Zero);
    }

    [Test]
    public void A_batched_epoch_gives_each_line_in_order()
    {
        var splitter = new NmeaLineSplitter();
        var lines = Lines(splitter, Gga + "\r\n" + Panda + "\r\n" + Ksxt + "\r\n");
        Assert.That(lines, Is.EqualTo(new[] { Gga, Panda, Ksxt }));
        Assert.That(splitter.JoinedLines, Is.Zero);
    }

    [Test]
    public void Lines_without_line_ends_are_cut_at_the_next_dollar()
    {
        var splitter = new NmeaLineSplitter();
        Assert.That(Lines(splitter, Panda + Ksxt), Is.EqualTo(new[] { Panda, Ksxt }));
    }

    [Test]
    public void A_line_cut_across_two_datagrams_is_joined_and_counted()
    {
        var splitter = new NmeaLineSplitter();
        int cut = Panda.Length - 7; // inside the last field
        Assert.That(Lines(splitter, Panda[..cut]), Is.Empty, "nothing whole yet");
        Assert.That(Lines(splitter, Panda[cut..] + "\r\n"), Is.EqualTo(new[] { Panda }));
        Assert.That(splitter.JoinedLines, Is.EqualTo(1));
        Assert.That(splitter.DroppedBytes, Is.Zero);
    }

    [Test]
    public void A_cut_inside_the_checksum_waits_for_both_digits()
    {
        var splitter = new NmeaLineSplitter();
        Assert.That(Lines(splitter, Panda[..^1]), Is.Empty, "ends with *5: not a whole checksum");
        Assert.That(Lines(splitter, Panda[^1..]), Is.EqualTo(new[] { Panda }));
        Assert.That(splitter.JoinedLines, Is.EqualTo(1));
    }

    [Test]
    public void A_line_spread_over_three_datagrams_is_joined_once()
    {
        var splitter = new NmeaLineSplitter();
        Assert.That(Lines(splitter, Ksxt[..30]), Is.Empty);
        Assert.That(Lines(splitter, Ksxt[30..60]), Is.Empty);
        Assert.That(Lines(splitter, Ksxt[60..] + "\r\n" + Panda + "\r\n"), Is.EqualTo(new[] { Ksxt, Panda }));
        Assert.That(splitter.JoinedLines, Is.EqualTo(1));
    }

    [Test]
    public void A_tail_that_never_completes_is_dropped_when_a_new_line_starts()
    {
        var splitter = new NmeaLineSplitter();
        Assert.That(Lines(splitter, Panda[..20]), Is.Empty);
        Assert.That(Lines(splitter, Ksxt + "\r\n"), Is.EqualTo(new[] { Ksxt }), "the new line is whole on its own");
        Assert.That(splitter.DroppedBytes, Is.EqualTo(20));
        Assert.That(splitter.JoinedLines, Is.Zero);
    }

    [Test]
    public void Tails_are_kept_per_source()
    {
        var splitter = new NmeaLineSplitter();
        Assert.That(Lines(splitter, Panda[..30], GpsSource.Gps1), Is.Empty);
        Assert.That(Lines(splitter, Ksxt[..40], GpsSource.Gps2), Is.Empty);
        Assert.That(Lines(splitter, Ksxt[40..] + "\r\n", GpsSource.Gps2), Is.EqualTo(new[] { Ksxt }));
        Assert.That(Lines(splitter, Panda[30..] + "\r\n", GpsSource.Gps1), Is.EqualTo(new[] { Panda }));
        Assert.That(splitter.JoinedLines, Is.EqualTo(2));
    }

    [Test]
    public void Stray_bytes_between_lines_are_skipped_and_counted()
    {
        var splitter = new NmeaLineSplitter();
        Assert.That(Lines(splitter, "\r\n" + Panda + "\r\nAB" + Ksxt + "\r\n"), Is.EqualTo(new[] { Panda, Ksxt }));
        Assert.That(splitter.DroppedBytes, Is.EqualTo(2), "line ends cost nothing; the two letters are garbage");
    }

    [Test]
    public void Hash_lines_are_handed_out_for_the_parser_to_name()
    {
        var splitter = new NmeaLineSplitter();
        const string ins = "#INSPVAXA,COM1,0,55.0,FINESTEERING,2000,10.0;SOL_COMPUTED,INS_SOLUTION_GOOD*1a2b3c4d";
        Assert.That(Lines(splitter, ins + "\r\n" + Panda + "\r\n"), Is.EqualTo(new[] { ins, Panda }));
    }

    [Test]
    public void An_overlong_partial_line_is_garbage_not_a_tail()
    {
        var splitter = new NmeaLineSplitter();
        var junk = "$" + new string('x', NmeaLineSplitter.MaxTail + 10);
        Assert.That(Lines(splitter, junk), Is.Empty);
        Assert.That(splitter.DroppedBytes, Is.EqualTo(junk.Length));
        Assert.That(Lines(splitter, Panda + "\r\n"), Is.EqualTo(new[] { Panda }), "the next datagram is unaffected");
    }

    [Test]
    public void A_tail_that_grows_past_the_limit_is_dropped()
    {
        var splitter = new NmeaLineSplitter();
        var first = "$" + new string('a', 300);
        var second = new string('b', 300);
        Assert.That(Lines(splitter, first), Is.Empty);
        Assert.That(Lines(splitter, second), Is.Empty);
        Assert.That(splitter.DroppedBytes, Is.EqualTo(601));
        Assert.That(Lines(splitter, Panda), Is.EqualTo(new[] { Panda }));
    }

    [Test]
    public void Only_text_datagrams_are_for_the_splitter()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NmeaLineSplitter.IsTextDatagram(B(Panda)), Is.True);
            Assert.That(NmeaLineSplitter.IsTextDatagram(B("#INSPVAXA,COM1;x*1a2b3c4d")), Is.True);
            Assert.That(NmeaLineSplitter.IsTextDatagram(B("\r\n")), Is.True, "stray line ends are text");
            Assert.That(NmeaLineSplitter.IsTextDatagram(B("3,25,-16.369*0A\r\n")), Is.True, "the rest of a cut line");
            Assert.That(NmeaLineSplitter.IsTextDatagram(new byte[] { 0x80, 0x81, 0x7F, 0xFD }), Is.False, "a PGN");
            Assert.That(NmeaLineSplitter.IsTextDatagram(new byte[] { 0xD0, 0xF2, 0x81, 0xF8 }), Is.False, "a stranger's binary broadcast");
            Assert.That(NmeaLineSplitter.IsTextDatagram(ReadOnlySpan<byte>.Empty), Is.False);
        });
    }

    [Test]
    public void An_empty_datagram_changes_nothing()
    {
        var splitter = new NmeaLineSplitter();
        Assert.That(Lines(splitter, Panda[..30]), Is.Empty);
        Assert.That(Lines(splitter, ""), Is.Empty);
        Assert.That(Lines(splitter, Panda[30..]), Is.EqualTo(new[] { Panda }), "the empty datagram did not drop the tail");
    }
}
