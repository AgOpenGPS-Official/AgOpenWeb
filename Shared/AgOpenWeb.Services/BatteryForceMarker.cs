// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using AgOpenWeb.Models;

namespace AgOpenWeb.Services;

/// <summary>
/// Dev-mode override that fakes a battery reading so the strip icon renders
/// on machines without a battery (Mac mini, desktop tower, etc.). Drop a
/// <c>.force_battery</c> file into the AgOpenWeb Documents folder and
/// every platform's <see cref="Interfaces.IBatteryService"/> short-circuits
/// to the fake reading instead of reporting Unavailable.
/// </summary>
public static class BatteryForceMarker
{
    public const string MarkerFileName = ".force_battery";

    /// <summary>Fake reading published when the marker file is present.</summary>
    public static readonly BatteryStatus FakeStatus = new()
    {
        IsAvailable = true,
        Level = 0.65,
        IsCharging = true,
    };

    public static bool IsEnabled()
    {
        try
        {
            return File.Exists(Path.Combine(AppDataRoot.Documents, MarkerFileName));
        }
        catch
        {
            return false;
        }
    }
}
