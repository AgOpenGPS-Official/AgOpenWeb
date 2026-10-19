// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Models;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Battery;

/// <summary>
/// Fallback used in tests and on platforms where no battery is present.
/// Always reports <see cref="BatteryStatus.Unavailable"/>; the strip's icon
/// is hidden when bound to this.
/// </summary>
public sealed class NullBatteryService : IBatteryService
{
    public BatteryStatus CurrentStatus => BatteryStatus.Unavailable;
    public event EventHandler<BatteryStatus>? StatusChanged { add { } remove { } }
    public void Start() { }
    public void Stop() { }
}
