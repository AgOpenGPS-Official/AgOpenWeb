// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// MainViewModel partial: the numbers behind the field data card (#321), after
/// AgOpenGPS <c>CFieldData</c> / <c>FormFieldData</c>. Worked area and the trip area live
/// in the coverage service; this adds the trip distance and gathers the rest.
/// </summary>
public partial class MainViewModel
{
    /// <summary>A step shorter than this isn't counted (AgOpenGPS: 0.1 m), so a parked
    /// tractor's position noise doesn't add up.</summary>
    internal const double TripMinStepMeters = 0.1;

    /// <summary>A step longer than this is a jump (first fix, simulator moved, field
    /// changed), not travel.</summary>
    internal const double TripMaxStepMeters = 50;

    private bool _tripHasLast;
    private double _tripLastEasting, _tripLastNorthing;

    /// <summary>Distance driven since the trip was last reset, metres. Starts again when a
    /// field is opened and when the applied area is deleted, like AgOpenGPS's distanceUser.</summary>
    public double TripDistanceMeters { get; private set; }

    /// <summary>Count the travel to this fix into the trip distance.</summary>
    internal void AccumulateTripDistance(double easting, double northing)
    {
        if (!_tripHasLast)
        {
            _tripHasLast = true;
            _tripLastEasting = easting;
            _tripLastNorthing = northing;
            return;
        }
        double de = easting - _tripLastEasting, dn = northing - _tripLastNorthing;
        double step = Math.Sqrt(de * de + dn * dn);
        if (step < TripMinStepMeters) return;
        if (step <= TripMaxStepMeters) TripDistanceMeters += step;
        _tripLastEasting = easting;
        _tripLastNorthing = northing;
    }

    /// <summary>The trip distance starts again; the next fix is its first point.</summary>
    internal void RestartTripDistance()
    {
        TripDistanceMeters = 0;
        _tripHasLast = false;
    }

    /// <summary>The card's Reset button: trip area and trip distance back to zero.</summary>
    public void ResetTrip()
    {
        _coverageMapService.ResetUserArea();
        RestartTripDistance();
    }

    /// <summary>Boundary area less inner boundaries, m²; 0 with no boundary.</summary>
    public double FieldBoundaryAreaSqM
    {
        get
        {
            var b = State.Field.CurrentBoundary;
            return b != null && b.IsValid ? Math.Max(0, b.AreaHectares * 10000.0) : 0;
        }
    }
}
