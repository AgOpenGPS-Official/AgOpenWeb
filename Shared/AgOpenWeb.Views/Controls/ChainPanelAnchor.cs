// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
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
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace AgOpenWeb.Views.Controls;

/// <summary>
/// Shared upper-left position (in window / TopLevel coordinates) of the currently
/// active panel in a left-nav → dialog chain.
///
/// A panel publishes its position here whenever it is shown or dragged; the next
/// panel in the chain reads it on open so it appears with its upper-left corner
/// aligned over the panel that launched it — even if that panel was dragged away
/// from home. The chain root (a left-nav fly-out) resets to its home position on
/// open and republishes, so a freshly opened chain starts at home rather than
/// chasing a stale position.
/// </summary>
public static class ChainPanelAnchor
{
    /// <summary>Upper-left of the active chain panel, or null if none captured.</summary>
    public static Point? Current { get; set; }

    /// <summary>
    /// Position a tool overlay so its upper-left lands at the current chain anchor
    /// (over the fly-out that launched it). Handles both Canvas-hosted overlays
    /// (boundary recording, recorded path) and alignment/Panel-hosted ones
    /// (offset-fix pad). No-op when no anchor has been captured.
    /// </summary>
    public static void PositionAtAnchor(Control panel)
    {
        if (Current is not { } anchor) return;
        var top = TopLevel.GetTopLevel(panel);
        if (top is null || panel.Parent is not Visual parent) return;
        var origin = parent.TranslatePoint(new Point(0, 0), top) ?? default;
        if (parent is Canvas)
        {
            Canvas.SetLeft(panel, anchor.X - origin.X);
            Canvas.SetTop(panel, anchor.Y - origin.Y);
        }
        else
        {
            panel.HorizontalAlignment = HorizontalAlignment.Left;
            panel.VerticalAlignment = VerticalAlignment.Top;
            panel.Margin = new Thickness(anchor.X - origin.X, anchor.Y - origin.Y, 0, 0);
        }
    }
}
