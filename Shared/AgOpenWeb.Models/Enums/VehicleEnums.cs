// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Models;

/// <summary>
/// Vehicle types supported by the guidance system
/// </summary>
public enum VehicleType
{
    Tractor = 0,
    Harvester = 1,
    FourWD = 2
}

/// <summary>
/// Steering algorithm selection
/// </summary>
public enum SteeringAlgorithm
{
    PurePursuit,
    Stanley
}
