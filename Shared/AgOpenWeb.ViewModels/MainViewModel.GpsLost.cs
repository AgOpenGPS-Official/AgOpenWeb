// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// MainViewModel partial: GPS data lost. When the position sentences stop (the module
/// unplugged, the network gone, the simulator off) for <see cref="GpsLostAfterMs"/>,
/// autosteer disengages and the sections go off, and the client shows a warning over
/// the map until data flows again, from the module or the simulator. AgOpenGPS stops
/// guidance and blanks the map when its sentence counter runs out; here the map stays,
/// and the operator re-engages once data is back.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Longer than a dropped packet or two at 10 Hz, so the warning doesn't flap.</summary>
    internal const int GpsLostAfterMs = 2000;

    private DateTime? _gpsSilentSince;

    /// <summary>Called from the 100 ms module-status loop with the GPS data-flow state.</summary>
    internal void UpdateGpsLost(bool gpsOk, DateTime now)
    {
        if (gpsOk)
        {
            _gpsSilentSince = null;
            if (State.Connections.IsGpsLost)
            {
                State.Connections.IsGpsLost = false;
                _logger.LogInformation("GPS data back");
            }
            return;
        }

        _gpsSilentSince ??= now;
        if (State.Connections.IsGpsLost || (now - _gpsSilentSince.Value).TotalMilliseconds < GpsLostAfterMs)
            return;

        State.Connections.IsGpsLost = true;
        _logger.LogWarning("GPS data lost; autosteer and sections off");

        if (IsAutoSteerEngaged)
        {
            IsAutoSteerEngaged = false;
            _autoSteerService.Disengage();
            SyncGuidanceStateToPipeline();
            _audioService.Play(SoundEffect.AutoSteerOff);
            LogAutoSteerOff("GPS data lost");
        }

        if (SwitchAllSectionsOff())
            _audioService.Play(SoundEffect.SectionOff);
    }

    /// <summary>
    /// Both masters off and every section button Off, at once: the module gets no on bit from
    /// the next tick. Also a section switched on by its own button or hotkey with neither
    /// master on (#316). Used when the GPS is lost and when the field closes (AgOpenGPS
    /// JobClose: manual and auto off, AllSectionsAndButtonsToState(Off)). True when anything
    /// was on.
    /// </summary>
    private bool SwitchAllSectionsOff()
    {
        if (!(IsSectionMasterOn || IsManualSectionMode || AnySectionNotOff())) return false;
        IsSectionMasterOn = false;
        IsManualSectionMode = false;
        _sectionControlService.SetAllSections(SectionButtonState.Off);
        return true;
    }

    private bool AnySectionNotOff()
    {
        var states = _sectionControlService.SectionStates;
        for (int i = 0; i < states.Count; i++)
            if (states[i].ButtonState != SectionButtonState.Off || states[i].IsOn) return true;
        return false;
    }
}
