// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Threading.Tasks;

namespace AgValoniaGPS.Desktop;

/// <summary>
/// Phase 10 headless host. Boots the app as a plain .NET process — NO Avalonia
/// Application, window, or windowing platform — so it can run on a display-less
/// server/daemon (systemd / Windows Service) with the browser as the only UI.
///
/// It reproduces the non-UI half of <see cref="App.OnFrameworkInitializationCompleted"/>:
/// build DI, load settings/config/persistent state, construct the single
/// <c>MainViewModel</c> (which starts the 100 Hz control loop + the render-pull /
/// status timers), start the RemoteServer, and wire its command handler + projectors
/// to the VM via the shared <see cref="App.WireRemoteServer"/>. The UI-thread role is
/// played by a single-thread <see cref="HostLoopDispatcher"/> (registered as both
/// <see cref="IUiDispatcher"/> and <see cref="IUiTimerFactory"/>), so render-pull,
/// status, and remote commands serialize on one thread exactly as they do on the
/// Avalonia UI thread in the windowed build.
///
/// Selected by the absence of <c>--windowed</c> (see Program.Main).
/// See Plans/WEBUI_MIGRATION_PLAN.md Phase 10 and Plans/WEBUI_SESSION_HANDOFF.md.
/// </summary>
internal static class HeadlessHost
{
    public static async Task RunAsync(string[] args)
    {
        Console.WriteLine("[headless] AgOpenWeb host starting (no window; browser is the UI)…");

        var backend = new BackendHost();
        await backend.StartAsync(args);

        Console.WriteLine("[headless] ready — browse to http://localhost:5174 (or the LAN IP).");

        // Block until SIGINT (Ctrl-C) / SIGTERM (systemd stop) / SIGQUIT via the generic
        // host lifetime, then stop (fires the ApplicationStopping save before disposal).
        await backend.WaitForShutdownAsync();
        await backend.StopAsync();
    }
}
