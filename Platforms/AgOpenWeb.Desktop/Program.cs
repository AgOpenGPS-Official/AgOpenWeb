// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
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

using System;

namespace AgOpenWeb.Desktop;

sealed class Program
{
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

        // Mode selection. The backend is identical in both modes; only how it's hosted differs.
        // The UI is always the browser/WebView at http://<host>:5174 — there is no native UI.
        //   --headless → display-less daemon (force; systemd / Windows Service).
        //   --launcher → in-process WebView launcher window (force; app-like).
        //   no flag    → Windows + macOS: the all-in-one WebView launcher (a desktop app);
        //                Linux: headless daemon (the SBC / mini-PC server case). Linux
        //                launcher bundles pass --launcher; daemon bundles pass --headless.
        bool forceHeadless = Array.IndexOf(args, "--headless") >= 0;
        bool forceLauncher = Array.IndexOf(args, "--launcher") >= 0;

        if (forceLauncher || ((OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) && !forceHeadless))
            Launcher.LauncherEntry.Run(args);
        else
            HeadlessHost.RunAsync(args).GetAwaiter().GetResult();
    }
}
