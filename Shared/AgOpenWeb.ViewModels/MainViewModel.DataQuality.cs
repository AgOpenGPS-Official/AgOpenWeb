// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using AgOpenWeb.Services.DataQuality;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// MainViewModel partial: the data the app is given is judged, and the operator is told when
/// it is not fit to use. One <see cref="DataQualityMonitor"/> reads every sensor (how the
/// position fixes and each module's stream arrive, whether the positions come in order; a new
/// check is a new <see cref="IDataSensor"/>, added in <see cref="BuildDataQuality"/>). Here the
/// operator is told once when a sensor goes faulty and once when it is good again, the log
/// says so, and the bug report dump carries every sensor's figures
/// (<see cref="DataQualityReport"/>).
/// <para>
/// From #922 (22 Nov 2026): a tablet got one fix in five late or not at all and the steer
/// module went silent for 2 s twice a minute. The report said the app "works at maybe 1 Hz",
/// the app itself said nothing, and the cause was only found by opening the dump's GPS log.
/// AgOpenWeb runs on many set-ups, so it judges the data instead of trusting the set-up.
/// </para>
/// A warning only: nothing is switched off, and nothing is repaired (faulty data is put right
/// where it comes from). Runs on the 100 ms module-status loop, judging once a second.
/// </summary>
public partial class MainViewModel
{
    private int _dataQualityTick;
    private DataQualityMonitor? _dataQualityMonitor;

    /// <summary>Each sensor as it was judged in the last second: the receiver's first, then
    /// the modules'. Changed in place, on the host loop.</summary>
    public IReadOnlyList<DataReading> DataQuality => (_dataQualityMonitor ??= BuildDataQuality()).Readings;

    /// <summary>Every sensor there is. A receiver's sentences are not judged while the
    /// simulator is the source: how they arrive is then nobody's trouble.</summary>
    private DataQualityMonitor BuildDataQuality()
    {
        var monitor = new DataQualityMonitor();
        var gps = _autoSteerService.GpsSentences?.DataSensors;
        if (gps != null)
            foreach (var sensor in gps) monitor.Add(sensor, idle: () => IsSimulatorEnabled);
        var modules = _udpService.DataSensors;
        if (modules != null)
            foreach (var sensor in modules) monitor.Add(sensor);
        monitor.Changed += OnDataQualityChanged;
        return monitor;
    }

    /// <summary>Called from the 100 ms module-status loop.</summary>
    internal void UpdateDataQuality()
    {
        if (++_dataQualityTick < 10) return;
        _dataQualityTick = 0;
        JudgeDataQuality();
    }

    /// <summary>Judge every sensor now and tell the operator of a change.</summary>
    internal void JudgeDataQuality() => JudgeDataQuality(System.Diagnostics.Stopwatch.GetTimestamp());

    /// <summary>As <see cref="JudgeDataQuality()"/>, with the current time given (tests).</summary>
    internal void JudgeDataQuality(long now) => (_dataQualityMonitor ??= BuildDataQuality()).Judge(now);

    private void OnDataQualityChanged(IDataSensor sensor, DataReading reading, DataChange change)
    {
        var inv = CultureInfo.InvariantCulture;
        switch (change)
        {
            case DataChange.FaultBegan:
                _logger.LogWarning("[Data] {Sensor}: FAULT, {Figures}", sensor.Name, sensor.Figures());
                ReportFailure(string.Create(inv, $"{sensor.Name}: {sensor.Describe()}. {sensor.Advice}"));
                break;
            case DataChange.FaultDropped:
                // It stopped (or the simulator took over): GPS lost and the module dots say that.
                _logger.LogInformation("[Data] {Sensor}: no longer arriving, so no longer judged", sensor.Name);
                break;
            default:
                _logger.LogInformation("[Data] {Sensor}: {Recovered} ({Figures})", sensor.Name, sensor.Recovered, sensor.Figures());
                ReportFailure(string.Create(inv, $"{sensor.Name}: {sensor.Recovered}"));
                break;
        }
    }

    /// <summary>Every sensor's figures for the bug report dump's <c>system_info.txt</c>.</summary>
    public string DataQualityReport() => (_dataQualityMonitor ??= BuildDataQuality()).Report();
}
