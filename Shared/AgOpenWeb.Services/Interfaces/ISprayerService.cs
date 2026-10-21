// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;

namespace AgOpenWeb.Services.Interfaces;

/// <summary>Sprayer alarms, one bit each.</summary>
[Flags]
public enum SprayerAlarms
{
    None = 0,
    /// <summary>A section sprays and the pressure is below the minimum.</summary>
    PressureLow = 1,
    /// <summary>The pressure is above the maximum.</summary>
    PressureHigh = 2,
    /// <summary>Less flow than the open nozzles should spray at this pressure.</summary>
    FlowLow = 4,
    /// <summary>More flow than the open nozzles should spray at this pressure.</summary>
    FlowHigh = 8,
    /// <summary>Flow while the module has every section closed (valve stuck open, or a
    /// section switched to manual).</summary>
    FlowSectionsOff = 16,
    /// <summary>The module stopped sending.</summary>
    NoData = 32,
}

/// <summary>
/// What the sprayer's machine module last reported in PGN 160, and what the app makes of it.
/// </summary>
/// <param name="PressureBar">Boom pressure, bar.</param>
/// <param name="FlowLpm">Measured flow, L/min.</param>
/// <param name="TotalLitres">Litres counted since the module started.</param>
/// <param name="MasterOn">The master switch is on.</param>
/// <param name="Sections">The sections the module has open, section 1 in bit 0.</param>
/// <param name="OpenNozzles">Nozzles on the open sections.</param>
/// <param name="OpenWidthM">Width of the open sections, m.</param>
/// <param name="ExpectedFlowLpm">The flow the open nozzles should spray at this pressure,
/// L/min; NaN with the flow check off.</param>
/// <param name="Alarms">The alarms on now.</param>
public readonly record struct SprayerStatus(
    double PressureBar,
    double FlowLpm,
    double TotalLitres,
    bool MasterOn,
    int Sections,
    int OpenNozzles,
    double OpenWidthM,
    double ExpectedFlowLpm,
    SprayerAlarms Alarms);

/// <summary>
/// Reads the sprayer data the machine module sends once a second (PGN 160: pressure, flow,
/// litres, master switch, open sections), checks it against the nozzles of the open
/// sections, and sounds the alarm when something is wrong.
/// </summary>
public interface ISprayerService
{
    /// <summary>The latest data; null until the module has been heard.</summary>
    SprayerStatus? Status { get; }
}
