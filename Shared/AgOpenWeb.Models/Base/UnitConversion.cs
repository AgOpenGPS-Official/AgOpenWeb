// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Models.Base;

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
