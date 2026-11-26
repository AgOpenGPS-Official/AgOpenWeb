// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AgOpenWeb.Services.DataQuality;

/// <summary>What changed for a sensor between two judgements.</summary>
public enum DataChange
{
    /// <summary>The data is no longer fit to use.</summary>
    FaultBegan,
    /// <summary>The data is good again.</summary>
    FaultEnded,
    /// <summary>The data stopped, or is no longer looked at, while it was faulty: another
    /// warning says that it stopped (GPS lost, the module dots).</summary>
    FaultDropped,
}

/// <summary>
/// The one place where the data the app is given is judged: every <see cref="IDataSensor"/>
/// is read here, and a change is reported once through <see cref="Changed"/>. What the
/// operator sees, what the log says and what the bug report carries is the same for every
/// sensor; a new kind of check is a new sensor, added with <see cref="Add"/>.
/// <para>Used from the host loop only.</para>
/// </summary>
public sealed class DataQualityMonitor
{
    private readonly List<IDataSensor> _sensors = new();
    private readonly List<Func<bool>?> _idle = new();
    private DataReading[] _readings = Array.Empty<DataReading>();
    private bool[] _told = Array.Empty<bool>();
    private bool[] _explained = Array.Empty<bool>();

    /// <summary>A sensor went faulty, or is no longer. Its texts describe the reading given.</summary>
    public event Action<IDataSensor, DataReading, DataChange>? Changed;

    /// <summary>The sensors, in the order they were added.</summary>
    public IReadOnlyList<IDataSensor> Sensors => _sensors;

    /// <summary>Each sensor's last judgement, in the order of <see cref="Sensors"/>. Changed in place.</summary>
    public IReadOnlyList<DataReading> Readings => _readings;

    /// <param name="idle">True while the sensor's data is not the data in use (a receiver's
    /// sentences with the simulator as the source): it is then not judged.</param>
    public void Add(IDataSensor sensor, Func<bool>? idle = null)
    {
        _sensors.Add(sensor);
        _idle.Add(idle);
        Array.Resize(ref _readings, _sensors.Count);
        Array.Resize(ref _told, _sensors.Count);
        Array.Resize(ref _explained, _sensors.Count);
        _readings[^1] = new DataReading(sensor.Name, DataFinding.NoData, false, 0, 0, 0);
    }

    /// <summary>Read every sensor and report what changed.</summary>
    public void Judge(long now)
    {
        for (int i = 0; i < _sensors.Count; i++)
        {
            var r = _sensors[i].Read(now);
            if (_idle[i]?.Invoke() == true) r = r with { Finding = DataFinding.NoData, Faulty = false };
            _readings[i] = r;
        }
        for (int i = 0; i < _sensors.Count; i++)
        {
            var sensor = _sensors[i];
            var r = _readings[i];
            // One message per cause: a fault another sensor explains is that sensor's to report.
            int by = r.Faulty && sensor.ExplainedBy != null ? _sensors.IndexOf(sensor.ExplainedBy) : -1;
            _explained[i] = by >= 0 && _readings[by].Faulty;
            if (_explained[i])
            {
                _readings[i] = r with { Faulty = false };
                _told[i] = false;
                continue;
            }
            if (r.Faulty == _told[i]) continue;
            _told[i] = r.Faulty;
            Changed?.Invoke(sensor, r,
                r.Faulty ? DataChange.FaultBegan
                : r.Finding == DataFinding.NoData ? DataChange.FaultDropped
                : DataChange.FaultEnded);
        }
    }

    /// <summary>Every sensor's figures for the bug report dump's <c>system_info.txt</c>, so a
    /// report of a slow or late app is told from faulty data without opening the GPS log.</summary>
    public string Report()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("Data quality (each sensor's last judgement; FAULT = the operator was told):");
        if (_sensors.Count == 0) sb.AppendLine("  not judged yet");
        for (int i = 0; i < _sensors.Count; i++)
        {
            var r = _readings[i];
            sb.Append(inv, $"  {r.Name}: ");
            if (r.Faulty) sb.Append("FAULT, ");
            sb.Append(r.Finding == DataFinding.NoData && !r.Faulty && _idle[i]?.Invoke() == true
                ? "not judged"
                : _sensors[i].Figures());
            if (_explained[i]) sb.Append(inv, $" (not reported: {_sensors[i].ExplainedBy!.Name} says why)");
            sb.AppendLine(inv, $", faults {_sensors[i].Faults}");
        }
        return sb.ToString();
    }
}
