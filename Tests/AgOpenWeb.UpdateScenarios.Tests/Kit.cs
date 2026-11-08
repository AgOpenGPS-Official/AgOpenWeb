// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace AgOpenWeb.UpdateScenarios.Tests;

/// <summary>What the scenarios share: data folders, file listings, the rig's red flags.</summary>
public static class Kit
{
    public static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    public static bool Want(string? only, string code) =>
        only == null || only.Split(',').Any(o => code.StartsWith(o.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string NewDataRoot(string code) =>
        Path.Combine(Path.GetTempPath(), "aow-update-" + Safe(code) + "-" + Guid.NewGuid().ToString("N")[..8]);

    public static string Safe(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-');
        return sb.ToString();
    }

    /// <summary>Every file under the data root, as "relative path (size)", sorted. The .bak and
    /// the coverage tiles are summarised so the report stays readable.</summary>
    public static List<string> ListFiles(string dataRoot)
    {
        var list = new List<string>();
        if (!Directory.Exists(dataRoot)) return list;
        foreach (var f in Directory.EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(dataRoot, f).Replace('\\', '/');
            list.Add(rel);
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    public static string FilesSummary(string title, string dataRoot)
    {
        var files = ListFiles(dataRoot);
        var shown = new List<string>();
        int tiles = 0;
        foreach (var f in files)
        {
            if (f.Contains("/coverage/", StringComparison.Ordinal) && !f.EndsWith("manifest.json", StringComparison.Ordinal)) { tiles++; continue; }
            long size = 0;
            try { size = new FileInfo(Path.Combine(dataRoot, f)).Length; } catch { }
            shown.Add(F($"{f} ({size} B)"));
        }
        var sb = new StringBuilder();
        sb.Append(title).Append(F($" ({files.Count} files"));
        if (tiles > 0) sb.Append(F($", {tiles} coverage tiles not listed"));
        sb.Append("):\n\n```\n");
        foreach (var s in shown) sb.Append(s).Append('\n');
        sb.Append("```");
        return sb.ToString();
    }

    /// <summary>Exceptions, hung loop, VM changed off the host loop, errors logged: the harness's
    /// own red flags, reported as findings of this build.</summary>
    public static void CheckRig(Scenario sc, UpdateRig rig, string when, string prefix = "UPDATE")
    {
        var events = rig.TakeEvents();
        var bad = events.Where(e => e.Kind is RigEventKind.Exception or RigEventKind.Hang or RigEventKind.OffLoop or RigEventKind.Error).ToList();
        // The farm's casters are made-up hosts: the NTRIP client's "cannot find caster" is expected.
        var warnings = events.Where(e => e.Kind == RigEventKind.Warning).Select(e => e.Text.Split('\n')[0])
            .Where(t => !t.Contains("[NTRIP]", StringComparison.Ordinal)).Distinct().ToList();
        if (bad.Count == 0) sc.Ok(when + ": no exception, no hung host loop, no error logged");
        foreach (var e in bad.Take(4))
            sc.Problem(e.Kind == RigEventKind.Hang ? Severity.Bug : Severity.Bug, prefix + "-" + e.Kind.ToString().ToUpperInvariant(), when + ": " + e.Text.Split('\n')[0]);
        if (warnings.Count > 0)
            sc.Note(when + ": warnings logged: " + string.Join(" | ", warnings.Take(5)) + (warnings.Count > 5 ? F($" … ({warnings.Count})") : ""));
    }

    /// <summary>What AgOpenWeb told the operator meanwhile (failures, status lines).</summary>
    public static List<string> Said(UpdateRig rig, Func<string, bool>? about = null)
    {
        return rig.TakeEvents()
            .Where(e => e.Kind is RigEventKind.Failure or RigEventKind.Status or RigEventKind.Warning or RigEventKind.Error)
            .Select(e => e.Kind + ": " + e.Text.Split('\n')[0])
            .Where(t => about == null || about(t))
            .Distinct()
            .Take(10)
            .ToList();
    }

    public static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), true);
    }

    /// <summary>Removes a scenario's data folder, unless AGOPENWEB_SCENARIO_KEEP is set (to look
    /// at what the builds wrote).</summary>
    public static void TryDelete(string dir)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_KEEP"))) return;
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    /// <summary>
    /// A <c>save_error.txt</c> anywhere under the data root is the app saying it could not write a
    /// file (SettingsService writes one when appsettings.json fails): a finding with the file and
    /// the error's first line, once per file, by <paramref name="who"/> (a release tag or "this build").
    /// The file is removed so the next build's own failure is told apart.
    /// </summary>
    public static void CheckSaveErrors(Scenario sc, string dataRoot, string prefix, string who)
    {
        if (!Directory.Exists(dataRoot)) return;
        foreach (var f in Directory.EnumerateFiles(dataRoot, "save_error.txt", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            string[] lines;
            try { lines = File.ReadAllLines(f); } catch { continue; }
            string path = lines.FirstOrDefault(l => l.StartsWith("Path:", StringComparison.Ordinal))?[5..].Trim() ?? "?";
            string error = lines.FirstOrDefault(l => l.StartsWith("Error:", StringComparison.Ordinal))?[6..].Trim() ?? lines.FirstOrDefault() ?? "";
            sc.Problem(Severity.Robustness, prefix + "-SAVE-ERROR", F($"{who} could not write {Path.GetFileName(path)} and left {Path.GetRelativePath(dataRoot, f)}: {error}"));
            try { File.Delete(f); } catch { }
        }
    }
}
