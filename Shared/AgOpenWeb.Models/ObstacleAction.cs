// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Models;

/// <summary>
/// What the tractor does at an inner boundary (an obstacle) when a pass meets it, chosen per
/// obstacle in the boundary list and kept in <c>field.geojson</c> (<c>atObstacle</c>).
/// </summary>
public enum ObstacleAction
{
    /// <summary>A U-turn in front of it, as at the field edge (AgOpenGPS: Drive thru = No).</summary>
    Turn = 0,

    // 1 is left for going around an obstacle, which is not part of this yet.

    /// <summary>Drive on across it; only the sections switch off over it (AgOpenGPS: Drive thru = Yes).</summary>
    DriveThrough = 2,
}

/// <summary>The names <see cref="ObstacleAction"/> has in files and web commands.</summary>
public static class ObstacleActionNames
{
    public const string Turn = "turn";
    public const string DriveThrough = "driveThrough";

    public static string ToName(this ObstacleAction action) => action switch
    {
        ObstacleAction.DriveThrough => DriveThrough,
        _ => Turn,
    };

    /// <summary>The action a name stands for; null for a name that is none of them.</summary>
    public static ObstacleAction? Parse(string? name) => name switch
    {
        Turn => ObstacleAction.Turn,
        DriveThrough => ObstacleAction.DriveThrough,
        _ => null,
    };
}
