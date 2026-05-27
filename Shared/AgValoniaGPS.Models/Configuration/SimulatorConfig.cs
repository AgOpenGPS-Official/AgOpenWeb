// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgValoniaGPS.Models.Configuration;

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
