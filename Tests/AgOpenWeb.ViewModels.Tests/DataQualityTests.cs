// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.GPS;
using AgOpenWeb.Services.Gps;
using AgOpenWeb.Services.DataQuality;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The data from the modules arrives with gaps or in bursts, and the operator is told. From
/// #922 (22 Nov 2026): a tablet got one fix in five late or not at all; the report said the app
/// "works at maybe 1 Hz" and the app said nothing.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class DataQualityTests
{
    /// <summary>A PANDA sentence with the receiver's time <paramref name="seconds"/> after 16:00:00.</summary>
    private static byte[] Panda(double seconds)
    {
        int cs = (int)Math.Round(seconds * 100);
        string time = $"16{cs / 6000 % 60:00}{cs / 100 % 60:00}.{cs % 100:00}";
        return System.Text.Encoding.ASCII.GetBytes(
            "$PANDA," + time + ",3924.90,N,00731.80,W,4,12,0.7,341.9,1.2,4.8,2217,31,-12,0.5*5A\r\n");
    }

    private MainViewModelBuilder _b = null!;
    private MainViewModel _vm = null!;
    private GpsSentenceMonitor _gps = null!;
    private StreamQualityMonitor _steer = null!;
    private readonly List<string> _told = new();
    private double _now;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _b = new MainViewModelBuilder();
        _gps = new GpsSentenceMonitor(() => ConfigurationStore.Instance.Connections.GpsUpdateRate);
        _steer = new StreamQualityMonitor("Steer module data (PGN 253)");
        _b.AutoSteerService.GpsSentences.Returns(_gps);
        _b.UdpService.DataSensors.Returns(new IDataSensor[] { _steer });
        _vm = _b.Build();
        _vm.IsSimulatorEnabled = false;   // a real receiver
        _told.Clear();
        _vm.FailureReported += _told.Add;
        _now = 100;
    }

    private static long At(double seconds) => (long)(seconds * Stopwatch.Frequency);

    /// <summary>Fixes and PGN 253 for <paramref name="seconds"/>, a fix lost wherever
    /// <paramref name="lost"/> says so and delivered a second time, late, wherever
    /// <paramref name="again"/> does, the app judging once a second.</summary>
    private void Run(double seconds, Func<int, bool>? lost = null, Func<int, bool>? again = null)
    {
        int ticks = (int)(seconds * 100);
        for (int i = 1; i <= ticks; i++)
        {
            double t = _now + i * 0.01;
            if (i % 4 == 0) _steer.Record(At(t));                       // 25 Hz
            if (i % 10 == 0 && !(lost?.Invoke(i / 10) ?? false))
                _gps.Record(Panda(t), NmeaParseResult.Accepted, At(t)); // 10 Hz
            // The fix of 0.3 s ago once more, as a second and slower link delivers it.
            if (i % 10 == 3 && (again?.Invoke(i / 10) ?? false))
                _gps.Record(Panda(t - 0.33), NmeaParseResult.Accepted, At(t));
            if (i % 100 == 0) _vm.JudgeDataQuality(At(t));
        }
        _now += seconds;
    }

    [Test]
    public void EvenStreams_NothingIsSaid()
    {
        Run(60);

        Assert.That(_told, Is.Empty);
        Assert.That(_vm.DataQuality.Select(q => q.Finding), Is.EqualTo(new[] { DataFinding.Even, DataFinding.InOrder, DataFinding.AtRate, DataFinding.Whole, DataFinding.Even }));
    }

    [Test]
    public void OneFixInFiveLost_TheOperatorIsToldOnce_AndToldWhenItIsGoodAgain()
    {
        Run(60, lost: n => n % 5 == 0);

        Assert.That(_told, Has.Count.EqualTo(1), "told once");
        Assert.That(_told[0], Does.Match(@"^GPS positions: arriving with gaps, about (19|20|21)% missing \(8(\.\d)? of 10 a second\)\. "));
        Assert.That(_told[0], Does.Contain("Check the network cable to the modules"));
        Assert.That(_vm.DataQuality[0].Faulty, Is.True);
        Assert.That(_vm.DataQuality[4].Faulty, Is.False, "the steer module's stream is judged by itself");
        Assert.That(_vm.DataQuality[2].Finding, Is.EqualTo(DataFinding.AtRate), "the receiver still sends at 10 a second: what is lost is the link's");

        Run(60);

        Assert.That(_told, Has.Count.EqualTo(2));
        Assert.That(_told[1], Is.EqualTo("GPS positions: arriving evenly again"));
    }

    [Test]
    public void TheFixesStop_NothingMoreIsSaid()
    {
        // GPS lost has its own warning: this one ends without a word.
        Run(60, lost: n => n % 5 == 0);
        Assert.That(_told, Has.Count.EqualTo(1));

        Run(10, lost: _ => true);

        Assert.That(_told, Has.Count.EqualTo(1));
        Assert.That(_vm.DataQuality[0].Finding, Is.EqualTo(DataFinding.NoData));
    }

    [Test]
    public void WithTheSimulator_AReceiversFixesAreNotJudged()
    {
        _vm.IsSimulatorEnabled = true;

        Run(60, lost: n => n % 5 == 0);

        Assert.That(_told.Where(t => t.StartsWith("GPS positions")), Is.Empty);
        Assert.That(_vm.DataQuality[0].Faulty, Is.False);
    }

    [Test]
    public void TheBugReportCarriesEveryStreamsFigures()
    {
        Run(60, lost: n => n % 5 == 0);

        string report = _vm.DataQualityReport();

        Assert.That(report, Does.Match(@"  GPS positions: FAULT, gaps, 8(\.\d)? of 10 a second, (19|20|21)(\.\d)?% missing, longest wait 200 ms; worst (19|20|21)(\.\d)?% missing, faults 1"));
        Assert.That(report, Does.Contain("  Steer module data (PGN 253): even, 25 of 25 a second, 0% missing, longest wait 40 ms; worst 0% missing, faults 0"));
    }

    [Test]
    public void PositionsDeliveredTwice_TheOperatorIsToldOnce_AndToldWhenTheyAreInOrderAgain()
    {
        // #900: a tablet on the modules' cable that has also joined their router's Wi-Fi gets
        // some fixes a second time, 0.3 s late. Here one in sixty, as on the tester's tractor
        // (98 in ten minutes): far too few to show in how the fixes arrive.
        Run(120, again: n => n % 60 == 0);

        Assert.That(_told, Has.Count.EqualTo(1), "told once");
        Assert.That(_told[0], Does.Match(@"^GPS position times: \d+ in the last minute were the same position again or an older one\. "));
        Assert.That(_told[0], Does.Contain("Use one link to the modules, a network cable"));
        Assert.That(_vm.DataQuality[1].Faulty, Is.True);
        Assert.That(_vm.DataQuality[1].Finding, Is.EqualTo(DataFinding.OutOfOrder));
        Assert.That(_vm.DataQuality[0].Faulty, Is.False, "the fixes still arrive evenly: that sensor has nothing to say");

        Run(90);
        Assert.That(_told, Has.Count.EqualTo(2));
        Assert.That(_told[1], Is.EqualTo("GPS position times: in order again"));
    }

    [Test]
    public void TheBugReportCarriesThePositionTimes()
    {
        Run(120, again: n => n % 60 == 0);
        Assert.That(_vm.DataQualityReport(), Does.Match(@"  GPS position times: FAULT, \d+ in the last minute not newer than the one before; \d+ since the start, faults 1"));

        Run(90);
        Assert.That(_vm.DataQualityReport(), Does.Match(@"  GPS position times: in order; \d+ not newer than the one before since the start, faults 1"));
    }

    [Test]
    public void AReceiverSetToHalfTheRate_TheOperatorIsTold()
    {
        // 5 fixes a second, evenly: nothing is lost, so how they arrive is "even".
        Run(60, lost: n => n % 2 == 0);

        Assert.That(_told, Is.EqualTo(new[] { "GPS position rate: only 5 a second, 10 expected. Set the GPS receiver to send its positions at that rate" }));
        Assert.That(_vm.DataQuality[0].Finding, Is.EqualTo(DataFinding.Even));
        Assert.That(_vm.DataQualityReport(), Does.Contain("  GPS position rate: FAULT, slow, sent at 5 a second, 10 expected, faults 1"));

        ConfigurationStore.Instance.Connections.GpsUpdateRate = 5;   // the app set up for it
        Run(30, lost: n => n % 2 == 0);
        Assert.That(_told[^1], Is.EqualTo("GPS position rate: at the expected rate again"));
    }

    [Test]
    public void DamagedSentences_TheOperatorIsTold()
    {
        for (int i = 0; i < 6; i++)
        {
            Run(5);
            _gps.Record(Panda(0), NmeaParseResult.BadChecksum, At(_now));
        }
        Run(5);

        Assert.That(_told, Has.Count.EqualTo(1));
        Assert.That(_told[0], Does.Match(@"^GPS sentences: \d in the last minute arrived damaged and were not used\. Check the cable and the baud rate"));
        Assert.That(_vm.DataQualityReport(), Does.Match(@"  GPS sentences: FAULT, \d damaged in the last minute; 6 damaged since the start \(6 failed the checksum\), faults 1"));
    }

    [Test]
    public void DamagedSentences_AreAlsoMissingPositions_ButTheOperatorGetsOneMessage()
    {
        // One sentence in five damaged: it is refused, so its fix does not arrive either.
        for (int sec = 0; sec < 60; sec++)
        {
            Run(1, lost: n => n % 5 == 0);
            _gps.Record(Panda(0), NmeaParseResult.BadChecksum, At(_now - 0.5));
            _gps.Record(Panda(0), NmeaParseResult.BadChecksum, At(_now));
        }

        Assert.That(_told, Has.Count.EqualTo(1));
        Assert.That(_told[0], Does.StartWith("GPS sentences: "));
        Assert.That(_vm.DataQuality[0].Finding, Is.EqualTo(DataFinding.Gaps), "the gaps are still shown");
        Assert.That(_vm.DataQuality[0].Faulty, Is.False, "but they are the damaged sentences' to report");
        Assert.That(_vm.DataQualityReport(), Does.Match(@"  GPS positions: gaps, .*\(not reported: GPS sentences says why\), faults"));

        // The sentences arrive whole again, the link still loses one fix in five: now it is said.
        Run(90, lost: n => n % 5 == 0);
        Assert.That(_told, Has.Count.EqualTo(3));
        Assert.That(_told, Has.Member("GPS sentences: arriving whole again"));
        Assert.That(_told.Count(t => t.StartsWith("GPS positions: arriving with gaps")), Is.EqualTo(1));
    }
}
