// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AgOpenWeb.Services.Network;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// MainViewModel partial: the data from the modules arrives with gaps or in bursts. The
/// position fixes and each module's own periodic stream are judged by
/// <see cref="StreamQualityMonitor"/>; here the operator is told once when a stream goes
/// faulty and once when it is good again, the log says so, and the bug report dump carries
/// every stream's figures (<see cref="DataQualityReport"/>).
/// <para>
/// From #922 (22 Nov 2026): a tablet got one fix in five late or not at all and the steer
/// module went silent for 2 s twice a minute. The report said the app "works at maybe 1 Hz",
/// the app itself said nothing, and the cause was only found by opening the dump's GPS log.
/// </para>
/// A warning only: nothing is switched off, and nothing is repaired (a stream that arrives
/// like this is fixed at the link, with a cable). Runs on the 100 ms module-status loop,
/// judging once a second.
/// </summary>
public partial class MainViewModel
{
    private const string DataFaultAdvice = "Check the network cable to the modules (Wi-Fi is not a link for module data)";

    private int _dataQualityTick;
    private StreamQuality[] _dataQuality = Array.Empty<StreamQuality>();
    private bool[] _dataFaultTold = Array.Empty<bool>();

    /// <summary>Each stream as it was judged in the last second: the GPS fixes first, then the
    /// modules' streams. Changed in place, on the host loop.</summary>
    public IReadOnlyList<StreamQuality> DataQuality => _dataQuality;

    /// <summary>Called from the 100 ms module-status loop.</summary>
    internal void UpdateDataQuality()
    {
        if (++_dataQualityTick < 10) return;
        _dataQualityTick = 0;
        JudgeDataQuality();
    }

    /// <summary>Judge every stream now and tell the operator of a change.</summary>
    internal void JudgeDataQuality() => JudgeDataQuality(System.Diagnostics.Stopwatch.GetTimestamp());

    /// <summary>As <see cref="JudgeDataQuality()"/>, with the current time given (tests).</summary>
    internal void JudgeDataQuality(long now)
    {
        var fixes = _autoSteerService.GpsSentences?.Fixes;
        var modules = _udpService.ModuleStreams;
        int count = (fixes != null ? 1 : 0) + (modules?.Count ?? 0);
        if (_dataQuality.Length != count)
        {
            _dataQuality = new StreamQuality[count];
            _dataFaultTold = new bool[count];
        }

        int i = 0;
        if (fixes != null)
        {
            var q = fixes.Evaluate(now);
            // With the simulator as the source a receiver's sentences are not used: how they
            // arrive is nobody's trouble.
            if (IsSimulatorEnabled) q = q with { Verdict = StreamVerdict.NoData, Faulty = false };
            NoteDataQuality(i++, q);
        }
        if (modules != null)
            for (int m = 0; m < modules.Count; m++)
                NoteDataQuality(i++, modules[m].Evaluate(now));
    }

    private void NoteDataQuality(int index, in StreamQuality q)
    {
        _dataQuality[index] = q;
        if (q.Faulty == _dataFaultTold[index]) return;
        _dataFaultTold[index] = q.Faulty;

        var inv = CultureInfo.InvariantCulture;
        if (q.Faulty)
        {
            string what = DescribeDataFault(q);
            _logger.LogWarning("[Data] {Stream}: {What}; longest wait {Longest:F0} ms", q.Name, what, q.LongestGapMs);
            ReportFailure(string.Create(inv, $"{q.Name}: {what}. {DataFaultAdvice}"));
        }
        else if (q.Verdict == StreamVerdict.NoData)
        {
            // It stopped (or the simulator took over): GPS lost and the module dots say that.
            _logger.LogInformation("[Data] {Stream}: no longer arriving, so no longer judged", q.Name);
        }
        else
        {
            _logger.LogInformation("[Data] {Stream}: arriving evenly again ({Rate:F1} a second)", q.Name, q.RateHz);
            ReportFailure(string.Create(inv, $"{q.Name}: arriving evenly again"));
        }
    }

    private static string DescribeDataFault(in StreamQuality q)
    {
        var inv = CultureInfo.InvariantCulture;
        return q.Verdict == StreamVerdict.Bursts
            ? string.Create(inv, $"arriving in bursts, not evenly ({q.RateHz:0.#} a second)")
            : string.Create(inv, $"arriving with gaps, about {q.MissedPercent:0}% missing ({q.RateHz:0.#} of {q.NominalHz:0.#} a second)");
    }

    /// <summary>Every stream's figures for the bug report dump's <c>system_info.txt</c>, so a
    /// report of a slow or late app is told from a faulty link without opening the GPS log.</summary>
    public string DataQualityReport()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(inv, $"Data quality (how each stream arrived in the last {StreamQualityMonitor.WindowSeconds:0} s; FAULT = the operator was told):");
        if (_dataQuality.Length == 0) sb.AppendLine("  not judged yet");
        foreach (var q in _dataQuality)
        {
            sb.Append(inv, $"  {q.Name}: ");
            switch (q.Verdict)
            {
                case StreamVerdict.NoData:
                    sb.Append("no data");
                    break;
                case StreamVerdict.Bursts:
                    sb.Append(inv, $"{(q.Faulty ? "FAULT, " : "")}in bursts, {q.RateHz:0.#} a second, longest wait {q.LongestGapMs:0} ms");
                    break;
                default:
                    sb.Append(inv, $"{(q.Faulty ? "FAULT, " : "")}{(q.Verdict == StreamVerdict.Gaps ? "gaps" : "even")}, {q.RateHz:0.#} of {q.NominalHz:0.#} a second, {q.MissedPercent:0.#}% missing, longest wait {q.LongestGapMs:0} ms");
                    break;
            }
            sb.AppendLine(inv, $"; worst {q.WorstMissedPercent:0.#}% missing, faults {q.Faults}");
        }
        return sb.ToString();
    }
}
