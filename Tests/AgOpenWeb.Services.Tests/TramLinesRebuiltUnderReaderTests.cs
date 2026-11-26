// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Tram;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// The tram lines are read on the GPS thread (AutoSteer asks <see cref="TramLineService.DetectTramWheels"/>
/// every cycle) while the host loop rebuilds them on a tram edit, a track rename or a field
/// open. The lists used to be cleared and refilled in place, so a reader in the middle of
/// one got "Collection was modified" or read past the end. Now a rebuild publishes new lists.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class TramLinesRebuiltUnderReaderTests
{
    private ConfigurationStore _config = null!;
    private TramLineService _tram = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _config = ConfigurationStore.Instance;
        _config.Vehicle.TrackWidth = 1.8;
        _config.Tool.Width = 12;
        _config.Tram.TramWidth = 12;
        _config.Tram.DisplayMode = TramDisplayMode.All;
        _tram = new TramLineService(new TramLineOffsetService(), NullLogger<TramLineService>.Instance, _config);
    }

    [TearDown]
    public void TearDown() => ConfigurationStore.SetInstance(new ConfigurationStore());

    private static List<Vec2> Line(int i) => new()
    {
        new Vec2(i * 3.0, -200), new Vec2(i * 3.0 + 0.5, 0), new Vec2(i * 3.0, 200),
    };

    private static List<Vec3> Fence() => new()
    {
        new Vec3(-150, -150, 0), new Vec3(150, -150, 0), new Vec3(150, 150, 0), new Vec3(-150, 150, 0),
    };

    [Test]
    public void ReadingTheLines_WhileTheyAreRebuilt_NeverThrows()
    {
        var errors = new ConcurrentBag<Exception>();
        var fence = Fence();
        int rebuilds = 0;
        var writer = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                while (sw.ElapsedMilliseconds < 600)
                {
                    _tram.Clear();
                    for (int i = -30; i <= 30; i++) _tram.AddTramLine(Line(i));
                    _tram.GenerateBoundaryTramTracks(fence, passCount: 2);
                    Interlocked.Increment(ref rebuilds);
                }
            }
            catch (Exception e) { errors.Add(e); }
        });

        // The GPS thread: the wheels asked against the lines, over and over.
        int reads = 0;
        while (!writer.IsCompleted)
        {
            try
            {
                double e = (reads % 200) - 100;
                var pos = new Vec3(e, (reads % 50) * 4 - 100, 0);
                _tram.IsOnTramLine(pos, 0.5);
                _tram.DistanceToNearestTramLine(pos);
                _tram.DetectTramWheels(pos, 0, 0.5);
                reads++;
            }
            catch (Exception ex) { errors.Add(ex); }
        }
        writer.Wait();

        Assert.Multiple(() =>
        {
            Assert.That(errors, Is.Empty, string.Join("\n", errors));
            Assert.That(rebuilds, Is.GreaterThan(5), "the writer rebuilt the lines while the reader read");
            Assert.That(reads, Is.GreaterThan(100), "the reader read while the writer rebuilt");
        });
    }

    [Test]
    public void AReaderKeepsTheListItTook_UntilItIsDone()
    {
        for (int i = 0; i < 5; i++) _tram.AddTramLine(Line(i));
        var seen = _tram.ParallelTramLines;

        _tram.Clear();
        _tram.AddTramLine(Line(40));

        Assert.That(seen, Has.Count.EqualTo(5), "the list a reader took is not changed under it");
        Assert.That(_tram.ParallelTramLines, Has.Count.EqualTo(1), "the next reader sees the new lines");
    }
}
