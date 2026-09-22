// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Gps;

/// <summary>
/// Vehicle heading from the GPS fix, dual antenna and IMU, ported from AgOpenGPS
/// <c>FormGPS.UpdateFixPosition</c> (Position.designer.cs, the "Fix" and "Dual"
/// heading sources) so a rig set up in AgOpenGPS steers the same here (#112).
///
/// <list type="bullet">
/// <item><b>Fix</b> (single antenna): keeps the last <see cref="TotalFixSteps"/>
/// fixes that were at least <see cref="ConnectionConfig.MinGpsStep"/> apart, and
/// takes the heading from the newest fix back to the first stored fix at least
/// <see cref="ConnectionConfig.FixToFixDistance"/> away. With an IMU, the IMU
/// heading plus an offset is used; each new GPS heading nudges that offset by the
/// fusion weight, so the IMU is slowly pulled onto GPS.</item>
/// <item><b>Dual</b>: the dual-antenna heading plus
/// <see cref="ConnectionConfig.DualHeadingOffset"/>. With
/// <see cref="ConnectionConfig.AutoDualFix"/> on and the speed above
/// <see cref="ConnectionConfig.DualSwitchSpeed"/> (km/h), it switches to Fix
/// with the dual heading standing in for the IMU.</item>
/// </list>
///
/// Not ported here (tracked in #125): reverse detection and the forward/reverse
/// steer-angle heading compensation. AgOpenGPS also corrects the fix for antenna
/// offset and roll before taking the fix-to-fix heading; AgOpenWeb applies that
/// transform after this stage, on the raw antenna fixes.
/// </summary>
public class GpsHeadingFusionService : IGpsHeadingFusionService
{
    private const int TotalFixSteps = 10;           // AgOpenGPS totalFixSteps
    private const double StartSpeedKmh = 1.5;       // no first heading below this (AgOpenGPS)
    /// <summary>The web slider stores the GPS share, 0–1. AgOpenGPS's slider is GPS %
    /// 0–100 with weight = % × 0.002, so weight = share × 0.2 (default 0.3 → 0.06).</summary>
    public const double FusionShareToWeight = 0.2;

    private readonly ConfigurationStore _configStore;

    public GpsHeadingFusionService(ConfigurationStore configStore)
    {
        _configStore = configStore;
    }

    private ConnectionConfig Connections => _configStore.Connections;

    private struct StepFix { public double E, N; public bool IsSet; }
    private readonly StepFix[] _steps = new StepFix[TotalFixSteps];

    private bool _isFirstHeadingSet;
    private double _gpsHeading;      // radians, last fix-to-fix heading
    private double _fixHeading;      // radians, the output
    private double _imuGpsOffset;    // radians, IMU → GPS alignment

    public double FuseHeading(double gpsHeading, double imuHeading, bool imuValid,
                              double speedMs, double easting, double northing)
    {
        var con = Connections;
        double speedKmh = Math.Abs(speedMs) * 3.6;

        // The IMU heading this fix, if any (radians).
        double? imu = imuValid ? ToRad(imuHeading) : null;

        bool useFix = true;
        if (con.IsDualGps)
        {
            double dual = Wrap(ToRad(gpsHeading + con.DualHeadingOffset));
            if (con.AutoDualFix && speedKmh > con.DualSwitchSpeed)
                imu = dual;                       // dual stands in for the IMU
            else
                useFix = false;

            if (!useFix)
            {
                // Dual: the antenna heading is the heading. Keep the step history
                // current so a switch to Fix picks up smoothly (AgOpenGPS does too).
                _isFirstHeadingSet = true;
                _fixHeading = _gpsHeading = dual;
                PushStep(easting, northing);
                return Output(_fixHeading);
            }
        }

        // ── Fix ──────────────────────────────────────────────────────────
        if (!_isFirstHeadingSet)
        {
            if (speedKmh < StartSpeedKmh || !TryStartHeading(easting, northing, imu))
                return ByPass(gpsHeading, imu);
            return Output(_fixHeading);
        }

        // How far since the last stored fix. Too close → keep the current heading.
        if (!_steps[0].IsSet || Dist2(_steps[0], easting, northing) < Sq(con.MinGpsStep))
        {
            if (!_steps[0].IsSet) PushStep(easting, northing);
            return ByPass(gpsHeading, imu);
        }

        // Newest stored fix at least FixToFixDistance back.
        double minHeadingDist2 = Sq(con.FixToFixDistance);
        int stepIdx = -1;
        double d2 = 0;
        for (int i = 0; i < TotalFixSteps; i++)
        {
            if (!_steps[i].IsSet) break;
            d2 = Dist2(_steps[i], easting, northing);
            stepIdx = i;
            if (d2 > minHeadingDist2) break;
        }
        if (stepIdx < 0 || d2 < minHeadingDist2 * 0.5)
            return ByPass(gpsHeading, imu);   // not stored, like AgOpenGPS

        double newHeading = Wrap(Math.Atan2(easting - _steps[stepIdx].E, northing - _steps[stepIdx].N));
        _gpsHeading = newHeading;

        if (imu is double imuRad)
        {
            // Nudge the IMU offset toward GPS by the fusion weight.
            _imuGpsOffset = Wrap(_imuGpsOffset + AngleDelta(imuRad + _imuGpsOffset, _gpsHeading) * FusionWeight);
            _fixHeading = Wrap(imuRad + _imuGpsOffset);
        }
        else
        {
            _fixHeading = _gpsHeading;
        }

        PushStep(easting, northing);
        return Output(_fixHeading);
    }

    /// <summary>Fusion weight per GPS heading update (AgOpenGPS fusionWeight).</summary>
    private double FusionWeight => Math.Clamp(Connections.HeadingFusionWeight, 0, 1) * FusionShareToWeight;

    /// <summary>
    /// Forget the stored fixes, e.g. when the local plane changes and old fixes are
    /// in another frame. The current heading and IMU offset are kept, so heading
    /// holds until the new frame has enough travel for a fix-to-fix heading.
    /// </summary>
    public void Reset()
    {
        for (int i = 0; i < TotalFixSteps; i++) _steps[i] = default;
    }

    // First heading: three stored fixes, heading from the oldest to the newest; the
    // IMU offset is snapped straight onto it (AgOpenGPS "Start").
    private bool TryStartHeading(double easting, double northing, double? imu)
    {
        PushStep(easting, northing);
        if (!_steps[2].IsSet) return false;

        _gpsHeading = Wrap(Math.Atan2(easting - _steps[2].E, northing - _steps[2].N));
        _fixHeading = _gpsHeading;
        if (imu is double imuRad)
        {
            _imuGpsOffset = Wrap(AngleDelta(imuRad, _gpsHeading));
            _fixHeading = Wrap(imuRad + _imuGpsOffset);
        }
        _isFirstHeadingSet = true;
        return true;
    }

    // No new fix-to-fix heading this time. With an IMU, heading follows it (plus the
    // offset); otherwise hold the last heading, or pass the sentence heading through
    // until there is one.
    private double ByPass(double sentenceHeadingDeg, double? imu)
    {
        if (imu is double imuRad)
            _fixHeading = Wrap(imuRad + _imuGpsOffset);
        else if (!_isFirstHeadingSet)
            return Output(ToRad(sentenceHeadingDeg));
        return Output(_fixHeading);
    }

    private double Output(double headingRad)
    {
        double deg = headingRad * 180.0 / Math.PI;
        deg %= 360.0;
        if (deg < 0) deg += 360.0;
        return deg;
    }

    private void PushStep(double e, double n)
    {
        for (int i = TotalFixSteps - 1; i > 0; i--) _steps[i] = _steps[i - 1];
        _steps[0] = new StepFix { E = e, N = n, IsSet = true };
    }

    /// <summary>Signed smallest angle that takes <paramref name="from"/> to
    /// <paramref name="to"/>, in (-π, π] — what AgOpenGPS's gyroDelta folding computes.</summary>
    internal static double AngleDelta(double from, double to)
    {
        double d = (to - from) % (2 * Math.PI);
        if (d > Math.PI) d -= 2 * Math.PI;
        else if (d <= -Math.PI) d += 2 * Math.PI;
        return d;
    }

    private static double Wrap(double rad)
    {
        rad %= 2 * Math.PI;
        return rad < 0 ? rad + 2 * Math.PI : rad;
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
    private static double Sq(double v) => v * v;
    private static double Dist2(StepFix s, double e, double n) => Sq(e - s.E) + Sq(n - s.N);
}
