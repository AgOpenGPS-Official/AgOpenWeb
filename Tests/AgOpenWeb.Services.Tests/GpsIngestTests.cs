// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

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

    [SetUp]
    public void SetUp()
    {
        _gps = Substitute.For<IGpsService>();
        _published = new List<double>();
        _gps.When(g => g.UpdateGpsData(Arg.Any<GpsData>())).Do(ci => _published.Add(ci.Arg<GpsData>().CurrentPosition.Latitude));
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
        Feed(WithChecksum("GNGGA,123519.00,4807.038,N,01131.000,E,4,12,0.9,545.4,M,46.9,M,1.0,0000") + "\r\n");
        Feed("#INSPVAXA,COM1,0,55.0,FINESTEERING,2000,10.0;SOL_COMPUTED*1a2b3c4d\r\n");
        Assert.That(_published, Is.Empty);
        var snap = _steer.GpsSentences.GetSnapshot();
        Assert.That(snap.UnknownSentence, Is.EqualTo(2));
        Assert.That(snap.Rejected, Is.EqualTo(2));
        _gps.DidNotReceive().MarkRealGpsParsed();
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
}
