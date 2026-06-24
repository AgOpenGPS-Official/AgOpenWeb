// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using Avalonia.Controls;
using Avalonia.Input;
using AgOpenWeb.ViewModels;

namespace AgOpenWeb.Views.Controls.Dialogs;

public partial class ResumeJobDialogPanel : UserControl
{
    public ResumeJobDialogPanel()
    {
        InitializeComponent();
    }

    private void Backdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.NavCloseChainCommand?.Execute(null);
        }
        e.Handled = true;
    }
}
