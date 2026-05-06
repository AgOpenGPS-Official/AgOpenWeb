// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using Avalonia.Controls;
using Avalonia.Input;
using AgValoniaGPS.ViewModels;

namespace AgValoniaGPS.Views.Controls.Dialogs;

public partial class ResumeTaskDialogPanel : UserControl
{
    public ResumeTaskDialogPanel()
    {
        InitializeComponent();
    }

    private void Backdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm
            && vm.CancelResumeTaskDialogCommand?.CanExecute(null) == true)
        {
            vm.CancelResumeTaskDialogCommand.Execute(null);
        }
        e.Handled = true;
    }
}
