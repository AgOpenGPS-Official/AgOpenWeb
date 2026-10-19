// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Models.State;

/// <summary>
/// Which zone the tractor is currently in, relative to the field's boundary/headland polygons.
/// Used by the YouTurn state machine to gate turn creation and reset logic.
/// </summary>
public enum TractorZone
{
    OutsideBoundary = 0,
    InHeadland = 1,
    InCultivatedArea = 2,
}
