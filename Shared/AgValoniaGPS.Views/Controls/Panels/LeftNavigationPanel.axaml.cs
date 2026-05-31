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

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Reactive;
using Avalonia.VisualTree;
using AgValoniaGPS.Views.Controls;

namespace AgValoniaGPS.Views.Controls.Panels;

public partial class LeftNavigationPanel : UserControl
{
    public LeftNavigationPanel()
    {
        InitializeComponent();

        // Wire up sub-panel drag events
        // SimulatorPanel moved to the window-level floating canvas (see platform views)
        WireUpSubPanelDrag<ScreenAlertsPanel>("ScreenAlertsPanelControl");
        WireUpSubPanelDrag<NetworkIoPanel>("NetworkIoPanelControl");
        WireUpSubPanelDrag<FileMenuPanel>("FileMenuPanelControl");
        WireUpSubPanelDrag<ToolsPanel>("ToolsPanelControl");
        WireUpSubPanelDrag<ConfigurationPanel>("ConfigurationPanelControl");
        WireUpSubPanelDrag<FieldOperationsPanel>("FieldOperationsPanelControl");
        WireUpSubPanelDrag<FieldToolsPanel>("FieldToolsPanelControl");
        WireUpSubPanelDrag<BoundaryRecordingPanel>("BoundaryRecordingPanelControl");
        WireUpSubPanelDrag<BoundaryPlayerPanel>("BoundaryPlayerPanelControl");
    }

    private void WireUpSubPanelDrag<T>(string controlName) where T : UserControl
    {
        var panel = this.FindControl<T>(controlName);
        if (panel == null) return;

        // Remember the panel's home position (its XAML Canvas.Left/Top) and snap
        // back to it each time the panel is shown, so a fly-out always reopens at
        // home rather than wherever it was last dragged.
        var homeLeft = Canvas.GetLeft(panel);
        var homeTop = Canvas.GetTop(panel);
        panel.GetObservable(Visual.IsVisibleProperty).Subscribe(new AnonymousObserver<bool>(visible =>
        {
            if (visible)
            {
                Canvas.SetLeft(panel, homeLeft);
                Canvas.SetTop(panel, homeTop);
                // This fly-out is the chain root; publish its home position so the
                // first dialog opens aligned over it.
                PublishFlyoutAnchor(panel);
            }
        }));

        // Use reflection to check for DragMoved event
        var dragMovedEvent = typeof(T).GetEvent("DragMoved");
        if (dragMovedEvent != null)
        {
            dragMovedEvent.AddEventHandler(panel, new EventHandler<Vector>((sender, delta) =>
            {
                if (sender is Control control)
                {
                    var left = Canvas.GetLeft(control);
                    var top = Canvas.GetTop(control);
                    if (double.IsNaN(left)) left = 0;
                    if (double.IsNaN(top)) top = 0;
                    Canvas.SetLeft(control, left + delta.X);
                    Canvas.SetTop(control, top + delta.Y);
                    // Keep the chain anchor in sync so a child opens over the
                    // dragged fly-out rather than at its old home.
                    PublishFlyoutAnchor(control);
                }
            }));
        }
    }

    /// <summary>
    /// Publish a fly-out's current on-screen upper-left to <see cref="ChainPanelAnchor"/>.
    /// Computed from the host Canvas origin plus the panel's Canvas.Left/Top so it is
    /// free of layout lag (the attached coords are already up to date during a drag).
    /// </summary>
    private static void PublishFlyoutAnchor(Control panel)
    {
        var top = TopLevel.GetTopLevel(panel);
        if (top == null || panel.Parent is not Visual canvas) return;
        if (canvas.TranslatePoint(new Point(0, 0), top) is { } origin)
        {
            var l = Canvas.GetLeft(panel); if (double.IsNaN(l)) l = 0;
            var t = Canvas.GetTop(panel); if (double.IsNaN(t)) t = 0;
            ChainPanelAnchor.Current = new Point(origin.X + l, origin.Y + t);
        }
    }
}
