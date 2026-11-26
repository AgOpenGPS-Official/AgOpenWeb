// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Models;

/// <summary>
/// Snapshot of the device's battery state. Used by the strip's battery icon.
/// <see cref="IsAvailable"/> is false on machines without a battery (most
/// desktops) or when the platform API didn't return a usable reading; the
/// icon hides in that case.
/// </summary>
public readonly record struct BatteryStatus
{
    /// <summary>True when the platform exposes a real battery reading.</summary>
    public bool IsAvailable { get; init; }

    /// <summary>Charge fraction, 0.0 to 1.0. Undefined when <see cref="IsAvailable"/> is false.</summary>
    public double Level { get; init; }

    /// <summary>True when the device is plugged in / charging.</summary>
    public bool IsCharging { get; init; }

    public static BatteryStatus Unavailable => default;
}
