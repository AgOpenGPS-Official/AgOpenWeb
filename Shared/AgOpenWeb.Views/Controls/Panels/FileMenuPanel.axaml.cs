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

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AgOpenWeb.Views.Controls.Panels;

public partial class FileMenuPanel : UserControl
{
    public event EventHandler<Vector>? DragMoved;

    public FileMenuPanel()
    {
        InitializeComponent();
        var fp = this.FindControl<FloatingPanel>("FP");
        if (fp != null)
            fp.DragMoved += (s, delta) => DragMoved?.Invoke(this, delta);
        // Picking any item dismisses the menu. Click bubbles to here before the
        // bound command runs; we don't mark it handled, so the command still runs.
        AddHandler(Button.ClickEvent, CloseOnItemClick, RoutingStrategies.Bubble);
    }

    private void CloseOnItemClick(object? sender, RoutedEventArgs e)
        => (DataContext as AgOpenWeb.ViewModels.MainViewModel)?.CloseAllNavFlyouts();
}
