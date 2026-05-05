// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using AgValoniaGPS.Models.Configuration;

namespace AgValoniaGPS.Services.Gps;

/// <summary>
/// Antenna-to-pivot transform with optional roll correction. Single source
/// of truth — the GPS pipeline calls this once per cycle on local-plane
/// (E, N), and tests call it directly with synthetic positions.
///
/// Sign conventions:
///   AntennaPivot:  Positive = antenna is AHEAD of pivot (typical roof mount).
///   AntennaOffset: Negative = antenna is LEFT of centerline, Positive = RIGHT.
///   Roll: positive = vehicle tilts right.
/// </summary>
public static class AntennaToPivotTransform
{
    /// <summary>
    /// Apply the fore/aft, lateral, and roll-correction shifts in place
    /// to <paramref name="easting"/> / <paramref name="northing"/>.
    /// </summary>
    public static void Apply(
        ref double easting,
        ref double northing,
        double headingRadians,
        VehicleConfig vehicle,
        double imuRollDegrees)
    {
        // Fore/aft offset (AntennaPivot): antenna is AHEAD of pivot, so to
        // recover pivot we move BACKWARD from antenna position.
        if (Math.Abs(vehicle.AntennaPivot) > 0.001)
        {
            easting -= Math.Sin(headingRadians) * vehicle.AntennaPivot;
            northing -= Math.Cos(headingRadians) * vehicle.AntennaPivot;
        }

        // Lateral offset (AntennaOffset): antenna RIGHT of centerline, so
        // to recover centerline we move LEFT (perpendicular RIGHT vector,
        // negated).
        if (Math.Abs(vehicle.AntennaOffset) > 0.001)
        {
            double perpHeading = headingRadians + Math.PI / 2.0;
            easting -= Math.Sin(perpHeading) * vehicle.AntennaOffset;
            northing -= Math.Cos(perpHeading) * vehicle.AntennaOffset;
        }

        // Roll correction: high-mounted antenna shifts laterally when the
        // vehicle tilts. Project antenna height through roll perpendicular
        // to heading. Matches legacy AgOpenGPS formula.
        if (Math.Abs(imuRollDegrees) > 0.01 && Math.Abs(vehicle.AntennaHeight) > 0.01)
        {
            double rollRadians = imuRollDegrees * Math.PI / 180.0;
            double rollCorrectionDistance = Math.Sin(rollRadians) * -vehicle.AntennaHeight;

            easting += Math.Cos(-headingRadians) * rollCorrectionDistance;
            northing += Math.Sin(-headingRadians) * rollCorrectionDistance;
        }
    }
}
