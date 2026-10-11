// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Isobus;

/// <summary>
/// ISOBUS task controller link (#312), after AgOpenGPS <c>CISOBUS</c> and the protocol in
/// AOG-TaskController <c>docs/PROTOCOL.md</c>.
///
/// <list type="bullet">
/// <item><b>PGN 240 (0xF0)</b> from the task controller (source 0x80) every 100 ms:
/// byte 0 bit 0 = section control enabled, bits 1–3 = implements connected; byte 1 = the
/// implement's section count (0 = alive, no implement); then that implement's reported
/// section states, one bit per section. The task controller counts as alive for
/// <see cref="AliveTimeout"/> after a heartbeat.</item>
/// <item><b>PGN 241 (0xF1)</b> to the task controller: one byte, 1 = automatic, 0 = manual.
/// The task controller's heartbeat is the truth for the setting. A request is repeated
/// every <see cref="ResendInterval"/> until the heartbeat shows it, for at most
/// <see cref="RequestTimeout"/>. The last request is sent again when a task controller
/// comes back (restarted) reporting the other setting. A change that arrives with no
/// request of ours pending was made somewhere else and is followed, not undone.</item>
/// </list>
///
/// The sections themselves reach the task controller in PGN 254 / 229, which the app
/// sends anyway.
/// </summary>
public sealed class IsobusTaskControllerService : IIsobusTaskControllerService
{
    /// <summary>The source address the task controller sends from.</summary>
    public const byte TaskControllerSource = 0x80;

    public static readonly TimeSpan AliveTimeout = TimeSpan.FromSeconds(1);    // AgOpenGPS IsAlive
    public static readonly TimeSpan ResendInterval = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private readonly IUdpCommunicationService _udp;
    private readonly object _lock = new();

    private DateTime _lastHeartbeat;   // default = never
    private bool _enabled;
    private int _sectionCount;
    private int _clientCount;
    private ulong _reported;

    private bool? _wanted;             // the operator's last request; null = none yet
    private DateTime? _pendingUntil;   // a request is waiting for the heartbeat to show it
    private DateTime _lastSend;

    public IsobusTaskControllerService(IUdpCommunicationService udp)
    {
        _udp = udp;
        _udp.DataReceived += OnUdpDataReceived;
    }

    public IsobusStatus Status
    {
        get
        {
            lock (_lock)
            {
                return IsAliveAt(Clock.Current.Now)
                    ? new IsobusStatus(true, _enabled, _sectionCount, _clientCount, _reported)
                    : default;
            }
        }
    }

    public void RequestSectionControl(bool enabled)
    {
        lock (_lock)
        {
            DateTime now = Clock.Current.Now;
            _wanted = enabled;
            _pendingUntil = now + RequestTimeout;
            Send(enabled, now);
        }
    }

    public void ToggleSectionControl()
    {
        lock (_lock)
        {
            if (!IsAliveAt(Clock.Current.Now)) return;
            RequestSectionControl(!_enabled);
        }
    }

    private bool IsAliveAt(DateTime now) =>
        _lastHeartbeat != default && now - _lastHeartbeat < AliveTimeout;

    private void Send(bool enabled, DateTime now)
    {
        _lastSend = now;
        _udp.SendToModules(PgnBuilder.BuildIsobusSectionControlPgn(enabled));
    }

    private void OnUdpDataReceived(object? sender, UdpDataReceivedEventArgs e)
    {
        if (e.PGN != PgnNumbers.ISOBUS_HEARTBEAT) return;
        if (!TryParseHeartbeat(e.Data, out bool enabled, out int clients, out int count, out ulong reported))
            return;

        lock (_lock)
        {
            DateTime now = Clock.Current.Now;
            bool wasAlive = IsAliveAt(now);
            _lastHeartbeat = now;
            _enabled = enabled;
            _clientCount = clients;
            _sectionCount = count;
            _reported = reported;

            if (_wanted is not bool wanted) return;
            if (wanted == enabled)
            {
                _pendingUntil = null;
            }
            else if (!wasAlive)
            {
                // A task controller that was away is back with the other setting: it
                // restarted. Ask again.
                _pendingUntil = now + RequestTimeout;
                Send(wanted, now);
            }
            else if (_pendingUntil is DateTime until)
            {
                if (now >= until)
                {
                    // It isn't taking the request. Show what it reports and stop asking.
                    _wanted = enabled;
                    _pendingUntil = null;
                }
                else if (now - _lastSend >= ResendInterval)
                {
                    Send(wanted, now);
                }
            }
            else
            {
                _wanted = enabled;   // switched somewhere else: follow it
            }
        }
    }

    /// <summary>
    /// PGN 240 as the task controller sends it: { 0x80, 0x81, 0x80, 0xF0, length, flags,
    /// section count, state bytes…, CRC }. With sections, the length must be exactly
    /// 2 + ceil(count / 8), as AgOpenGPS requires; with none, the state bytes are absent.
    /// Sections beyond 64 are not reported. The CRC is not checked (AgOpenGPS doesn't).
    /// </summary>
    public static bool TryParseHeartbeat(byte[] data, out bool sectionControlEnabled,
        out int clientCount, out int sectionCount, out ulong reportedSections)
    {
        sectionControlEnabled = false;
        clientCount = 0;
        sectionCount = 0;
        reportedSections = 0;

        if (data.Length < 7 || data[0] != PgnMessage.HEADER1 || data[1] != PgnMessage.HEADER2) return false;
        if (data[2] != TaskControllerSource || data[3] != PgnNumbers.ISOBUS_HEARTBEAT) return false;
        int length = data[4];
        if (length < 2 || data.Length < 5 + length) return false;

        sectionControlEnabled = (data[5] & 0x01) != 0;
        clientCount = (data[5] >> 1) & 0x07;
        int count = data[6];
        if (count == 0) return true;

        if (length != 2 + (count + 7) / 8) return false;
        sectionCount = count;
        for (int i = 0; i < count && i < 64; i++)
        {
            if ((data[7 + i / 8] & (1 << (i % 8))) != 0)
                reportedSections |= 1UL << i;
        }
        return true;
    }
}
