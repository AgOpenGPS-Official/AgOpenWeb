// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Collections.Generic;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Models.Sections;

/// <summary>
/// The field facts section control needs on its 100 Hz control-loop tick: the outer
/// boundary, the headland line, whether a field is open at all and whether the headland
/// is switched on. An immutable record the UI thread publishes whole whenever one of them
/// changes, so the control loop never reads the UI-bound <c>State.Field</c> mirror
/// (CONTRIBUTING, Threading Model: services take their inputs, not <c>ApplicationState</c>).
/// </summary>
/// <param name="Boundary">The field's boundary, or null when the open field has none.</param>
/// <param name="HeadlandLine">The headland line, or null when the field has no headland.</param>
/// <param name="HasActiveField">Whether a field is open. With a field but no boundary, Auto
/// still works from coverage alone; with no field, sections are gated off (#347, #419).</param>
/// <param name="IsHeadlandOn">The headland toggle; headland section control needs it on (#106).</param>
public sealed record SectionFieldContext(
    Boundary? Boundary,
    IReadOnlyList<Vec3>? HeadlandLine,
    bool HasActiveField,
    bool IsHeadlandOn)
{
    /// <summary>No field open, nothing switched on.</summary>
    public static readonly SectionFieldContext None = new(null, null, false, false);
}
