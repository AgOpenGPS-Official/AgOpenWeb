// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Services.Interfaces;

/// <summary>
/// What the ISOBUS task controller last reported in its PGN 240 heartbeat (#312).
/// </summary>
/// <param name="IsAlive">A heartbeat arrived within the last second.</param>
/// <param name="SectionControlEnabled">The task controller drives the implement's sections
/// (automatic); false = manual.</param>
/// <param name="SectionCount">The implement's section count; 0 = task controller alive, no
/// implement with sections.</param>
/// <param name="ClientCount">Implements connected to the task controller (0–7; 0 from task
/// controllers that don't report it).</param>
/// <param name="ReportedSections">The implement's own section states, section 1 in bit 0.</param>
public readonly record struct IsobusStatus(
    bool IsAlive,
    bool SectionControlEnabled,
    int SectionCount,
    int ClientCount,
    ulong ReportedSections);

/// <summary>
/// The app's side of the ISOBUS task controller link (AOG-TaskController, standalone or in
/// the AOW HAT daemon): reads its PGN 240 heartbeat and sends PGN 241 to switch section
/// control on the implement on and off (#312). Like AgOpenGPS's <c>CISOBUS</c>.
/// </summary>
public interface IIsobusTaskControllerService
{
    /// <summary>The latest heartbeat; all zero while no task controller is heard.</summary>
    IsobusStatus Status { get; }

    /// <summary>Ask the task controller to switch section control on the implement on
    /// (automatic) or off (manual). Sent again until the heartbeat shows it, and once more
    /// when a restarted task controller comes back with the other setting.</summary>
    void RequestSectionControl(bool enabled);

    /// <summary>Request the opposite of what the task controller reports. Ignored while no
    /// task controller is heard.</summary>
    void ToggleSectionControl();
}
