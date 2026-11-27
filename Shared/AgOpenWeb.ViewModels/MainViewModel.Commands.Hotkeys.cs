// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Windows.Input;

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using CommunityToolkit.Mvvm.Input;

using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenWeb.ViewModels;

public partial class MainViewModel
{
    // Editing state
    private HotkeyAction? _editingHotkeyAction;
    public HotkeyAction? EditingHotkeyAction
    {
        get => _editingHotkeyAction;
        set => SetProperty(ref _editingHotkeyAction, value);
    }
}
