// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

namespace AgValoniaGPS.Models.RoutePlanning;

/// <summary>
/// Direction of material flow for the agricultural operation, which
/// determines whether headland passes are covered before or after the
/// interior cells. Per Hameed et al. 2013 (Figs 6 &amp; 7).
/// </summary>
public enum OperationDirection
{
    /// <summary>
    /// Material is flowing INTO the field (seeding, fertilizing, planting).
    /// Drive the interior cells first; the headland passes are driven last
    /// so they aren't compacted by the loaded vehicle's repeated transits.
    /// </summary>
    InputFlow,

    /// <summary>
    /// Material is flowing OUT of the field (harvest, mowing, baling).
    /// Drive the headland passes first to clear the perimeter, so the
    /// vehicle has room to U-turn and transit while harvesting the interior.
    /// </summary>
    OutputFlow,
}
