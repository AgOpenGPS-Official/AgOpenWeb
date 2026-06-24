using System.Threading.Tasks;

namespace AgOpenWeb.iOS;

/// <summary>
/// iOS boundary-imagery capture. Desktop crash-isolates SkiaSharp compositing in a
/// CHILD PROCESS, which iOS cannot do (no fork). Real iOS capture needs an IN-PROCESS
/// compositor — a follow-up that moves Desktop's BoundaryImageryCapture into Shared and
/// calls it directly here. Until then this returns false: the boundary is still created
/// (RemoteCreateBoundaryFromMapPoints runs), just without an aerial background.
/// </summary>
internal sealed class IosImageryCapture : AgOpenWeb.RemoteWiring.IBoundaryImageryCapture
{
    public Task<bool> TryCaptureAsync(double mercMinX, double mercMaxX, double mercMinY, double mercMaxY, string outPath)
        => Task.FromResult(false);
}
