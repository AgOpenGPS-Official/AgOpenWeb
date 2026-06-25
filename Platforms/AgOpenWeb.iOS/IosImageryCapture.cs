using System.Threading.Tasks;

namespace AgOpenWeb.iOS;

/// <summary>
/// iOS boundary-imagery capture — IN-PROCESS. Desktop crash-isolates the SkiaSharp
/// compositing in a child process (libGL/libfontconfig can SIGSEGV a headless board),
/// but iOS can't fork and its SkiaSharp is Metal/CPU (no libGL/fontconfig), and the app
/// already runs SkiaSharp app-wide — so compositing the Bing tiles directly here is both
/// necessary and safe. The shared <see cref="AgOpenWeb.RemoteServer.BoundaryImageryCapture"/>
/// does the work; the host then reads the PNG back via ApplyCapturedBackground.
/// </summary>
internal sealed class IosImageryCapture : AgOpenWeb.RemoteWiring.IBoundaryImageryCapture
{
    public async Task<bool> TryCaptureAsync(double mercMinX, double mercMaxX, double mercMinY, double mercMaxY, string outPath)
    {
        var path = await AgOpenWeb.RemoteServer.BoundaryImageryCapture
            .CaptureAsync(mercMinX, mercMaxX, mercMinY, mercMaxY, outPath)
            .ConfigureAwait(false);
        return path is not null;
    }
}
