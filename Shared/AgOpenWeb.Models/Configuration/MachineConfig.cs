// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenWeb.Models.Configuration;

/// <summary>
/// Pin function assignments for machine relay control.
/// </summary>
public enum PinFunction
{
    None = 0,
    Section1 = 1,
    Section2 = 2,
    Section3 = 3,
    Section4 = 4,
    Section5 = 5,
    Section6 = 6,
    Section7 = 7,
    Section8 = 8,
    Section9 = 9,
    Section10 = 10,
    Section11 = 11,
    Section12 = 12,
    Section13 = 13,
    Section14 = 14,
    Section15 = 15,
    Section16 = 16,
    HydUp = 17,
    HydDown = 18,
    TramLeft = 19,
    TramRight = 20,
    GeoStop = 21
}

/// <summary>
/// Machine module and relay configuration.
/// Controls hydraulic lift timing and pin assignments.
/// </summary>
public class MachineConfig : ObservableObject
{
    // Hydraulic Lift Settings
    private bool _hydraulicLiftEnabled;
    public bool HydraulicLiftEnabled
    {
        get => _hydraulicLiftEnabled;
        set => SetProperty(ref _hydraulicLiftEnabled, value);
    }

    private int _raiseTime = 4;
    public int RaiseTime
    {
        get => _raiseTime;
        set => SetProperty(ref _raiseTime, value);
    }

    private double _lookAhead = 2.0;
    public double LookAhead
    {
        get => _lookAhead;
        set => SetProperty(ref _lookAhead, value);
    }

    private int _lowerTime = 2;
    public int LowerTime
    {
        get => _lowerTime;
        set => SetProperty(ref _lowerTime, value);
    }

    private bool _invertRelay;
    public bool InvertRelay
    {
        get => _invertRelay;
        set => SetProperty(ref _invertRelay, value);
    }

    // User Custom Values (sent to machine module)
    private int _user1Value = 1;
    public int User1Value
    {
        get => _user1Value;
        set => SetProperty(ref _user1Value, value);
    }

    private int _user2Value = 2;
    public int User2Value
    {
        get => _user2Value;
        set => SetProperty(ref _user2Value, value);
    }

    private int _user3Value = 3;
    public int User3Value
    {
        get => _user3Value;
        set => SetProperty(ref _user3Value, value);
    }

    private int _user4Value = 4;
    public int User4Value
    {
        get => _user4Value;
        set => SetProperty(ref _user4Value, value);
    }

    // Sprayer readout (SprayerService): what the machine module's PGN 160 is checked against.
    // The nozzles of a section are its width over the nozzle spacing.

    private double _sprayerNozzleSpacing = 0.5;
    /// <summary>Distance between nozzles on the boom, m. 0 = no flow check.</summary>
    public double SprayerNozzleSpacing
    {
        get => _sprayerNozzleSpacing;
        set => SetProperty(ref _sprayerNozzleSpacing, value);
    }

    private double _sprayerNozzleFlow = 1.6;
    /// <summary>One nozzle's flow at <see cref="SprayerReferencePressure"/>, L/min (the
    /// nozzle chart; ISO red 04 = 1.6 L/min at 3 bar). 0 = no flow check.</summary>
    public double SprayerNozzleFlow
    {
        get => _sprayerNozzleFlow;
        set => SetProperty(ref _sprayerNozzleFlow, value);
    }

    private double _sprayerReferencePressure = 3.0;
    /// <summary>The pressure the nozzle flow is given at, bar.</summary>
    public double SprayerReferencePressure
    {
        get => _sprayerReferencePressure;
        set => SetProperty(ref _sprayerReferencePressure, value);
    }

    private double _sprayerMinPressure = 1.0;
    /// <summary>Alarm below this pressure while a section sprays, bar. 0 = off.</summary>
    public double SprayerMinPressure
    {
        get => _sprayerMinPressure;
        set => SetProperty(ref _sprayerMinPressure, value);
    }

    private double _sprayerMaxPressure = 6.0;
    /// <summary>Alarm above this pressure, bar. 0 = off.</summary>
    public double SprayerMaxPressure
    {
        get => _sprayerMaxPressure;
        set => SetProperty(ref _sprayerMaxPressure, value);
    }

    private int _sprayerFlowTolerance = 20;
    /// <summary>How far the measured flow may be from the expected one, %.</summary>
    public int SprayerFlowTolerance
    {
        get => _sprayerFlowTolerance;
        set => SetProperty(ref _sprayerFlowTolerance, value);
    }

    // Pin Assignments (24 pins)
    // Default: Pins 1-6 = Section 1-6, rest = None
    private PinFunction[] _pinAssignments = new PinFunction[24]
    {
        PinFunction.Section1, PinFunction.Section2, PinFunction.Section3,
        PinFunction.Section4, PinFunction.Section5, PinFunction.Section6,
        PinFunction.None, PinFunction.None, PinFunction.None, PinFunction.None,
        PinFunction.None, PinFunction.None, PinFunction.None, PinFunction.None,
        PinFunction.None, PinFunction.None, PinFunction.None, PinFunction.None,
        PinFunction.None, PinFunction.None, PinFunction.None, PinFunction.None,
        PinFunction.None, PinFunction.None
    };

    public PinFunction[] PinAssignments
    {
        get => _pinAssignments;
        set => SetProperty(ref _pinAssignments, value);
    }

    /// <summary>
    /// Get or set a specific pin assignment.
    /// </summary>
    public PinFunction GetPinAssignment(int pinIndex)
    {
        if (pinIndex < 0 || pinIndex >= 24) return PinFunction.None;
        return _pinAssignments[pinIndex];
    }

    public void SetPinAssignment(int pinIndex, PinFunction function)
    {
        if (pinIndex < 0 || pinIndex >= 24) return;
        var newAssignments = (PinFunction[])_pinAssignments.Clone();
        newAssignments[pinIndex] = function;
        PinAssignments = newAssignments;
    }

    /// <summary>
    /// Reset all pins to default (Pins 1-6 = Section 1-6, rest = None).
    /// </summary>
    public void ResetPinAssignments()
    {
        PinAssignments = new PinFunction[24]
        {
            PinFunction.Section1, PinFunction.Section2, PinFunction.Section3,
            PinFunction.Section4, PinFunction.Section5, PinFunction.Section6,
            PinFunction.None, PinFunction.None, PinFunction.None, PinFunction.None,
            PinFunction.None, PinFunction.None, PinFunction.None, PinFunction.None,
            PinFunction.None, PinFunction.None, PinFunction.None, PinFunction.None,
            PinFunction.None, PinFunction.None, PinFunction.None, PinFunction.None,
            PinFunction.None, PinFunction.None
        };
    }
}
