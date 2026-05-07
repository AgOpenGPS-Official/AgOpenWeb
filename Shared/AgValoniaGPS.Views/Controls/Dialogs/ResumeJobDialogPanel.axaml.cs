// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using Avalonia.Controls;
using Avalonia.Input;
using AgValoniaGPS.ViewModels;

namespace AgValoniaGPS.Views.Controls.Dialogs;

public partial class ResumeJobDialogPanel : UserControl
{
    public ResumeJobDialogPanel()
    {
        InitializeComponent();
    }

    private void Backdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm
            && vm.CancelResumeJobDialogCommand?.CanExecute(null) == true)
        {
            vm.CancelResumeJobDialogCommand.Execute(null);
        }
        e.Handled = true;
    }
}
