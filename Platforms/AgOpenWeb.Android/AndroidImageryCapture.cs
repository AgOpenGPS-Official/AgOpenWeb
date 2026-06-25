// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Threading.Tasks;

namespace AgOpenWeb.Android;

/// <summary>
/// Android boundary-imagery capture — IN-PROCESS, same as iOS. Desktop crash-isolates the
/// SkiaSharp compositing in a child process (libGL/libfontconfig can SIGSEGV a headless
/// board), but Android can't fork a .NET child and its SkiaSharp runs in-process app-wide —
/// so compositing the Bing tiles directly here is both necessary and safe. The shared
/// <see cref="AgOpenWeb.RemoteServer.BoundaryImageryCapture"/> does the work; the host then
/// reads the PNG back via ApplyCapturedBackground.
/// </summary>
internal sealed class AndroidImageryCapture : AgOpenWeb.RemoteWiring.IBoundaryImageryCapture
{
    public async Task<bool> TryCaptureAsync(double mercMinX, double mercMaxX, double mercMinY, double mercMaxY, string outPath)
    {
        var path = await AgOpenWeb.RemoteServer.BoundaryImageryCapture
            .CaptureAsync(mercMinX, mercMaxX, mercMinY, mercMaxY, outPath)
            .ConfigureAwait(false);
        return path is not null;
    }
}
