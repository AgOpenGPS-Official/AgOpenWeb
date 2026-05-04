// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgValoniaGPS.ViewModels;

/// <summary>
/// Backs the LoadVehicleToolDialogPanel — a two-panel picker mirroring
/// AgOpenGPS 6.8.2's FormLoadVehicleTool. Per <c>Plans/VEHICLE_TOOL_SPLIT_PLAN.md</c>
/// (#346), the operator picks a vehicle profile and a tool profile
/// independently; "Load" applies the active pair atomically.
/// </summary>
public partial class LoadVehicleToolDialogViewModel : ObservableObject
{
    private readonly IConfigurationService _configurationService;
    private readonly Action _onClose;
    private readonly Action<string, Action> _confirm;

    public LoadVehicleToolDialogViewModel(
        IConfigurationService configurationService,
        Action onClose,
        Action<string, Action> confirm)
    {
        _configurationService = configurationService;
        _onClose = onClose;
        _confirm = confirm;

        Refresh();

        LoadCommand = new RelayCommand(Load, () => CanLoad);
        CancelCommand = new RelayCommand(Cancel);
        NewVehicleCommand = new RelayCommand<string>(NewVehicle);
        DeleteVehicleCommand = new RelayCommand(DeleteVehicle, () => SelectedVehicle != null && !IsActiveVehicle(SelectedVehicle));
        RenameVehicleCommand = new RelayCommand<string>(RenameVehicle);
        ResetVehicleCommand = new RelayCommand(ResetVehicle);
        NewToolCommand = new RelayCommand<string>(NewTool);
        DeleteToolCommand = new RelayCommand(DeleteTool, () => SelectedTool != null && !IsActiveTool(SelectedTool));
        RenameToolCommand = new RelayCommand<string>(RenameTool);
        ResetToolCommand = new RelayCommand(ResetTool);
    }

    public ObservableCollection<string> Vehicles { get; } = new();
    public ObservableCollection<string> Tools { get; } = new();

    [ObservableProperty]
    private string? _selectedVehicle;

    [ObservableProperty]
    private string? _selectedTool;

    [ObservableProperty]
    private string? _statusMessage;

    public string CurrentVehicle => _configurationService.Store.ActiveVehicleProfileName;
    public string CurrentTool => _configurationService.Store.ActiveToolProfileName;

    public string VehiclePreview => SelectedVehicle is null
        ? string.Empty
        : BuildVehiclePreview(SelectedVehicle);

    public string ToolPreview => SelectedTool is null
        ? string.Empty
        : BuildToolPreview(SelectedTool);

    public bool CanLoad =>
        (SelectedVehicle != null && !IsActiveVehicle(SelectedVehicle))
        || (SelectedTool != null && !IsActiveTool(SelectedTool));

    public ICommand LoadCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand NewVehicleCommand { get; }
    public ICommand DeleteVehicleCommand { get; }
    public ICommand RenameVehicleCommand { get; }
    public ICommand ResetVehicleCommand { get; }
    public ICommand NewToolCommand { get; }
    public ICommand DeleteToolCommand { get; }
    public ICommand RenameToolCommand { get; }
    public ICommand ResetToolCommand { get; }

    partial void OnSelectedVehicleChanged(string? value)
    {
        OnPropertyChanged(nameof(VehiclePreview));
        OnPropertyChanged(nameof(CanLoad));
        ((RelayCommand)LoadCommand).NotifyCanExecuteChanged();
        ((RelayCommand)DeleteVehicleCommand).NotifyCanExecuteChanged();
    }

    partial void OnSelectedToolChanged(string? value)
    {
        OnPropertyChanged(nameof(ToolPreview));
        OnPropertyChanged(nameof(CanLoad));
        ((RelayCommand)LoadCommand).NotifyCanExecuteChanged();
        ((RelayCommand)DeleteToolCommand).NotifyCanExecuteChanged();
    }

    private bool IsActiveVehicle(string? name) =>
        string.Equals(name, CurrentVehicle, StringComparison.OrdinalIgnoreCase);

    private bool IsActiveTool(string? name) =>
        string.Equals(name, CurrentTool, StringComparison.OrdinalIgnoreCase);

    public void Refresh()
    {
        Vehicles.Clear();
        foreach (var v in _configurationService.GetAvailableProfiles().OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            Vehicles.Add(v);

        Tools.Clear();
        foreach (var t in _configurationService.GetAvailableToolProfiles().OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            Tools.Add(t);

        OnPropertyChanged(nameof(CurrentVehicle));
        OnPropertyChanged(nameof(CurrentTool));
        OnPropertyChanged(nameof(VehiclePreview));
        OnPropertyChanged(nameof(ToolPreview));
    }

    private void Load()
    {
        // Save current store state before switching so unsaved edits survive.
        _configurationService.SaveProfile(CurrentVehicle);

        var v = SelectedVehicle ?? CurrentVehicle;
        var t = SelectedTool ?? CurrentTool;
        if (_configurationService.LoadProfiles(v, t))
        {
            StatusMessage = $"Loaded {v} / {t}";
            _onClose();
        }
        else
        {
            StatusMessage = $"Failed to load {v} / {t}";
        }
    }

    private void Cancel() => _onClose();

    private void NewVehicle(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusMessage = "Type a name in the box, then click New";
            return;
        }
        if (Vehicles.Any(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"Vehicle profile '{name}' already exists";
            return;
        }
        // "New" duplicates the currently-loaded store under a new vehicle name.
        _configurationService.SaveProfiles(name, _configurationService.Store.ActiveToolProfileName);
        Refresh();
        SelectedVehicle = name;
        StatusMessage = $"Created vehicle '{name}'";
    }

    private void DeleteVehicle()
    {
        if (SelectedVehicle is null) return;
        var name = SelectedVehicle;
        _confirm($"Delete vehicle profile '{name}'?", () =>
        {
            if (_configurationService.DeleteVehicleProfile(name))
            {
                StatusMessage = $"Deleted vehicle '{name}'";
                Refresh();
                SelectedVehicle = null;
            }
            else
            {
                StatusMessage = $"Cannot delete '{name}' (active or missing)";
            }
        });
    }

    private void RenameVehicle(string? newName)
    {
        newName = newName?.Trim();
        if (SelectedVehicle is null)
        {
            StatusMessage = "Select a vehicle profile in the list, then click Rename";
            return;
        }
        if (string.IsNullOrWhiteSpace(newName))
        {
            StatusMessage = "Type a new name in the box, then click Rename";
            return;
        }
        var oldName = SelectedVehicle;
        if (_configurationService.RenameVehicleProfile(oldName, newName))
        {
            StatusMessage = $"Renamed '{oldName}' → '{newName}'";
            Refresh();
            SelectedVehicle = newName;
        }
        else
        {
            StatusMessage = $"Cannot rename to '{newName}' (collision or missing source)";
        }
    }

    private void ResetVehicle()
    {
        _confirm("Reset Default vehicle profile?", () =>
        {
            _configurationService.CreateProfile("Default");
            Refresh();
            SelectedVehicle = "Default";
        });
    }

    private void NewTool(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusMessage = "Type a name in the box, then click New";
            return;
        }
        if (Tools.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"Tool profile '{name}' already exists";
            return;
        }
        _configurationService.SaveProfiles(_configurationService.Store.ActiveVehicleProfileName, name);
        Refresh();
        SelectedTool = name;
        StatusMessage = $"Created tool '{name}'";
    }

    private void DeleteTool()
    {
        if (SelectedTool is null) return;
        var name = SelectedTool;
        _confirm($"Delete tool profile '{name}'?", () =>
        {
            if (_configurationService.DeleteToolProfile(name))
            {
                StatusMessage = $"Deleted tool '{name}'";
                Refresh();
                SelectedTool = null;
            }
            else
            {
                StatusMessage = $"Cannot delete '{name}' (active or missing)";
            }
        });
    }

    private void RenameTool(string? newName)
    {
        newName = newName?.Trim();
        if (SelectedTool is null)
        {
            StatusMessage = "Select a tool profile in the list, then click Rename";
            return;
        }
        if (string.IsNullOrWhiteSpace(newName))
        {
            StatusMessage = "Type a new name in the box, then click Rename";
            return;
        }
        var oldName = SelectedTool;
        if (_configurationService.RenameToolProfile(oldName, newName))
        {
            StatusMessage = $"Renamed '{oldName}' → '{newName}'";
            Refresh();
            SelectedTool = newName;
        }
        else
        {
            StatusMessage = $"Cannot rename to '{newName}' (collision or missing source)";
        }
    }

    private void ResetTool()
    {
        _confirm("Reset Default tool profile?", () =>
        {
            // Same convention as ResetVehicle — CreateProfile creates both
            // vehicle and tool defaults under Default. The current store
            // state is overwritten; the operator confirmed.
            _configurationService.CreateProfile("Default");
            Refresh();
            SelectedTool = "Default";
        });
    }

    private string BuildVehiclePreview(string name)
    {
        // Preview uses an ephemeral store so the live store isn't perturbed.
        var preview = new ConfigurationStore();
        var current = ConfigurationStore.Instance;
        try
        {
            ConfigurationStore.SetInstance(preview);
            // We don't actually have a per-name read API that targets a
            // throwaway store, so fall back to "load into the current store"
            // when previewing the active profile.
        }
        finally
        {
            ConfigurationStore.SetInstance(current);
        }

        // Show the live values for the active profile; placeholder otherwise.
        if (IsActiveVehicle(name))
        {
            var v = current.Vehicle;
            return $"Type: {v.Type}\nWheelbase: {v.Wheelbase:F2} m\nAntenna pivot: {v.AntennaPivot:F2} m\nTrack width: {v.TrackWidth:F2} m";
        }
        return $"Vehicle profile: {name}\n(Load to view details)";
    }

    private string BuildToolPreview(string name)
    {
        var current = ConfigurationStore.Instance;
        if (IsActiveTool(name))
        {
            var t = current.Tool;
            string attach = t.IsToolFrontFixed ? "Front fixed"
                          : t.IsToolRearFixed ? "Rear fixed"
                          : t.IsToolTrailing ? "Trailing"
                          : "—";
            return $"Width: {t.Width:F2} m\nOverlap: {t.Overlap:F2} m\nOffset: {t.Offset:F2} m\nSections: {current.NumSections}\nAttach: {attach}";
        }
        return $"Tool profile: {name}\n(Load to view details)";
    }
}
