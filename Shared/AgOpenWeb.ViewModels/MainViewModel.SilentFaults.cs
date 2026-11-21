// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// MainViewModel partial: things AgOpenWeb knows are wrong with the set-up and used to keep to
/// itself. From the nightly harnesses (20 Nov 2026):
/// <list type="bullet">
/// <item><b>Corrections not used</b> (GPS/NTRIP K7, CORRECTIONS-UNUSED-SILENT): the caster's
/// corrections arrive and are forwarded, the headline says "Corrections arriving", and the
/// receiver stays on plain GPS with no correction age, because its corrections input is on
/// another box. After <see cref="CorrectionsUnusedTicks"/> of that the operator is told once,
/// and told again when the receiver starts using them.</item>
/// <item><b>ISOBUS section count</b> (ISOBUS K7, ISOBUS-SECTION-COUNT-MISMATCH-SILENT): the tool
/// has 20 sections and the one implement on the task controller 12; the map painted sections
/// 13-20, which no implement has. With section control on and one implement, a count that
/// differs from the tool's for <see cref="SectionCountMismatchTicks"/> is told once per pair of
/// counts. With two implements the heartbeat carries one of them, so nothing is said.</item>
/// </list>
/// Both run on the 100 ms module-status loop.
/// </summary>
public partial class MainViewModel
{
    /// <summary>30 s: a receiver fed corrections reports DGPS or float within seconds.</summary>
    internal const int CorrectionsUnusedTicks = 300;
    /// <summary>1 s: an implement registering, or the tool being changed, settles in that time.</summary>
    internal const int SectionCountMismatchTicks = 10;

    private int _correctionsUnusedFor;
    private bool _correctionsUnusedTold;
    private int _sectionCountMismatchFor;
    private (int Implement, int Tool) _sectionCountMismatchTold;

    /// <summary>Called from the 100 ms module-status loop.</summary>
    internal void UpdateCorrectionsUnused()
    {
        bool streaming = _ntripService.Status is { State: NtripState.Streaming };
        int quality = State.Vehicle.FixQuality;
        double age = State.Vehicle.Age;
        // The receiver uses corrections when it reports DGPS, RTK or a correction age (NaN for
        // sentences that carry none, so the quality decides there).
        bool used = quality >= 2 || age > 0;
        bool unused = streaming && quality == 1 && !used && !IsSimulatorEnabled;

        if (!unused)
        {
            _correctionsUnusedFor = 0;
            if (_correctionsUnusedTold && streaming && used)
            {
                _correctionsUnusedTold = false;
                _logger.LogInformation("[NTRIP] The receiver is using the corrections now (fix {Fix}, age {Age:F1} s)", quality, age);
                ReportFailure("The receiver is using the corrections now");
            }
            return;
        }

        if (++_correctionsUnusedFor < CorrectionsUnusedTicks || _correctionsUnusedTold) return;
        _correctionsUnusedTold = true;
        _logger.LogWarning("[NTRIP] Corrections arriving for {Seconds} s, but the receiver is on plain GPS with no correction age: it is not using them",
            CorrectionsUnusedTicks / 10);
        ReportFailure("Corrections are arriving, but the receiver is not using them: it is still on plain GPS. Check that its corrections input is on the box AgOpenWeb hears the positions from, or turn on Broadcast corrections in Network IO");
    }

    /// <summary>Called from the 100 ms module-status loop with the task controller's state.</summary>
    internal void UpdateIsobusSectionCount(IsobusStatus status)
    {
        int tool = ConfigStore.NumSections;
        bool mismatch = status.IsAlive && status.SectionControlEnabled && status.ClientCount == 1
            && status.SectionCount > 0 && status.SectionCount != tool;
        if (!mismatch)
        {
            _sectionCountMismatchFor = 0;
            if (status.IsAlive && status.SectionCount == tool) _sectionCountMismatchTold = default;
            return;
        }

        var counts = (status.SectionCount, tool);
        if (++_sectionCountMismatchFor < SectionCountMismatchTicks || _sectionCountMismatchTold == counts) return;
        _sectionCountMismatchTold = counts;
        _logger.LogWarning("[ISOBUS] The implement has {Implement} sections, the tool {Tool}", status.SectionCount, tool);
        ReportFailure(tool > status.SectionCount
            ? $"The ISOBUS implement has {status.SectionCount} sections and the tool here {tool}: sections {status.SectionCount + 1}-{tool} are painted on the map, but no implement has them. Set the tool to {status.SectionCount} sections"
            : $"The ISOBUS implement has {status.SectionCount} sections and the tool here {tool}: its sections {tool + 1}-{status.SectionCount} are never switched. Set the tool to {status.SectionCount} sections");
    }
}
