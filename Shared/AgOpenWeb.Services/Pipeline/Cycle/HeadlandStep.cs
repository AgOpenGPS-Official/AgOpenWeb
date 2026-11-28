// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.Headland;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Pipeline;

/// <summary>
/// The GPS cycle's headland: whether the tool is in it (the lift and the tram lights), PGN 239's
/// hydraulic-lift byte, and the distance to the headland line the operator sees, held across
/// gaps. One of the cycle's steps (Plans/MAINVIEWMODEL_REFACTOR_PLAN.md item 8); its state is its
/// own: the last lift byte, the last distance shown and the headland line's index.
///
/// <para><b>Thread.</b> The GPS cycle's worker only.</para>
///
/// <para><b>Reads from the cycle:</b> the tool's position and heading, the speed, reversing, the
/// headland line, the boundary and the headland switch, as arguments; the section layout
/// (<see cref="ISectionControlService.GetSectionWorldPosition"/>), for the tool's corners.</para>
/// </summary>
internal sealed class HeadlandStep
{
    private readonly ConfigurationStore _configStore;
    private readonly ISectionControlService _sectionControlService;

    public HeadlandStep(ConfigurationStore configStore, ISectionControlService sectionControlService)
    {
        _configStore = configStore;
        _sectionControlService = sectionControlService;
    }

    // ── Headland proximity ──────────────────────────────────────────────
    private readonly HeadlandDetectionService _headlandDetector = new();
    private readonly PolygonIndexCache _headlandIndex = new(); // the hydraulic lift's test

    private double? _lastHeadlandDistance;
    private bool _lastHeadlandWarning;

    /// <summary>
    /// The distance to the headland line and its warning for the operator, this fix, held
    /// across a gap while the headland is on.
    /// </summary>
    public void Proximity(List<Vec3>? headlandLine, Vec3 toolPivot, bool headlandOn,
        out double? headlandDist, out bool headlandWarning)
    {
        ComputeHeadlandProximity(headlandLine, toolPivot, headlandOn,
            out headlandDist, out headlandWarning);

        // Hold last valid distance so the HUD doesn't disappear in gaps
        // (e.g., between crossing the headland line and entering the U-turn arc).
        // Only with the headland on: off, there is no distance (AgOpenGPS draws none), and the
        // held one stayed on the screen, frozen while the tractor drove on, until the headland
        // was turned on again (a tester's box stood at 173 m for 55 s, Tapada da Casa).
        if (headlandDist != null)
        {
            _lastHeadlandDistance = headlandDist;
            _lastHeadlandWarning = headlandWarning;
        }
        else if (headlandOn && _lastHeadlandDistance != null && headlandLine != null)
        {
            headlandDist = _lastHeadlandDistance;
            headlandWarning = _lastHeadlandWarning;
        }
        else
        {
            _lastHeadlandDistance = null;
            _lastHeadlandWarning = false;
        }
    }

    private void ComputeHeadlandProximity(
        List<Vec3>? headlandLine, Vec3 toolPivot, bool headlandOn,
        out double? distance, out bool warning)
    {
        distance = null;
        warning = false;

        if (headlandLine == null || headlandLine.Count < 3)
            return;

        var input = new Models.Headland.HeadlandDetectionInput
        {
            // The headland distance HUD follows the headland toggle (AgOpenGPS, #106).
            IsHeadlandOn = headlandOn,
            VehiclePosition = toolPivot,
            Boundaries = new List<Models.Headland.BoundaryData>
            {
                // The cycle's own copy (SetHeadlandLine), not copied again: only read.
                new Models.Headland.BoundaryData { HeadlandLine = headlandLine }
            }
        };

        var output = _headlandDetector.DetectHeadland(input);
        distance = output.HeadlandDistance;
        warning = output.ShouldTriggerWarning;
    }

    /// <summary>
    /// Is the tool in the headland, as AgOpenGPS decides it for the lift and the tram lights
    /// (OpenGL.Designer.cs, CHead / HeadlandDetectionService): both outer corners of the tool
    /// out of the work area (inside the headland line), and no work area in the band ahead of
    /// the tool out to the lift look-ahead (speed × Machine look-ahead time, at most 20 m).
    /// Outside the boundary counts as headland. So the tool is "in" only once all of it has
    /// crossed the line, and "out" as soon as work area comes within the look-ahead: the
    /// implement goes up late enough to finish the pass and comes down early enough to start
    /// the next. False with the headland off or no headland line.
    /// </summary>
    public bool IsToolInHeadland(Vec3 toolPosition, double toolHeading, double speed,
        List<Vec3>? headlandLine, bool headlandOn)
    {
        if (!headlandOn || headlandLine == null || headlandLine.Count < 3) return false;
        int n = _sectionControlService.NumSections;
        if (n <= 0) return false;

        var (left, _) = _sectionControlService.GetSectionWorldPosition(0, toolPosition, toolHeading);
        var (_, right) = _sectionControlService.GetSectionWorldPosition(n - 1, toolPosition, toolHeading);
        if (InWorkArea(left) || InWorkArea(right)) return false;

        // The look-ahead band: AgOpenGPS scans every pixel across the tool out to the look-ahead
        // at each edge (the edges' own speeds; the tool's speed here). Three columns, four steps.
        double ahead = Math.Min(Math.Abs(speed) * Math.Max(0, _configStore.Machine.LookAhead), 20.0);
        double se = Math.Sin(toolHeading), ce = Math.Cos(toolHeading);
        var centre = new Vec2((left.Easting + right.Easting) / 2, (left.Northing + right.Northing) / 2);
        if (InWorkArea(centre)) return false;
        if (ahead > 0)
        {
            for (int step = 1; step <= 4; step++)
            {
                double d = ahead * step / 4;
                if (InWorkArea(new Vec2(left.Easting + se * d, left.Northing + ce * d)) ||
                    InWorkArea(new Vec2(centre.Easting + se * d, centre.Northing + ce * d)) ||
                    InWorkArea(new Vec2(right.Easting + se * d, right.Northing + ce * d)))
                    return false;
            }
        }
        return true;

        bool InWorkArea(Vec2 p) => _headlandIndex.Contains(headlandLine, p.Easting, p.Northing);
    }

    /// <summary>
    /// PGN 239's hydraulic-lift byte, AgOpenGPS CHead.SetHydPosition: 2 (raise) with the tool in
    /// the headland, 1 (lower) out of it; 0 with the lift or the headland off. Slow (under
    /// 0.2 km/h) or in reverse it holds the last value: a 0 there would cancel a raise or lower
    /// the machine module is still timing (Machine_UDP_v5 stops both on anything but 1 or 2).
    /// </summary>
    public byte HydLiftFor(bool toolInHeadland, double speed, List<Vec3>? headlandLine, Boundary? boundary, bool headlandOn,
        bool isReverse)
    {
        if (!_configStore.Machine.HydraulicLiftEnabled || !headlandOn ||
            headlandLine == null || headlandLine.Count < 3 || boundary == null || !boundary.IsValid)
            return _lastHydLift = 0;
        if (Math.Abs(speed) * 3.6 <= 0.2 || isReverse) return _lastHydLift;
        return _lastHydLift = toolInHeadland ? (byte)2 : (byte)1;
    }

    private byte _lastHydLift;

    /// <summary>The lift byte for a tool at this pose (the headland test and the lift together).</summary>
    public byte ComputeHydLiftState(Vec3 toolPosition, double toolHeading, double speed, List<Vec3>? headlandLine,
        Boundary? boundary, bool headlandOn, bool isReverse)
        => HydLiftFor(IsToolInHeadland(toolPosition, toolHeading, speed, headlandLine, headlandOn),
            speed, headlandLine, boundary, headlandOn, isReverse);

}
