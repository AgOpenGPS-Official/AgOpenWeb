// Phase MT — Draw boundary on map. Host-side aerial capture: fetch the Bing tiles
// covering a drawn boundary's Web-Mercator bounds and composite them into a single PNG
// that becomes the field background. Mirrors the native BoundaryMapDialog's
// CaptureBackgroundImageAsync, but driven by bounds the web client supplies (it captures
// nothing itself — the host stays the brain). Each head supplies the native SkiaSharp.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SkiaSharp;

namespace AgOpenWeb.RemoteServer;

public static class BoundaryImageryCapture
{
    private const double WorldSize = 2.0 * Math.PI * 6378137.0; // Web-Mercator extent (m)

    /// <summary>
    /// Fetch + composite the Bing aerial covering the given Web-Mercator bbox into a PNG.
    /// Returns the output file path, or null on failure. Runs off the UI thread.
    ///
    /// SkiaSharp here links libGL + libfontconfig natively and can hard-crash (SIGSEGV)
    /// on a headless Linux board — uncatchable in-process. So the host NEVER calls this
    /// directly; it spawns a child process (see ImageryCaptureProcess) that calls this and
    /// exits, so a native crash takes down only the child. <paramref name="outPath"/> lets
    /// the parent name the file it then reads back; null = a temp file (legacy callers).
    /// </summary>
    public static async Task<string?> CaptureAsync(
        double mercMinX, double mercMaxX, double mercMinY, double mercMaxY, string? outPath = null)
    {
        double mercWidth = mercMaxX - mercMinX, mercHeight = mercMaxY - mercMinY;
        if (mercWidth <= 0 || mercHeight <= 0) return null;

        // Pick the finest Bing zoom whose output stays within the pixel cap (memory ceiling).
        const int maxPixels = 4096;
        int targetZoom = 1;
        for (int z = 20; z >= 1; z--)
        {
            double res = WorldSize / (256.0 * (1 << z));
            if (Math.Max(mercWidth, mercHeight) / res <= maxPixels) { targetZoom = z; break; }
        }

        // Center of the boundary bounding box for imagery probe
        double centerX = (mercMinX + mercMaxX) / 2.0;
        double centerY = (mercMinY + mercMaxY) / 2.0;

        // Find the finest zoom level that actually has real imagery available
        // (Bing returns no-tile/placeholder for zoom levels beyond coverage in that area).
        int initialZoom = 0;
        for (int z = targetZoom; z >= 1; z--)
        {
            double tileMercZ = WorldSize / (1 << z);
            int cx = (int)Math.Floor((centerX + WorldSize / 2) / tileMercZ);
            int cy = (int)Math.Floor((WorldSize / 2 - centerY) / tileMercZ);
            var probe = await RemoteServerHost.FetchSatTileAsync(Quadkey(cx, cy, z));
            if (probe is { Length: >= 1000 })
            {
                initialZoom = z;
                break;
            }
        }

        if (initialZoom == 0) return null; // No imagery available at any zoom level

        for (int zoom = initialZoom; zoom >= 1; zoom--)
        {
            double resolution = WorldSize / (256.0 * (1 << zoom));
            int outW = Math.Max((int)Math.Ceiling(mercWidth / resolution), 1);
            int outH = Math.Max((int)Math.Ceiling(mercHeight / resolution), 1);
            double tileMerc = WorldSize / (1 << zoom);

            int txMin = (int)Math.Floor((mercMinX + WorldSize / 2) / tileMerc);
            int txMax = (int)Math.Floor((mercMaxX + WorldSize / 2) / tileMerc);
            int tyMin = (int)Math.Floor((WorldSize / 2 - mercMaxY) / tileMerc); // tile y grows southward
            int tyMax = (int)Math.Floor((WorldSize / 2 - mercMinY) / tileMerc);
            if ((txMax - txMin) > 64 || (tyMax - tyMin) > 64) continue; // sanity

            // Fetch all tiles concurrently (cached in RemoteServerHost).
            var jobs = new List<Task<(int tx, int ty, byte[]? bytes)>>();
            for (int tx = txMin; tx <= txMax; tx++)
                for (int ty = tyMin; ty <= tyMax; ty++)
                {
                    int cx = tx, cy = ty;
                    jobs.Add(Task.Run(async () => (cx, cy, await RemoteServerHost.FetchSatTileAsync(Quadkey(cx, cy, zoom)))));
                }
            var results = await Task.WhenAll(jobs);

            using var bitmap = new SKBitmap(outW, outH, SKColorType.Rgba8888, SKAlphaType.Opaque);
            int drawn = 0;
            using (var canvas = new SKCanvas(bitmap))
            using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High })
            {
                canvas.Clear(SKColors.Black);
                foreach (var (tx, ty, bytes) in results)
                {
                    if (bytes is null || bytes.Length < 1000) continue; // skip placeholders/empties
                    using var tile = SKBitmap.Decode(bytes);
                    if (tile is null) continue;
                    double tMinX = -WorldSize / 2 + tx * tileMerc, tMaxX = tMinX + tileMerc;
                    double tMaxY = WorldSize / 2 - ty * tileMerc, tMinY = tMaxY - tileMerc;
                    var dst = new SKRect(
                        (float)((tMinX - mercMinX) / resolution),
                        (float)((mercMaxY - tMaxY) / resolution),  // pixel y inverted vs Mercator y
                        (float)((tMaxX - mercMinX) / resolution),
                        (float)((mercMaxY - tMinY) / resolution));
                    canvas.DrawBitmap(tile, dst, paint);
                    drawn++;
                }
            }
            if (drawn == 0) continue; // Try lower zoom if decoding failed

            string path;
            if (outPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                path = outPath;
            }
            else
            {
                var dir = Path.Combine(Path.GetTempPath(), "AgOpenWeb_SatCap");
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, "BackPic_" + Guid.NewGuid().ToString("N") + ".png");
            }
            using (var data = bitmap.Encode(SKEncodedImageFormat.Png, 100))
            using (var fs = File.Create(path))
                data.SaveTo(fs);
            return path;
        }

        return null;
    }

    private static string Quadkey(int x, int y, int z)
    {
        var sb = new StringBuilder(z);
        for (int i = z; i > 0; i--)
        {
            int d = 0, m = 1 << (i - 1);
            if ((x & m) != 0) d += 1;
            if ((y & m) != 0) d += 2;
            sb.Append((char)('0' + d));
        }
        return sb.ToString();
    }
}
