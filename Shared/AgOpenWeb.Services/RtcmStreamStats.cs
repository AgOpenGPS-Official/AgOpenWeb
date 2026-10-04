// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AgOpenWeb.Models.Timing;

namespace AgOpenWeb.Services;

/// <summary>One RTCM message type as seen on the caster's stream.</summary>
/// <param name="Type">RTCM message number (1005, 1077, …).</param>
/// <param name="Count">Messages with a valid checksum.</param>
/// <param name="Bytes">Their total size, framing included.</param>
/// <param name="SecondsSinceLast">Age of the newest one.</param>
/// <param name="MeanIntervalSeconds">Average spacing, or NaN after a single message.</param>
public sealed record RtcmTypeStat(int Type, long Count, long Bytes, double SecondsSinceLast, double MeanIntervalSeconds);

/// <summary>What the caster has sent this session, by message. Counted beside the forwarder:
/// it says whether the stream reaching the app is complete, not what was forwarded.</summary>
public sealed record RtcmStreamSnapshot(
    long Messages, long ChecksumFailures, long BytesSkipped, bool ChunkedReply,
    double SessionSeconds, IReadOnlyList<RtcmTypeStat> Types)
{
    public static readonly RtcmStreamSnapshot Empty = new(0, 0, 0, false, 0, Array.Empty<RtcmTypeStat>());

    /// <summary>The base position (1005 or 1006) has arrived. Without it there is no RTK solution.</summary>
    public bool HasStationPosition => Types.Any(t => t.Type is 1005 or 1006);

    /// <summary>Observations (legacy 1001–1004 / 1009–1012 or MSM 1071–1137) have arrived.</summary>
    public bool HasObservations => Types.Any(t => RtcmMessages.IsObservation(t.Type));

    /// <summary>Plain-text report for the bug report dump.</summary>
    public string ToReport(double differentialAgeSeconds, int fixQuality)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("RTCM stream from the NTRIP caster (this session, as received by the app)");
        sb.AppendLine(ci, $"session: {SessionSeconds:F0} s");
        sb.AppendLine(ci, $"messages: {Messages}, checksum failures: {ChecksumFailures}, bytes skipped: {BytesSkipped}");
        sb.AppendLine(ci, $"chunked reply: {(ChunkedReply ? "YES (chunk markers are in the forwarded stream)" : "no")}");
        sb.AppendLine(ci, $"base position (1005/1006): {(HasStationPosition ? "received" : "NOT received")}");
        sb.AppendLine(ci, $"observations: {(HasObservations ? "received" : "NOT received")}");
        sb.AppendLine(ci, $"receiver: fix quality {fixQuality}, differential age {differentialAgeSeconds:F1} s");
        sb.AppendLine();
        sb.AppendLine("type  count     bytes   every(s)  last(s)  what");
        foreach (var t in Types)
        {
            string every = double.IsNaN(t.MeanIntervalSeconds) ? "-" : t.MeanIntervalSeconds.ToString("F1", ci);
            sb.AppendLine(ci, $"{t.Type,4}  {t.Count,5}  {t.Bytes,8}  {every,9}  {t.SecondsSinceLast,7:F1}  {RtcmMessages.Describe(t.Type)}");
        }
        return sb.ToString();
    }
}

/// <summary>Builds the bug report dump's <c>ntrip_rtcm.txt</c>.</summary>
public static class NtripRtcmReport
{
    /// <summary>The caster's RTCM by message type plus the receiver's fix quality and
    /// differential age; null when NTRIP has not been used this run.</summary>
    public static string? Build(Interfaces.INtripClientService? ntrip, Interfaces.IGpsService? gps)
    {
        try
        {
            var snap = ntrip?.GetRtcmStreamSnapshot();
            if (ntrip == null || snap == null || (snap.Messages == 0 && !ntrip.IsActive)) return null;
            var data = gps?.CurrentData;
            return snap.ToReport(data?.DifferentialAge ?? 0, data?.FixQuality ?? 0);
        }
        catch (Exception ex)
        {
            return "ntrip report failed: " + ex.Message;
        }
    }
}

/// <summary>RTCM 3 message numbers the forwarder cares about.</summary>
public static class RtcmMessages
{
    /// <summary>Per-epoch satellite observations: legacy GPS / GLONASS, or any MSM.</summary>
    public static bool IsObservation(int type) =>
        type is >= 1001 and <= 1004 or >= 1009 and <= 1012 || IsMsm(type);

    /// <summary>Multiple Signal Messages: 1071–1137, the last digit 1–7 being the MSM level.</summary>
    public static bool IsMsm(int type) => type is >= 1071 and <= 1137 && type % 10 is >= 1 and <= 7;

    public static string Describe(int type)
    {
        if (IsMsm(type))
        {
            string system = ((type - 1070) / 10) switch
            {
                0 => "GPS", 1 => "GLONASS", 2 => "Galileo", 3 => "SBAS",
                4 => "QZSS", 5 => "BeiDou", 6 => "NavIC", _ => "?",
            };
            return $"{system} observations (MSM{type % 10})";
        }
        return type switch
        {
            >= 1001 and <= 1004 => "GPS observations (legacy)",
            >= 1009 and <= 1012 => "GLONASS observations (legacy)",
            1005 => "base position",
            1006 => "base position + antenna height",
            1007 or 1008 => "antenna descriptor",
            1013 => "system parameters",
            1019 => "GPS ephemeris",
            1020 => "GLONASS ephemeris",
            1029 => "text",
            1033 => "receiver + antenna descriptor",
            1042 => "BeiDou ephemeris",
            1044 => "QZSS ephemeris",
            1045 or 1046 => "Galileo ephemeris",
            1230 => "GLONASS code-phase biases",
            >= 4001 and <= 4095 => "proprietary",
            -1 => "empty",
            _ => "",
        };
    }
}

/// <summary>
/// Frames the caster's stream and counts messages by type. Runs beside the forwarder and
/// changes nothing it sends. Thread-safe: the receive loop feeds it; the health log and the
/// bug report read it.
/// </summary>
internal sealed class RtcmStreamStats
{
    private sealed class Entry { public long Count, Bytes, First, Last; }

    private readonly IClock? _clock;
    private readonly object _lock = new();
    private readonly RtcmFramer _framer = new();
    private readonly SortedDictionary<int, Entry> _types = new();
    private long _sessionStart;
    private bool _sessionStarted;
    private bool _chunked;

    /// <param name="clock">Time source; null follows <see cref="Clock.Current"/>.</param>
    public RtcmStreamStats(IClock? clock = null) => _clock = clock;

    private IClock Time => _clock ?? Clock.Current;

    /// <summary>Start a new session: forget the previous one's counts and any partial message.</summary>
    public void Reset(bool chunkedReply = false)
    {
        lock (_lock)
        {
            _framer.Reset();
            _types.Clear();
            _sessionStart = Time.GetTimestamp();
            _sessionStarted = true;
            _chunked = chunkedReply;
        }
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            long now = Time.GetTimestamp();
            if (!_sessionStarted) { _sessionStart = now; _sessionStarted = true; }
            _framer.Feed(data, (type, message) =>
            {
                if (!_types.TryGetValue(type, out var e))
                    _types[type] = e = new Entry { First = now };
                e.Count++;
                e.Bytes += message.Length;
                e.Last = now;
            });
        }
    }

    public RtcmStreamSnapshot Snapshot()
    {
        lock (_lock)
        {
            long now = Time.GetTimestamp();
            var types = new List<RtcmTypeStat>(_types.Count);
            foreach (var (type, e) in _types)
            {
                double mean = e.Count > 1 ? Time.ElapsedMs(e.First, e.Last) / 1000.0 / (e.Count - 1) : double.NaN;
                types.Add(new RtcmTypeStat(type, e.Count, e.Bytes, Time.ElapsedMs(e.Last, now) / 1000.0, mean));
            }
            double session = _sessionStarted ? Time.ElapsedMs(_sessionStart, now) / 1000.0 : 0;
            return new RtcmStreamSnapshot(_framer.Messages, _framer.ChecksumFailures, _framer.BytesSkipped,
                _chunked, session, types);
        }
    }
}
