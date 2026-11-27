// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// What a field session owns (Plans/MAINVIEWMODEL_REFACTOR_PLAN.md, item 6, stage 2): made when a
/// session starts (a field opened, or closed: no field is a session too) and dropped when the next
/// one starts, so nothing of one field outlives it by a reset forgotten on close. Each domain's
/// data moves in with its own commit. Owned by the host loop, as the view model is.
/// </summary>
internal sealed class FieldSessionData(IUiDispatcher host)
{
    /// <summary>The field's marker flags: the list and its numbering.</summary>
    public FieldFlags Flags { get; } = new(host);
}
