// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Pipeline;

/// <summary>
/// Lock-free position estimator. The latest <see cref="PoseSnapshot"/> is
/// stored as a single atomic reference; the GPS receive thread overwrites
/// it via <see cref="Interlocked.Exchange{T}"/> and any reader gets a
/// fully-consistent record without coordinating.
/// </summary>
public sealed class PositionEstimator : IPositionEstimator
{
    private PoseSnapshot? _latest;
    private double _filteredYawRate;
    private bool _hasFilteredYaw;
    // Fixes in a row whose receiver's time was newer than the one before (FixInterval).
    private int _timedInARow;

    /// <summary>
    /// Fixes in a row that must each come with a newer receiver's time before that time is used
    /// for the time between two fixes (<see cref="FixInterval"/>): a receiver that writes its time
    /// to the whole second sends ten fixes with one time, and is never used.
    /// </summary>
    public const int TimedFixesToTrust = 10;

    private const int DayCentiseconds = 24 * 60 * 60 * 100;

    /// <summary>The longest time between two fixes taken from the receiver's clock, in 1/100 s (2 s);
    /// further apart, the clock was set or fixes were lost, and the time between arrivals stands.</summary>
    public const int MaxFixIntervalCentiseconds = 200;

    public double MaxStaleSeconds { get; set; } = 1.0;

    // Low-pass weight for the IMU-less DERIVED yaw rate. The raw rate is a single-pair
    // heading-delta derivative, so when autosteer holds a line with small steering
    // corrections it oscillates — and GetPose extrapolates the latest rate FORWARD,
    // amplifying that dither into a visible heading "bounce" / whole-map wiggle (most
    // noticeable approaching the headland, where the line-follow correction is busiest).
    // EMA-damping the rate stops the forward prediction from chasing each correction,
    // while still ramping up within a few samples for a real sustained turn. Only the
    // derived (no-IMU) path is filtered; a real IMU yaw rate is used as-is.
    public double YawRateFilterAlpha { get; set; } = 0.3;

    public void UpdateFromGps(PoseSnapshot snapshot)
    {
        // Derive yaw rate from the heading delta versus the previous snapshot
        // when none was supplied (e.g., the internal simulator has no IMU and
        // passes YawRate=0). Without this, GetPose dead-reckons heading as
        // constant between samples and snaps to the new value at each
        // arrival. That snap propagates into the hitch's lateral position
        // (which depends on heading × hitch length) and from there into the
        // dead-reckoned tool, producing visible per-sample jitter while
        // turning. Using the heading-delta-derived yaw rate makes heading
        // ramp smoothly across the inter-sample interval, killing the snap.
        var prev = Volatile.Read(ref _latest);
        double fixInterval = FixInterval(prev, snapshot);
        if (prev is not null && Math.Abs(snapshot.YawRateRadPerSec) < 1e-9)
        {
            // The heading changed over the time between the two positions, which is the receiver's
            // to say. A snapshot's own stamp is when the GPS cycle published it: after a cycle
            // that ran long the next fix is published soon after, and two fixes that reach the
            // cycle together (a job harness feeding fixes faster than real time) are stamped
            // almost at one instant, yet each is a tenth of a second of driving on from the one
            // before. Over the time between those stamps the rate came out many times too high in
            // a bend, and the pose dead-reckoned from it until the next fix turned the implement
            // with it: in a job harness run an 8 m front boom was placed 23 degrees round, its end
            // 0.85 m "in" an obstacle the boom was 1 m clear of.
            double dt = fixInterval > 0
                ? fixInterval
                : Clock.Current.ElapsedSeconds(prev.TimestampTicks, snapshot.TimestampTicks);
            if (dt > 1e-6)
            {
                double dh = snapshot.Heading - prev.Heading;
                while (dh > Math.PI) dh -= 2 * Math.PI;
                while (dh < -Math.PI) dh += 2 * Math.PI;
                double rawYaw = dh / dt;
                // EMA-damp the derived rate (see YawRateFilterAlpha) so a forward
                // extrapolation in GetPose can't amplify autosteer line-holding dither.
                _filteredYawRate = _hasFilteredYaw
                    ? _filteredYawRate + YawRateFilterAlpha * (rawYaw - _filteredYawRate)
                    : rawYaw;
                _hasFilteredYaw = true;
                snapshot = snapshot with { YawRateRadPerSec = _filteredYawRate };
            }
        }
        else if (Math.Abs(snapshot.YawRateRadPerSec) >= 1e-9)
        {
            // A real IMU yaw rate arrived — drop the derived-rate filter state so a later
            // return to the derived path doesn't resume from a stale value.
            _hasFilteredYaw = false;
        }
        Interlocked.Exchange(ref _latest, snapshot);
    }

    /// <summary>
    /// The seconds between two fixes by the receiver's own clock (<see cref="PoseSnapshot.FixTimeCentiseconds"/>),
    /// or 0 when it is not to be used and the time between their arrivals stands, as before: a fix
    /// with no time, a time that is not newer (the same fix again, an older one, a receiver that does
    /// not know the time yet) or more than <see cref="MaxFixIntervalCentiseconds"/> on (a clock that
    /// was set, fixes lost), and the first <see cref="TimedFixesToTrust"/> fixes after any of those.
    /// The first rate measured by the receiver's time starts the rate's filter afresh.
    /// </summary>
    private double FixInterval(PoseSnapshot? prev, PoseSnapshot now)
    {
        if (prev is null || prev.FixTimeCentiseconds < 0 || now.FixTimeCentiseconds < 0)
        {
            _timedInARow = 0;
            return 0;
        }
        int apart = now.FixTimeCentiseconds - prev.FixTimeCentiseconds;
        // Midnight: the time of day starts again, which is newer.
        if (apart < -DayCentiseconds / 2) apart += DayCentiseconds;
        // Not newer, or further on than fixes are apart: a receiver that sets its clock (the time
        // from the satellites after a start on its own clock) jumps on by seconds or hours, and over
        // that the turn in a tenth of a second would read as no turn at all.
        if (apart <= 0 || apart > MaxFixIntervalCentiseconds)
        {
            _timedInARow = 0;
            return 0;
        }
        if (_timedInARow < TimedFixesToTrust)
        {
            _timedInARow++;
            return 0;
        }
        if (_timedInARow == TimedFixesToTrust)
        {
            // The first rate by the receiver's time starts the filter afresh: what it holds was
            // measured over arrivals, and a rate read many times too high there would take a
            // second or two more to fade.
            _timedInARow++;
            _hasFilteredYaw = false;
        }
        return apart / 100.0;
    }

    public PoseSnapshot? GetLatestSnapshot() => Volatile.Read(ref _latest);

    public InterpolatedPose GetPose(long nowTicks)
    {
        var snapshot = Volatile.Read(ref _latest);
        if (snapshot is null)
            return default;

        double dt = Clock.Current.ElapsedSeconds(snapshot.TimestampTicks, nowTicks);

        // Clamp to non-negative — a clock skew or out-of-order read shouldn't
        // rewind the prediction.
        if (dt < 0) dt = 0;

        // Cap dead reckoning to MaxStaleSeconds so long GPS dropouts don't
        // produce wild predictions; the watchdog upstream is the real safety.
        if (dt > MaxStaleSeconds) dt = MaxStaleSeconds;

        double heading = snapshot.Heading + snapshot.YawRateRadPerSec * dt;
        double dx = Math.Sin(heading) * snapshot.SpeedMps * dt;
        double dy = Math.Cos(heading) * snapshot.SpeedMps * dt;

        return new InterpolatedPose(
            new Vec2(snapshot.Position.Easting + dx, snapshot.Position.Northing + dy),
            heading,
            snapshot.SpeedMps,
            snapshot.Roll);
    }
}
