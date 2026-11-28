// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.Gps;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Pipeline;

/// <summary>
/// Where the fix puts the vehicle, the first of the GPS cycle's steps after its inputs
/// (Plans/MAINVIEWMODEL_REFACTOR_PLAN.md item 8): the local plane (made from the first fix, or
/// again after a long jump with no field open) and the far-from-field warning, the fused heading,
/// reversing, the filtered roll, the antenna, and the pivot moved by the drift. Its state is its
/// own: the plane it made until the host loop commits it, the latch of the far warning, the plane
/// the heading's stored fixes are in, the heading fusion, reversing and the roll.
///
/// <para><b>Thread.</b> The GPS cycle's worker only. The host loop's "a field was opened" reaches
/// it through the cycle's start (<see cref="FieldOpened"/>).</para>
///
/// <para><b>Reads from the cycle:</b> the fix, the committed local plane, a field open and the
/// drift, as arguments. What it works out leaves through <see cref="Locate"/>'s results and the
/// read-only properties; nothing else writes them.</para>
/// </summary>
internal sealed class PoseStep
{
    private readonly ConfigurationStore _configStore;
    private readonly IGpsHeadingFusionService _headingFusion;

    public PoseStep(ConfigurationStore configStore, IGpsHeadingFusionService headingFusion)
    {
        _configStore = configStore;
        _headingFusion = headingFusion;
    }

    // Re-anchor the temporary first-fix LocalPlane when the live GPS jumps
    // farther than this from the existing origin (no field loaded). The
    // flat-earth approximation gets noisy at very large local-plane offsets
    // and the camera/render math goes weird, so silently re-anchor.
    private const double TempOriginReinitDistanceM = 50_000.0;

    // Surface a "GPS far from field" warning when the live GPS reports a
    // position farther than this from the loaded field's origin. Autosteer
    // is dropped immediately as a safety measure on the UI thread.
    private const double FieldOriginWarnDistanceM = 10_000.0;

    private LocalPlane? _headingPlane; // plane the heading's stored fixes are in
    private bool _isReverse;           // this cycle's reverse detection (#125)
    private double _sentenceHeading;   // heading field of this cycle's sentence, before fusion
    private double _roll;              // filtered roll, degrees (#110)

    // One-shot latch so the field-far warning fires once per loaded field.
    // SetHasActiveField clears it on the false->true transition (new field
    // opened) so the next field gets a fresh chance to warn.
    private bool _farFromFieldWarned;

    // Phase E: cycle-local cache of a LocalPlane auto-created from the first
    // GPS fix. The cycle uses this for coord conversion in the same tick it
    // emits it on GpsCycleResult.FirstFixLocalPlane; ApplyGpsCycleResult then
    // commits it to State.Field.LocalPlane on the UI thread, which pushes it back
    // here through SetLocalPlane. Once the UI commit catches up (next cycle we see
    // _committedLocalPlane match _cycleLocalPlane), we drop our reference.
    private LocalPlane? _cycleLocalPlane;

    private int _lastJumpCount; // the fusion's JumpCount already told

    /// <summary>This fix's reverse detection (#125).</summary>
    public bool IsReverse => _isReverse;
    /// <summary>The filtered roll, degrees (#110).</summary>
    public double Roll => _roll;
    /// <summary>The heading field of this fix's sentence, before fusion.</summary>
    public double SentenceHeading => _sentenceHeading;
    /// <summary>The fusion's dual-antenna and direction-change findings, for the AutoSteer rules and the snapshot.</summary>
    public bool IsDualHeadingOffTravel => _headingFusion.IsDualHeadingOffTravel;
    public double DualHeadingOffTravelDeg => _headingFusion.DualHeadingOffTravelDeg;
    public bool IsDualHeadingMissing => _headingFusion.IsDualHeadingMissing;
    public bool IsChangingDirection => _headingFusion.IsChangingDirection;

    /// <summary>A field was opened (the host loop, taken at the cycle's start): the far warning may be given again.</summary>
    public void FieldOpened() => _farFromFieldWarned = false;

    /// <summary>
    /// This fix in the local plane: <paramref name="pos"/> gets the fused heading; the results
    /// are what the rest of the cycle and its snapshot use.
    /// </summary>
    public void Locate(GpsData data, ref Position pos, LocalPlane? committedLocalPlane, bool hasActiveField,
        double driftE, double driftN,
        out LocalPlane? firstFixLocalPlane, out LocalPlane? replacementLocalPlane, out double replacementDistanceKm,
        out FarFromFieldWarning? farFromFieldWarning, out LocalPlane? headingPlane, out string? jumpNotice,
        out double antennaE, out double antennaN,
        out double driftedEasting, out double driftedNorthing, out double headingRad)
    {
        // ── (1) Coordinate conversion for real GPS path ─────────────────
        double posEasting = pos.Easting;
        double posNorthing = pos.Northing;

        // Phase E: if a UI-committed LocalPlane already exists (user opened a
        // field, or we auto-created one on a previous cycle and ApplyGpsCycleResult
        // has since committed it and pushed it back through SetLocalPlane), drop our
        // cycle-local cache — the committed instance is now the authoritative one.
        if (_cycleLocalPlane != null && ReferenceEquals(committedLocalPlane, _cycleLocalPlane))
            _cycleLocalPlane = null;

        firstFixLocalPlane = null;
        if (Math.Abs(posEasting) < 0.001 && Math.Abs(posNorthing) < 0.001
            && data.FixQuality > 0)
        {
            // Auto-create local plane from first GPS fix if none exists.
            // Cycle-local cache so we don't cross-thread-write an ObservableObject;
            // the UI thread commits it via ApplyGpsCycleResult.
            var localPlane = committedLocalPlane ?? _cycleLocalPlane;
            if (localPlane == null)
            {
                localPlane = new LocalPlane(
                    new Wgs84(pos.Latitude, pos.Longitude),
                    new SharedFieldProperties());
                _cycleLocalPlane = localPlane;
                firstFixLocalPlane = localPlane; // emit to UI this cycle
            }

            var geoCoord = localPlane.ConvertWgs84ToGeoCoord(
                new Wgs84(pos.Latitude, pos.Longitude));
            posEasting = geoCoord.Easting;
            posNorthing = geoCoord.Northing;
        }

        // Origin guard: if the live GPS source has drifted very far from the
        // current LocalPlane origin, either silently re-anchor (no field) or
        // surface a warning (field loaded). Skip when only the cycle-local
        // cache exists — that means we just bootstrapped the plane this tick.
        replacementLocalPlane = null;
        replacementDistanceKm = 0.0;
        farFromFieldWarning = null;
        if (committedLocalPlane != null && data.FixQuality > 0)
        {
            var converted = committedLocalPlane.ConvertWgs84ToGeoCoord(
                new Wgs84(pos.Latitude, pos.Longitude));
            double distFromOrigin = Math.Sqrt(
                converted.Easting * converted.Easting +
                converted.Northing * converted.Northing);

            if (!hasActiveField && distFromOrigin > TempOriginReinitDistanceM)
            {
                var newPlane = new LocalPlane(
                    new Wgs84(pos.Latitude, pos.Longitude),
                    new SharedFieldProperties());
                _cycleLocalPlane = newPlane;
                replacementLocalPlane = newPlane;
                replacementDistanceKm = distFromOrigin / 1000.0;
                posEasting = 0;
                posNorthing = 0;
            }
            else if (hasActiveField
                     && distFromOrigin > FieldOriginWarnDistanceM
                     && !_farFromFieldWarned)
            {
                farFromFieldWarning = new FarFromFieldWarning(
                    distFromOrigin, pos.Latitude, pos.Longitude);
                _farFromFieldWarned = true;
            }
        }

        // Stored fixes are in local-plane metres: when the plane changes (field
        // opened/closed, re-anchor) the old ones are in another frame (#112).
        headingPlane = replacementLocalPlane ?? committedLocalPlane ?? _cycleLocalPlane;
        if (!ReferenceEquals(headingPlane, _headingPlane))
        {
            _headingFusion.Reset();
            _headingPlane = headingPlane;
        }

        // Stage 3 (Phase B C2): Heading fusion. Replaces the raw NMEA heading
        // with the dual-antenna-aware / fix-to-fix / IMU-blended value.
        // Receives real local easting/northing — see TMP-009 in the parking lot.
        _sentenceHeading = pos.Heading;
        double fusedHeading = _headingFusion.FuseHeading(
            pos.Heading, data.ImuHeading, data.ImuValid,
            pos.Speed, posEasting, posNorthing, data.HasDualHeading);
        _headingFusion.UpdateDualHeadingWarning(data.FixQuality);
        pos = pos with { Heading = fusedHeading };
        // A sideways position jump (the receiver changed its solution): the heading was kept
        // across it; say so, since AutoSteer now follows a position that moved.
        jumpNotice = null;
        if (_headingFusion.JumpCount != _lastJumpCount)
        {
            _lastJumpCount = _headingFusion.JumpCount;
            jumpNotice = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "GPS position jumped {0:F1} m sideways (the receiver changed its solution): heading kept, AutoSteer follows the new position",
                _headingFusion.LastJumpM);
        }
        // Reverse (#125): guidance, U-turn and hydraulic lift need to know, and the
        // heading above already faces the way the vehicle points.
        _isReverse = _headingFusion.IsReverse;

        // Roll filter (AgOpenGPS ahrs.rollFilter: roll = roll × f + new × (1 − f)). AgOpenGPS
        // smooths the steer module's IMU roll; AgOpenWeb's roll comes with the GPS
        // sentence, so it's filtered here (#110). 0 = no filtering (the default).
        double rollFilter = Math.Clamp(_configStore.Ahrs.RollFilter, 0, 0.99);
        _roll = _roll * rollFilter + data.ImuRoll * (1 - rollFilter);

        // Antenna in local metres (AgOpenGPS pn.fix): contour Pure Pursuit measures its
        // distance from the line here (#110).
        antennaE = posEasting + driftE; antennaN = posNorthing + driftN;

        // ── (1b) Antenna-to-pivot transform in local coordinates ────────
        // Single source of truth for the antenna-to-pivot transform. Runs
        // here on local-plane (E, N) so the math is consistent regardless
        // of how the GPS data arrived (real NMEA, simulator, replay).
        AntennaToPivotTransform.Apply(
            ref posEasting,
            ref posNorthing,
            pos.Heading * Math.PI / 180.0,
            _configStore.Vehicle,
            _roll);

        // ── (2) Apply drift compensation ────────────────────────────────
        driftedEasting = posEasting + driftE;
        driftedNorthing = posNorthing + driftN;
        headingRad = pos.Heading * Math.PI / 180.0;
    }
}
