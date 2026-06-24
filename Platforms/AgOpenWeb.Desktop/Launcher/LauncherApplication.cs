// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using Avalonia;
using Avalonia.Controls; // ShutdownMode
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace AgOpenWeb.Desktop.Launcher;

/// <summary>
/// A minimal Avalonia <see cref="Application"/> for the launcher — NOT the full guidance
/// <see cref="App"/> (no map window, no MainViewModel-as-UI). Just a Fluent theme and the
/// tiny <see cref="LauncherWindow"/>; the backend it controls runs headless in-process.
/// </summary>
public sealed class LauncherApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new LauncherWindow(LauncherEntry.Args);
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
