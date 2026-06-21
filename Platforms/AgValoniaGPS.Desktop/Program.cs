// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

﻿using Avalonia;
using Avalonia.Skia;
using HotAvalonia;
using System;

namespace AgValoniaGPS.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // One-shot crash-isolated imagery capture child (spawned by the host on a
        // boundary-on-map draw). SkiaSharp links libGL/libfontconfig and can hard-crash
        // on a headless board; running it here in a throwaway process means such a crash
        // never touches the guidance host. Must be the FIRST thing Main does — no host,
        // no Avalonia. See ImageryCaptureProcess.
        if (args.Length > 0 && args[0] == ImageryCaptureProcess.CaptureArg)
        {
            Environment.Exit(ImageryCaptureProcess.RunCli(args));
            return;
        }

        // Mode selection. The backend is identical in every mode; only how it's hosted differs:
        //   --windowed (or AGOPENWEB_WINDOWED=1) → legacy full native UI (verify/compare).
        //   --launcher                           → in-process launcher window (app-like).
        //   --headless                           → display-less daemon (force, e.g. Windows Service).
        //   no flag                              → Windows: launcher (the AgOpen audience expects a
        //                                          program); Linux/macOS: headless daemon (systemd).
        // The browser at http://<host>:5174 is the UI in launcher + headless modes alike.
        // See WEBUI_SESSION_HANDOFF.md.
        bool windowed = Array.IndexOf(args, "--windowed") >= 0
            || Environment.GetEnvironmentVariable("AGOPENWEB_WINDOWED") == "1";
        bool forceHeadless = Array.IndexOf(args, "--headless") >= 0;
        bool forceLauncher = Array.IndexOf(args, "--launcher") >= 0;

        if (windowed)
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        else if (forceLauncher || (OperatingSystem.IsWindows() && !forceHeadless))
            Launcher.LauncherEntry.Run(args);
        else
            HeadlessHost.RunAsync(args).GetAwaiter().GetResult();
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseHotReload()
            // Matches Android/iOS: prevent the 50 MB coverage texture from being
            // re-uploaded every frame when the default 28 MB Skia GPU cache is
            // exceeded. 192 MB fits coverage + its mipmap chain + other
            // textures comfortably.
            .With(new SkiaOptions
            {
                MaxGpuResourceSizeBytes = 192L * 1024 * 1024
            });
}
