// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AgOpenWeb.Views.Controls.Dialogs;

public partial class SmartWasDialogPanel : UserControl
{
    public SmartWasDialogPanel()
    {
        InitializeComponent();
    }

    private void Backdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only close if the click was on the backdrop itself (not bubbling from inner panel).
        if (DataContext is AgOpenWeb.ViewModels.MainViewModel vm)
            vm.CloseSmartWasDialogCommand?.Execute(null);
    }

    private void InnerPanel_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Swallow inner clicks so they don't bubble to the backdrop and close the dialog.
        e.Handled = true;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        // The UserControl's DataContext is MainViewModel (inherited from
        // DialogOverlayHost). Only the inner Border switches to SmartWasViewModel.
        if (DataContext is AgOpenWeb.ViewModels.MainViewModel vm)
            vm.CloseSmartWasDialogCommand?.Execute(null);
    }
}
