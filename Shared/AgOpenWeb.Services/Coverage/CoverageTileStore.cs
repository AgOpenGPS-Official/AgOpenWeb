// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
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
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using AgOpenWeb.Services.Storage;

namespace AgOpenWeb.Services.Coverage;

/// <summary>
/// On-disk layout of a job's coverage as world-anchored tiles, so a save rewrites only the
/// tiles painted since the last one (Plans/Completed/COVERAGE_TILED_PERSISTENCE_PLAN.md).
///
/// <code>
/// jobs/&lt;task&gt;/coverage/
///   manifest.json          display cell size, worked area, field bounds (written last)
///   d/&lt;x&gt;_&lt;y&gt;.tile         detection bits: 1024 x 1024 cells at 0.1 m (102.4 m square)
///   s&lt;µm&gt;/&lt;x&gt;_&lt;y&gt;.tile     display RGB565: 256 x 256 pixels at the display cell size
/// </code>
///
/// Tile keys are absolute (world cell or display pixel index / tile size), so growing the
/// field bounds never renames a tile. The display folder is named after its cell size: a
/// display-quality change writes a fresh folder, the manifest switches to it, and only then
/// is the old one removed. A tile with no coverage has no file. The folder listing is the
/// index; a tile that fails its CRC is skipped on load and costs that tile, not the job.
/// </summary>
internal sealed class CoverageTileStore
{
    public const int DetectTileShift = 10;
    public const int DetectTileCells = 1 << DetectTileShift;  // per side, 1 bit each
    public const int DetectTileBytes = DetectTileCells * DetectTileCells / 8;
    public const int DisplayTileShift = 8;
    public const int DisplayTilePixels = 1 << DisplayTileShift; // per side, RGB565 each
    public const int DisplayTileLength = DisplayTilePixels * DisplayTilePixels;

    public const byte LayerDetection = 0;
    public const byte LayerDisplay = 1;

    private const uint Magic = 0x54564F43;                 // "COVT" little-endian
    private const byte Version = 1;
    private const byte EncodingRaw = 0;
    private const byte EncodingRle = 1;
    private const int HeaderSize = 32;
    private const string TileExtension = ".tile";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Root { get; }
    public string DetectionDir => Path.Combine(Root, "d");
    public string ManifestPath => Path.Combine(Root, "manifest.json");

    public CoverageTileStore(string jobDirectory) => Root = Path.Combine(jobDirectory, "coverage");

    public bool ManifestExists => File.Exists(ManifestPath);

    public string DisplayDir(double cellSize) =>
        Path.Combine(Root, string.Create(CultureInfo.InvariantCulture, $"s{(long)Math.Round(cellSize * 1e6)}"));

    public static string TileFileName(int tx, int ty) =>
        string.Create(CultureInfo.InvariantCulture, $"{tx}_{ty}{TileExtension}");

    public static long Key(int tx, int ty) => ((long)tx << 32) | (uint)ty;
    public static int KeyX(long key) => (int)(key >> 32);
    public static int KeyY(long key) => (int)key;

    // ---------- manifest ----------

    public sealed class Manifest
    {
        public int SchemaVersion { get; set; } = 1;
        public double DisplayCellSize { get; set; }
        public double TotalWorkedArea { get; set; }
        public double MinE { get; set; }
        public double MaxE { get; set; }
        public double MinN { get; set; }
        public double MaxN { get; set; }
    }

    public void WriteManifest(Manifest manifest) =>
        AtomicJsonFile.WriteJson(ManifestPath, manifest, JsonOptions);

    public Manifest? ReadManifest()
    {
        var result = AtomicJsonFile.Read<Manifest>(ManifestPath, JsonOptions,
            m => m.SchemaVersion == 1 && m.DisplayCellSize > 0 && m.MaxE > m.MinE && m.MaxN > m.MinN);
        return result.Loaded ? result.Value : null;
    }

    // ---------- tiles ----------

    /// <summary>Tile keys present in a folder, from the file names.</summary>
    public static List<long> ListTiles(string dir)
    {
        var keys = new List<long>();
        if (!Directory.Exists(dir))
            return keys;
        foreach (var path in Directory.EnumerateFiles(dir, "*" + TileExtension))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            int sep = name.IndexOf('_', 1); // index 0 may be a minus sign
            if (sep > 0 &&
                int.TryParse(name.AsSpan(0, sep), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int tx) &&
                int.TryParse(name.AsSpan(sep + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int ty))
                keys.Add(Key(tx, ty));
        }
        return keys;
    }

    /// <summary>
    /// Write a detection tile (<see cref="DetectTileBytes"/> bytes), raw or RLE, whichever is
    /// smaller. Returns the bytes written.
    /// </summary>
    public long WriteDetectionTile(int tx, int ty, ReadOnlySpan<byte> bits, RunBuffer rle)
    {
        rle.Reset();
        int i = 0;
        while (i < bits.Length)
        {
            byte value = bits[i];
            int len = RunLength(bits.Slice(i), value);
            rle.AddByteRuns(value, len);
            i += len;
        }
        var payload = rle.Length < bits.Length ? rle.Span : bits;
        byte encoding = rle.Length < bits.Length ? EncodingRle : EncodingRaw;
        return WriteTile(Path.Combine(DetectionDir, TileFileName(tx, ty)), LayerDetection, encoding, tx, ty, 0.1, payload);
    }

    /// <summary>Write a display tile (<see cref="DisplayTileLength"/> RGB565 pixels).</summary>
    public long WriteDisplayTile(int tx, int ty, double cellSize, ReadOnlySpan<ushort> pixels, RunBuffer rle)
    {
        rle.Reset();
        int i = 0;
        while (i < pixels.Length)
        {
            ushort value = pixels[i];
            int len = RunLength(pixels.Slice(i), value);
            rle.AddWordRuns(value, len);
            i += len;
        }
        var raw = MemoryMarshal.AsBytes(pixels); // little-endian on every target
        var payload = rle.Length < raw.Length ? rle.Span : raw;
        byte encoding = rle.Length < raw.Length ? EncodingRle : EncodingRaw;
        return WriteTile(Path.Combine(DisplayDir(cellSize), TileFileName(tx, ty)), LayerDisplay, encoding, tx, ty, cellSize, payload);
    }

    // Tiles written to .tmp but not yet flushed and renamed into place (see CommitTiles).
    private readonly List<(string Tmp, string Path)> _pending = new();

    private long WriteTile(string path, byte layer, byte encoding, int tx, int ty, double cellSize, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        header[4] = Version;
        header[5] = layer;
        header[6] = encoding;
        header[7] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8), tx);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(12), ty);
        BinaryPrimitives.WriteDoubleLittleEndian(header.Slice(16), cellSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(24), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(28), Crc32.Compute(payload));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(header);
            stream.Write(payload);
        }
        _pending.Add((tmp, path));
        return HeaderSize + payload.Length;
    }

    /// <summary>
    /// Flush every tile written since the last commit to disk, then rename each over its
    /// target, so a power cut leaves either the old tile or the new one. Flushing them as a
    /// batch is what makes a full save affordable on an SD card: the first flush commits the
    /// filesystem journal with all the pending data, and the rest find little left to do.
    /// </summary>
    public void CommitTiles()
    {
        foreach (var (tmp, _) in _pending)
            using (var stream = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                stream.Flush(flushToDisk: true);
        foreach (var (tmp, path) in _pending)
            File.Move(tmp, path, overwrite: true);
        _pending.Clear();
    }

    /// <summary>
    /// Read a detection tile into <paramref name="bits"/> (<see cref="DetectTileBytes"/>).
    /// False if the file is missing, damaged, or not this tile.
    /// </summary>
    public bool TryReadDetectionTile(int tx, int ty, Span<byte> bits)
    {
        var payload = ReadTilePayload(Path.Combine(DetectionDir, TileFileName(tx, ty)), LayerDetection, tx, ty,
                                      out byte encoding, out _);
        if (payload == null)
            return false;
        if (encoding == EncodingRaw)
        {
            if (payload.Length != bits.Length) return false;
            payload.CopyTo(bits);
            return true;
        }
        // [run:u16][value:u8]
        if (payload.Length % 3 != 0) return false;
        int pos = 0;
        for (int i = 0; i < payload.Length; i += 3)
        {
            int run = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(i));
            if (pos + run > bits.Length) return false;
            bits.Slice(pos, run).Fill(payload[i + 2]);
            pos += run;
        }
        return pos == bits.Length;
    }

    /// <summary>
    /// Read a display tile into <paramref name="pixels"/> (<see cref="DisplayTileLength"/>).
    /// False if the file is missing, damaged, not this tile, or not at <paramref name="cellSize"/>.
    /// </summary>
    public bool TryReadDisplayTile(int tx, int ty, double cellSize, Span<ushort> pixels)
    {
        var payload = ReadTilePayload(Path.Combine(DisplayDir(cellSize), TileFileName(tx, ty)), LayerDisplay, tx, ty,
                                      out byte encoding, out double tileCell);
        if (payload == null || Math.Abs(tileCell - cellSize) > 1e-9)
            return false;
        if (encoding == EncodingRaw)
        {
            if (payload.Length != pixels.Length * 2) return false;
            payload.AsSpan().CopyTo(MemoryMarshal.AsBytes(pixels));
            return true;
        }
        // [run:u16][value:u16]
        if (payload.Length % 4 != 0) return false;
        int pos = 0;
        for (int i = 0; i < payload.Length; i += 4)
        {
            int run = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(i));
            if (pos + run > pixels.Length) return false;
            pixels.Slice(pos, run).Fill(BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(i + 2)));
            pos += run;
        }
        return pos == pixels.Length;
    }

    private static byte[]? ReadTilePayload(string path, byte layer, int tx, int ty, out byte encoding, out double cellSize)
    {
        encoding = 0;
        cellSize = 0;
        try
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < HeaderSize) return Reject(path, "truncated header");
            var h = data.AsSpan(0, HeaderSize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(h) != Magic || h[4] != Version || h[5] != layer)
                return Reject(path, "not a coverage tile of this layer");
            encoding = h[6];
            if (BinaryPrimitives.ReadInt32LittleEndian(h.Slice(8)) != tx ||
                BinaryPrimitives.ReadInt32LittleEndian(h.Slice(12)) != ty)
                return Reject(path, "tile coordinates don't match its name");
            cellSize = BinaryPrimitives.ReadDoubleLittleEndian(h.Slice(16));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(24));
            if (length != data.Length - HeaderSize) return Reject(path, "truncated payload");
            var payload = data.AsSpan(HeaderSize);
            if (Crc32.Compute(payload) != BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(28)))
                return Reject(path, "CRC mismatch");
            if (encoding != EncodingRaw && encoding != EncodingRle) return Reject(path, "unknown encoding");
            return payload.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Reject(path, ex.Message);
        }
    }

    private static byte[]? Reject(string path, string why)
    {
        Console.WriteLine($"[Coverage] Skipping tile {Path.GetFileName(path)}: {why}");
        return null;
    }

    public static void DeleteTile(string dir, int tx, int ty)
    {
        var path = Path.Combine(dir, TileFileName(tx, ty));
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>Delete display folders other than the one in use.</summary>
    public void DeleteOtherDisplayDirs(double keepCellSize)
    {
        if (!Directory.Exists(Root))
            return;
        var keep = Path.GetFileName(DisplayDir(keepCellSize));
        foreach (var dir in Directory.EnumerateDirectories(Root, "s*"))
            if (Path.GetFileName(dir) != keep)
                Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// Length of the run of <paramref name="value"/> at the start of <paramref name="span"/>
    /// (vectorised). At least 1: the cycle thread may change the first element after the
    /// caller read it, and a zero-length run would never advance.
    /// </summary>
    public static int RunLength<T>(ReadOnlySpan<T> span, T value) where T : unmanaged, IEquatable<T>
    {
        int next = span.IndexOfAnyExcept(value);
        return next < 0 ? span.Length : Math.Max(next, 1);
    }

    /// <summary>
    /// Reusable RLE output: [run:u16][value] records, value one byte (detection) or one
    /// little-endian u16 (display). Runs longer than 65535 split.
    /// </summary>
    public sealed class RunBuffer
    {
        private byte[] _buf = new byte[64 * 1024];
        public int Length { get; private set; }
        public ReadOnlySpan<byte> Span => _buf.AsSpan(0, Length);

        public void Reset() => Length = 0;

        public void AddByteRuns(byte value, int count)
        {
            for (int left = count; left > 0; left -= 65535)
            {
                Ensure(3);
                BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(Length), (ushort)Math.Min(left, 65535));
                _buf[Length + 2] = value;
                Length += 3;
            }
        }

        public void AddWordRuns(ushort value, int count)
        {
            for (int left = count; left > 0; left -= 65535)
            {
                Ensure(4);
                BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(Length), (ushort)Math.Min(left, 65535));
                BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(Length + 2), value);
                Length += 4;
            }
        }

        private void Ensure(int n)
        {
            if (Length + n > _buf.Length)
                Array.Resize(ref _buf, _buf.Length * 2);
        }
    }

    /// <summary>CRC-32 (IEEE 802.3), to catch torn or truncated tiles.</summary>
    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data)
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
