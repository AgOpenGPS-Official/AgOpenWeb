// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AgOpenWeb.RemoteServer;

/// <summary>
/// What each browser says about how it draws the map (<c>diag.client</c>): its WebGL renderer,
/// the largest texture, pixel ratio, canvas size and the frame rate it reaches. The bug report
/// dump prints it in <c>system_info.txt</c>, so a report of a slow map says straight away whether
/// the device draws with its GPU or in software (a tablet without a working GPU driver).
/// Kept for the browsers connected now and the last few that left, newest first.
/// </summary>
public sealed class ClientDiagnostics
{
    private const int Keep = 6;
    private const int MaxReportLength = 1000;

    private sealed class Entry
    {
        public required Guid Conn { get; init; }
        public DateTime LastUtc { get; set; }
        public string Report { get; set; } = "";
        public DateTime? LeftUtc { get; set; }
    }

    private readonly object _lock = new();
    private readonly List<Entry> _entries = new();

    /// <summary>A browser's report: tab-separated <c>key=value</c> pairs. Replaces its last one.</summary>
    public void Report(Guid conn, string report, DateTime? nowUtc = null)
    {
        if (report.Length > MaxReportLength) report = report[..MaxReportLength];
        var now = nowUtc ?? DateTime.UtcNow;
        lock (_lock)
        {
            var e = _entries.FirstOrDefault(x => x.Conn == conn);
            if (e is null) { e = new Entry { Conn = conn }; _entries.Add(e); }
            e.Report = report;
            e.LastUtc = now;
            e.LeftUtc = null;
            Prune();
        }
    }

    /// <summary>The browser's connection closed; its last report is kept for the dump.</summary>
    public void Left(Guid conn, DateTime? nowUtc = null)
    {
        lock (_lock)
        {
            var e = _entries.FirstOrDefault(x => x.Conn == conn);
            if (e is not null) { e.LeftUtc = nowUtc ?? DateTime.UtcNow; Prune(); }
        }
    }

    // Every connected browser is kept; of those that left, only the newest, up to Keep in all.
    private void Prune()
    {
        var gone = _entries.Where(x => x.LeftUtc is not null).OrderByDescending(x => x.LeftUtc).ToList();
        int room = Math.Max(0, Keep - (_entries.Count - gone.Count));
        foreach (var e in gone.Skip(room)) _entries.Remove(e);
    }

    /// <summary>The dump's "Browsers" section; empty when no browser has reported.</summary>
    public string Summary(Func<Guid, bool> holdsControl, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        List<(Guid Conn, DateTime Last, string Report, DateTime? Left)> list;
        lock (_lock)
            list = _entries.Select(e => (e.Conn, e.LastUtc, e.Report, e.LeftUtc))
                .OrderBy(e => e.LeftUtc is null ? 0 : 1).ThenByDescending(e => e.LastUtc).ToList();
        if (list.Count == 0) return "";

        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        foreach (var (conn, last, report, left) in list)
        {
            var f = Parse(report);
            string role = left is { } l
                ? string.Create(inv, $"left {Ago(now - l)} ago")
                : holdsControl(conn) ? "in control" : "observer";
            sb.AppendLine(inv, $"Browser ({role}, reported {Ago(now - last)} ago):");
            if (Verdict(f) is { } verdict) sb.AppendLine(inv, $"  !! {verdict}");
            foreach (var key in Order)
                if (f.TryGetValue(key, out var v) && v.Length > 0)
                    sb.AppendLine(inv, $"  {Label(key)}: {v}");
            foreach (var (k, v) in f)
                if (!Order.Contains(k) && v.Length > 0) sb.AppendLine(inv, $"  {k}: {v}");
        }
        return sb.ToString();
    }

    private static readonly string[] Order =
        { "renderer", "vendor", "gl", "glVersion", "maxTexture", "fps", "drawMs", "canvas", "dpr", "screen", "skia", "ua" };

    private static string Label(string key) => key switch
    {
        "renderer" => "GPU renderer",
        "vendor" => "GPU vendor",
        "gl" => "WebGL",
        "glVersion" => "WebGL version",
        "maxTexture" => "Max texture",
        "fps" => "Map frame rate",
        "drawMs" => "Map draw time (ms, avg / max)",
        "canvas" => "Canvas (device px)",
        "dpr" => "Pixel ratio",
        "screen" => "Screen (CSS px)",
        "skia" => "CanvasKit",
        "ua" => "User agent",
        _ => key,
    };

    internal static Dictionary<string, string> Parse(string report)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in report.Split('\t'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            // One line per value in the dump: no line breaks from a browser's text.
            d[part[..eq].Trim()] = part[(eq + 1)..].Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
        return d;
    }

    // Renderers that are a CPU, not a GPU: Mesa's llvmpipe/softpipe, Chrome's SwiftShader,
    // Windows' Basic Render Driver.
    private static readonly Regex Software = new(
        @"llvmpipe|softpipe|swiftshader|software|basic render", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The headline for a device that can't draw the map well, or null.</summary>
    internal static string? Verdict(IReadOnlyDictionary<string, string> f)
    {
        f.TryGetValue("gl", out var gl);
        if (gl is "none") return "NO WEBGL: this browser cannot draw the map";
        if (f.TryGetValue("renderer", out var r) && Software.IsMatch(r))
            return "SOFTWARE RENDERING: no GPU acceleration, the map is drawn by the CPU";
        return null;
    }

    private static string Ago(TimeSpan t) =>
        t.TotalSeconds < 90 ? ((int)Math.Max(0, t.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + " s"
        : t.TotalMinutes < 90 ? ((int)t.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min"
        : ((int)t.TotalHours).ToString(CultureInfo.InvariantCulture) + " h";
}
