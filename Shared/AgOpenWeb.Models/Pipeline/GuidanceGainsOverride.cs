// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Models.Pipeline;

/// <summary>
/// The line guidance gains a tuning run tries for a while, in place of the operator's own:
/// the Steer Response (Pure Pursuit hold look-ahead, m) and the two Stanley gains. The
/// settings themselves are not touched, so the AutoSteer page, a profile save and every other
/// reader keep the operator's values; clearing it steers by them again from the next cycle.
/// </summary>
public sealed record GuidanceGainsOverride(double LookAheadHold, double StanleyDistanceGain, double StanleyHeadingGain);
