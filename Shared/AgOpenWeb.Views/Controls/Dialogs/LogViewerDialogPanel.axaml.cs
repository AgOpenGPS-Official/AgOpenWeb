// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace AgOpenWeb.Views.Controls.Dialogs;

public partial class LogViewerDialogPanel : UserControl
{
    public LogViewerDialogPanel()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Run the viewer's cleanup (unsubscribe from LogStore) whenever the dialog
        // is dismissed by ANY means — Back, Close, or backdrop — not just a Close
        // button. Tied to visibility so the LogStore subscription can't leak.
        if (change.Property == IsVisibleProperty && !change.GetNewValue<bool>() &&
            DataContext is AgOpenWeb.ViewModels.MainViewModel vm)
        {
            vm.CloseLogViewerDialogCommand?.Execute(null);
        }
    }

    private void Backdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is AgOpenWeb.ViewModels.MainViewModel vm)
            vm.NavCloseChainCommand?.Execute(null);
    }
}
