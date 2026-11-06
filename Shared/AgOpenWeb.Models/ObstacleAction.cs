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

    /// <summary>Steer around it and back onto the same pass, tractor and implement kept out of it.</summary>
    GoRound = 1,

    /// <summary>Drive on across it; only the sections switch off over it (AgOpenGPS: Drive thru = Yes).</summary>
    DriveThrough = 2,
}

/// <summary>The names <see cref="ObstacleAction"/> has in files and web commands.</summary>
public static class ObstacleActionNames
{
    public const string Turn = "turn";
    public const string GoRound = "goRound";
    public const string DriveThrough = "driveThrough";

    public static string ToName(this ObstacleAction action) => action switch
    {
        ObstacleAction.GoRound => GoRound,
        ObstacleAction.DriveThrough => DriveThrough,
        _ => Turn,
    };

    /// <summary>The action a name stands for; null for a name that is none of them.</summary>
    public static ObstacleAction? Parse(string? name) => name switch
    {
        Turn => ObstacleAction.Turn,
        GoRound => ObstacleAction.GoRound,
        DriveThrough => ObstacleAction.DriveThrough,
        _ => null,
    };
}
