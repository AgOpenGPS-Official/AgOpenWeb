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
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Open over the fly-out that launched it (the chain anchor).
        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
            AgValoniaGPS.Views.Controls.ChainPanelAnchor.PositionAtAnchor(this);
    }
}
