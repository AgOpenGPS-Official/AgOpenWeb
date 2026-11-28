// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Pipeline;

/// <summary>
/// The GPS cycle's rules that stop AutoSteer or tell the machine to, with what each keeps from
/// one fix to the next: out of bounds (#494, PGN 239's geoStop), the steering speed limits
/// (#106) and the RTK fix alarm (#106). One of the cycle's steps (Plans/
/// MAINVIEWMODEL_REFACTOR_PLAN.md item 8); its state is its own.
///
/// <para><b>Thread.</b> The GPS cycle's worker only. <see cref="NowMs"/> is set by tests before
/// the cycle runs.</para>
///
/// <para><b>Reads from the cycle:</b> the pivot, the boundary and turn line, U-turns on, a turn
/// under way, the speed and the fix quality, as arguments.</para>
/// </summary>
internal sealed class AutoSteerRules
{
    private readonly ConfigurationStore _configStore;
    private readonly IAudioService _audioService;

    public AutoSteerRules(ConfigurationStore configStore, IAudioService audioService)
    {
        _configStore = configStore;
        _audioService = audioService;
    }

    private readonly PolygonIndexCache _turnLineIndex = new(); // the out-of-bounds test's turn area

    // Out of bounds (#494): the pivot outside the field. Kept between cycles: during a U-turn
    // outside the turn area it holds its last value, as AgOpenGPS's mc.isOutOfBounds does.
    private bool _isOutOfBounds;

    // ── RTK quality tracking ────────────────────────────────────────────
    private int _previousFixQuality;

    /// <summary>Out of bounds as last worked out (<see cref="UpdateOutOfBounds"/>), for PGN 239 and the snapshot.</summary>
    public bool IsOutOfBounds => _isOutOfBounds;

    /// <summary>Work out out of bounds for this fix (<see cref="OutOfBounds"/>) and keep it.</summary>
    public bool UpdateOutOfBounds(Boundary? boundary, List<Vec3>? turnLine, bool youTurnArea, bool turnUnderWay,
        double easting, double northing) =>
        _isOutOfBounds = OutOfBounds(boundary, turnLine, youTurnArea, turnUnderWay, easting, northing);

    /// <summary>Not steering on a receiver this fix: the below-minimum-speed wait starts again.</summary>
    public void NotSteering() => _belowMinSteerSpeedSinceMs = null;

    /// <summary>
    /// Is the pivot out of bounds (#494)? AgOpenGPS mc.isOutOfBounds (CFence.IsPointInsideFenceArea):
    /// outside the outer boundary or inside any inner one, drive-through or not (an inner boundary
    /// is an obstacle: a creek, a wet hole, a pylon); no boundary, never. With automatic U-turns on,
    /// inside the turn area (the turn line: the fence moved in by the U-turn distance) is in bounds, and outside it the test is made only
    /// when no turn is under way: a turn reaching past the turn line keeps the last answer.
    /// Inside an inner boundary is out of bounds whatever U-turns are doing (#611): AgOpenGPS
    /// counts it as turn area with U-turns on (IsPointInsideTurnArea != -1), so a tractor in a
    /// ditch sent no geoStop there; an inner boundary is never driven through.
    /// </summary>
    private bool OutOfBounds(Boundary? boundary, List<Vec3>? turnLine, bool youTurnArea, bool turnUnderWay,
        double easting, double northing)
    {
        if (boundary?.OuterBoundary is not { IsValid: true }) return false;
        foreach (var inner in boundary.InnerBoundaries)
            if (inner.IsPointInside(easting, northing)) return true;
        if (youTurnArea)
        {
            if (_turnLineIndex.Contains(turnLine!, easting, northing)) return false;
            if (turnUnderWay) return _isOutOfBounds;
        }
        return !boundary.OuterBoundary.IsPointInside(easting, northing);
    }

    // Monotonic ms clock; tests substitute it to step past the below-min-speed grace period.
    public Func<long> NowMs { get; set; } = () => Environment.TickCount64;
    private long? _belowMinSteerSpeedSinceMs;
    private const long BelowMinSteerSpeedGraceMs = 8000; // AgOpenGPS: 80 frames at 10 Hz

    /// <summary>
    /// AgOpenGPS steering speed limits: above Max steer speed disengages at once; below Min
    /// steer speed for ~8 s disengages (so you can engage from a standstill and pull away).
    /// A limit of 0 is off. Returns the disengage reason, or null. The panel settings used to
    /// be read by nothing (#106).
    /// </summary>
    public string? CheckSteerSpeedLimits(double speedKmh)
    {
        var a = _configStore.AutoSteer;
        if (a.MaxSteerSpeed > 0 && speedKmh > a.MaxSteerSpeed)
        {
            _belowMinSteerSpeedSinceMs = null;
            return $"AutoSteer disengaged - above maximum steering speed ({FormatSpeed(a.MaxSteerSpeed)})";
        }
        if (a.MinSteerSpeed > 0 && speedKmh < a.MinSteerSpeed)
        {
            long now = NowMs();
            _belowMinSteerSpeedSinceMs ??= now;
            if (now - _belowMinSteerSpeedSinceMs.Value >= BelowMinSteerSpeedGraceMs)
            {
                _belowMinSteerSpeedSinceMs = null;
                return $"AutoSteer disengaged - below minimum steering speed ({FormatSpeed(a.MinSteerSpeed)})";
            }
            return null;
        }
        _belowMinSteerSpeedSinceMs = null;
        return null;
    }

    private string FormatSpeed(double kmh) =>
        _configStore.IsMetric ? $"{kmh:0.#} kph" : $"{kmh * 0.621371:0.#} mph";

    /// <summary>
    /// RTK fix alarm, as AgOpenGPS (isRTK_AlarmOn / isRTK_KillAutosteer): only when the
    /// "RTK lost alarm" setting is on; "lost" means leaving RTK fixed (quality 4) — float
    /// counts as lost. Returns true on the lost edge when "Alarm stops AutoSteer" is set, so
    /// the caller disengages. Both settings used to be ignored (#106): the sound played
    /// regardless and autosteer never stopped.
    /// </summary>
    public bool CheckRtkQualityChange(int fixQuality)
    {
        if (fixQuality == _previousFixQuality) return false;
        bool wasRtk = _previousFixQuality == 4;
        bool isRtk = fixQuality == 4;
        _previousFixQuality = fixQuality;

        var con = _configStore.Connections;
        if (!con.RtkLostAlarm) return false;

        if (wasRtk && !isRtk)
        {
            _audioService.Play(SoundEffect.RtkLost);
            return con.RtkLostAction != 0; // web toggle "Alarm stops AutoSteer" (1 = pause AutoSteer)
        }
        if (!wasRtk && isRtk)
            _audioService.Play(SoundEffect.RtkRecovered);
        return false;
    }

}
