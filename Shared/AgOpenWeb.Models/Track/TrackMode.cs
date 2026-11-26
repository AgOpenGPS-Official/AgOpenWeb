// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Models;

/// <summary>
/// Track mode matching WinForms TrackMode enum for file compatibility.
/// Integer values must be preserved for file format backwards compatibility.
/// </summary>
public enum TrackMode
{
    None = 0,
    AB = 2,
    Curve = 4,
    BndTrackOuter = 8,
    BndTrackInner = 16,
    BndCurve = 32,
    WaterPivot = 64,
    RecordedPath = 128,
    Contour = 256
}
