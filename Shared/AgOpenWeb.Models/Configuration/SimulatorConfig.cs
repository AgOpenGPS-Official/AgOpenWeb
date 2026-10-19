// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenWeb.Models.Configuration;

/// <summary>
/// Simulator configuration - ONE place only.
/// Replaces: simulator fields previously scattered across AppSettings and profile DTOs
/// </summary>
public class SimulatorConfig : ObservableObject
{
    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    // NOTE: the last simulator POSITION (Latitude/Longitude/Speed/SteerAngle)
    // is persistent state → PersistentAppState. Only the Enabled preference
    // (and the transient Heading) remain here.

    private double _heading;
    public double Heading
    {
        get => _heading;
        set => SetProperty(ref _heading, value);
    }
}
