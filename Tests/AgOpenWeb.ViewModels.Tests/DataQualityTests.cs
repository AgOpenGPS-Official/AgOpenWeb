// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.GPS;
using AgOpenWeb.Services.Gps;
using AgOpenWeb.Services.Network;
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
    private static readonly byte[] Panda = System.Text.Encoding.ASCII.GetBytes(
        "$PANDA,162255.50,3924.90,N,00731.80,W,4,12,0.7,341.9,1.2,4.8,2217,31,-12,0.5*5A\r\n");

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
        _gps = new GpsSentenceMonitor();
        _steer = new StreamQualityMonitor("Steer module data (PGN 253)");
        _b.AutoSteerService.GpsSentences.Returns(_gps);
        _b.UdpService.ModuleStreams.Returns(new[] { _steer });
        _vm = _b.Build();
        _vm.IsSimulatorEnabled = false;   // a real receiver
        _told.Clear();
        _vm.FailureReported += _told.Add;
        _now = 100;
    }

    private static long At(double seconds) => (long)(seconds * Stopwatch.Frequency);

    /// <summary>Fixes and PGN 253 for <paramref name="seconds"/>, a fix lost wherever
    /// <paramref name="lost"/> says so, the app judging once a second.</summary>
    private void Run(double seconds, Func<int, bool>? lost = null)
    {
        int ticks = (int)(seconds * 100);
        for (int i = 1; i <= ticks; i++)
        {
            double t = _now + i * 0.01;
            if (i % 4 == 0) _steer.Record(At(t));                       // 25 Hz
            if (i % 10 == 0 && !(lost?.Invoke(i / 10) ?? false))
                _gps.Record(Panda, NmeaParseResult.Accepted, At(t));    // 10 Hz
            if (i % 100 == 0) _vm.JudgeDataQuality(At(t));
        }
        _now += seconds;
    }

    [Test]
    public void EvenStreams_NothingIsSaid()
    {
        Run(60);

        Assert.That(_told, Is.Empty);
        Assert.That(_vm.DataQuality.Select(q => q.Verdict), Is.All.EqualTo(StreamVerdict.Even));
    }

    [Test]
    public void OneFixInFiveLost_TheOperatorIsToldOnce_AndToldWhenItIsGoodAgain()
    {
        Run(60, lost: n => n % 5 == 0);

        Assert.That(_told, Has.Count.EqualTo(1), "told once");
        Assert.That(_told[0], Does.Match(@"^GPS positions: arriving with gaps, about (19|20|21)% missing \(8(\.\d)? of 10 a second\)\. "));
        Assert.That(_told[0], Does.Contain("Check the network cable to the modules"));
        Assert.That(_vm.DataQuality[0].Faulty, Is.True);
        Assert.That(_vm.DataQuality[1].Faulty, Is.False, "the steer module's stream is judged by itself");

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
        Assert.That(_vm.DataQuality[0].Verdict, Is.EqualTo(StreamVerdict.NoData));
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
}
