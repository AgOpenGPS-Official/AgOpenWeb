// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Systemd;

namespace AgOpenWeb.Desktop;

/// <summary>
/// Pets the systemd hardware watchdog when the headless host runs as a
/// <c>Type=notify</c> service with <c>WatchdogSec=</c> set. systemd exports the
/// configured timeout as <c>WATCHDOG_USEC</c>; we send <c>WATCHDOG=1</c> via
/// sd_notify at HALF that interval (systemd's recommendation) so a hung host is
/// detected and restarted (<c>Restart=always</c>) instead of silently wedging.
///
/// A no-op in every other case: not under systemd (<see cref="ISystemdNotifier"/>
/// not registered → null), or WATCHDOG_USEC unset (watchdog disabled). So it is
/// safe to register unconditionally and harmless under <c>dotnet run</c>.
/// See Plans/DEPLOYMENT_PATTERNS.md (systemd daemon) and the install unit.
/// </summary>
internal sealed class SystemdWatchdogService : BackgroundService
{
    private readonly IServiceProvider _services;

    public SystemdWatchdogService(IServiceProvider services) => _services = services;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ISystemdNotifier is only registered by UseSystemd() when actually running
        // as a systemd service, so resolve it optionally.
        var notifier = _services.GetService<ISystemdNotifier>();
        var usecStr = Environment.GetEnvironmentVariable("WATCHDOG_USEC");
        if (notifier is null
            || string.IsNullOrEmpty(usecStr)
            || !long.TryParse(usecStr, out var usec)
            || usec <= 0)
        {
            return; // no watchdog configured → nothing to pet
        }

        // Half the watchdog timeout, clamped to a sane floor so a tiny WatchdogSec
        // can't spin the loop.
        var intervalMs = Math.Max(250.0, usec / 1000.0 / 2.0);
        var watchdog = new ServiceState("WATCHDOG=1");

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                notifier.Notify(watchdog);
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
