// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

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
