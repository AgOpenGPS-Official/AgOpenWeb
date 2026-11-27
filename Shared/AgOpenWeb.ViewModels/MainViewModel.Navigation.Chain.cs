// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.State;
using CommunityToolkit.Mvvm.Input;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// Shared chain navigation for the left-nav fly-out → dialog → dialog chains.
///
/// Model: only one panel is visible at a time. Opening the next panel closes the
/// current one; <b>Back</b> closes the current one and reopens the previous;
/// <b>Close</b> dismisses the whole chain back to the map.
///
/// The dialog layers live on <see cref="UIState"/>'s back-stack
/// (<see cref="UIState.PushDialog"/> / <see cref="UIState.GoBack"/>). The bottom of
/// every chain is a left-nav fly-out, which is plain VM bool state (not a
/// <see cref="DialogType"/>), so the originating fly-out is tracked here and
/// reopened when Back unwinds past the first dialog.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The left-nav fly-outs that can originate a dialog chain.</summary>
    private enum NavFlyout
    {
        None,
        ScreenAlerts,
        FileMenu,
        Tools,
        FieldOperations,
        FieldTools,
        NetworkIo,
    }

    // The fly-out most recently closed by CloseAllNavFlyouts. Menu fly-outs close
    // themselves on item-click (CloseOnItemClick) BEFORE the item's command runs,
    // so by the time OpenChainDialog reads CurrentFlyout() the fly-out is already
    // gone. This remembers it so the origin can still be captured. Consumed (set
    // to None) by OpenChainDialog so it can't go stale across unrelated opens.
    private NavFlyout _lastClosedFlyout = NavFlyout.None;

    /// <summary>
    /// True when the active confirmation was raised directly from a left-nav
    /// fly-out item (no parent dialog) so a Back button can return to that fly-out.
    /// </summary>
    public bool IsConfirmationBackVisible =>
        _previousDialogBeforeConfirmation == DialogType.None
        && _lastClosedFlyout != NavFlyout.None;

    /// <summary>
    /// Open the FIRST dialog of a chain from the currently-open fly-out. Records
    /// the origin fly-out (which the existing DialogChanged handler then closes).
    /// </summary>
    private void OpenChainDialog(DialogType dialog)
    {
        // Prefer a still-open fly-out; fall back to the one just closed by an
        // item-click. Consume the fallback so a later non-fly-out open (e.g. a
        // hotkey) doesn't inherit a stale origin.
        var origin = CurrentFlyout();
        if (origin == NavFlyout.None)
            origin = _lastClosedFlyout;
        _lastClosedFlyout = NavFlyout.None;
        State.UI.PushDialog(dialog);
    }

    /// <summary>
    /// Open a DEEPER dialog within the chain — the current dialog is pushed onto
    /// the back-stack so Back returns to it.
    /// </summary>
    private void PushChainDialog(DialogType dialog) => State.UI.PushDialog(dialog);

    /// <summary>
    /// Close: dismiss the whole chain back to the map (no fly-out reopened).
    /// </summary>
    private void CloseChain()
    {
        State.UI.CloseDialog();
    }

    /// <summary>Identify which left-nav fly-out is currently open (if any).</summary>
    private NavFlyout CurrentFlyout()
    {
        if (IsScreenAlertsPanelVisible) return NavFlyout.ScreenAlerts;
        if (IsFileMenuPanelVisible) return NavFlyout.FileMenu;
        if (IsToolsPanelVisible) return NavFlyout.Tools;
        if (IsFieldOperationsPanelVisible) return NavFlyout.FieldOperations;
        if (IsFieldToolsPanelVisible) return NavFlyout.FieldTools;
        if (IsNetworkIoPanelVisible) return NavFlyout.NetworkIo;
        return NavFlyout.None;
    }
}
