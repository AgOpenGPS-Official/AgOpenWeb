// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// Licensed under GNU GPL v3. See LICENSE.md.

using Avalonia.Controls;
using Avalonia.Input;

namespace AgOpenWeb.Views.Controls.Dialogs;

public partial class TramSettingsDialogPanel : UserControl
{
    public TramSettingsDialogPanel()
    {
        InitializeComponent();
    }

    private void Backdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel vm)
            vm.State.UI.CloseDialog();
    }
}
