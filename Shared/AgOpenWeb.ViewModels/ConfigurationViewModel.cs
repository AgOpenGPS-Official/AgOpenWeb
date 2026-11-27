// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Input;

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.YouTurn;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.Interfaces;
using CommunityToolkit.Mvvm.Input;

using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// ViewModel for the Configuration Dialog.
/// Binds directly to ConfigurationStore - no property mapping needed.
/// </summary>
public partial class ConfigurationViewModel : ObservableObject
{
    private readonly IConfigurationService _configService;

    // Dialog visibility is driven by UIState.ActiveDialog (chain navigation);
    // the split Vehicle/Tool config dialogs bind to State.UI.IsVehicleConfigDialogVisible /
    // IsToolConfigDialogVisible. This VM only owns the config data + Apply/Cancel.

    #region Numeric Input Dialog

    private bool _isNumericInputVisible;
    public bool IsNumericInputVisible
    {
        get => _isNumericInputVisible;
        set => SetProperty(ref _isNumericInputVisible, value);
    }

    private decimal? _numericInputValue;
    public decimal? NumericInputValue
    {
        get => _numericInputValue;
        set => SetProperty(ref _numericInputValue, value);
    }

    private string _numericInputDisplayText = string.Empty;
    public string NumericInputDisplayText
    {
        get => _numericInputDisplayText;
        set => SetProperty(ref _numericInputDisplayText, value);
    }

    private bool _numericInputIntegerOnly;
    public bool NumericInputIntegerOnly
    {
        get => _numericInputIntegerOnly;
        set => SetProperty(ref _numericInputIntegerOnly, value);
    }

    private bool _numericInputAllowNegative = true;
    public bool NumericInputAllowNegative
    {
        get => _numericInputAllowNegative;
        set => SetProperty(ref _numericInputAllowNegative, value);
    }

    private double _numericInputMin = double.MinValue;
    private double _numericInputMax = double.MaxValue;
    private bool _isFirstDigitEntry = true; // Track if user has started typing

    private Action<double>? _numericInputCallback;

    public ICommand ConfirmNumericInputCommand { get; private set; } = null!;
    public ICommand CancelNumericInputCommand { get; private set; } = null!;
    public ICommand NumericInputDigitCommand { get; private set; } = null!;
    public ICommand NumericInputBackspaceCommand { get; private set; } = null!;
    public ICommand NumericInputClearCommand { get; private set; } = null!;
    public ICommand NumericInputNegateCommand { get; private set; } = null!;

    private void InitializeNumericInputCommands()
    {
        ConfirmNumericInputCommand = new RelayCommand(() =>
        {
            if (_numericInputCallback != null)
            {
                // Parse from display text to get actual value
                // Use InvariantCulture to handle decimal point consistently
                if (decimal.TryParse(NumericInputDisplayText, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                {
                    var value = (double)parsed;
                    // Clamp to min/max
                    value = Math.Clamp(value, _numericInputMin, _numericInputMax);
                    _numericInputCallback(value);
                    Config.MarkChanged();
                }
            }
            IsNumericInputVisible = false;
            _numericInputCallback = null;
        });

        CancelNumericInputCommand = new RelayCommand(() =>
        {
            IsNumericInputVisible = false;
            _numericInputCallback = null;
        });

        NumericInputDigitCommand = new RelayCommand<string>(digit =>
        {
            if (string.IsNullOrEmpty(digit)) return;

            // Handle decimal point
            if (digit == ".")
            {
                if (NumericInputIntegerOnly) return;

                if (_isFirstDigitEntry)
                {
                    // Start fresh with "0."
                    NumericInputDisplayText = "0.";
                    _isFirstDigitEntry = false;
                }
                else if (!NumericInputDisplayText.Contains("."))
                {
                    NumericInputDisplayText += ".";
                }
                return;
            }

            // First digit replaces the initial value
            if (_isFirstDigitEntry)
            {
                NumericInputDisplayText = digit;
                _isFirstDigitEntry = false;
            }
            else
            {
                // Append digit
                if (NumericInputDisplayText == "0")
                    NumericInputDisplayText = digit;
                else
                    NumericInputDisplayText += digit;
            }

            // Update the backing value
            if (decimal.TryParse(NumericInputDisplayText, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                _numericInputValue = parsed;
                OnPropertyChanged(nameof(NumericInputValue));
            }
        });

        NumericInputBackspaceCommand = new RelayCommand(() =>
        {
            _isFirstDigitEntry = false; // User is editing

            var current = NumericInputDisplayText;
            if (current.Length > 1)
            {
                // Handle negative numbers - don't delete just the minus sign
                if (current.Length == 2 && current.StartsWith("-"))
                    NumericInputDisplayText = "0";
                else
                    NumericInputDisplayText = current.Substring(0, current.Length - 1);
            }
            else
            {
                NumericInputDisplayText = "0";
            }

            if (decimal.TryParse(NumericInputDisplayText, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                _numericInputValue = parsed;
                OnPropertyChanged(nameof(NumericInputValue));
            }
        });

        NumericInputClearCommand = new RelayCommand(() =>
        {
            NumericInputDisplayText = "0";
            _numericInputValue = 0;
            _isFirstDigitEntry = false;
            OnPropertyChanged(nameof(NumericInputValue));
        });

        NumericInputNegateCommand = new RelayCommand(() =>
        {
            if (!NumericInputAllowNegative) return;

            // Do NOT change _isFirstDigitEntry -- negate toggles sign without
            // affecting whether the next digit replaces or appends

            if (NumericInputDisplayText.StartsWith("-"))
            {
                NumericInputDisplayText = NumericInputDisplayText.Substring(1);
            }
            else if (NumericInputDisplayText != "0")
            {
                NumericInputDisplayText = "-" + NumericInputDisplayText;
            }

            if (decimal.TryParse(NumericInputDisplayText, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                _numericInputValue = parsed;
                OnPropertyChanged(nameof(NumericInputValue));
            }
        });
    }

    #endregion

    #region Text Input Dialog

    private void InitializeTextInputCommands()
    {
    }

    #endregion

    #region Color Picker Dialog

    // -1 = single coverage color, 0-15 = section index

    private void InitializeColorPickerCommands()
    {
    }

    #endregion

    #region Direct Access to Configuration

    /// <summary>
    /// The configuration store - bind directly to sub-configs in XAML
    /// Example: {Binding Config.Vehicle.Wheelbase}
    /// </summary>
    public ConfigurationStore Config => _configService.Store;

    // Convenience accessors for cleaner XAML bindings
    public VehicleConfig Vehicle => Config.Vehicle;
    public ToolConfig Tool => Config.Tool;
    public GuidanceConfig Guidance => Config.Guidance;
    public DisplayConfig Display => Config.Display;
    public SimulatorConfig Simulator => Config.Simulator;

    /// <summary>Persistent application state (day/night value, etc.) — survives restart.</summary>
    public PersistentAppState PersistentState => PersistentAppState.Instance;
    public ConnectionConfig Connections => Config.Connections;
    public AhrsConfig Ahrs => Config.Ahrs;
    public MachineConfig Machine => Config.Machine;

    // ISO 11783 hitch/coupling types, in code order (list index = ISO code + 1, so
    // index 0 = code -1 "Not available", index 1 = code 0 "Unknown", ...). The combo
    // shows these descriptions; SelectedHitchType maps to/from Tool.HitchType (the code).
    public ObservableCollection<string> HitchTypeOptions { get; } = new()
    {
        "Not available",
        "Unknown",
        "ISO 6489-3 Tractor drawbar",
        "ISO 730 Three-point-hitch semi-mounted",
        "ISO 730 Three-point-hitch mounted",
        "ISO 6489-1 Hitch-hook",
        "ISO 6489-2 Clevis coupling 40",
        "ISO 6489-4 Piton type coupling",
        "ISO 6489-5 CUNA hitch",
        "ISO 24347 Ball type hitch",
        "Chassis Mounted - Self-Propelled",
        "ISO 5692-2 Pivot wagon hitch"
    };

    /// <summary>
    /// Calculated total width from sections based on mode.
    /// In Individual mode: sum of first NumSections widths.
    /// In Zone mode: NumSections × DefaultSectionWidth.
    /// </summary>
    public double CalculatedSectionTotal
    {
        get
        {
            if (Tool.IsSectionsNotZones)
            {
                // Individual sections mode - sum actual widths
                double total = 0;
                for (int i = 0; i < Config.NumSections && i < Models.Configuration.ToolConfig.MaxSections; i++)
                    total += Tool.GetSectionWidth(i);
                return total / 100.0; // cm to meters
            }
            else
            {
                // Zones mode - all sections same width
                return Config.NumSections * Tool.DefaultSectionWidth / 100.0;
            }
        }
    }

    // ── Units (#417) ──────────────────────────────────────────────────
    // Widths are stored in cm; totals in meters. When the user selects
    // Imperial, section widths display/edit in inches and totals in feet.
    // Conversion happens only here at the display/input boundary — the
    // model stays metric. All of these are reactive: raised on IsMetric,
    // NumSections, DefaultSectionWidth, and individual width edits.

    /// <summary>Default section width in the current display unit (TwoWay).</summary>
    public double DefaultSectionWidthDisplay
    {
        get => Config.IsMetric
            ? Tool.DefaultSectionWidth
            : UnitConversion.CmToInches(Tool.DefaultSectionWidth);
        set
        {
            Tool.DefaultSectionWidth = Config.IsMetric ? value : UnitConversion.InchesToCm(value);
        }
    }

    /// <summary>Formatted total tool width with unit ("16.00 m" / "52.49 ft").
    /// Used by both the in-tab total and the dialog footer — replaces the
    /// stale, non-notifying Config.ActualToolWidth binding (#417 math bug).</summary>
    public string CalculatedTotalWidthText
    {
        get
        {
            double meters = CalculatedSectionTotal;
            return Config.IsMetric
                ? $"{meters:F2} m"
                : $"{UnitConversion.MetersToFeet(meters):F2} ft";
        }
    }

    /// <summary>
    /// Single coverage color when multi-colored sections is disabled.
    /// </summary>
    public uint SingleCoverageColor => Tool.SingleCoverageColor;

    /// <summary>
    /// Refreshes all section width properties after a change.
    /// </summary>
    private void RefreshSectionWidthProperties()
    {
        OnPropertyChanged(nameof(CalculatedSectionTotal));
        OnPropertyChanged(nameof(CalculatedTotalWidthText));
    }

    /// <summary>
    /// Re-raise every unit-dependent display property after a metric/imperial
    /// switch so widths and totals re-render in the new unit (#417).
    /// </summary>
    private void RefreshUnitDependentProperties()
    {
        OnPropertyChanged(nameof(DefaultSectionWidthDisplay));
        OnPropertyChanged(nameof(CalculatedTotalWidthText));
        RefreshSectionWidthProperties();
    }

    #endregion

    #region Profile Management

    /// <summary>
    /// Whether there are unsaved changes (delegates to ConfigurationStore)
    /// </summary>
    public bool HasUnsavedChanges
    {
        get => Config.HasUnsavedChanges;
        set => Config.HasUnsavedChanges = value;
    }

    #endregion

    #region Commands

    public ICommand ApplyCommand { get; }
    public ICommand CancelCommand { get; }

    public ICommand SetRollZeroCommand { get; private set; } = null!;

    public ICommand ToggleTramDisplayCommand { get; private set; } = null!;

    public ICommand ToggleGridCommand { get; private set; } = null!;

    #endregion

    #region Events

    public event EventHandler? CloseRequested;

    #endregion

    public ConfigurationViewModel(IConfigurationService configService)
    {
        _configService = configService;

        // Initialize commands
        ApplyCommand = new RelayCommand(ApplyChanges);
        CancelCommand = new RelayCommand(Cancel);

        // Initialize numeric input commands
        InitializeNumericInputCommands();
        InitializeTextInputCommands();
        InitializeColorPickerCommands();

        // Initialize edit commands for all tabs
        InitializeVehicleEditCommands();
        InitializeToolEditCommands();
        InitializeSectionsEditCommands();
        InitializeUTurnEditCommands();
        InitializeGpsEditCommands();
        InitializeRollEditCommands();
        InitializeTramCommands();
        InitializeMachineCommands();
        InitializeDisplayCommands();
        InitializeAdditionalOptionsCommands();

        // Subscribe to config changes for HasUnsavedChanges notification
        Config.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConfigurationStore.HasUnsavedChanges))
            {
                OnPropertyChanged(nameof(HasUnsavedChanges));
            }
            // Update calculated section total when NumSections changes.
            // Also refresh the per-section displays so sections newly seeded
            // with the Default Section Width render their seeded value (#417).
            if (e.PropertyName == nameof(ConfigurationStore.NumSections))
            {
                RefreshSectionWidthProperties();
            }
            // Re-render all widths/totals in the new unit on metric switch (#417)
            if (e.PropertyName == nameof(ConfigurationStore.IsMetric))
            {
                RefreshUnitDependentProperties();
            }
        };

        // Subscribe to tool changes for CalculatedSectionTotal
        Tool.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ToolConfig.DefaultSectionWidth))
            {
                OnPropertyChanged(nameof(CalculatedSectionTotal));
                OnPropertyChanged(nameof(CalculatedTotalWidthText));
                OnPropertyChanged(nameof(DefaultSectionWidthDisplay));
            }
        };

    }

    private void InitializeVehicleEditCommands()
    {
    }

    private void InitializeToolEditCommands()
    {
    }

    private void InitializeSectionsEditCommands()
    {
    }

    private void InitializeUTurnEditCommands()
    {
    }

    private void InitializeGpsEditCommands()
    {
    }

    private void InitializeRollEditCommands()
    {
        // Set roll zero to current roll value (would need access to current sensor data)
        // For now, this just resets to 0
        SetRollZeroCommand = new RelayCommand(() =>
        {
            Ahrs.RollZero = 0;
            Config.MarkChanged();
        });
    }

    private void InitializeTramCommands()
    {
        ToggleTramDisplayCommand = new RelayCommand(() =>
        {
            Guidance.TramDisplay = !Guidance.TramDisplay;
            Config.MarkChanged();
        });
    }

    private void InitializeMachineCommands()
    {
    }

    private void InitializeDisplayCommands()
    {
        ToggleGridCommand = new RelayCommand(() =>
        {
            Display.GridVisible = !Display.GridVisible;
            Config.MarkChanged();
        });
    }

    private void InitializeAdditionalOptionsCommands()
    {
    }

    private void ApplyChanges()
    {
        _configService.SaveProfiles(Config.ActiveVehicleProfileName, Config.ActiveToolProfileName);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Cancel()
    {
        _configService.ReloadCurrentProfile();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
