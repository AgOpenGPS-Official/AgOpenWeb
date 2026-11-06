// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// AutoSteer goes off when a Vehicle &amp; Tool setting it steers by changes under it: the
/// implement's width or offset moves every pass line, the antenna's offset, pivot or height
/// and the roll setup move where the app thinks the machine is, a profile load changes all
/// of them. Steering on after that is a jump of the line under the wheels. In the Vehicle &amp;
/// Tool scenarios, cutting the boom from 8 to 6 sections mid-pass swung a tractor 3.3 m across
/// the pass at a 30° steer angle, a 2.5 m implement offset 2.8 m, and loading another tractor's
/// profile took it 8 m off its pass. Any browser on the LAN can change a setting, so the cab
/// may not even know. Off, with the reason; the operator engages again on the new line.
/// </summary>
public partial class MainViewModel
{
    private static readonly HashSet<string> VehicleSteersBy = new(StringComparer.Ordinal)
    {
        nameof(VehicleConfig.Type), nameof(VehicleConfig.Wheelbase), nameof(VehicleConfig.AntennaHeight),
        nameof(VehicleConfig.AntennaPivot), nameof(VehicleConfig.AntennaOffset), nameof(VehicleConfig.HitchLength),
    };

    private static readonly HashSet<string> ToolSteersBy = new(StringComparer.Ordinal)
    {
        nameof(ToolConfig.Width), nameof(ToolConfig.SectionWidths), nameof(ToolConfig.Overlap), nameof(ToolConfig.Offset),
        nameof(ToolConfig.HitchLength), nameof(ToolConfig.TrailingHitchLength), nameof(ToolConfig.TankTrailingHitchLength),
        nameof(ToolConfig.TrailingToolToPivotLength), nameof(ToolConfig.CurrentToolType),
    };

    private int _steeringGeometry;

    private void WatchSettingsUnderAutoSteer()
    {
        var s = ConfigStore;
        _steeringGeometry = SteeringGeometry();
        s.Vehicle.PropertyChanged += (_, e) => { if (VehicleSteersBy.Contains(e.PropertyName ?? "")) SteeringSettingChanged("vehicle settings changed"); };
        s.Tool.PropertyChanged += (_, e) => { if (ToolSteersBy.Contains(e.PropertyName ?? "")) SteeringSettingChanged("implement settings changed"); };
        s.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConfigurationStore.NumSections)) SteeringSettingChanged("implement settings changed");
        };
        s.Connections.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ConnectionConfig.IsDualGps) or nameof(ConnectionConfig.DualHeadingOffset))
                SteeringSettingChanged("GPS settings changed");
        };
        s.Ahrs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AhrsConfig.RollZero) or nameof(AhrsConfig.IsRollInvert))
                SteeringSettingChanged("roll settings changed");
        };
    }

    // Some setters announce a change without one (a section width set to what it was, the
    // implement type picked again): only a real change of what AutoSteer steers by counts.
    private void SteeringSettingChanged(string reason)
    {
        int now = SteeringGeometry();
        if (now == _steeringGeometry) return;
        _steeringGeometry = now;
        StopAutoSteerForSettings(reason);
    }

    private int SteeringGeometry()
    {
        var s = ConfigStore; var v = s.Vehicle; var t = s.Tool;
        var h = new HashCode();
        h.Add(v.Type); h.Add(v.Wheelbase); h.Add(v.AntennaHeight); h.Add(v.AntennaPivot); h.Add(v.AntennaOffset); h.Add(v.HitchLength);
        h.Add(s.NumSections);
        for (int i = 0; i < s.NumSections; i++) h.Add(t.GetSectionWidth(i));
        h.Add(t.Width); h.Add(t.Overlap); h.Add(t.Offset); h.Add(t.HitchLength); h.Add(t.TrailingHitchLength);
        h.Add(t.TankTrailingHitchLength); h.Add(t.TrailingToolToPivotLength);
        h.Add(t.IsToolFrontFixed); h.Add(t.IsToolRearFixed); h.Add(t.IsToolTrailing); h.Add(t.IsToolTBT);
        h.Add(s.Connections.IsDualGps); h.Add(s.Connections.DualHeadingOffset);
        h.Add(s.Ahrs.RollZero); h.Add(s.Ahrs.IsRollInvert);
        return h.ToHashCode();
    }

    private void StopAutoSteerForSettings(string reason)
    {
        if (!IsAutoSteerEngaged) return;
        IsAutoSteerEngaged = false;
        _autoSteerService.Disengage();
        SyncGuidanceStateToPipeline();
        _audioService.Play(SoundEffect.AutoSteerOff);
        LogAutoSteerOff(reason);
        ReportFailure("AutoSteer off: " + reason);
    }
}
