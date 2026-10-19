// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Models;

namespace AgOpenWeb.Services.Interfaces;

/// <summary>
/// Reads the host device's battery state on a slow (~15 s) polling cadence.
/// Each platform contributes its own implementation; the Null variant is used
/// on platforms where no battery is present so callers can bind unconditionally.
/// </summary>
public interface IBatteryService
{
    /// <summary>Latest cached reading. Safe to read from any thread.</summary>
    BatteryStatus CurrentStatus { get; }

    /// <summary>Fires on the dispatcher thread when the reading changes.</summary>
    event EventHandler<BatteryStatus>? StatusChanged;

    /// <summary>Begin polling. Idempotent.</summary>
    void Start();

    /// <summary>Stop polling and release resources.</summary>
    void Stop();
}
