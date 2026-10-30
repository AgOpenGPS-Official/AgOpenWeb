// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using AgOpenWeb.Models.Ntrip;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services;

/// <summary>What a caster's list of mount points came to: the list, or why there is none.</summary>
/// <param name="Error">The reason, as one sentence; null when the list was read.</param>
/// <param name="Partial">The list ends where the caster stopped sending: it is not the whole table.</param>
public sealed record SourcetableResult(IReadOnlyList<NtripMountpoint> Mountpoints, string? Error, bool Partial = false);

/// <summary>Asks a caster for its mount points (its source table) and reads them.</summary>
public static class NtripSourcetable
{
    private const int MaxBytes = 8 * 1024 * 1024;
    private static readonly byte[] EndMarker = "ENDSOURCETABLE"u8.ToArray();

    /// <summary>
    /// The STR lines of a source table, nearest first when <paramref name="from"/> (latitude, longitude)
    /// is given, else by name. CAS and NET lines are skipped.
    /// </summary>
    public static List<NtripMountpoint> Parse(string text, (double Latitude, double Longitude)? from = null)
    {
        var list = new List<NtripMountpoint>();
        foreach (string line in (text ?? "").Replace("\r", "").Split('\n'))
        {
            if (!line.StartsWith("STR;", StringComparison.Ordinal)) continue;
            string[] f = line.Split(';');
            if (f.Length < 3 || f[1].Length == 0) continue;

            double lat = Number(Field(f, 9)), lon = Number(Field(f, 10));
            double distance = from is { } p && (lat != 0 || lon != 0) ? DistanceKm(p.Latitude, p.Longitude, lat, lon) : double.NaN;
            list.Add(new NtripMountpoint(f[1], Field(f, 2), Field(f, 3), Field(f, 6), Field(f, 8), distance, Field(f, 11) == "1"));
        }
        // Known distances first, nearest at the top; the rest by name.
        return list.OrderBy(m => double.IsNaN(m.DistanceKm) ? double.MaxValue : m.DistanceKm)
            .ThenBy(m => m.Mount, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Field(string[] f, int i) => i < f.Length ? f[i].Trim() : "";

    private static double Number(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    /// <summary>Great-circle distance in kilometres.</summary>
    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double EarthRadiusKm = 6371.0;
        double dLat = (lat2 - lat1) * Math.PI / 180.0, dLon = (lon2 - lon1) * Math.PI / 180.0;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    /// <summary>Connects as the client does (NTRIP 2.0, then 1.0 for a caster that does not understand it) and reads the table.</summary>
    public static async Task<SourcetableResult> FetchAsync(string host, int port, string user, string password,
        (double Latitude, double Longitude)? from, NtripTimeouts timeouts, CancellationToken ct)
    {
        // No mount point in the request is the request for the table.
        var config = new NtripConfiguration { CasterAddress = host.Trim(), CasterPort = port, MountPoint = "", Username = user, Password = password };
        using NtripConnection conn = await NtripConnection.OpenAsync(config, NtripVersion.V2, () => null, timeouts, ct, sourcetable: true).ConfigureAwait(false);
        if (!conn.Outcome.IsAccepted)
            return new SourcetableResult([], conn.Outcome.Message);

        // The bytes are kept and decoded once at the end: a character can be split between two reads.
        var bytes = new MemoryStream();
        var decoder = conn.Outcome.Chunked ? new ChunkedDecoder() : null;
        bool ended = false, timedOut = false;

        void Store(ReadOnlySpan<byte> data)
        {
            bytes.Write(data);
            // Only the new bytes and the end of the old ones can hold the marker.
            int length = (int)bytes.Length, tail = Math.Min(length, data.Length + EndMarker.Length);
            ended |= bytes.GetBuffer().AsSpan(length - tail, tail).IndexOf(EndMarker) >= 0;
        }
        void Add(ReadOnlySpan<byte> data)
        {
            if (decoder != null && !decoder.IsBroken) decoder.Feed(data, Store);
            else Store(data);
        }

        Add(conn.InitialBody);
        try
        {
            while (!ended && bytes.Length < MaxBytes)
            {
                int n = await conn.Reader!.ReadAsync(timeouts.Header, ct).ConfigureAwait(false);
                if (n <= 0) break;   // closed, or silent for too long: use what there is
                Add(conn.Reader.Buffer.AsSpan(0, n));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The caster closed after sending the table.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            timedOut = true;
        }

        string text = Decode(bytes.GetBuffer().AsSpan(0, (int)bytes.Length));
        // Without the end marker the last line may be cut in the middle: leave it out.
        if (!ended && text.LastIndexOf('\n') is int lastLine and >= 0) text = text[..lastLine];

        List<NtripMountpoint> list = Parse(text, from);
        if (list.Count > 0) return new SourcetableResult(list, null, Partial: !ended);
        return new SourcetableResult([], timedOut ? "The caster took too long to send its list." : "The caster sent no mount points.");
    }

    // Casters send UTF-8 or, mostly the older ones, Latin-1 names.
    private static string Decode(ReadOnlySpan<byte> data)
    {
        try { return new UTF8Encoding(false, true).GetString(data); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(data); }
    }
}
