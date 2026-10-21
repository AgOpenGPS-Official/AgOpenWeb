// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgOpenWeb.Services.Network;

/// <summary>Why the watcher asks its subscribers to look at the network.</summary>
public enum NetworkCheckReason
{
    /// <summary>An interface gained or lost an address (settled for a moment first).</summary>
    AddressChanged,
    /// <summary>The fallback beat: catches a change no event reported.</summary>
    Heartbeat,
}

/// <summary>The order subscribers run in on each check: the modules first, then
/// corrections, then uploads, so they come up in the same order every time.</summary>
public enum NetworkStage
{
    Modules = 0,
    Corrections = 1,
    Uploads = 2,
}

/// <summary>
/// The one place that notices the network changing (#385). Services subscribe instead of
/// polling on timers of their own; a check only wakes them (find the modules, retry the
/// caster, send queued reports) and never tears down a working connection.
/// </summary>
public interface INetworkWatcher
{
    void Subscribe(NetworkStage stage, Action<NetworkCheckReason> onCheck);

    /// <summary>A platform that hears about network changes itself (Android, iOS) reports
    /// them here.</summary>
    void NotifyChanged();
}

/// <summary>
/// Hears <see cref="NetworkChange.NetworkAddressChanged"/> where the platform has it, plus
/// whatever a head reports through <see cref="NotifyChanged"/>. Changes come in bursts (one
/// Wi-Fi join fires several), so a check runs once they have settled for
/// <see cref="DefaultSettle"/>. A <see cref="DefaultHeartbeat"/> beat runs a check either way,
/// for platforms that report nothing.
/// </summary>
public sealed class NetworkWatcher : INetworkWatcher, IDisposable
{
    public static readonly TimeSpan DefaultSettle = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultHeartbeat = TimeSpan.FromSeconds(30);

    private readonly ILogger<NetworkWatcher> _logger;
    private readonly TimeSpan _settle;
    private readonly Timer _settleTimer;
    private readonly Timer _heartbeat;
    private readonly bool _systemEvents;
    private readonly object _gate = new();
    private readonly List<(NetworkStage Stage, Action<NetworkCheckReason> OnCheck)> _subscribers = new();

    public NetworkWatcher(ILogger<NetworkWatcher>? logger = null)
        : this(logger, systemEvents: true, DefaultSettle, DefaultHeartbeat) { }

    internal NetworkWatcher(ILogger<NetworkWatcher>? logger, bool systemEvents, TimeSpan settle, TimeSpan heartbeat)
    {
        _logger = logger ?? NullLogger<NetworkWatcher>.Instance;
        _settle = settle;
        _settleTimer = new Timer(_ => Check(NetworkCheckReason.AddressChanged));
        _heartbeat = new Timer(_ => Check(NetworkCheckReason.Heartbeat), null, heartbeat, heartbeat);
        if (systemEvents)
        {
            try
            {
                NetworkChange.NetworkAddressChanged += OnSystemChange;
                _systemEvents = true;
            }
            catch (Exception ex) // iOS and Android: their heads report changes instead
            {
                _logger.LogDebug("[Network] no system change events: {Message}", ex.Message);
            }
        }
    }

    public void Subscribe(NetworkStage stage, Action<NetworkCheckReason> onCheck)
    {
        lock (_gate)
        {
            _subscribers.Add((stage, onCheck));
        }
    }

    public void NotifyChanged() => _settleTimer.Change(_settle, Timeout.InfiniteTimeSpan);

    private void OnSystemChange(object? sender, EventArgs e) => NotifyChanged();

    /// <summary>Run every subscriber, in stage order (then the order they subscribed), one
    /// check at a time.</summary>
    internal void Check(NetworkCheckReason reason)
    {
        lock (_gate)
        {
            if (reason == NetworkCheckReason.AddressChanged)
                _logger.LogInformation("[Network] addresses changed");
            foreach (var (stage, onCheck) in _subscribers.OrderBy(s => s.Stage).ToList())
            {
                try { onCheck(reason); }
                catch (Exception ex) { _logger.LogWarning(ex, "[Network] {Stage} check failed", stage); }
            }
        }
    }

    public void Dispose()
    {
        if (_systemEvents)
        {
            try { NetworkChange.NetworkAddressChanged -= OnSystemChange; } catch { /* already gone */ }
        }
        _settleTimer.Dispose();
        _heartbeat.Dispose();
    }
}
