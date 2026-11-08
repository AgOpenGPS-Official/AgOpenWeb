// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace AgOpenWeb.VehicleToolScenarios.Tests;

/// <summary>
/// Every setting the operator can change, read from the source each run, so a setting added
/// tomorrow shows up in the sweep's report without anyone remembering to list it:
/// the keys the host's config bridge handles (<c>ApplyConfigSet</c> in
/// RemoteServerWiring.Helpers.cs) and the keys the web client sends (<c>data-key</c> in
/// index.html, <c>cfgSend('…')</c> and <c>data-cfg="…"</c> in the scripts).
/// </summary>
public static partial class SettingKeys
{
    /// <summary>The repository root (the folder with AgOpenWeb.sln), from the test's folder up.</summary>
    public static string? RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "AgOpenWeb.sln"))) return d.FullName;
        return null;
    }

    public static SortedSet<string> HostKeys(string root)
    {
        var src = File.ReadAllText(Path.Combine(root, "Shared", "AgOpenWeb.RemoteWiring", "RemoteServerWiring.Helpers.cs"));
        int a = src.IndexOf("private static void ApplyConfigSet(", StringComparison.Ordinal);
        if (a < 0) return new SortedSet<string>(StringComparer.Ordinal);
        // The switch ends where the next member starts.
        int b = src.IndexOf("\n    // =====", a, StringComparison.Ordinal);
        var body = b > a ? src[a..b] : src[a..];
        return new SortedSet<string>(CaseLabel().Matches(body).Select(m => m.Groups[1].Value), StringComparer.Ordinal);
    }

    public static SortedSet<string> ClientKeys(string root)
    {
        var www = Path.Combine(root, "Shared", "AgOpenWeb.RemoteServer", "wwwroot");
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(www, "*.html").Concat(Directory.EnumerateFiles(Path.Combine(www, "js"), "*.js")))
        {
            var text = File.ReadAllText(f);
            foreach (Match m in ClientKey().Matches(text)) keys.Add(m.Groups[1].Value);
        }
        return keys;
    }

    [GeneratedRegex("case \"([a-zA-Z]+(?:\\.[A-Za-z0-9]+)?)\":")]
    private static partial Regex CaseLabel();

    [GeneratedRegex("(?:data-key=\"|data-cfg=\"|cfgSend\\(['\"]|config\\.set\\|)([a-z][a-zA-Z]*\\.[a-zA-Z][A-Za-z0-9]+)")]
    private static partial Regex ClientKey();
}
