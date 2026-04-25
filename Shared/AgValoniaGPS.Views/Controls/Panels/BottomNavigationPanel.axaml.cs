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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AgValoniaGPS.Models.State;

namespace AgValoniaGPS.Views.Controls.Panels;

public partial class BottomNavigationPanel : UserControl
{
    private Border? _mainPanel;
    private Border? _abLineFlyoutPanel;
    private Border? _flagsFlyoutPanel;
    private Button? _abLineMenuButton;
    private Button? _flagMenuButton;
    private bool _isABLineFlyoutOpen;
    private bool _isFlagsFlyoutOpen;
    private AgValoniaGPS.ViewModels.MainViewModel? _subscribedViewModel;

    public BottomNavigationPanel()
    {
        InitializeComponent();

        // Find controls
        _mainPanel = this.FindControl<Border>("MainPanel");
        _abLineMenuButton = this.FindControl<Button>("ABLineMenuButton");
        _flagMenuButton = this.FindControl<Button>("FlagMenuButton");
        _abLineFlyoutPanel = this.FindControl<Border>("ABLineFlyoutPanel");
        _flagsFlyoutPanel = this.FindControl<Border>("FlagsFlyoutPanel");

        // Wire up menu buttons to toggle flyouts
        if (_abLineMenuButton != null)
        {
            _abLineMenuButton.Click += ABLineMenuButton_Click;
        }

        if (_flagMenuButton != null)
        {
            _flagMenuButton.Click += FlagMenuButton_Click;
        }

        // Re-anchor flyouts to their actual measured size whenever they resize
        // (e.g. after first layout, or when HasActiveTrack toggles the menu length).
        // Fixes #263: nudge/reset/half-tool buttons were being pushed off the bottom
        // edge because the hard-coded height estimate was ~220 px smaller than the
        // full menu with an active track.
        if (_abLineFlyoutPanel != null)
            _abLineFlyoutPanel.SizeChanged += Flyout_SizeChanged;
        if (_flagsFlyoutPanel != null)
            _flagsFlyoutPanel.SizeChanged += Flyout_SizeChanged;

        // Close flyouts when clicking outside. We attach at the TopLevel (window) so we
        // catch clicks on the map too — attaching at this UserControl only fires for
        // clicks inside the bottom bar's visual tree, missing the map area above it.
        this.AttachedToVisualTree += OnAttachedToVisualTree;
        this.DetachedFromVisualTree += OnDetachedFromVisualTree;

        // Subscribe to dialog changes to close flyouts when dialogs close
        this.DataContextChanged += OnDataContextChanged;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        topLevel?.AddHandler(
            InputElement.PointerPressedEvent,
            OnTopLevelPointerPressed,
            RoutingStrategies.Tunnel);
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        topLevel?.RemoveHandler(InputElement.PointerPressedEvent, OnTopLevelPointerPressed);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Unsubscribe from old viewmodel to avoid multiple subscriptions
        if (_subscribedViewModel != null)
        {
            _subscribedViewModel.State.UI.DialogChanged -= OnDialogChanged;
            _subscribedViewModel = null;
        }

        // Subscribe to dialog state changes
        if (DataContext is AgValoniaGPS.ViewModels.MainViewModel vm)
        {
            vm.State.UI.DialogChanged += OnDialogChanged;
            _subscribedViewModel = vm;
        }
    }

    private void OnDialogChanged(object? sender, DialogChangedEventArgs e)
    {
        // Close flyouts when any dialog closes (especially after opening from flyout)
        if (e.Current == DialogType.None)
        {
            CloseAllFlyouts();
        }
    }

    private void ABLineMenuButton_Click(object? sender, RoutedEventArgs e)
    {
        // Close flags flyout if open
        CloseFlagsFlyout();
        ToggleABLineFlyout();
        e.Handled = true;
    }

    private void FlagMenuButton_Click(object? sender, RoutedEventArgs e)
    {
        // Close AB line flyout if open
        CloseABLineFlyout();
        ToggleFlagsFlyout();
        e.Handled = true;
    }

    private void ToggleABLineFlyout()
    {
        _isABLineFlyoutOpen = !_isABLineFlyoutOpen;
        if (_abLineFlyoutPanel != null)
        {
            if (_isABLineFlyoutOpen)
                ShowFlyoutAboveButton(_abLineFlyoutPanel, _abLineMenuButton);
            else
                _abLineFlyoutPanel.IsVisible = false;
        }
    }

    private void ToggleFlagsFlyout()
    {
        _isFlagsFlyoutOpen = !_isFlagsFlyoutOpen;
        if (_flagsFlyoutPanel != null)
        {
            if (_isFlagsFlyoutOpen)
                ShowFlyoutAboveButton(_flagsFlyoutPanel, _flagMenuButton);
            else
                _flagsFlyoutPanel.IsVisible = false;
        }
    }

    private void ShowFlyoutAboveButton(Border flyout, Button? button)
    {
        if (button == null || _mainPanel == null) return;

        // Order matters: IsVisible must be true before Measure() — while hidden,
        // Avalonia excludes the control from layout and Measure() reports 0×0,
        // which on re-open would anchor Canvas.Top to -10 and let the full-height
        // flyout render off the bottom of the screen (#263).
        flyout.IsVisible = true;
        flyout.Measure(Size.Infinity);
        var size = flyout.DesiredSize;

        var buttonBounds = button.Bounds;
        var buttonPosition = button.TranslatePoint(new Point(0, 0), _mainPanel);
        if (!buttonPosition.HasValue) return;

        var flyoutLeft = buttonPosition.Value.X + buttonBounds.Width / 2 - size.Width / 2;
        Canvas.SetLeft(flyout, flyoutLeft);
        Canvas.SetTop(flyout, -size.Height - 10);
    }

    private void CloseABLineFlyout()
    {
        _isABLineFlyoutOpen = false;
        if (_abLineFlyoutPanel != null)
        {
            _abLineFlyoutPanel.IsVisible = false;
        }
    }

    private void CloseFlagsFlyout()
    {
        _isFlagsFlyoutOpen = false;
        if (_flagsFlyoutPanel != null)
        {
            _flagsFlyoutPanel.IsVisible = false;
        }
    }

    private void CloseAllFlyouts()
    {
        CloseABLineFlyout();
        CloseFlagsFlyout();
    }

    private void OnTopLevelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isABLineFlyoutOpen
            && IsPointerOutside(e, _abLineFlyoutPanel)
            && IsPointerOutside(e, _abLineMenuButton))
        {
            CloseABLineFlyout();
        }

        if (_isFlagsFlyoutOpen
            && IsPointerOutside(e, _flagsFlyoutPanel)
            && IsPointerOutside(e, _flagMenuButton))
        {
            CloseFlagsFlyout();
        }
    }

    private static bool IsPointerOutside(PointerPressedEventArgs e, Control? target)
    {
        if (target == null) return true;
        var p = e.GetPosition(target);
        var b = target.Bounds;
        return p.X < 0 || p.Y < 0 || p.X > b.Width || p.Y > b.Height;
    }

    /// <summary>
    /// Close all flyouts when any action button is clicked.
    /// Call this from flyout button click handlers if needed.
    /// </summary>
    public void CloseFlyoutOnAction()
    {
        CloseAllFlyouts();
    }

    private void Flyout_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Border flyout && flyout.IsVisible)
            Canvas.SetTop(flyout, -e.NewSize.Height - 10);
    }
}
