// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using Avalonia.Controls;
using Avalonia.Input;

namespace AgOpenWeb.Views.Controls.Dialogs;

public partial class AppSettingsDialogPanel : UserControl
{
    public AppSettingsDialogPanel()
    {
        InitializeComponent();
    }

    private void Backdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is AgOpenWeb.ViewModels.MainViewModel vm)
        {
            vm.NavCloseChainCommand?.Execute(null);
        }
    }
}
