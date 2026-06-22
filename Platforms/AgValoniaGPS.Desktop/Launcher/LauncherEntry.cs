// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using Avalonia;

namespace AgValoniaGPS.Desktop.Launcher;

/// <summary>
/// Entry for the in-process launcher mode (Windows default; <c>--launcher</c> on any OS).
/// Boots a minimal Avalonia desktop app (<see cref="LauncherApplication"/>) whose only window
/// supervises the headless <see cref="BackendHost"/>. Distinct from <see cref="HeadlessHost"/>
/// (display-less daemon) and the full windowed <see cref="App"/> (legacy native UI).
/// </summary>
internal static class LauncherEntry
{
    /// <summary>The process args, exposed to <see cref="LauncherApplication"/> so the window
    /// passes them through to <see cref="BackendHost.StartAsync"/> (host config binding).</summary>
    public static string[] Args { get; private set; } = Array.Empty<string>();

    public static void Run(string[] args)
    {
        Args = args;
        AppBuilder.Configure<LauncherApplication>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }
}
