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
/// with the dual heading standing in for the IMU. Only a fix that carries a
/// dual-antenna heading ($PAOGI) is used this way: a $PANDA fix's heading is the
/// IMU heading, so with Dual on it takes the Fix path, exactly as with Dual off,
/// and <see cref="IsDualHeadingMissing"/> is raised (#157). AgIO never puts
/// PANDA's heading in headingTrueDual.</item>
/// </list>
///
/// Reverse detection (#125), when <see cref="ConnectionConfig.ReverseDetection"/> is
/// on: with an IMU, a fix-to-fix heading more than 90° off the IMU heading means
/// reversing; without one, a filtered fix-to-fix change of more than 90° does (and
/// while that filter is still settling, <see cref="IsChangingDirection"/> holds the
/// heading). In Dual the travel over <see cref="ConnectionConfig.DualReverseDistance"/>
/// is compared with the antenna heading. While reversing the heading is flipped so it
/// still points the way the vehicle faces.
///
/// Not ported: the forward/reverse steer-angle heading compensation. AgOpenGPS
/// also corrects the fix for antenna offset and roll before taking the fix-to-fix
/// heading; AgOpenWeb applies that transform after this stage, on the raw fixes.
/// </summary>
public class GpsHeadingFusionService : IGpsHeadingFusionService
{
    private const int TotalFixSteps = 10;           // AgOpenGPS totalFixSteps
    private const double StartSpeedKmh = 1.5;       // no first heading below this (AgOpenGPS)
    private const double HalfPi = Math.PI / 2;      // AgOpenGPS uses 1.57
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
    private bool _hasHeading;        // a Fix or Dual heading has been output at least once
    private StepFix _startFix;       // where the run towards a first heading began
    private bool _lastWasDual;       // the previous fix took its heading from the dual antenna
    private volatile bool _resetDirectionRequested;
    private double _gpsHeading;      // radians, last fix-to-fix heading
    private double _fixHeading;      // radians, the output
    private double _imuGpsOffset;    // radians, IMU → GPS alignment
    private double _filteredDelta;   // radians, no-IMU reverse filter
    private bool _isReverseWithImu;
    private bool _hasReverseFix;
    private double _reverseFixE, _reverseFixN;

    /// <summary>Last fix-to-fix GPS heading, degrees (AgOpenGPS gpsHeading) — heading chart.</summary>
    public double GpsHeadingDeg => _gpsHeading * 180.0 / Math.PI;

    /// <summary>IMU heading + offset, degrees, or NaN with no IMU (AgOpenGPS imuCorrected).</summary>
    public double ImuCorrectedDeg { get; private set; } = double.NaN;

    /// <summary>True while the vehicle is detected as reversing (#125).</summary>
    public bool IsReverse { get; private set; }

    /// <summary>True while a single antenna with no IMU can't yet tell whether the
    /// vehicle changed direction; AgOpenGPS stops steering meanwhile.</summary>
    public bool IsChangingDirection { get; private set; }

    /// <summary>True while Dual GPS is on but the fixes carry no dual-antenna
    /// heading ($PANDA), so the single-antenna heading is used instead (#157).</summary>
    public bool IsDualHeadingMissing { get; private set; }

    public double FuseHeading(double gpsHeading, double imuHeading, bool imuValid,
                              double speedMs, double easting, double northing,
                              bool hasDualHeading)
    {
        var con = Connections;
        double speedKmh = Math.Abs(speedMs) * 3.6;

        // Dual GPS on, but this fix has no antenna heading ($PANDA: the heading field
        // is the IMU's). Don't treat it as one: fall through to Fix, as with Dual off.
        bool wasMissing = IsDualHeadingMissing;
        IsDualHeadingMissing = con.IsDualGps && !hasDualHeading;
        if (IsDualHeadingMissing && !wasMissing)
        {
            // The dual path marked the heading as started without learning the IMU
            // offset; restart Fix so the offset snaps onto the first fix-to-fix heading.
            // Otherwise the raw IMU heading meets the travel heading and reads as reverse.
            _hasReverseFix = false;
            _isFirstHeadingSet = false;
            Reset();
        }

        // Reset Direction (the operator tapped the vehicle): start over as if no heading
        // had been learned. Applied here, on the thread that runs the fusion.
        if (_resetDirectionRequested)
        {
            _resetDirectionRequested = false;
            if (!_lastWasDual)
            {
                _isFirstHeadingSet = false;
                IsReverse = _isReverseWithImu = IsChangingDirection = false;
                _filteredDelta = 0;
                Reset();
            }
        }

        // The IMU heading this fix, if any (radians).
        double? imu = imuValid ? ToRad(imuHeading) : null;
        ImuCorrectedDeg = imu is double ir ? Wrap(ir + _imuGpsOffset) * 180.0 / Math.PI : double.NaN;

        bool useFix = true;
        if (con.IsDualGps && hasDualHeading)
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
                IsChangingDirection = false;
                DetectDualReverse(dual, easting, northing, con.DualReverseDistance);
                _isFirstHeadingSet = _hasHeading = true;
                _lastWasDual = true;
                _fixHeading = _gpsHeading = dual;
                PushStep(easting, northing);
                return Output(_fixHeading);
            }
        }

        // ── Fix ──────────────────────────────────────────────────────────
        _lastWasDual = false;
        if (!_isFirstHeadingSet)
        {
            // Too slow to tell a direction: the run towards a first heading starts again
            // from wherever the vehicle next gets going, not from where it last stood.
            if (speedKmh < StartSpeedKmh) _startFix = default;
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

        if (imu is double imuRad)
        {
            // Reverse: travel more than 90° off where the IMU says we face.
            IsChangingDirection = false;
            if (con.ReverseDetection
                && Math.Abs(AngleDelta(Wrap(imuRad + _imuGpsOffset), newHeading)) > HalfPi)
            {
                IsReverse = _isReverseWithImu = true;
                newHeading = Wrap(newHeading + Math.PI);
            }
            else
            {
                IsReverse = _isReverseWithImu = false;
            }
            _gpsHeading = newHeading;

            // Nudge the IMU offset toward GPS by the fusion weight (a slow 0.02
            // while reversing, as AgOpenGPS does).
            double w = _isReverseWithImu ? 0.02 : FusionWeight;
            _imuGpsOffset = Wrap(_imuGpsOffset + AngleDelta(imuRad + _imuGpsOffset, _gpsHeading) * w);
            _fixHeading = Wrap(imuRad + _imuGpsOffset);
        }
        else
        {
            if (con.ReverseDetection)
            {
                // Reverse without an IMU: the fix-to-fix heading turns ~180° against
                // the last one. Filter it; while the filter hasn't caught up with
                // the latest change we can't tell yet, so hold the heading.
                double delta = Math.Abs(AngleDelta(_gpsHeading, newHeading));
                _filteredDelta = delta * 0.2 + _filteredDelta * 0.8;
                IsChangingDirection = Math.Abs(_filteredDelta - delta) > 0.5;
                if (IsChangingDirection)
                    return ByPass(gpsHeading, imu);   // not stored, like AgOpenGPS

                IsReverse = _filteredDelta > HalfPi;
                if (IsReverse) newHeading = Wrap(newHeading + Math.PI);
            }
            else
            {
                IsReverse = IsChangingDirection = false;
            }
            _fixHeading = _gpsHeading = newHeading;
        }

        PushStep(easting, northing);
        _hasHeading = true;
        return Output(_fixHeading);
    }

    // Dual reverse (AgOpenGPS): every DualReverseDistance of travel, compare the
    // direction moved with the antenna heading; more than ~115° apart = reversing.
    private void DetectDualReverse(double dualHeading, double easting, double northing, double distance)
    {
        if (!_hasReverseFix)
        {
            _reverseFixE = easting; _reverseFixN = northing; _hasReverseFix = true;
            return;
        }
        if (Dist2(new StepFix { E = _reverseFixE, N = _reverseFixN }, easting, northing) <= Sq(distance))
            return;
        double moved = Wrap(Math.Atan2(easting - _reverseFixE, northing - _reverseFixN));
        IsReverse = Math.Abs(AngleDelta(dualHeading, moved)) > 2.0;
        _reverseFixE = easting; _reverseFixN = northing;
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
        _hasReverseFix = false;
        _startFix = default;
    }

    /// <summary>
    /// Learn the direction again from the next forward travel: forget the first heading,
    /// the reverse state and the stored fixes, as AgOpenGPS does when the vehicle is tapped
    /// ("Reset Direction, drive forward"). For when the first heading was taken while the
    /// vehicle was backing: with an IMU every forward metre after that reads as reverse
    /// until this is done. The heading on screen holds until the new one is learned.
    /// Ignored while the heading comes from the dual antenna. Safe to call from any thread;
    /// it takes effect on the next fix.
    /// </summary>
    public void ResetDirection() => _resetDirectionRequested = true;

    // First heading: from where this run above the start speed began to the first fix far
    // enough from it (the same reach a fix-to-fix heading needs); the IMU offset is snapped
    // straight onto it (AgOpenGPS "Start").
    //
    // AgOpenGPS takes it from three consecutive fixes whatever their spacing. At 10 Hz and
    // the 1.5 km/h start speed that is 8 cm of travel, which GPS noise can turn into a
    // heading pointing backwards; with an IMU the offset then locks 180° out and forward
    // travel reads as reverse from there on (PR #249).
    private bool TryStartHeading(double easting, double northing, double? imu)
    {
        if (!_startFix.IsSet)
        {
            _startFix = new StepFix { E = easting, N = northing, IsSet = true };
            PushStep(easting, northing);
            return false;
        }

        if (Dist2(_startFix, easting, northing) < Sq(Connections.FixToFixDistance) * 0.5)
        {
            // Keep the step history filling, as it will be needed after the start.
            if (Dist2(_steps[0], easting, northing) >= Sq(Connections.MinGpsStep))
                PushStep(easting, northing);
            return false;
        }

        PushStep(easting, northing);
        _gpsHeading = Wrap(Math.Atan2(easting - _startFix.E, northing - _startFix.N));
        _fixHeading = _gpsHeading;
        if (imu is double imuRad)
        {
            _imuGpsOffset = Wrap(AngleDelta(imuRad, _gpsHeading));
            _fixHeading = Wrap(imuRad + _imuGpsOffset);
        }
        _isFirstHeadingSet = _hasHeading = true;
        return true;
    }

    // No new fix-to-fix heading this time. With an IMU, heading follows it (plus the
    // offset); otherwise hold the last heading, or pass the sentence heading through
    // until there has ever been one. A restart (Dual → $PANDA, #157) forgets the stored
    // fixes but not the heading: with no IMU, $PANDA's heading field is 0, and showing
    // it would swing the tractor to north at every stop until the next fix-to-fix heading.
    private double ByPass(double sentenceHeadingDeg, double? imu)
    {
        if (imu is double imuRad)
            _fixHeading = Wrap(imuRad + _imuGpsOffset);
        else if (!_hasHeading)
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
