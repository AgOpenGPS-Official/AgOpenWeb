// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenWeb.Models.Configuration;

/// <summary>
/// Central configuration store - the ONLY place configuration lives.
/// All components access this directly; no private copies.
/// Implements INotifyPropertyChanged via ObservableObject for UI binding.
/// </summary>
public class ConfigurationStore : ObservableObject
{
    private static ConfigurationStore? _instance;

    /// <summary>
    /// Singleton instance. Use DI where possible, but this provides
    /// easy access for services that need configuration.
    /// </summary>
    public static ConfigurationStore Instance => _instance ??= new ConfigurationStore();

    /// <summary>
    /// For testing - allows replacing the singleton instance
    /// </summary>
    public static void SetInstance(ConfigurationStore store) => _instance = store;

    /// <summary>The backend that owned <paramref name="store"/> stopped: the static lets go of it,
    /// unless another store took its place already (<see cref="AgOpenWeb.Models.State.BackendStateLease"/>).</summary>
    internal static void Release(ConfigurationStore store) =>
        System.Threading.Interlocked.CompareExchange(ref _instance, null, store);

    // Sub-configurations (each is an ObservableObject for binding)
    public VehicleConfig Vehicle { get; } = new();
    public ToolConfig Tool { get; } = new();
    public GuidanceConfig Guidance { get; } = new();
    public DisplayConfig Display { get; } = new();
    public SimulatorConfig Simulator { get; } = new();
    public ConnectionConfig Connections { get; } = new();
    public AhrsConfig Ahrs { get; } = new();
    public MachineConfig Machine { get; } = new();
    public TramConfig Tram { get; } = new();
    public AutoSteerConfig AutoSteer { get; } = new();
    public HotkeyConfig Hotkeys { get; } = new();

    // Profile management — split into vehicle and tool sides per the
    // AgOpenGPS 6.8.2-style vehicle/tool split (#346). The pre-split
    // ActiveProfileName/Path used to track a single combined profile and
    // were renamed to ActiveVehicleProfileName/Path; the tool side adds
    // ActiveToolProfileName/Path tracking the loaded tool profile.
    private string _activeProfileName = "Default";
    public string ActiveVehicleProfileName
    {
        get => _activeProfileName;
        set => SetProperty(ref _activeProfileName, value);
    }

    private string _activeProfilePath = string.Empty;
    public string ActiveVehicleProfilePath
    {
        get => _activeProfilePath;
        set => SetProperty(ref _activeProfilePath, value);
    }

    private string _activeToolProfileName = "Default";
    public string ActiveToolProfileName
    {
        get => _activeToolProfileName;
        set => SetProperty(ref _activeToolProfileName, value);
    }

    /// <summary>
    /// What the last profile load converted from an older format (a tool's hitch, #547), for the
    /// operator and the log; null when nothing was. The loader saves the converted files at once.
    /// </summary>
    public string? ProfileMigrationNote { get; set; }

    private string _activeToolProfilePath = string.Empty;
    public string ActiveToolProfilePath
    {
        get => _activeToolProfilePath;
        set => SetProperty(ref _activeToolProfilePath, value);
    }

    // Dirty tracking for save prompts
    private bool _hasUnsavedChanges;
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        set => SetProperty(ref _hasUnsavedChanges, value);
    }

    // Unit settings
    private bool _isMetric = true;
    public bool IsMetric
    {
        get => _isMetric;
        set => SetProperty(ref _isMetric, value);
    }

    // Section configuration
    private int _numSections = 1;
    public int NumSections
    {
        get => _numSections;
        set
        {
            int clamped = Math.Clamp(value, 1, ToolConfig.MaxSections);
            int previous = _numSections;
            // Seed newly-activated sections with the current Default Section
            // Width ("Width applied to new sections") BEFORE raising the
            // NumSections change, so listeners that re-read section widths see
            // the seeded values. Sections that already existed keep their
            // individually-set widths. Profile load sets explicit per-section
            // widths AFTER NumSections, so those overwrite this seed (#417).
            if (clamped > previous)
            {
                for (int i = previous; i < clamped; i++)
                    Tool.SetSectionWidth(i, Tool.DefaultSectionWidth);
            }
            SetProperty(ref _numSections, clamped);
        }
    }

    // The operator's section count and default width (Vehicle & Tool › Sections) set every
    // section to the default width (#676). AgOpenGPS does it on a count change (ConfigTool
    // cboxNumSections), and a default width that changed nothing on screen confused its users.
    // Sections of their own widths are typed one by one after. A profile load sets NumSections
    // and the widths directly, not through these.

    /// <summary>The operator changed the section count: every section takes the default width.</summary>
    public void ApplySectionCount(int count)
    {
        Tool.SetAllSectionWidths(Tool.DefaultSectionWidth);
        NumSections = count;
    }

    /// <summary>The operator changed the default section width: every section takes it.</summary>
    public void ApplyDefaultSectionWidth(double cm)
    {
        Tool.DefaultSectionWidth = cm;
        Tool.SetAllSectionWidths(cm);
    }

    /// <summary>
    /// Calculates actual tool width from active sections (in meters).
    /// Use this for guidance calculations instead of Tool.Width.
    /// </summary>
    public double ActualToolWidth
    {
        get
        {
            double total = 0;
            for (int i = 0; i < _numSections && i < ToolConfig.MaxSections; i++)
            {
                total += Tool.GetSectionWidth(i) / 100.0; // cm to meters
            }
            return total > 0 ? total : Tool.Width; // Fallback to stored width if no sections
        }
    }

    private double[] _sectionPositions = new double[ToolConfig.MaxSections + 1];
    public double[] SectionPositions
    {
        get => _sectionPositions;
        set => SetProperty(ref _sectionPositions, value);
    }

    // Events for significant changes
    public event EventHandler? ProfileLoaded;
    public event EventHandler? ProfileSaved;

    /// <summary>
    /// Raises the ProfileLoaded event
    /// </summary>
    public void OnProfileLoaded() => ProfileLoaded?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Raises the ProfileSaved event
    /// </summary>
    public void OnProfileSaved() => ProfileSaved?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Marks the configuration as having unsaved changes
    /// </summary>
    public void MarkChanged() => HasUnsavedChanges = true;
}
