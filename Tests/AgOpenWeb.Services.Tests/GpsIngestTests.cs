// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.GPS;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Datagrams in, fixes out, through the splitter and the parser in AutoSteerService: the
/// same sentences give the same fixes however a bridge chunked them (the receiver-field
/// determinism of Plans/GPS_RECEIVER_SENTENCES_PLAN.md), and nothing is allocated per
/// datagram on the one-line path.
/// </summary>
[TestFixture]
public class GpsIngestTests
{
    private IGpsService _gps = null!;
    private AutoSteerService _steer = null!;
    private List<double> _published = null!;
    private List<int> _fixTimes = null!;

    [SetUp]
    public void SetUp()
    {
        _gps = Substitute.For<IGpsService>();
        _published = new List<double>();
        _fixTimes = new List<int>();
        _gps.When(g => g.UpdateGpsData(Arg.Any<GpsData>())).Do(ci =>
        {
            _published.Add(ci.Arg<GpsData>().CurrentPosition.Latitude);
            _fixTimes.Add(ci.Arg<GpsData>().FixTimeCentiseconds);
        });
        _steer = new AutoSteerService(Substitute.For<ITrackGuidanceService>(), Substitute.For<IUdpCommunicationService>(), _gps, new ConfigurationStore());
        _steer.Start();
    }

    [TearDown]
    public void TearDown() => _steer.Stop();

    private static string Panda(double latDeg)
    {
        int d = (int)latDeg;
        double m = (latDeg - d) * 60;
        string body = string.Format(CultureInfo.InvariantCulture,
            "PANDA,123456.00,{0:00}{1:00.0000},N,01131.0000,E,4,12,0.9,100.0,0.0,5.5,900,12,0,0.00", d, m);
        byte checksum = 0;
        foreach (char c in body) checksum ^= (byte)c;
        return $"${body}*{checksum:X2}";
    }

    private static string WithChecksum(string body)
    {
        byte checksum = 0;
        foreach (char c in body) checksum ^= (byte)c;
        return $"${body}*{checksum:X2}";
    }

    private void Feed(string datagram, GpsSource source = GpsSource.ModulePort) =>
        _steer.ProcessGpsDatagram(Encoding.ASCII.GetBytes(datagram), source);

    [Test]
    public void One_sentence_per_datagram_publishes_one_fix_each()
    {
        Feed(Panda(48.1) + "\r\n");
        Feed(Panda(48.2));
        Assert.That(_published, Is.EqualTo(new[] { 48.1, 48.2 }).Within(1e-6));
        Assert.That(_steer.GpsLines.JoinedLines, Is.Zero);
    }

    [Test]
    public void A_fix_carries_the_receiver_s_own_time_of_day()
    {
        Feed(Panda(48.1) + "\r\n");
        Assert.That(_fixTimes, Is.EqualTo(new[] { (12 * 3600 + 34 * 60 + 56) * 100 }), "12:34:56.00 in 1/100 s");
    }

    [Test]
    public void A_fix_made_of_several_sentences_carries_the_time_of_its_position_sentences()
    {
        // GGA + VTG + HPR. While the family is being learned a fix goes out late, on the first
        // sentence of the next epoch, which carries the next time; once it is learned, each on its
        // own HPR. Either way the fix has the time of its own GGA.
        for (int e = 0; e < 8; e++)
        {
            Feed(Encoding.ASCII.GetString(NmeaEpochAssemblerTests.Line(NmeaEpochAssemblerTests.Gga(e))) + "\r\n");
            Feed(Encoding.ASCII.GetString(NmeaEpochAssemblerTests.Line(NmeaEpochAssemblerTests.Vtg())) + "\r\n");
            Feed(Encoding.ASCII.GetString(NmeaEpochAssemblerTests.Line(NmeaEpochAssemblerTests.Hpr(e))) + "\r\n");
        }
        int first = (12 * 3600 + 34 * 60 + 56) * 100;
        Assert.That(_fixTimes, Is.EqualTo(Enumerable.Range(0, 8).Select(e => first + e * 10)),
            "every epoch's own time, a tenth of a second from one fix to the next");
    }

    [Test]
    public void A_fix_closed_by_the_next_epoch_s_first_sentence_carries_its_own_time()
    {
        // GGA only, not learned yet: each fix goes out when the next GGA arrives. It took that next
        // GGA's time, a tenth of a second on.
        for (int e = 0; e < 3; e++)
            Feed(Encoding.ASCII.GetString(NmeaEpochAssemblerTests.Line(NmeaEpochAssemblerTests.Gga(e))) + "\r\n");
        int first = (12 * 3600 + 34 * 60 + 56) * 100;
        Assert.That(_fixTimes, Is.EqualTo(new[] { first, first + 10 }), "the fixes of 12:34:56.00 and .10, sent on the GGA of .10 and .20");
    }

    [Test]
    public void A_fix_with_no_time_does_not_take_the_time_of_a_sentence_before_it()
    {
        // A GGA that never becomes a fix (its epoch still open), then a fix whose sentence has no time.
        Feed(Encoding.ASCII.GetString(NmeaEpochAssemblerTests.Line(NmeaEpochAssemblerTests.Gga(0))) + "\r\n");
        Feed(WithChecksum("PANDA,,4806.0000,N,01131.0000,E,4,12,0.9,100.0,0.0,5.5,900,12,0,0.00") + "\r\n");
        Assert.That(_published, Has.Count.EqualTo(1), "the PANDA is the fix");
        Assert.That(_fixTimes, Is.EqualTo(new[] { -1 }));

        // And a fix with a time, then one without: none again.
        Feed(Panda(48.1) + "\r\n");
        Feed(WithChecksum("PANDA,,4806.0000,N,01131.0000,E,4,12,0.9,100.0,0.0,5.5,900,12,0,0.00") + "\r\n");
        Assert.That(_fixTimes, Is.EqualTo(new[] { -1, (12 * 3600 + 34 * 60 + 56) * 100, -1 }));
    }

    [Test]
    public void A_batched_datagram_publishes_a_fix_per_line()
    {
        Feed(Panda(48.1) + "\r\n" + Panda(48.2) + "\r\n" + Panda(48.3) + "\r\n");
        Assert.That(_published, Is.EqualTo(new[] { 48.1, 48.2, 48.3 }).Within(1e-6));
    }

    [Test]
    public void The_same_sentences_give_the_same_fixes_however_they_were_chunked()
    {
        var sentences = new[] { Panda(48.1), Panda(48.2), Panda(48.3), Panda(48.4) };
        string stream = string.Join("\r\n", sentences) + "\r\n";

        // One per datagram.
        foreach (var s in sentences) Feed(s + "\r\n");
        var onePer = _published.ToArray(); _published.Clear();

        // Two per datagram.
        Feed(sentences[0] + "\r\n" + sentences[1] + "\r\n");
        Feed(sentences[2] + "\r\n" + sentences[3] + "\r\n");
        var twoPer = _published.ToArray(); _published.Clear();

        // Cut every 37 bytes, regardless of line ends.
        for (int i = 0; i < stream.Length; i += 37) Feed(stream.Substring(i, Math.Min(37, stream.Length - i)));
        var cut = _published.ToArray();

        Assert.That(onePer, Is.EqualTo(new[] { 48.1, 48.2, 48.3, 48.4 }).Within(1e-6));
        Assert.That(twoPer, Is.EqualTo(onePer));
        Assert.That(cut, Is.EqualTo(onePer));
        Assert.That(_steer.GpsLines.JoinedLines, Is.GreaterThan(0), "the cut stream needed the tail");
    }

    [Test]
    public void A_line_this_build_does_not_decode_is_counted_and_publishes_nothing()
    {
        Feed(WithChecksum("GNZDA,123519.00,09,10,2026,00,00") + "\r\n");
        string bestnav = "#BESTNAVA,COM1,0,55.0,FINESTEERING,2000,10.0,00000040,4e77,43562;SOL_COMPUTED,NARROW_INT,51.1,-114.0";
        Feed(bestnav + "*" + AgOpenWeb.Services.Gps.UnicoreCrc32.Compute(Encoding.ASCII.GetBytes(bestnav[1..])).ToString("x8") + "\r\n");
        Assert.That(_published, Is.Empty);
        var snap = _steer.GpsSentences.GetSnapshot();
        Assert.That(snap.UnknownSentence, Is.EqualTo(2));
        Assert.That(snap.Rejected, Is.EqualTo(2));
        _gps.DidNotReceive().MarkRealGpsParsed();
    }

    [Test]
    public void An_epoch_member_is_neither_a_fix_nor_a_refusal()
    {
        Feed(WithChecksum("GNGGA,123519.00,4807.038,N,01131.000,E,4,12,0.9,545.4,M,46.9,M,1.0,0000") + "\r\n");
        Assert.That(_published, Is.Empty, "the epoch is open until the next one starts");
        var snap = _steer.GpsSentences.GetSnapshot();
        Assert.That(snap.Rejected, Is.Zero);
        Assert.That(snap.Sentences.Any(s => s.Type == "GGA"), Is.True, "but the card shows it");
    }

    [Test]
    public void The_source_port_is_remembered()
    {
        Feed(Panda(48.1), GpsSource.Gps2);
        Assert.That(_steer.LastGpsSource, Is.EqualTo(GpsSource.Gps2));
        Feed(Panda(48.1), GpsSource.ModulePort);
        Assert.That(_steer.LastGpsSource, Is.EqualTo(GpsSource.ModulePort));
    }

    [Test]
    public void The_byte_array_entry_point_is_the_module_port()
    {
        var bytes = Encoding.ASCII.GetBytes(Panda(48.1));
        _steer.ProcessGpsBuffer(bytes, bytes.Length);
        Assert.That(_published, Is.EqualTo(new[] { 48.1 }).Within(1e-6));
        Assert.That(_steer.LastGpsSource, Is.EqualTo(GpsSource.ModulePort));
    }

    [Test]
    public void A_multi_sentence_receiver_gives_the_same_fixes_however_it_was_chunked()
    {
        // Learned family GGA+VTG+HPR, then four epochs with distinct latitudes.
        var sentences = new List<string>();
        for (int e = 0; e < 8; e++)
        {
            sentences.Add(Encoding.ASCII.GetString(NmeaEpochAssemblerTests.Line(NmeaEpochAssemblerTests.Gga(e, latDdmm: 4807.0 + e))));
            sentences.Add(Encoding.ASCII.GetString(NmeaEpochAssemblerTests.Line(NmeaEpochAssemblerTests.Vtg())));
            sentences.Add(Encoding.ASCII.GetString(NmeaEpochAssemblerTests.Line(NmeaEpochAssemblerTests.Hpr(e))));
        }
        string stream = string.Join("\r\n", sentences) + "\r\n";

        double[] Run(Action feed)
        {
            _published.Clear();
            _steer.Stop();
            _steer = new AutoSteerService(Substitute.For<ITrackGuidanceService>(), Substitute.For<IUdpCommunicationService>(), _gps, new ConfigurationStore());
            _steer.Start();
            feed();
            return _published.ToArray();
        }

        var onePer = Run(() => { foreach (var s in sentences) Feed(s + "\r\n"); });
        var burst = Run(() => { for (int i = 0; i < sentences.Count; i += 3) Feed(sentences[i] + "\r\n" + sentences[i + 1] + "\r\n" + sentences[i + 2] + "\r\n"); });
        var cut = Run(() => { for (int i = 0; i < stream.Length; i += 41) Feed(stream.Substring(i, Math.Min(41, stream.Length - i))); });

        Assert.That(onePer.Length, Is.EqualTo(8), "three learned late, then each on its HPR");
        Assert.That(burst, Is.EqualTo(onePer));
        Assert.That(cut, Is.EqualTo(onePer));
        Assert.That(_steer.GpsEpochs.FamilyText, Is.EqualTo("GGA+VTG+HPR"));
    }

    // A receiver with no fix (indoors; the HAT's GPS on the bench, 2026-11-15) still sends its
    // sentence at 10 Hz, quality 0 and position 0,0.
    private static string NoFix() =>
        WithChecksum("PANDA,123456.00,0000.0000,N,00000.0000,E,0,0,99.9,0.0,0.0,0.0,0,0,0,0.00");

    [Test]
    public void A_receiver_without_a_fix_does_not_count_as_a_live_source()
    {
        // The live gate keeps the simulator off while a receiver drives the pipeline: with a
        // no-fix sentence counted, the simulator refused to start, and stopped at once.
        Feed(NoFix());
        _gps.DidNotReceive().MarkRealGpsParsed();
        Feed(Panda(48.1));
        _gps.Received(1).MarkRealGpsParsed();
    }

    [Test]
    public void While_the_simulator_is_the_source_a_receiver_without_a_fix_is_left_out_and_one_with_a_fix_takes_over()
    {
        _steer.SetSimulatorIsSource(true);
        Feed(NoFix());
        Assert.That(_published, Is.Empty, "no 0,0 between the simulator's positions");
        _gps.DidNotReceive().MarkRealGpsParsed();

        Feed(Panda(48.1));
        Assert.That(_published, Is.EqualTo(new[] { 48.1 }).Within(1e-6), "a fix takes over, as before");
        _gps.Received(1).MarkRealGpsParsed();
    }

    [Test]
    public void Without_the_simulator_a_receiver_without_a_fix_is_published_as_before()
    {
        Feed(NoFix());
        Assert.That(_published, Has.Count.EqualTo(1), "the status shows the receiver has no fix");
    }
}
