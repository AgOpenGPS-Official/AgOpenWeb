// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
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

namespace AgValoniaGPS.Models.Base;

/// <summary>
/// Display-boundary length conversions. All lengths are stored internally in
/// metric (cm for tool/section widths, meters for totals); these helpers
/// convert only at the UI display/input boundary when the user selects
/// Imperial units. Keep storage metric — convert here, never in the model.
/// </summary>
public static class UnitConversion
{
    public const double CmPerInch = 2.54;
    public const double FeetPerMeter = 3.280839895;

    public static double CmToInches(double cm) => cm / CmPerInch;
    public static double InchesToCm(double inches) => inches * CmPerInch;
    public static double MetersToFeet(double meters) => meters * FeetPerMeter;
}
