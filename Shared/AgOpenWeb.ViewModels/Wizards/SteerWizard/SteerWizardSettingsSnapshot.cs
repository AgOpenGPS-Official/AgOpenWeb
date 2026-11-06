// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;

namespace AgOpenWeb.ViewModels.Wizards.SteerWizard;

/// <summary>
/// The settings the steer wizard can change, as they were when it opened, so Cancel can put
/// them back. The steps and the web's boxes write into the live settings as they go (the
/// module runs each value at once, which the tests need), so Cancel used to keep everything
/// the finished steps set, unsaved: the module ran them until the next restart, then they
/// were gone. Finish saves; Cancel undoes.
/// </summary>
public sealed class SteerWizardSettingsSnapshot
{
    private readonly VehicleType _vehicleType;
    private readonly double _wheelbase, _trackWidth, _antennaHeight, _antennaPivot, _antennaOffset, _maxSteerAngle;
    private readonly double _rollZero;
    private readonly bool _isRollInvert;
    private readonly AutoSteerConfigDto _autoSteer;
    private readonly bool _isPurePursuit;
    private readonly double _lookAheadHold, _integralGain, _stanleyDistanceGain, _stanleyHeadingGain;
    private readonly bool _hadUnsavedChanges;

    private SteerWizardSettingsSnapshot(ConfigurationStore store)
    {
        var v = store.Vehicle;
        _vehicleType = v.Type;
        _wheelbase = v.Wheelbase;
        _trackWidth = v.TrackWidth;
        _antennaHeight = v.AntennaHeight;
        _antennaPivot = v.AntennaPivot;
        _antennaOffset = v.AntennaOffset;
        _maxSteerAngle = v.MaxSteerAngle;
        _rollZero = store.Ahrs.RollZero;
        _isRollInvert = store.Ahrs.IsRollInvert;
        _autoSteer = store.AutoSteer.ToDto();
        var g = store.Guidance;
        _isPurePursuit = g.IsPurePursuit;
        _lookAheadHold = g.GoalPointLookAheadHold;
        _integralGain = g.PurePursuitIntegralGain;
        _stanleyDistanceGain = g.StanleyDistanceErrorGain;
        _stanleyHeadingGain = g.StanleyHeadingErrorGain;
        _hadUnsavedChanges = store.HasUnsavedChanges;
    }

    /// <summary>The settings as they are now.</summary>
    public static SteerWizardSettingsSnapshot Take(ConfigurationStore store) => new(store);

    /// <summary>
    /// Put the settings back as they were. Each value goes through its own setter, so the
    /// module gets the old settings again (PGN 251/252) and the browsers the old boxes.
    /// </summary>
    public void Restore(ConfigurationStore store)
    {
        var v = store.Vehicle;
        v.Type = _vehicleType;
        v.Wheelbase = _wheelbase;
        v.TrackWidth = _trackWidth;
        v.AntennaHeight = _antennaHeight;
        v.AntennaPivot = _antennaPivot;
        v.AntennaOffset = _antennaOffset;
        v.MaxSteerAngle = _maxSteerAngle;
        store.Ahrs.RollZero = _rollZero;
        store.Ahrs.IsRollInvert = _isRollInvert;
        store.AutoSteer.ApplyFromDto(_autoSteer);
        var g = store.Guidance;
        g.IsPurePursuit = _isPurePursuit;
        g.GoalPointLookAheadHold = _lookAheadHold;
        g.PurePursuitIntegralGain = _integralGain;
        g.StanleyDistanceErrorGain = _stanleyDistanceGain;
        g.StanleyHeadingErrorGain = _stanleyHeadingGain;
        store.HasUnsavedChanges = _hadUnsavedChanges;
    }
}
