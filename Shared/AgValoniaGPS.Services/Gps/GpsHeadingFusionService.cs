// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Services.Interfaces;

namespace AgValoniaGPS.Services.Gps;

/// <summary>
/// Heading fusion extracted verbatim from <c>NmeaParserService.ProcessHeading</c>.
/// The string parser will retire in Phase B Commit 5 — this is the new home
/// for the logic, called by the cycle worker on the background thread rather
/// than inline in the parser.
///
/// Reads <see cref="ConnectionConfig"/> each call so config changes (dual
/// GPS mode toggle, offset tweak) take effect on the next cycle.
/// </summary>
public class GpsHeadingFusionService : IGpsHeadingFusionService
{
    private static ConnectionConfig Connections => ConfigurationStore.Instance.Connections;

    private double _previousEasting;
    private double _previousNorthing;
    private double _previousHeading;
    private bool _hasPreviousPosition;

    public double FuseHeading(double gpsHeading, double speedMs, double easting, double northing)
    {
        double finalHeading = gpsHeading;

        // Dual GPS mode - heading comes from dual antenna baseline.
        if (Connections.IsDualGps)
        {
            finalHeading = gpsHeading + Connections.DualHeadingOffset;

            while (finalHeading < 0) finalHeading += 360;
            while (finalHeading >= 360) finalHeading -= 360;

            // At low speed, dual-antenna heading may be unreliable — prefer fix-to-fix.
            if (speedMs < Connections.DualSwitchSpeed && _hasPreviousPosition)
            {
                double fixToFix = CalculateFixToFixHeading(easting, northing);
                if (fixToFix >= 0) finalHeading = fixToFix;
            }
        }
        else
        {
            // Single antenna — fix-to-fix when moving fast enough.
            if (speedMs >= Connections.MinGpsStep && _hasPreviousPosition)
            {
                double fixToFix = CalculateFixToFixHeading(easting, northing);
                if (fixToFix >= 0) finalHeading = fixToFix;
            }
        }

        // IMU fusion when an IMU heading is available and fusion is enabled.
        double fusionWeight = Connections.HeadingFusionWeight;
        if (fusionWeight > 0 && fusionWeight < 1.0 && SensorState.Instance.HasValidImu)
        {
            double imuHeading = SensorState.Instance.ImuHeading;

            double diff = imuHeading - finalHeading;
            if (diff > 180) diff -= 360;
            if (diff < -180) diff += 360;

            // fusionWeight = GPS weight, (1 - fusionWeight) = IMU weight.
            finalHeading = finalHeading + diff * (1.0 - fusionWeight);

            while (finalHeading < 0) finalHeading += 360;
            while (finalHeading >= 360) finalHeading -= 360;
        }

        _previousEasting = easting;
        _previousNorthing = northing;
        _previousHeading = finalHeading;
        _hasPreviousPosition = true;

        return finalHeading;
    }

    public void Reset()
    {
        _previousEasting = 0;
        _previousNorthing = 0;
        _previousHeading = 0;
        _hasPreviousPosition = false;
    }

    private double CalculateFixToFixHeading(double easting, double northing)
    {
        double dx = easting - _previousEasting;
        double dy = northing - _previousNorthing;
        double distance = Math.Sqrt(dx * dx + dy * dy);

        // Guard against noise-driven heading swings when stationary.
        if (distance < Connections.FixToFixDistance) return -1;

        double heading = Math.Atan2(dx, dy) * 180.0 / Math.PI;
        if (heading < 0) heading += 360;
        return heading;
    }
}
