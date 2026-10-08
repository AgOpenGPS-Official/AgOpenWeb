// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

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
        }

        if (IsSectionMasterOn || IsManualSectionMode)
        {
            IsSectionMasterOn = false;
            IsManualSectionMode = false;
            _sectionControlService.SetAllSections(SectionButtonState.Off);
            _audioService.Play(SoundEffect.SectionOff);
        }
    }
}
