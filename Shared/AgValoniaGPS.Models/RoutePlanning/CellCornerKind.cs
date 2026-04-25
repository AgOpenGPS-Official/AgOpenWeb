// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

namespace AgValoniaGPS.Models.RoutePlanning;

/// <summary>
/// Whether a cell corner sits on the original outer field boundary
/// (HEADLAND — the vehicle can enter/exit there because the headland is
/// just outside) or was created by a decomposition cut (INTERNAL — using
/// it as an entry/exit means crossing the field, not the headland).
/// </summary>
public enum CellCornerKind
{
    /// <summary>Default — corner has not been classified yet.</summary>
    Unknown,

    /// <summary>Corner lies on the original outer boundary; valid entry/exit.</summary>
    Headland,

    /// <summary>Corner lies on a decomposition cut, in the field interior.</summary>
    Internal,
}

/// <summary>
/// Identifies one of a cell's four bounding-box corners in the rotated
/// (sweep, perpendicular) frame. Corners are named by their (sweep, perp)
/// extreme: e.g. <see cref="LowSweepLowPerp"/> is the corner with the
/// smallest sweep coord and smallest perp coord.
/// </summary>
public enum CellCorner
{
    LowSweepLowPerp = 0,
    LowSweepHighPerp = 1,
    HighSweepLowPerp = 2,
    HighSweepHighPerp = 3,
}
