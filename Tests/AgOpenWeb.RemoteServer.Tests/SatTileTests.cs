// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.RemoteServer;
using NUnit.Framework;
using SkiaSharp;
using static AgOpenWeb.RemoteServer.RemoteServerHost;

namespace AgOpenWeb.RemoteServer.Tests;

[TestFixture]
public class SatTileReplyTests
{
    private static readonly byte[] RealTile = SatTileFakes.Png(SKColors.Green);

    [Test]
    public void ErrorStatus_IsAFailure_NotNoTile() =>
        Assert.That(ClassifyTileReply(false, false, null), Is.EqualTo(TileReply.Failed));

    [Test]
    public void NoTileHeader_IsNoTile() =>
        Assert.That(ClassifyTileReply(true, true, Array.Empty<byte>()), Is.EqualTo(TileReply.NoTile));

    [Test]
    public void EmptyBody_IsNoTile() =>
        Assert.That(ClassifyTileReply(true, false, Array.Empty<byte>()), Is.EqualTo(TileReply.NoTile));

    [Test]
    public void PlaceholderJpeg_IsNoTile()
    {
        // The 1033-byte "no imagery" JPEG Bing serves without &n=z (captured 2026-10-19).
        var placeholder = File.ReadAllBytes(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "bing-no-tile.jpeg"));
        Assert.That(ClassifyTileReply(true, false, placeholder), Is.EqualTo(TileReply.NoTile));
    }

    [Test]
    public void Imagery_IsATile() =>
        Assert.That(ClassifyTileReply(true, false, RealTile), Is.EqualTo(TileReply.Tile));
}

[TestFixture]
public class BoundaryImageryCaptureTests
{
    // A 400 m square field: z20 fits the 4096 px cap (2684 px), so capture starts there.
    private const double MinX = 1_000_000, MaxX = 1_000_400, MinY = 5_000_000, MaxY = 5_000_400;
    private const double WorldSize = 2.0 * Math.PI * 6378137.0;

    private string _out = null!;

    [SetUp]
    public void SetUp() => _out = Path.Combine(Path.GetTempPath(), "AgOpenWeb_SatCapTest_" + Guid.NewGuid().ToString("N") + ".png");

    [TearDown]
    public void TearDown() { if (File.Exists(_out)) File.Delete(_out); }

    [Test]
    public async Task EmptyBox_ReturnsNull()
    {
        Assert.That(await BoundaryImageryCapture.CaptureAsync(100, 50, 100, 50), Is.Null);
        Assert.That(await BoundaryImageryCapture.CaptureAsync(100, 100, 200, 200), Is.Null);
    }

    [Test]
    public async Task NoImageryAtAnyZoom_ReturnsNull()
    {
        var path = await BoundaryImageryCapture.CaptureAsync(MinX, MaxX, MinY, MaxY, _out, _ => Task.FromResult<byte[]?>(null));
        Assert.That(path, Is.Null);
    }

    [Test]
    public async Task ImageryEndingAtZ18_CapturesAtZ18()
    {
        var tile = SatTileFakes.Png(SKColors.Red);
        var path = await BoundaryImageryCapture.CaptureAsync(MinX, MaxX, MinY, MaxY, _out,
            qk => Task.FromResult(qk.Length <= 18 ? tile : null));

        Assert.That(path, Is.EqualTo(_out));
        using var bmp = SKBitmap.Decode(path);
        double res18 = WorldSize / (256.0 * (1 << 18));
        Assert.That(bmp.Width, Is.EqualTo((int)Math.Ceiling((MaxX - MinX) / res18)));
    }

    [Test]
    public async Task MissingEdgeTile_IsFilledFromItsParent()
    {
        // z18 has imagery except the field's north-west tile; z17 has it everywhere.
        string missing = BoundaryImageryCapture.Quadkey(TileX(MinX, 18), TileY(MaxY, 18), 18);
        byte[] red = SatTileFakes.Png(SKColors.Red), blue = SatTileFakes.Png(SKColors.Blue);
        var path = await BoundaryImageryCapture.CaptureAsync(MinX, MaxX, MinY, MaxY, _out,
            qk => Task.FromResult<byte[]?>(qk.Length > 18 || qk == missing ? null : qk.Length == 18 ? red : blue));

        Assert.That(path, Is.Not.Null);
        using var bmp = SKBitmap.Decode(path);
        Assert.That(bmp.GetPixel(1, 1), Is.EqualTo(SKColors.Blue), "north-west corner comes from the z17 parent");
        Assert.That(bmp.GetPixel(bmp.Width - 2, bmp.Height - 2), Is.EqualTo(SKColors.Red));
        Assert.That(bmp.Pixels.Any(p => p == SKColors.Black), Is.False, "no black holes");
    }

    private static int TileX(double mercX, int z) => (int)Math.Floor((mercX + WorldSize / 2) / (WorldSize / (1 << z)));
    private static int TileY(double mercY, int z) => (int)Math.Floor((WorldSize / 2 - mercY) / (WorldSize / (1 << z)));
}

internal static class SatTileFakes
{
    public static byte[] Png(SKColor color)
    {
        using var bmp = new SKBitmap(256, 256);
        bmp.Erase(color);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
