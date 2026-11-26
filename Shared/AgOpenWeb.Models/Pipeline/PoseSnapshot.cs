// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Models.Pipeline;

/// <summary>
/// Immutable snapshot of vehicle pose + motion state at a single GPS sample
/// instant. Published by GpsService each time a GPS frame arrives, swapped
/// atomically into the position estimator. Readers receive a fully-consistent
/// record without locking.
///
/// Used by the host control loop (running at 100 Hz) to interpolate vehicle
/// pose between GPS samples (typically 10 Hz) so that section control,
/// guidance, and PGN sends can run at sub-frame resolution.
///
/// <paramref name="FixTimeCentiseconds"/> is the receiver's own time of day on the fix, in 1/100 s
/// (<see cref="GpsData.FixTimeCentiseconds"/>; under 0 = none): the estimator derives the yaw rate
/// over the time between two fixes by it, not over the time between their arrivals.
/// </summary>
public sealed record PoseSnapshot(
    Vec2 Position,
    double Heading,
    double SpeedMps,
    double YawRateRadPerSec,
    double Roll,
    long TimestampTicks,
    int FixTimeCentiseconds = -1);
