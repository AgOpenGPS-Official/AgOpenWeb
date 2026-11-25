// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.State;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Each fix's speed, trip distance and distance off the line go to the ISOBUS task controller
/// in PGN 242 (#921).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class IsobusProcessDataWiringTests
{
    private MainViewModelBuilder _b = null!;
    private MainViewModel _vm = null!;
    private readonly List<(ushort Ddi, int Value)> _sent = new();

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _b = new MainViewModelBuilder();
        _vm = _b.Build();
        _sent.Clear();
        _b.UdpService.IsTaskControllerHeard.Returns(true);
        _b.UdpService.When(u => u.SendToTaskController(Arg.Any<byte[]>())).Do(c =>
        {
            var pgn = c.Arg<byte[]>();
            if (pgn[3] == 0xF2) _sent.Add(((ushort)(pgn[5] | pgn[6] << 8), BitConverter.ToInt32(pgn, 7)));
        });
    }

    private int? Value(ushort ddi) => _sent.Where(v => v.Ddi == ddi).Select(v => (int?)v.Value).LastOrDefault();

    [Test]
    public void AFixOnALine_SendsSpeedTripDistanceAndDistanceOff()
    {
        _vm.AccumulateTripDistance(0, 0);
        _vm.AccumulateTripDistance(0, 1.5);

        _vm.SendIsobusProcessData(new GpsCycleResult
        {
            GpsValid = true, Speed = 2.5, IsReverse = false,
            Guidance = new GuidanceSnapshot { IsGuidanceActive = true, CrossTrackError = -0.0346 },
        });

        Assert.That(Value(397), Is.EqualTo(2500));
        Assert.That(Value(597), Is.EqualTo((int)(_vm.TripDistanceMeters * 1000)));
        Assert.That(_vm.TripDistanceMeters, Is.GreaterThan(0));
        Assert.That(Value(513), Is.EqualTo(-3500));
    }

    [Test]
    public void TheGuidanceServicesNoLineNumber_IsNotSentAsADeviation()
    {
        // 32000: AgOpenGPS's "no line" number, which the guidance service gives where it has no line.
        _vm.SendIsobusProcessData(new GpsCycleResult
        {
            GpsValid = true, Speed = 2.5,
            Guidance = new GuidanceSnapshot { IsGuidanceActive = true, CrossTrackError = 32000 },
        });

        Assert.That(Value(397), Is.EqualTo(2500));
        Assert.That(Value(513), Is.Null);
    }

    [Test]
    public void ReversingWithNoLine_SendsANegativeSpeedAndNoDeviation()
    {
        _vm.SendIsobusProcessData(new GpsCycleResult
        {
            GpsValid = true, Speed = 1.0, IsReverse = true,
            Guidance = new GuidanceSnapshot { IsGuidanceActive = false },
        });

        Assert.That(Value(397), Is.EqualTo(-1000));
        Assert.That(Value(513), Is.Null);
    }
}
