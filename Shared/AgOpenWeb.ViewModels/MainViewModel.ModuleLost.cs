// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// MainViewModel partial: the machine module or the ISOBUS task controller stops answering in
/// the middle of a job. Before, only the module dot in the status bar changed (and the ISOBUS
/// button went away), while the map went on painting: the Machine Module harness's Wi-Fi drop
/// (K4) painted 495 m² in 28 s with the valves shut, the ISOBUS harness's (K4) 772 m² no
/// implement was told about (MODULE-LOST-SILENT, ISOBUS-TC-LOST-SILENT).
/// <list type="bullet">
/// <item><b>Machine module</b> (one that answered since the app started, while it is ticked
/// in Network IO): the map stops painting (<see cref="ISectionControlService.HoldPainting"/>)
/// until it answers again, and the operator is told once, with a sound, while sections are
/// in use. The sections keep their state: the module shuts its valves by itself when it hears
/// nothing and opens them again when it does, and the map follows it.</item>
/// <item><b>Task controller</b> with section control on: told once, with a sound. Painting
/// goes on: the implement keeps its last state, so it may still be working.</item>
/// </list>
/// </summary>
public partial class MainViewModel
{
    private bool _machineHeard;        // the machine module answered since the app started
    private bool _machineLost;         // ... and has stopped answering
    private bool _machineLostTold;     // the operator was told of this loss
    private bool _isobusWorking;       // a task controller with section control on is heard
    private bool _isobusLost;          // ... and has stopped answering

    /// <summary>Called from the 100 ms module-status loop with the machine module's state.</summary>
    internal void UpdateMachineModuleLost(bool machineOk)
    {
        if (machineOk)
        {
            _machineHeard = true;
            if (!_machineLost) return;
            _machineLost = false;
            _sectionControlService.HoldPainting = false;
            _logger.LogInformation("[Sections] Machine module answering again: painting resumes");
            if (_machineLostTold)
                ReportFailure("Machine module answering again: the map paints again");
            _machineLostTold = false;
            return;
        }

        bool expected = _machineHeard && ConfigStore.Connections.IsMachineConfigured;
        if (!_machineLost)
        {
            if (!expected) return;
            _machineLost = true;
            _sectionControlService.HoldPainting = true;
            _logger.LogWarning("[Sections] Machine module not answering: the map stops painting");
        }
        else if (!expected)
        {
            // Unticked in Network IO: the operator works without it, and the map paints again.
            _machineLost = _machineLostTold = false;
            _sectionControlService.HoldPainting = false;
            _logger.LogInformation("[Sections] Machine module unticked in Network IO: painting resumes");
            return;
        }

        if (!_machineLostTold && _sectionControlService.IsAnySectionOn)
        {
            _machineLostTold = true;
            _audioService.Play(SoundEffect.SectionOff);
            ReportFailure("Machine module not answering: its valves close by themselves, and the map stops painting until it answers again");
        }
    }

    /// <summary>Called from the 100 ms module-status loop with the task controller's state.</summary>
    internal void UpdateIsobusLost(IsobusStatus status)
    {
        if (status.IsAlive)
        {
            _isobusWorking = status.SectionControlEnabled;
            if (!_isobusLost) return;
            _isobusLost = false;
            _logger.LogInformation("[ISOBUS] Task controller answering again");
            ReportFailure("ISOBUS task controller answering again");
            return;
        }

        if (!_isobusWorking || _isobusLost) return;
        _isobusLost = true;
        _logger.LogWarning("[ISOBUS] Task controller not answering: the implement is not told of section changes");
        _audioService.Play(SoundEffect.SectionOff);
        ReportFailure("ISOBUS task controller not answering: the implement keeps its last state and is not told of section changes");
    }
}
