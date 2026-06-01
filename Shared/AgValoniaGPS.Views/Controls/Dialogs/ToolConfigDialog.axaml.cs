// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using Avalonia.Controls;
using Avalonia.Input;

namespace AgValoniaGPS.Views.Controls.Dialogs;

public partial class ToolConfigDialog : UserControl
{
    public ToolConfigDialog()
    {
        InitializeComponent();
    }

    private void Backdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Non-modal chain dialog: clicking off the card dismisses the whole chain.
        if (DataContext is AgValoniaGPS.ViewModels.MainViewModel vm)
        {
            vm.NavCloseChainCommand?.Execute(null);
        }
        e.Handled = true;
    }
}
