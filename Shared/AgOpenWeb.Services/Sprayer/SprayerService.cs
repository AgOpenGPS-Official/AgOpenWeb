// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Sprayer;

/// <summary>
/// Sprayer readout from the machine module (renatorgr Pulverizador_Nano).
///
/// <list type="bullet">
/// <item><b>PGN 160 (0xA0)</b> from the machine module (source 123) once a second:
/// { 0x80, 0x81, 123, 0xA0, 10, pressure cbar u16, flow cL/min u16, total dL u32,
/// master u8, sections u8, CRC }.</item>
/// </list>
///
/// The expected flow is the nozzles of the open sections times one nozzle's flow, scaled
/// to the measured pressure (flow grows with the square root of the pressure). A section's
/// nozzles are its width (Tool tab) over the nozzle spacing (Machine tab). An alarm counts
/// once its condition has held for <see cref="ConfirmMessages"/> messages in a row, and the
/// flow check waits as long again after the sections change, while the valves open and the
/// flow meter catches up.
/// </summary>
public sealed class SprayerService : ISprayerService
{
    /// <summary>The source address the machine module sends from.</summary>
    public const byte MachineSource = 123;

    /// <summary>Messages in a row (one a second) before an alarm comes on.</summary>
    public const int ConfirmMessages = 3;

    /// <summary>No message for this long = <see cref="SprayerAlarms.NoData"/>.</summary>
    public static readonly TimeSpan NoDataTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The alarm sounds again this often while one is on.</summary>
    public static readonly TimeSpan RepeatInterval = TimeSpan.FromSeconds(10);

    /// <summary>Flow below this counts as none, L/min (the flow meter's noise).</summary>
    public const double NoFlowLpm = 0.5;

    private readonly ConfigurationStore _config;
    private readonly IAudioService _audio;
    private readonly object _lock = new();

    private DateTime _lastMessage;     // default = never
    private SprayerStatus _status;
    private int _lastSections = -1;
    private int _sinceSectionChange;
    private readonly int[] _held = new int[5];   // messages in a row per condition (bits 0–4)
    private SprayerAlarms _alarms;
    private DateTime _lastSound;

    public SprayerService(IUdpCommunicationService udp, ConfigurationStore config, IAudioService audio)
    {
        _config = config;
        _audio = audio;
        udp.DataReceived += OnUdpDataReceived;
    }

    public SprayerStatus? Status
    {
        get
        {
            lock (_lock)
            {
                if (_lastMessage == default) return null;
                return Clock.Current.Now - _lastMessage < NoDataTimeout
                    ? _status
                    : _status with { Alarms = _status.Alarms | SprayerAlarms.NoData };
            }
        }
    }

    private void OnUdpDataReceived(object? sender, UdpDataReceivedEventArgs e)
    {
        if (e.PGN != PgnNumbers.SPRAYER_DATA) return;
        if (!TryParse(e.Data, out double pressure, out double flow, out double total, out bool master, out int sections))
            return;

        lock (_lock)
        {
            DateTime now = Clock.Current.Now;
            _lastMessage = now;
            var m = _config.Machine;

            int nozzles = 0;
            double width = 0;
            for (int i = 0; i < 8; i++)
            {
                if ((sections & (1 << i)) == 0) continue;
                double w = _config.Tool.GetSectionWidth(i) / 100.0;
                width += w;
                if (m.SprayerNozzleSpacing > 0) nozzles += (int)Math.Round(w / m.SprayerNozzleSpacing);
            }

            bool flowCheck = m.SprayerNozzleSpacing > 0 && m.SprayerNozzleFlow > 0 && m.SprayerReferencePressure > 0;
            double expected = flowCheck
                ? nozzles * m.SprayerNozzleFlow * Math.Sqrt(Math.Max(pressure, 0) / m.SprayerReferencePressure)
                : double.NaN;

            if (sections != _lastSections) _sinceSectionChange = 0;
            else _sinceSectionChange++;
            _lastSections = sections;
            bool settled = _sinceSectionChange >= ConfirmMessages;

            bool spraying = sections != 0;
            double tol = Math.Max(m.SprayerFlowTolerance, 1) / 100.0;
            var raw = SprayerAlarms.None;
            if (spraying && m.SprayerMinPressure > 0 && pressure < m.SprayerMinPressure) raw |= SprayerAlarms.PressureLow;
            if (m.SprayerMaxPressure > 0 && pressure > m.SprayerMaxPressure) raw |= SprayerAlarms.PressureHigh;
            if (spraying && flowCheck && settled && expected > NoFlowLpm)
            {
                if (flow < expected * (1 - tol)) raw |= SprayerAlarms.FlowLow;
                else if (flow > expected * (1 + tol)) raw |= SprayerAlarms.FlowHigh;
            }
            if (!spraying && settled && flow > NoFlowLpm) raw |= SprayerAlarms.FlowSectionsOff;

            var on = SprayerAlarms.None;
            for (int b = 0; b < _held.Length; b++)
            {
                var a = (SprayerAlarms)(1 << b);
                _held[b] = (raw & a) != 0 ? _held[b] + 1 : 0;
                if (_held[b] >= ConfirmMessages) on |= a;
            }

            // Sound when an alarm comes on, and again every RepeatInterval while one is on.
            if (on != SprayerAlarms.None
                && ((on & ~_alarms) != SprayerAlarms.None || now - _lastSound >= RepeatInterval))
            {
                _lastSound = now;
                _audio.Play(SoundEffect.SprayerAlarm);
            }
            _alarms = on;

            _status = new SprayerStatus(pressure, flow, total, master, sections, nozzles, width, expected, on);
        }
    }

    /// <summary>
    /// PGN 160 as the module sends it. The CRC is not checked (as for the other module PGNs).
    /// </summary>
    public static bool TryParse(byte[] data, out double pressureBar, out double flowLpm,
        out double totalLitres, out bool masterOn, out int sections)
    {
        pressureBar = flowLpm = totalLitres = 0;
        masterOn = false;
        sections = 0;

        if (data.Length < 16 || data[0] != PgnMessage.HEADER1 || data[1] != PgnMessage.HEADER2) return false;
        if (data[2] != MachineSource || data[3] != PgnNumbers.SPRAYER_DATA || data[4] < 10) return false;

        pressureBar = (data[5] | data[6] << 8) / 100.0;
        flowLpm = (data[7] | data[8] << 8) / 100.0;
        totalLitres = (uint)(data[9] | data[10] << 8 | data[11] << 16 | data[12] << 24) / 10.0;
        masterOn = data[13] != 0;
        sections = data[14];
        return true;
    }
}
