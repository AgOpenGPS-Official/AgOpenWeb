// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Models;

/// <summary>
/// Represents the vehicle configuration (tractor, tool dimensions, etc.)
/// </summary>
public class Vehicle
{
    /// <summary>
    /// Vehicle name/description
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Tool width in meters
    /// </summary>
    public double ToolWidth { get; set; }

    /// <summary>
    /// Tool offset from vehicle center in meters
    /// </summary>
    public double ToolOffset { get; set; }

    /// <summary>
    /// Antenna height above ground in meters
    /// </summary>
    public double AntennaHeight { get; set; }

    /// <summary>
    /// Antenna offset forward/backward from pivot in meters
    /// </summary>
    public double AntennaOffset { get; set; }

    /// <summary>
    /// Number of sections for section control
    /// </summary>
    public int NumberOfSections { get; set; }

    /// <summary>
    /// Wheelbase in meters
    /// </summary>
    public double Wheelbase { get; set; }

    /// <summary>
    /// Minimum turning radius in meters
    /// </summary>
    public double MinTurningRadius { get; set; }

    /// <summary>
    /// Whether this vehicle uses section control
    /// </summary>
    public bool IsSectionControlEnabled { get; set; }
}