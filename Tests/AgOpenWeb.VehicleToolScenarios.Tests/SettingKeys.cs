// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace AgOpenWeb.VehicleToolScenarios.Tests;

/// <summary>
/// Every setting the operator can change, read from the source each run, so a setting added
/// tomorrow shows up in the sweep's report without anyone remembering to list it:
/// the keys the host's config bridge handles (<c>ApplyConfigSet</c> in
/// RemoteServerWiring.Helpers.cs, <c>case "a":</c>, <c>case "a" or "b":</c> and guarded
/// <c>case "a" when …:</c> labels) and the keys the web client sends (<c>data-key</c>,
/// <c>data-cfg</c>, <c>data-cfgnum</c> and <c>data-tgl</c> attributes, <c>cfgSend('…')</c>,
/// and any quoted <c>'prefix.key'</c> literal with a prefix the bridge knows, such as the keys
/// the setup wizards pass to <c>wzNum</c> and <c>wzTgl</c>). A quoted literal the host routes
/// as a command (<c>'autosteer.toggle'</c>) is a command, not a setting.
/// </summary>
public static partial class SettingKeys
{
    /// <summary>Fewer host keys than this means the scan no longer reads the bridge right
    /// (160 on 2026-11-08).</summary>
    public const int MinHostKeys = 100;

    /// <summary>What a scan found. <see cref="Problem"/> is set when the scan itself failed
    /// (the bridge was not found, or far too few keys were read): the lists are then not to
    /// be trusted.</summary>
    public sealed record Scan(SortedSet<string> Host, SortedSet<string> Client, string? Problem);

    /// <summary>The repository root (the folder with AgOpenWeb.sln), from the test's folder up.</summary>
    public static string? RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "AgOpenWeb.sln"))) return d.FullName;
        return null;
    }

    public static Scan Read(string root)
    {
        var wiring = Path.Combine(root, "Shared", "AgOpenWeb.RemoteWiring");
        var host = HostKeys(File.ReadAllText(Path.Combine(wiring, "RemoteServerWiring.Helpers.cs")), out var problem);
        if (problem == null && host.Count < MinHostKeys)
            problem = $"only {host.Count} keys were read from ApplyConfigSet (at least {MinHostKeys} expected): the scan no longer matches the bridge's case labels";
        var commands = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(wiring, "*.cs"))
            commands.UnionWith(Labels(File.ReadAllText(f)));
        var prefixes = new HashSet<string>(host.Where(k => k.Contains('.')).Select(k => k[..k.IndexOf('.')]), StringComparer.Ordinal);
        return new Scan(host, ClientKeys(root, prefixes, commands, host), problem);
    }

    private static SortedSet<string> HostKeys(string src, out string? problem)
    {
        problem = null;
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        int a = src.IndexOf("private static void ApplyConfigSet(", StringComparison.Ordinal);
        if (a < 0)
        {
            problem = "ApplyConfigSet was not found in RemoteServerWiring.Helpers.cs: the scan reads its case labels";
            return keys;
        }
        // The switch ends where the next member starts.
        int b = src.IndexOf("\n    // =====", a, StringComparison.Ordinal);
        foreach (var k in Labels(b > a ? src[a..b] : src[a..], arms: false))
            if (KeyShape().IsMatch(k)) keys.Add(k);
        return keys;
    }

    /// <summary>Every string a switch matches on: <c>case</c> labels (with <c>or</c> and
    /// <c>when</c>) and, with <paramref name="arms"/>, switch expression arms (<c>"a" =&gt;</c>).</summary>
    private static IEnumerable<string> Labels(string src, bool arms = true) =>
        CaseLabel().Matches(src).Concat(arms ? ArmLabel().Matches(src) : Enumerable.Empty<Match>())
            .SelectMany(m => Quoted().Matches(m.Groups[1].Value).Select(q => q.Groups[1].Value));

    private static SortedSet<string> ClientKeys(string root, HashSet<string> prefixes, HashSet<string> commands, SortedSet<string> host)
    {
        var www = Path.Combine(root, "Shared", "AgOpenWeb.RemoteServer", "wwwroot");
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(www, "*.html").Concat(Directory.EnumerateFiles(Path.Combine(www, "js"), "*.js")))
        {
            var text = File.ReadAllText(f);
            foreach (Match m in ClientKey().Matches(text)) keys.Add(m.Groups[1].Value);
            foreach (Match m in QuotedKey().Matches(text))
            {
                var k = m.Groups[1].Value + "." + m.Groups[2].Value;
                if (prefixes.Contains(m.Groups[1].Value) && (host.Contains(k) || !commands.Contains(k))) keys.Add(k);
            }
        }
        return keys;
    }

    [GeneratedRegex("^[a-zA-Z]+(?:\\.[A-Za-z0-9]+)?$")]
    private static partial Regex KeyShape();

    [GeneratedRegex("\\bcase\\s+(\"[^\"\\n]+\"(?:\\s+or\\s+\"[^\"\\n]+\")*)\\s*(?:when\\b|:)")]
    private static partial Regex CaseLabel();

    [GeneratedRegex("(\"[^\"\\n]+\"(?:\\s+or\\s+\"[^\"\\n]+\")*)\\s*=>")]
    private static partial Regex ArmLabel();

    [GeneratedRegex("\"([^\"\\n]+)\"")]
    private static partial Regex Quoted();

    [GeneratedRegex("(?:data-key=\"|data-cfg=\"|data-cfgnum=\"|data-tgl=\"|cfgSend\\(['\"]|config\\.set\\|)([a-z][a-zA-Z]*\\.[a-zA-Z][A-Za-z0-9]+)")]
    private static partial Regex ClientKey();

    [GeneratedRegex("['\"]([a-z][a-zA-Z]*)\\.([a-zA-Z][A-Za-z0-9]+)['\"]")]
    private static partial Regex QuotedKey();
}
