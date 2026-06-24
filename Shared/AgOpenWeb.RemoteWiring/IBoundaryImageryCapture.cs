namespace AgOpenWeb.RemoteWiring;

/// <summary>
/// Platform hook for the "Draw boundary on map" aerial-imagery capture. Desktop
/// composites in a crash-isolated CHILD PROCESS (SkiaSharp can hard-crash a headless
/// board); iOS/Android composite in-process. The wiring only needs "give me a PNG for
/// this mercator rectangle at this path, true if it worked" — the how is per-platform.
/// </summary>
public interface IBoundaryImageryCapture
{
    Task<bool> TryCaptureAsync(double mercMinX, double mercMaxX, double mercMinY, double mercMaxY, string outPath);
}
