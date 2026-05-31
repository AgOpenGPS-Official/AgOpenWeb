// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
// Licensed under GNU GPL v3.

using Avalonia;
using Avalonia.Controls;

namespace AgValoniaGPS.Views.Controls.Dialogs;

public partial class RecordedPathDialogPanel : UserControl
{
    public RecordedPathDialogPanel()
    {
        InitializeComponent();

        // Hosted in a Panel (DialogOverlayHost), not a Canvas, so move the whole
        // control via its Margin when the FloatingPanel header is dragged
        // (same pattern as OffsetFixDialogPanel).
        var fp = this.FindControl<AgValoniaGPS.Views.Controls.FloatingPanel>("FP");
        if (fp != null)
            fp.DragMoved += (_, delta) =>
                Margin = new Thickness(Margin.Left + delta.X, Margin.Top + delta.Y,
                                       Margin.Right, Margin.Bottom);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Open over the fly-out that launched it (the chain anchor).
        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
            AgValoniaGPS.Views.Controls.ChainPanelAnchor.PositionAtAnchor(this);
    }
}
