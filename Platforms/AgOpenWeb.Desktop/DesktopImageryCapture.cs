using System.Threading.Tasks;

namespace AgOpenWeb.Desktop;

/// <summary>
/// Desktop boundary-imagery capture: composites in a crash-isolated CHILD PROCESS
/// (see <see cref="ImageryCaptureProcess"/>) — SkiaSharp can hard-crash a headless
/// board and must never take the host down. The Shared web wiring calls this through
/// <see cref="AgOpenWeb.RemoteWiring.IBoundaryImageryCapture"/>.
/// </summary>
internal sealed class DesktopImageryCapture : AgOpenWeb.RemoteWiring.IBoundaryImageryCapture
{
    public Task<bool> TryCaptureAsync(double mercMinX, double mercMaxX, double mercMinY, double mercMaxY, string outPath)
        => ImageryCaptureProcess.TryCaptureAsync(mercMinX, mercMaxX, mercMinY, mercMaxY, outPath);
}
