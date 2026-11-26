// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Models;

/// <summary>
/// Mode for creating AB lines/curves
/// </summary>
public enum ABCreationMode
{
    None,           // Not creating an AB line
    DriveAB,        // Drive from A to B - uses current position when tapping
    DrawAB,         // Draw on map - tap to place 2 points for straight line
    APlusLine,      // Create from current position + heading
    Curve,          // Record curve while driving
    DrawCurve       // Draw on map - tap to place multiple points for curve
}

/// <summary>
/// Which point is being set in AB creation
/// </summary>
public enum ABPointStep
{
    None,
    SettingPointA,
    SettingPointB
}
