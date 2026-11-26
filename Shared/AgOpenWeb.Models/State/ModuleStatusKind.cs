// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Models.State;

/// <summary>
/// Glanceable aggregate of the four hardware modules (GPS / IMU / AutoSteer / Machine),
/// shown as a single coloured button in the top status strip. The "configured set" is
/// the modules the user has marked as expected via the Network panel (defaults: all).
/// </summary>
public enum ModuleStatusKind
{
    /// <summary>Every configured module is currently producing data.</summary>
    AllPresent,

    /// <summary>At least one configured module is OK, at least one is absent.</summary>
    PartiallyPresent,

    /// <summary>No configured module is currently producing data.</summary>
    NonePresent,
}
