// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.DumpReplayScenarios.Tests;

/// <summary>
/// A vehicle's geometry, in AgOpenWeb's terms (as the field job harness): the pivot is the axle
/// that is not steered, pivot distances are ahead of it (+), offsets right of the centreline (+),
/// heights above the ground. Built from the reporter's settings in the dump.
/// </summary>
public sealed record VehicleSpec(
    string Name,
    int Type,
    double Wheelbase,
    double TrackWidth,
    double AntennaHeight,
    double AntennaPivot,
    double AntennaOffset,
    double HitchLength,
    double MaxSteerAngle);

/// <summary>An implement, in AgOpenWeb's terms (as the field job harness).</summary>
public sealed record ToolSpec(
    string Name,
    string Type,                 // front / rear / trailing / tbt
    double Width,                // m, working width
    int Sections,
    double Offset = 0,           // m right of the tractor's centreline (+)
    double HitchLength = 0,      // rigid: pivot axle to the working line; trailing / TBT: hitch pin to the tool's wheels
    double ToolToPivot = 0,      // trailing / TBT: the tool's wheels to the working line (+ behind)
    double TankHitch = 0,        // TBT: hitch pin to the tank's wheels
    double Overlap = 0);
