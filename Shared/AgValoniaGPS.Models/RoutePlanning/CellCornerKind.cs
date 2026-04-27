// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

namespace AgValoniaGPS.Models.RoutePlanning;

/// <summary>
/// Whether a cell corner sits on a headland (vehicle can enter/exit there
/// because there is a no-work buffer just outside the cell), or was created
/// by an interior decomposition cut. Two flavors of headland — outer field
/// boundary and inner-ring (obstacle) boundary — are tracked separately so
/// downstream consumers can prefer one over the other when picking entries.
/// Both are valid for U-turn endpoints and cell entry/exit.
/// </summary>
public enum CellCornerKind
{
    /// <summary>Default — corner has not been classified yet.</summary>
    Unknown,

    /// <summary>Corner lies on the outer field boundary; valid entry/exit.</summary>
    OuterHeadland,

    /// <summary>Corner lies on an expanded inner-ring (obstacle) boundary; valid entry/exit.</summary>
    InnerHeadland,

    /// <summary>Corner lies on a decomposition cut, in the field interior — no headland here, no entry/exit.</summary>
    Internal,
}

/// <summary>
/// Helpers for <see cref="CellCornerKind"/>.
/// </summary>
public static class CellCornerKindExtensions
{
    /// <summary>True when the corner sits on EITHER an outer-field-boundary
    /// headland or an inner-ring headland — i.e. the vehicle can U-turn here
    /// because a no-work buffer exists on the other side.</summary>
    public static bool IsHeadland(this CellCornerKind k)
        => k == CellCornerKind.OuterHeadland || k == CellCornerKind.InnerHeadland;
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
