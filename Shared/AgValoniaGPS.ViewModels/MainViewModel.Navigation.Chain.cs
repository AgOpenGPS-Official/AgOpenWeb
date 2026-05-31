// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
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

using AgValoniaGPS.Models.State;
using CommunityToolkit.Mvvm.Input;

namespace AgValoniaGPS.ViewModels;

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
        Configuration,
        FieldOperations,
        FieldTools,
        NetworkIo,
    }

    // Which fly-out the current chain was launched from, so Back can reopen it
    // once the dialog back-stack is exhausted.
    private NavFlyout _chainOriginFlyout = NavFlyout.None;

    // The fly-out most recently closed by CloseAllNavFlyouts. Menu fly-outs close
    // themselves on item-click (CloseOnItemClick) BEFORE the item's command runs,
    // so by the time OpenChainDialog reads CurrentFlyout() the fly-out is already
    // gone. This remembers it so the origin can still be captured. Consumed (set
    // to None) by OpenChainDialog so it can't go stale across unrelated opens.
    private NavFlyout _lastClosedFlyout = NavFlyout.None;

    private void InitializeChainNavigationCommands()
    {
        NavBackCommand = new RelayCommand(NavigateBack);
        NavCloseChainCommand = new RelayCommand(CloseChain);
    }

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
        _chainOriginFlyout = origin;
        _lastClosedFlyout = NavFlyout.None;
        State.UI.PushDialog(dialog);
    }

    /// <summary>
    /// Open a DEEPER dialog within the chain — the current dialog is pushed onto
    /// the back-stack so Back returns to it.
    /// </summary>
    private void PushChainDialog(DialogType dialog) => State.UI.PushDialog(dialog);

    /// <summary>
    /// Back: close the current panel and reopen the previous one. Pops to the
    /// parent dialog if there is one; otherwise reopens the originating fly-out.
    /// </summary>
    private void NavigateBack()
    {
        if (!State.UI.GoBack())
        {
            // Stack exhausted — we were at the first dialog; reopen its fly-out.
            ReopenFlyout(_chainOriginFlyout);
            _chainOriginFlyout = NavFlyout.None;
        }
    }

    /// <summary>
    /// Close: dismiss the whole chain back to the map (no fly-out reopened).
    /// </summary>
    private void CloseChain()
    {
        State.UI.CloseDialog();
        _chainOriginFlyout = NavFlyout.None;
    }

    /// <summary>Identify which left-nav fly-out is currently open (if any).</summary>
    private NavFlyout CurrentFlyout()
    {
        if (IsScreenAlertsPanelVisible) return NavFlyout.ScreenAlerts;
        if (IsFileMenuPanelVisible) return NavFlyout.FileMenu;
        if (IsToolsPanelVisible) return NavFlyout.Tools;
        if (IsConfigurationPanelVisible) return NavFlyout.Configuration;
        if (IsFieldOperationsPanelVisible) return NavFlyout.FieldOperations;
        if (IsFieldToolsPanelVisible) return NavFlyout.FieldTools;
        if (IsNetworkIoPanelVisible) return NavFlyout.NetworkIo;
        return NavFlyout.None;
    }

    /// <summary>Reopen a specific left-nav fly-out (all others stay closed).</summary>
    private void ReopenFlyout(NavFlyout flyout)
    {
        switch (flyout)
        {
            case NavFlyout.ScreenAlerts: IsScreenAlertsPanelVisible = true; break;
            case NavFlyout.FileMenu: IsFileMenuPanelVisible = true; break;
            case NavFlyout.Tools: IsToolsPanelVisible = true; break;
            case NavFlyout.Configuration: IsConfigurationPanelVisible = true; break;
            case NavFlyout.FieldOperations: IsFieldOperationsPanelVisible = true; break;
            case NavFlyout.FieldTools: IsFieldToolsPanelVisible = true; break;
            case NavFlyout.NetworkIo: IsNetworkIoPanelVisible = true; break;
            // NavFlyout.None: nothing to reopen (chain was opened programmatically).
        }
    }
}
