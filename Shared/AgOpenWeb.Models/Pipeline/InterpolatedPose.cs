// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Models.Pipeline;

/// <summary>
/// Pose interpolated from the latest <see cref="PoseSnapshot"/> using
/// dead reckoning: heading is advanced by the snapshot's yaw rate and
/// position is advanced by the velocity vector at the predicted heading.
/// Returned by <c>IPositionEstimator.GetPose(now)</c>.
/// </summary>
public readonly record struct InterpolatedPose(
    Vec2 Position,
    double Heading,
    double SpeedMps,
    double Roll);
