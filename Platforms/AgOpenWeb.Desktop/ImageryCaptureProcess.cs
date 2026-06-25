// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using AgOpenWeb.RemoteServer; // BoundaryImageryCapture (the SkiaSharp compositor, now Shared)

namespace AgOpenWeb.Desktop;

/// <summary>
/// Crash-isolated boundary imagery capture. The SkiaSharp compositing in
/// <see cref="BoundaryImageryCapture"/> links libGL + libfontconfig natively and can
/// hard-crash (SIGSEGV) on a headless Linux board — an uncatchable fault that would
/// otherwise take down the whole guidance host. So the host never composites in-process:
/// it re-invokes ITS OWN executable in a one-shot capture mode (<see cref="CaptureArg"/>),
/// waits for it, and reads back the PNG. If the child crashes, only the child dies —
/// the host logs it and keeps driving.
/// </summary>
internal static class ImageryCaptureProcess
{
    public const string CaptureArg = "--capture-imagery";

    /// <summary>
    /// Child entry: <c>--capture-imagery mercMinX mercMaxX mercMinY mercMaxY outPath</c>.
    /// Runs the SkiaSharp capture and exits. Returns a process exit code (0 = wrote the
    /// PNG; non-zero = nothing produced). Invoked from Program.Main before any host start.
    /// </summary>
    public static int RunCli(string[] args)
    {
        var inv = CultureInfo.InvariantCulture;
        if (args.Length < 6
            || !double.TryParse(args[1], NumberStyles.Float, inv, out var minX)
            || !double.TryParse(args[2], NumberStyles.Float, inv, out var maxX)
            || !double.TryParse(args[3], NumberStyles.Float, inv, out var minY)
            || !double.TryParse(args[4], NumberStyles.Float, inv, out var maxY))
        {
            Console.Error.WriteLine("[capture] bad args");
            return 2;
        }
        var outPath = args[5];
        try
        {
            var result = BoundaryImageryCapture.CaptureAsync(minX, maxX, minY, maxY, outPath)
                .GetAwaiter().GetResult();
            return result is not null ? 0 : 3; // 3 = no tiles drawn (e.g. no internet)
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[capture] failed: {ex.Message}");
            return 4;
        }
    }

    /// <summary>
    /// Parent side: run the capture in a child process. Returns true and the PNG path on
    /// success; false if the child failed, crashed, or timed out. Never throws into the host.
    /// </summary>
    public static async Task<bool> TryCaptureAsync(
        double mercMinX, double mercMaxX, double mercMinY, double mercMaxY,
        string outPath, int timeoutSeconds = 45)
    {
        var exe = Environment.ProcessPath; // the self-contained apphost re-invokes itself
        if (string.IsNullOrEmpty(exe)) { Console.Error.WriteLine("[capture] no ProcessPath"); return false; }

        var inv = CultureInfo.InvariantCulture;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add(CaptureArg);
        psi.ArgumentList.Add(mercMinX.ToString("R", inv));
        psi.ArgumentList.Add(mercMaxX.ToString("R", inv));
        psi.ArgumentList.Add(mercMinY.ToString("R", inv));
        psi.ArgumentList.Add(mercMaxY.ToString("R", inv));
        psi.ArgumentList.Add(outPath);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) { Console.Error.WriteLine("[capture] child did not start"); return false; }

            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                Console.Error.WriteLine("[capture] child timed out");
                return false;
            }

            if (proc.ExitCode == 0 && System.IO.File.Exists(outPath))
                return true;

            // Non-zero exit covers a clean "no tiles" (3) AND a native crash (the OS
            // reports a signal as exit code 128+signo). Either way: no imagery, host fine.
            var err = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
            Console.Error.WriteLine(
                $"[capture] child exit {proc.ExitCode} (no imagery){(string.IsNullOrWhiteSpace(err) ? "" : " — " + err.Trim())}");
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[capture] spawn failed: {ex.Message}");
            return false;
        }
    }
}
