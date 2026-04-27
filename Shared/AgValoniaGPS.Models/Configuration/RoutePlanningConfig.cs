// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgValoniaGPS.Models.RoutePlanning;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgValoniaGPS.Models.Configuration;

/// <summary>
/// Route-planning behavior configuration. Controls operation flow direction
/// (interior-then-headlands for input ops vs headlands-then-interior for
/// output ops) and decomposition tuning thresholds for noisy GPS-recorded
/// boundaries.
/// </summary>
public class RoutePlanningConfig : ObservableObject
{
    private OperationDirection _operationDirection = OperationDirection.InputFlow;
    /// <summary>
    /// Whether the planner emits headland coverage before or after interior
    /// cells. OutputFlow (harvest) = headlands first; InputFlow (seeding) =
    /// headlands last. Default InputFlow so the plan starts at the first
    /// interior swath (the green start marker lands there, not partway around
    /// a coverage loop).
    /// </summary>
    public OperationDirection OperationDirection
    {
        get => _operationDirection;
        set => SetProperty(ref _operationDirection, value);
    }

    private double _decompositionThresholdDegrees = 200.0;
    /// <summary>
    /// Field-interior angle (degrees) a vertex must exceed to trigger a
    /// sweep-line cell split. Standard value 180° splits at every reflex
    /// vertex; raising the threshold (default 200°) skips marginal reflexes
    /// that would create narrow sliver subfields from GPS-jitter on recorded
    /// boundaries (Versleijen 2019 §2.2.2).
    /// </summary>
    public double DecompositionThresholdDegrees
    {
        get => _decompositionThresholdDegrees;
        set => SetProperty(ref _decompositionThresholdDegrees, value);
    }

    // Note: Versleijen's MergeThresholdDegrees (merge adjacent cells with
    // similar optimal driving directions) is intentionally not surfaced here.
    // It requires per-cell driving-direction optimization, which we don't do —
    // our planner uses a single swath heading from the active Track for the
    // whole field. Adding per-cell direction optimization is a substantial
    // separate feature (would need a SwathAngleObjective evaluation per cell
    // and a Track-vs-cell-direction reconciliation policy). When that lands,
    // this is the right place to add MergeThresholdDegrees.
}
