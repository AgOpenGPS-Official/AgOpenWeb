// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using Avalonia;
using Avalonia.Controls;
using AgOpenWeb.Views.Controls;

namespace AgOpenWeb.Views.Controls.Panels;

public partial class ScreenAlertsPanel : UserControl
{
    // Forwarded from the inner FloatingPanel so LeftNavigationPanel's
    // WireUpSubPanelDrag can move this fly-out on the host Canvas.
    public event EventHandler<Vector>? DragMoved;

    public ScreenAlertsPanel()
    {
        InitializeComponent();
        var fp = this.FindControl<FloatingPanel>("FP");
        if (fp != null)
            fp.DragMoved += (s, delta) => DragMoved?.Invoke(this, delta);
    }
}
