// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using Avalonia.Controls;
using Avalonia.Input;

namespace AgOpenWeb.Views.Controls.Dialogs;

public partial class LoadVehicleToolDialogPanel : UserControl
{
    public LoadVehicleToolDialogPanel()
    {
        InitializeComponent();
    }

    private void Backdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Non-modal chain dialog: clicking off the card dismisses the whole chain
        // back to the map (matches StartWorkSessionDialogPanel).
        if (DataContext is AgOpenWeb.ViewModels.MainViewModel vm)
        {
            vm.NavCloseChainCommand?.Execute(null);
        }
        e.Handled = true;
    }
}
