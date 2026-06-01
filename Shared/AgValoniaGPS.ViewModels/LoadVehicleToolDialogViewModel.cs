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
using AgValoniaGPS.Services.Profile;
using AgValoniaGPS.Services.Storage;
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
    private readonly Action<string, string, string, Action> _confirmChoice;
    private readonly Action? _onConfigureVehicle;
    private readonly Action? _onConfigureTool;

    public LoadVehicleToolDialogViewModel(
        IConfigurationService configurationService,
        Action onClose,
        Action<string, Action> confirm,
        Action<string, string, string, Action> confirmChoice,
        Action? onConfigureVehicle = null,
        Action? onConfigureTool = null)
    {
        _configurationService = configurationService;
        _onClose = onClose;
        _confirm = confirm;
        _confirmChoice = confirmChoice;
        _onConfigureVehicle = onConfigureVehicle;
        _onConfigureTool = onConfigureTool;

        Refresh();

        LoadCommand = new RelayCommand(Load, () => CanLoad);
        CancelCommand = new RelayCommand(Cancel);
        ConfigureVehicleCommand = new RelayCommand(ConfigureVehicle);
        ConfigureToolCommand = new RelayCommand(ConfigureTool);
        NewVehicleCommand = new RelayCommand(() => NewVehicle(VehicleNameInput));
        DeleteVehicleCommand = new RelayCommand(DeleteVehicle, () => SelectedVehicle != null && !IsActiveVehicle(SelectedVehicle));
        RenameVehicleCommand = new RelayCommand(() => RenameVehicle(VehicleNameInput));
        ResetVehicleCommand = new RelayCommand(ResetVehicle);
        NewToolCommand = new RelayCommand(() => NewTool(ToolNameInput));
        DeleteToolCommand = new RelayCommand(DeleteTool, () => SelectedTool != null && !IsActiveTool(SelectedTool));
        RenameToolCommand = new RelayCommand(() => RenameTool(ToolNameInput));
        ResetToolCommand = new RelayCommand(ResetTool);

        // Pre-select the active pair so the lists open highlighting what's loaded.
        // (Done after commands are constructed; the SelectedX setters notify them.)
        SyncSelectionToActive();
    }

    public ObservableCollection<string> Vehicles { get; } = new();
    public ObservableCollection<string> Tools { get; } = new();

    [ObservableProperty]
    private string? _selectedVehicle;

    [ObservableProperty]
    private string? _selectedTool;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>
    /// Text typed into the per-panel "profile name" TextBox. Two-way bound
    /// from AXAML; cleared by the VM after a successful New / Rename.
    /// </summary>
    [ObservableProperty]
    private string _vehicleNameInput = string.Empty;

    [ObservableProperty]
    private string _toolNameInput = string.Empty;

    public string CurrentVehicle => _configurationService.Store.ActiveVehicleProfileName;
    public string CurrentTool => _configurationService.Store.ActiveToolProfileName;

    /// <summary>
    /// Combined active "Vehicle / Tool" label shown at the top of the
    /// picker so the operator sees the loaded pair without scanning the
    /// per-column "Current:" lines.
    /// </summary>
    public string CurrentSummary
    {
        get
        {
            var v = CurrentVehicle;
            var t = CurrentTool;
            if (string.IsNullOrEmpty(t)) return v;
            return $"{v} / {t}";
        }
    }

    /// <summary>
    /// The vehicle/tool pair the operator currently has highlighted (falling
    /// back to the active one per column). Updates live as list selection
    /// changes; shown in the picker's top banner so "Load Selected" / "Configure"
    /// clearly act on this combination.
    /// </summary>
    public string SelectedSummary
    {
        get
        {
            var v = SelectedVehicle ?? CurrentVehicle;
            var t = SelectedTool ?? CurrentTool;
            if (string.IsNullOrEmpty(t)) return v;
            return $"{v} / {t}";
        }
    }

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
    public ICommand ConfigureVehicleCommand { get; }
    public ICommand ConfigureToolCommand { get; }
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
        OnPropertyChanged(nameof(SelectedSummary));
        OnPropertyChanged(nameof(CanLoad));
        ((RelayCommand)LoadCommand).NotifyCanExecuteChanged();
        ((RelayCommand)DeleteVehicleCommand).NotifyCanExecuteChanged();
    }

    partial void OnSelectedToolChanged(string? value)
    {
        OnPropertyChanged(nameof(ToolPreview));
        OnPropertyChanged(nameof(SelectedSummary));
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
        OnPropertyChanged(nameof(CurrentSummary));
        OnPropertyChanged(nameof(VehiclePreview));
        OnPropertyChanged(nameof(ToolPreview));
    }

    private void Load()
    {
        var v = SelectedVehicle ?? CurrentVehicle;
        var t = SelectedTool ?? CurrentTool;

        // Probe the target pair for corruption before touching the live store,
        // so a damaged profile prompts the operator first instead of silently
        // swapping (or resetting) the running config.
        var probe = _configurationService.ProbeProfiles(v, t);

        if (probe.AnyUnrecoverable)
        {
            // No backup to fall back on — refuse to load and stay put.
            var names = string.Join(", ", probe.Damaged
                .Where(d => d.Outcome == LoadOutcome.CorruptNoBackup)
                .Select(d => d.Label));
            StatusMessage = $"Cannot load — {names} damaged with no backup. Staying on {CurrentSummary}.";
            return;
        }

        if (probe.AnyRecovered)
        {
            var d = probe.Damaged.First(x => x.Outcome == LoadOutcome.RecoveredFromBackup);
            var when = d.BackupTimestamp.HasValue ? $" (saved {d.BackupTimestamp.Value:g})" : "";

            // Name the profile you'd stay on — the *current* one of the same
            // kind as the damaged target (e.g. switching to a damaged tool keeps
            // your current tool).
            var keepWord = d.Kind == ProfileKind.Vehicle ? "vehicle" : "tool";
            var keepName = d.Kind == ProfileKind.Vehicle ? CurrentVehicle : CurrentTool;

            _confirmChoice(
                $"The {d.Label} file is damaged on disk.\n\n" +
                $"Load its last good backup{when}, or stay on your current {keepWord} '{keepName}'?",
                "Load Backup",
                $"Keep '{keepName}'",
                () => DoLoad(v, t));
            return;
        }

        DoLoad(v, t);
    }

    private void DoLoad(string v, string t)
    {
        // Save current store state before switching so unsaved edits in the
        // outgoing pair are not lost. Use the active *pair*: writing the
        // live tool config under the vehicle name (the legacy
        // SaveProfile(name) shape) would clobber the wrong tool file when
        // vehicle and tool are independently named.
        _configurationService.SaveProfiles(CurrentVehicle, CurrentTool);

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

    /// <summary>
    /// Highlight the currently-loaded vehicle/tool in their lists (no-op if a
    /// list doesn't contain the active name, e.g. an unsaved profile).
    /// </summary>
    private void SyncSelectionToActive()
    {
        SelectedVehicle = Vehicles.FirstOrDefault(IsActiveVehicle);
        SelectedTool = Tools.FirstOrDefault(IsActiveTool);
    }

    /// <summary>
    /// Open the Vehicle config dialog for the selected vehicle. The config
    /// dialog edits the live store, so make the selected profile active first
    /// (saving the outgoing pair) — that's what the operator expects when they
    /// pick a vehicle and click Configure.
    /// </summary>
    private void ConfigureVehicle()
    {
        var v = SelectedVehicle ?? CurrentVehicle;
        if (!IsActiveVehicle(v))
        {
            _configurationService.SaveProfiles(CurrentVehicle, CurrentTool);
            if (!_configurationService.LoadProfiles(v, CurrentTool))
            {
                StatusMessage = $"Could not load vehicle '{v}'";
                return;
            }
            AfterActivePairChanged();
        }
        _onConfigureVehicle?.Invoke();
    }

    /// <summary>Symmetric to <see cref="ConfigureVehicle"/> for the tool side.</summary>
    private void ConfigureTool()
    {
        var t = SelectedTool ?? CurrentTool;
        if (!IsActiveTool(t))
        {
            _configurationService.SaveProfiles(CurrentVehicle, CurrentTool);
            if (!_configurationService.LoadProfiles(CurrentVehicle, t))
            {
                StatusMessage = $"Could not load tool '{t}'";
                return;
            }
            AfterActivePairChanged();
        }
        _onConfigureTool?.Invoke();
    }

    /// <summary>Re-publish the active-pair labels and re-sync list highlighting
    /// after a load triggered from Configure.</summary>
    private void AfterActivePairChanged()
    {
        OnPropertyChanged(nameof(CurrentVehicle));
        OnPropertyChanged(nameof(CurrentTool));
        OnPropertyChanged(nameof(CurrentSummary));
        SyncSelectionToActive();
    }

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
        VehicleNameInput = string.Empty;
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
            VehicleNameInput = string.Empty;
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
        ToolNameInput = string.Empty;
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
            ToolNameInput = string.Empty;
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
        // Active profile already lives in the live store — no disk read.
        if (IsActiveVehicle(name))
            return FormatVehicle(_configurationService.Store);

        // Preview a non-active profile by reading its file into a throwaway
        // store so the live one isn't perturbed. Falls back to v1 reader for
        // pre-#346 files that haven't been migrated yet.
        // Preview must be side-effect-free: pass quarantineOnFailure: false so
        // merely selecting a damaged profile doesn't move its file aside.
        var temp = new ConfigurationStore();
        bool ok = VehicleProfileJsonService.Load(_configurationService.ProfilesDirectory, name, temp, out _, out _, quarantineOnFailure: false)
               || ProfileJsonServiceV1.Load(_configurationService.ProfilesDirectory, name, temp);
        if (!ok)
            return $"Vehicle profile '{name}'\n(file not found / unreadable)";
        return FormatVehicle(temp);
    }

    private string BuildToolPreview(string name)
    {
        if (IsActiveTool(name))
            return FormatTool(_configurationService.Store);

        // Preview must be side-effect-free (see BuildVehiclePreview).
        var temp = new ConfigurationStore();
        bool ok = ToolProfileJsonService.Load(_configurationService.ToolsDirectory, name, temp, out _, out _, quarantineOnFailure: false)
               || ProfileJsonServiceV1.Load(_configurationService.ProfilesDirectory, name, temp);
        if (!ok)
            return $"Tool profile '{name}'\n(file not found / unreadable)";
        return FormatTool(temp);
    }

    private static string FormatVehicle(ConfigurationStore store)
    {
        var v = store.Vehicle;
        return
            $"Type: {v.Type}\n" +
            $"Wheelbase: {v.Wheelbase:F2} m\n" +
            $"Track width: {v.TrackWidth:F2} m\n" +
            $"Antenna height: {v.AntennaHeight:F2} m\n" +
            $"Antenna pivot: {v.AntennaPivot:F2} m\n" +
            $"Antenna offset: {v.AntennaOffset:F2} m\n" +
            $"Max steer angle: {v.MaxSteerAngle:F1}°";
    }

    private static string FormatTool(ConfigurationStore store)
    {
        var t = store.Tool;
        string attach = t.IsToolFrontFixed ? "Front fixed"
                      : t.IsToolRearFixed ? "Rear fixed"
                      : t.IsToolTrailing ? "Trailing"
                      : "—";
        return
            $"Width: {t.Width:F2} m\n" +
            $"Overlap: {t.Overlap:F2} m\n" +
            $"Offset: {t.Offset:F2} m\n" +
            $"Sections: {store.NumSections}\n" +
            $"Min coverage: {t.MinCoverage}%\n" +
            $"Attach: {attach}";
    }
}
