// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;

namespace AgOpenWeb.Views.Controls;

/// <summary>
/// Event arguments for map click events containing world coordinates.
/// </summary>
public class MapClickEventArgs : EventArgs
{
    public double Easting { get; }
    public double Northing { get; }

    public MapClickEventArgs(double easting, double northing)
    {
        Easting = easting;
        Northing = northing;
    }
}
