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

namespace AgValoniaGPS.Views.Controls;

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
}
