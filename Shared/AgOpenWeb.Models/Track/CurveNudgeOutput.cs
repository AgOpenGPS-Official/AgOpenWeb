// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using System.Collections.Generic;

namespace AgOpenWeb.Models.Track;

/// <summary>
/// Output from curve nudging calculation.
/// </summary>
/// <param name="NewCurvePoints">New curve points after nudging, filtering, and smoothing. Empty if curve is too short.</param>
public record CurveNudgeOutput(List<Vec3> NewCurvePoints);
