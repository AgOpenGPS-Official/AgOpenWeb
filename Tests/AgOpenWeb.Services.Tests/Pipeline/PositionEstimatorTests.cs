// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Services.Pipeline;

namespace AgOpenWeb.Services.Tests.Pipeline;

[TestFixture]
public class PositionEstimatorTests
{
    [Test]
    public void GetPose_NoSnapshot_ReturnsDefault()
    {
        var estimator = new PositionEstimator();

        var pose = estimator.GetPose(Clock.Current.GetTimestamp());

        Assert.That(pose, Is.EqualTo(default(InterpolatedPose)));
    }

    [Test]
    public void GetLatestSnapshot_NoSnapshot_ReturnsNull()
    {
        var estimator = new PositionEstimator();

        Assert.That(estimator.GetLatestSnapshot(), Is.Null);
    }

    [Test]
    public void UpdateFromGps_RoundTrips_LatestSnapshot()
    {
        var estimator = new PositionEstimator();
        var snapshot = MakeSnapshot();

        estimator.UpdateFromGps(snapshot);

        Assert.That(estimator.GetLatestSnapshot(), Is.SameAs(snapshot));
    }

    [Test]
    public void GetPose_AtSnapshotTime_ReturnsSnapshotPose()
    {
        var estimator = new PositionEstimator();
        var snapshot = MakeSnapshot(
            position: new Vec2(100, 200),
            heading: 0.5,
            speedMps: 10,
            yawRate: 0.1,
            roll: 0.02);
        estimator.UpdateFromGps(snapshot);

        var pose = estimator.GetPose(snapshot.TimestampTicks);

        Assert.That(pose.Position.Easting, Is.EqualTo(100).Within(1e-9));
        Assert.That(pose.Position.Northing, Is.EqualTo(200).Within(1e-9));
        Assert.That(pose.Heading, Is.EqualTo(0.5).Within(1e-9));
        Assert.That(pose.SpeedMps, Is.EqualTo(10).Within(1e-9));
        Assert.That(pose.Roll, Is.EqualTo(0.02).Within(1e-9));
    }

    [Test]
    public void GetPose_StraightLineNorth_AdvancesPositionByVelocity()
    {
        // Heading=0 means due north (heading convention: 0=N, sin→E, cos→N).
        var estimator = new PositionEstimator();
        var t0 = Clock.Current.GetTimestamp();
        estimator.UpdateFromGps(MakeSnapshot(
            position: new Vec2(0, 0),
            heading: 0,
            speedMps: 10,
            yawRate: 0,
            timestampTicks: t0));

        var t1 = t0 + TicksFromSeconds(0.05); // 50 ms later
        var pose = estimator.GetPose(t1);

        // 10 m/s × 0.05 s = 0.5 m due north
        Assert.That(pose.Position.Easting, Is.EqualTo(0).Within(1e-9));
        Assert.That(pose.Position.Northing, Is.EqualTo(0.5).Within(1e-9));
        Assert.That(pose.Heading, Is.EqualTo(0).Within(1e-9));
    }

    [Test]
    public void GetPose_StraightLineEast_AdvancesPositionByVelocity()
    {
        // Heading=π/2 (90°) means due east.
        var estimator = new PositionEstimator();
        var t0 = Clock.Current.GetTimestamp();
        estimator.UpdateFromGps(MakeSnapshot(
            position: new Vec2(0, 0),
            heading: Math.PI / 2,
            speedMps: 10,
            yawRate: 0,
            timestampTicks: t0));

        var t1 = t0 + TicksFromSeconds(0.05);
        var pose = estimator.GetPose(t1);

        Assert.That(pose.Position.Easting, Is.EqualTo(0.5).Within(1e-9));
        Assert.That(pose.Position.Northing, Is.EqualTo(0).Within(1e-6));
    }

    [Test]
    public void GetPose_WithYawRate_AdvancesHeading()
    {
        var estimator = new PositionEstimator();
        var t0 = Clock.Current.GetTimestamp();
        estimator.UpdateFromGps(MakeSnapshot(
            position: new Vec2(0, 0),
            heading: 0,
            speedMps: 0,        // Stationary, isolate heading prediction.
            yawRate: 1.0,       // 1 rad/s
            timestampTicks: t0));

        var t1 = t0 + TicksFromSeconds(0.1); // 100 ms later
        var pose = estimator.GetPose(t1);

        Assert.That(pose.Heading, Is.EqualTo(0.1).Within(1e-9));
        Assert.That(pose.Position.Easting, Is.EqualTo(0).Within(1e-9));
        Assert.That(pose.Position.Northing, Is.EqualTo(0).Within(1e-9));
    }

    [Test]
    public void GetPose_NegativeDt_ClampsToSnapshotPose()
    {
        // Out-of-order or skewed clock read should not rewind the prediction.
        var estimator = new PositionEstimator();
        var t0 = Clock.Current.GetTimestamp();
        estimator.UpdateFromGps(MakeSnapshot(
            position: new Vec2(50, 50),
            heading: 0,
            speedMps: 10,
            yawRate: 0,
            timestampTicks: t0));

        var pose = estimator.GetPose(t0 - TicksFromSeconds(0.05));

        Assert.That(pose.Position.Easting, Is.EqualTo(50).Within(1e-9));
        Assert.That(pose.Position.Northing, Is.EqualTo(50).Within(1e-9));
    }

    [Test]
    public void GetPose_BeyondMaxStaleSeconds_ClampsForwardTime()
    {
        // GPS dropout: once dt exceeds MaxStaleSeconds, don't keep predicting.
        var estimator = new PositionEstimator { MaxStaleSeconds = 0.5 };
        var t0 = Clock.Current.GetTimestamp();
        estimator.UpdateFromGps(MakeSnapshot(
            position: new Vec2(0, 0),
            heading: 0,
            speedMps: 10,
            yawRate: 0,
            timestampTicks: t0));

        var pose = estimator.GetPose(t0 + TicksFromSeconds(5.0));

        // Capped at 0.5 s × 10 m/s = 5 m, not 50 m.
        Assert.That(pose.Position.Northing, Is.EqualTo(5.0).Within(1e-6));
    }

    [Test]
    public void UpdateFromGps_ConcurrentReadersGetConsistentSnapshot()
    {
        // Stress: one writer flips between two snapshots while many readers
        // pull. Each reader must observe a fully-consistent record (never a
        // mix of fields from snapshot A and snapshot B).
        var estimator = new PositionEstimator();
        var snapA = MakeSnapshot(position: new Vec2(1, 2), heading: 0.1,
                                  speedMps: 1, yawRate: 0, roll: 0.01,
                                  timestampTicks: 1000);
        var snapB = MakeSnapshot(position: new Vec2(3, 4), heading: 0.2,
                                  speedMps: 2, yawRate: 0, roll: 0.02,
                                  timestampTicks: 2000);
        estimator.UpdateFromGps(snapA);

        using var cts = new CancellationTokenSource();
        int torn = 0;
        var readers = new Task[8];
        for (int i = 0; i < readers.Length; i++)
        {
            readers[i] = Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    var s = estimator.GetLatestSnapshot();
                    if (s is null) continue;
                    bool isA = s.Position.Easting == 1 && s.Position.Northing == 2 &&
                               s.Heading == 0.1 && s.SpeedMps == 1 && s.Roll == 0.01 &&
                               s.TimestampTicks == 1000;
                    bool isB = s.Position.Easting == 3 && s.Position.Northing == 4 &&
                               s.Heading == 0.2 && s.SpeedMps == 2 && s.Roll == 0.02 &&
                               s.TimestampTicks == 2000;
                    if (!isA && !isB)
                        Interlocked.Increment(ref torn);
                }
            });
        }

        // Writer flips snapshots for ~50 ms.
        var writerEnd = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 0.05);
        bool useA = false;
        while (Stopwatch.GetTimestamp() < writerEnd)
        {
            estimator.UpdateFromGps(useA ? snapA : snapB);
            useA = !useA;
        }
        cts.Cancel();
        Task.WaitAll(readers);

        Assert.That(torn, Is.EqualTo(0),
            "Readers must always see a fully-consistent snapshot record");
    }

    // ── The time between two fixes is the receiver's ──────────────────────────

    private const double StepRad = -0.72 * Math.PI / 180;   // a 7.2 deg/s bend, read ten times a second
    private const double BendRate = StepRad / 0.1;

    /// <summary>A tractor in a steady bend, its fixes a tenth of a second apart on the receiver's clock.</summary>
    private sealed class Bend
    {
        public readonly PositionEstimator Estimator = new();
        public long Ticks = TicksFromSeconds(1000);
        public double Heading = 5.5;
        public int FixTime = (12 * 3600 + 34 * 60 + 56) * 100;
        /// <summary>False: the fixes carry no time (the simulator's).</summary>
        public bool Timed = true;

        /// <summary><paramref name="count"/> fixes reaching the app <paramref name="arrivalSeconds"/> apart.</summary>
        public void Fixes(int count, double arrivalSeconds, Action? afterEach = null)
        {
            for (int i = 0; i < count; i++)
            {
                Ticks += TicksFromSeconds(arrivalSeconds);
                Heading += StepRad;
                FixTime = (FixTime + 10) % (24 * 360000);
                Estimator.UpdateFromGps(new PoseSnapshot(new Vec2(0, 0), Heading, 2.5, 0, 0, Ticks, Timed ? FixTime : -1));
                afterEach?.Invoke();
            }
        }

        public double Rate => Estimator.GetLatestSnapshot()!.YawRateRadPerSec;
    }

    [Test]
    public void Fixes_that_arrive_together_are_still_a_tenth_of_a_second_apart()
    {
        // A job harness run: fixes fed ten at a time, then a pause. Over the time between their
        // arrivals the bend read 7.2 deg/s as some 450, and the pose predicted through the pause
        // turned an 8 m front boom 23 degrees round, into an obstacle it was 1 m clear of. The
        // fix published soon after a cycle that ran long is the same case, in small.
        var bend = new Bend();
        bend.Fixes(20, 0.1);
        Assert.That(bend.Rate, Is.EqualTo(BendRate).Within(Math.Abs(BendRate) * 0.01), "a steady 10 Hz");

        bend.Fixes(10, 0.001, () =>
            Assert.That(bend.Rate, Is.EqualTo(BendRate).Within(Math.Abs(BendRate) * 0.01),
                "a fix that came 1 ms after the one before is still a tenth of a second of driving on"));

        var pose = bend.Estimator.GetPose(bend.Ticks + TicksFromSeconds(0.076));
        double turnedDeg = (pose.Heading - bend.Heading) * 180 / Math.PI;
        Assert.That(turnedDeg, Is.EqualTo(-7.2 * 0.076).Within(0.05),
            "76 ms after the last fix the tractor has turned half a degree, not 23");
    }

    [Test]
    public void With_no_time_on_the_fixes_the_rate_is_over_their_arrivals_as_before()
    {
        var bend = new Bend { Timed = false };
        bend.Fixes(20, 0.1);
        Assert.That(bend.Rate, Is.EqualTo(BendRate).Within(Math.Abs(BendRate) * 0.01));

        // Half the time apart is twice the rate.
        bend.Fixes(20, 0.05);
        Assert.That(bend.Rate, Is.EqualTo(2 * BendRate).Within(Math.Abs(BendRate) * 0.02));
    }

    [Test]
    public void The_receiver_s_time_is_used_after_ten_fixes_in_a_row_each_newer_than_the_last()
    {
        var bend = new Bend();
        // The first fix has none before it, the next ten are counted: all over their arrivals.
        bend.Fixes(1 + PositionEstimator.TimedFixesToTrust, 0.05);
        Assert.That(bend.Rate, Is.EqualTo(2 * BendRate).Within(Math.Abs(BendRate) * 0.02),
            "not yet: the time between arrivals, as before");

        // The next one is: its rate starts the filter afresh, with nothing left of the rate
        // measured over arrivals.
        bend.Fixes(1, 0.05);
        Assert.That(bend.Rate, Is.EqualTo(BendRate).Within(Math.Abs(BendRate) * 0.001),
            "from then on the receiver's own tenth of a second");
        bend.Fixes(30, 0.05, () => Assert.That(bend.Rate, Is.EqualTo(BendRate).Within(Math.Abs(BendRate) * 0.001)));
    }

    [Test]
    public void A_receiver_that_writes_its_time_to_the_whole_second_is_never_used()
    {
        // Ten fixes with one time, then the next second: nine intervals of nothing and one of a
        // whole second. Used, the rate would fall to a tenth at every second's first fix.
        var estimator = new PositionEstimator();
        long ticks = TicksFromSeconds(1000);
        double heading = 5.5;
        int second = (12 * 3600 + 34 * 60 + 56) * 100;
        for (int i = 0; i < 60; i++)
        {
            ticks += TicksFromSeconds(0.1);
            heading += StepRad;
            estimator.UpdateFromGps(new PoseSnapshot(new Vec2(0, 0), heading, 2.5, 0, 0, ticks, second + i / 10 * 100));
            if (i > 0)
                Assert.That(estimator.GetLatestSnapshot()!.YawRateRadPerSec,
                    Is.EqualTo(BendRate).Within(Math.Abs(BendRate) * 0.01), $"fix {i}");
        }
    }

    [Test]
    public void Midnight_is_a_newer_time()
    {
        var bend = new Bend { FixTime = 24 * 360000 - 250 };   // 23:59:57.50
        bend.Fixes(20, 0.1);
        // 23:59:59.50 now: the ten that arrive together cross midnight.
        bend.Fixes(10, 0.001, () =>
            Assert.That(bend.Rate, Is.EqualTo(BendRate).Within(Math.Abs(BendRate) * 0.01)));
        Assert.That(bend.FixTime, Is.EqualTo(50), "00:00:00.50");
    }

    [Test]
    public void A_clock_that_jumps_on_is_not_the_time_between_two_fixes()
    {
        // A receiver that starts on its own clock and then takes the satellites' time jumps on by
        // seconds or hours between two fixes a tenth of a second apart. Over that jump the bend
        // would read as no turn at all: the arrivals stand for it, and the receiver's time is
        // used again once ten fixes have followed each other on the new time.
        var bend = new Bend();
        bend.Fixes(20, 0.1);
        Assert.That(bend.Rate, Is.EqualTo(BendRate).Within(Math.Abs(BendRate) * 0.01));

        bend.FixTime += 3 * 360000;   // three hours on
        bend.Fixes(1, 0.1);
        Assert.That(bend.Rate, Is.EqualTo(BendRate).Within(Math.Abs(BendRate) * 0.01),
            "the heading's step over the tenth of a second it took to arrive, not over three hours");

        bend.FixTime += PositionEstimator.MaxFixIntervalCentiseconds;   // 2.1 s on with the next fix's tenth
        bend.Fixes(1, 0.1);
        Assert.That(bend.Rate, Is.EqualTo(BendRate).Within(Math.Abs(BendRate) * 0.01), "2.1 s on is a jump too");

        // On the new time, arriving together: over arrivals until ten have followed each other.
        bend.Fixes(PositionEstimator.TimedFixesToTrust, 0.001);
        Assert.That(Math.Abs(bend.Rate), Is.GreaterThan(Math.Abs(BendRate) * 10), "counted anew after the jump");
        bend.Fixes(1, 0.001);
        Assert.That(bend.Rate, Is.EqualTo(BendRate).Within(Math.Abs(BendRate) * 0.01), "the receiver's time again");
    }

    [Test]
    public void Fixes_up_to_two_seconds_apart_are_timed_by_the_receiver()
    {
        // A receiver at 1 Hz, or fixes left out while a cycle ran: still the receiver's time.
        var bend = new Bend();
        bend.Fixes(20, 0.1);
        bend.FixTime += PositionEstimator.MaxFixIntervalCentiseconds - 10;   // 2.0 s on with the next fix's tenth
        bend.Fixes(1, 0.001);
        double over2s = StepRad / 2.0;
        Assert.That(bend.Rate, Is.EqualTo(BendRate + 0.3 * (over2s - BendRate)).Within(Math.Abs(BendRate) * 0.01),
            "the heading's step over the receiver's 2 s, not the 1 ms it took to arrive");
    }

    [Test]
    public void The_same_fix_again_is_not_a_tenth_of_a_second_on()
    {
        // A module heard twice (two links): the same time again is no interval, and the fixes
        // after it are counted anew before the receiver's time is used again.
        var bend = new Bend();
        bend.Fixes(20, 0.1);
        var last = bend.Estimator.GetLatestSnapshot()!;
        bend.Estimator.UpdateFromGps(last with { TimestampTicks = last.TimestampTicks + TicksFromSeconds(0.1), YawRateRadPerSec = 0 });
        Assert.That(bend.Estimator.GetLatestSnapshot()!.YawRateRadPerSec, Is.EqualTo(0.7 * BendRate).Within(Math.Abs(BendRate) * 0.01),
            "no turn over the tenth of a second it took to arrive: the filter's step towards 0");

        bend.Ticks = last.TimestampTicks + TicksFromSeconds(0.1);
        bend.Fixes(40, 0.1);
        Assert.That(bend.Rate, Is.EqualTo(BendRate).Within(Math.Abs(BendRate) * 0.01));
    }

    private static long TicksFromSeconds(double seconds)
        => (long)(seconds * Stopwatch.Frequency);

    private static PoseSnapshot MakeSnapshot(
        Vec2? position = null,
        double heading = 0,
        double speedMps = 0,
        double yawRate = 0,
        double roll = 0,
        long? timestampTicks = null)
        => new(
            position ?? new Vec2(0, 0),
            heading,
            speedMps,
            yawRate,
            roll,
            timestampTicks ?? Clock.Current.GetTimestamp());
}
