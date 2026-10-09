// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.GPS;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Gps;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Real receiver output replayed datagram by datagram through the ingest path (splitter,
/// parser, assembler): what a bench run produced, kept so it stays decoded the same way.
/// </summary>
[TestFixture]
public class CaptureReplayTests
{
    private sealed record Fix(double Lat, double Lon, double Heading, double Speed, int Fix_, int Sats, double Hdop, double Age, bool Dual);

    /// <summary>Feed datagrams through a fresh service the way the UDP receive path does, collecting every published fix.</summary>
    private static (List<Fix> fixes, AutoSteerService steer) Replay(IEnumerable<byte[]> datagrams)
    {
        var gps = Substitute.For<IGpsService>();
        var fixes = new List<Fix>();
        gps.When(g => g.UpdateGpsData(Arg.Any<GpsData>())).Do(ci =>
        {
            var d = ci.Arg<GpsData>();
            fixes.Add(new Fix(d.CurrentPosition.Latitude, d.CurrentPosition.Longitude, d.CurrentPosition.Heading,
                d.CurrentPosition.Speed, d.FixQuality, d.SatellitesInUse, d.Hdop, d.DifferentialAge, d.HasDualHeading));
        });
        var steer = new AutoSteerService(Substitute.For<ITrackGuidanceService>(), Substitute.For<IUdpCommunicationService>(), gps, new ConfigurationStore());
        steer.Start();
        foreach (var datagram in datagrams)
            if (NmeaLineSplitter.IsTextDatagram(datagram)) // the UDP service's gate
                steer.ProcessGpsDatagram(datagram, GpsSource.ModulePort);
        steer.Stop();
        return (fixes, steer);
    }

    /// <summary>
    /// UM982 behind an AiO v26 passthrough, 2026-10-09, Chris's bench: GPGGA + GPVTG + GPHPR
    /// at 10 Hz, one sentence per datagram, RTK float while the position reconverged after
    /// the output change, heading fixed. Two binary datagrams from an unrelated host on the
    /// LAN are in the capture too.
    /// </summary>
    [Test]
    public void UM982_GGA_VTG_HPR_through_the_AiO_passthrough()
    {
        var capture = NmeaCapture.Read("um982-gga-vtg-hpr.txt");
        Assert.That(capture.Count(d => !d.Binary), Is.EqualTo(300), "100 epochs of three sentences");
        Assert.That(capture.Count(d => d.Binary), Is.EqualTo(2), "the stranger's broadcasts");

        var (fixes, steer) = Replay(capture.Select(d => d.Bytes));

        Assert.Multiple(() =>
        {
            Assert.That(steer.GpsEpochs.FamilyText, Is.EqualTo("GGA+VTG+HPR"));
            Assert.That(fixes, Has.Count.EqualTo(100), "every epoch gave one fix, the first three one epoch late");
            Assert.That(steer.GpsEpochs.IncompleteEpochs, Is.Zero);
            Assert.That(steer.GpsEpochs.DroppedEpochs, Is.Zero);
            Assert.That(steer.GpsLines.JoinedLines, Is.Zero, "one whole line per datagram");
            Assert.That(steer.GpsLines.DroppedBytes, Is.Zero, "the binary datagrams never reached the splitter");
            var snap = steer.GpsSentences.GetSnapshot();
            Assert.That(snap.Rejected, Is.Zero);
            Assert.That(snap.Sentences.Select(s => s.Type), Is.EquivalentTo(new[] { "GGA", "VTG", "HPR" }));
        });

        // Every fix carries what the receiver measured in that epoch.
        Assert.Multiple(() =>
        {
            Assert.That(fixes.All(f => Math.Abs(f.Lat - 32.5904) < 0.001 && Math.Abs(f.Lon + 87.1804) < 0.001), Is.True, "Alabama bench");
            Assert.That(fixes.All(f => f.Fix_ == 5), Is.True, "GGA quality 5 throughout");
            Assert.That(fixes.All(f => f.Sats == 28 && Math.Abs(f.Hdop - 0.6) < 1e-9), Is.True);
            Assert.That(fixes.All(f => f.Age >= 0 && f.Age <= 1.0), Is.True, "corrections every second");
            Assert.That(fixes.All(f => f.Dual && f.Heading > 20.5 && f.Heading < 22.0), Is.True, "HPR heading, fixed");
            Assert.That(fixes.All(f => f.Speed < 0.01), Is.True, "parked");
        });

        // First fix = the first epoch's values, in the receiver's own order.
        Assert.That(fixes[0].Age, Is.EqualTo(1.0));
        Assert.That(fixes[0].Heading, Is.EqualTo(21.2556));
    }

    [Test]
    public void The_capture_gives_the_same_fixes_however_it_is_chunked()
    {
        var capture = NmeaCapture.Read("um982-gga-vtg-hpr.txt").Where(d => !d.Binary).Select(d => d.Bytes).ToList();
        var stream = capture.SelectMany(b => b).ToArray();

        var (onePer, _) = Replay(capture);
        var (burst, _) = Replay(Enumerable.Range(0, capture.Count / 3).Select(i => capture[i * 3].Concat(capture[i * 3 + 1]).Concat(capture[i * 3 + 2]).ToArray()));
        var (cut, cutSteer) = Replay(Enumerable.Range(0, (stream.Length + 36) / 37).Select(i => stream.Skip(i * 37).Take(37).ToArray()));

        Assert.That(burst, Is.EqualTo(onePer));
        Assert.That(cut, Is.EqualTo(onePer));
        Assert.That(cutSteer.GpsLines.JoinedLines, Is.GreaterThan(200), "almost every line was cut");
    }
}
