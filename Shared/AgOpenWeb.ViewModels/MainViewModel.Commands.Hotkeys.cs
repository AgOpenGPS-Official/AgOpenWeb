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
    // Hotkey commands
    public ICommand? ShowHotkeyConfigDialogCommand { get; private set; }
    public ICommand? CloseHotkeyConfigDialogCommand { get; private set; }
    public ICommand? ResetHotkeysToDefaultCommand { get; private set; }
    public ICommand? StartEditHotkeyCommand { get; private set; }

    // Editing state
    private HotkeyAction? _editingHotkeyAction;
    public HotkeyAction? EditingHotkeyAction
    {
        get => _editingHotkeyAction;
        set => SetProperty(ref _editingHotkeyAction, value);
    }

    // Dispatch table: HotkeyAction -> command
    private Dictionary<HotkeyAction, ICommand?>? _hotkeyDispatch;

    private void InitializeHotkeyCommands()
    {
        ShowHotkeyConfigDialogCommand = new RelayCommand(() =>
        {
            OpenChainDialog(DialogType.HotkeyConfig);
        });

        CloseHotkeyConfigDialogCommand = new RelayCommand(() =>
        {
            EditingHotkeyAction = null;
            State.UI.CloseDialog();
            _configurationService.SaveAppSettings();
        });

        ResetHotkeysToDefaultCommand = new RelayCommand(() =>
        {
            ConfigStore.Hotkeys.ResetToDefaults();
            _hotkeyDispatch = null;
        });

        StartEditHotkeyCommand = new RelayCommand<HotkeyAction>(action =>
        {
            EditingHotkeyAction = action;
        });

        BuildHotkeyDispatch();
    }

    private void BuildHotkeyDispatch()
    {
        _hotkeyDispatch = new Dictionary<HotkeyAction, ICommand?>
        {
            { HotkeyAction.AutoSteer, ToggleAutoSteerCommand },
            { HotkeyAction.CycleLines, CycleABLinesCommand },
            { HotkeyAction.FieldMenu, ShowFieldSelectionDialogCommand },
            { HotkeyAction.Flag, PlaceRedFlagCommand },
            { HotkeyAction.ManualSection, ToggleSectionMasterCommand },
            { HotkeyAction.AutoSection, ToggleSectionMasterCommand },
            { HotkeyAction.SnapPivot, SnapToPivotCommand },
            { HotkeyAction.NudgeLeft, NudgeLeftCommand },
            { HotkeyAction.NudgeRight, NudgeRightCommand },
            { HotkeyAction.VehicleSettings, ShowVehicleConfigDialogCommand },
            { HotkeyAction.SteerWizard, ShowSteerWizardCommand },
        };
    }

    public void CaptureHotkeyBinding(string key)
    {
        if (EditingHotkeyAction == null) return;

        ConfigStore.Hotkeys.SetKeyForAction(EditingHotkeyAction.Value, key);
        EditingHotkeyAction = null;
        _hotkeyDispatch = null;
    }

    public bool HandleHotkey(string key)
    {
        if (!ConfigStore.Display.KeyboardEnabled) return false;
        if (State.UI.IsDialogOpen) return false;

        var action = ConfigStore.Hotkeys.GetActionForKey(key);
        if (action == null) return false;

        // Section toggles need parameter
        if (action.Value >= HotkeyAction.Section1 && action.Value <= HotkeyAction.Section8)
        {
            ToggleSectionCommand?.Execute((int)(action.Value - HotkeyAction.Section1));
            return true;
        }

        _hotkeyDispatch ??= new Dictionary<HotkeyAction, ICommand?>();
        if (_hotkeyDispatch.Count == 0) BuildHotkeyDispatch();

        if (_hotkeyDispatch.TryGetValue(action.Value, out var cmd) && cmd != null)
        {
            cmd.Execute(null);
            return true;
        }

        StatusMessage = $"{action} not yet implemented";
        return true;
    }
}
