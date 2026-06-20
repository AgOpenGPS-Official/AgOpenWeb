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
        // Phase 10: AgOpenWeb boots HEADLESS by default — no Avalonia window; the
        // browser at http://<host>:5174 is the only UI, and the process can run as a
        // display-less daemon (systemd / Windows Service). Pass --windowed (or set
        // AGOPENWEB_WINDOWED=1) to launch the legacy native window for verify/compare
        // while the headless path is field-validated. See WEBUI_SESSION_HANDOFF.md.
        bool windowed = Array.IndexOf(args, "--windowed") >= 0
            || Environment.GetEnvironmentVariable("AGOPENWEB_WINDOWED") == "1";

        if (windowed)
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
