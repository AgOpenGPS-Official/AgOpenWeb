// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Source-scanning guards for the CONTRIBUTING "Threading Model" rules. Both pin the
/// state of the tree today with an allowlist, so the rule is "no new ones": the listed
/// sites are known debt, to be paid down, not a licence.
/// </summary>
[TestFixture]
public class ThreadingModelTests
{
    // Rule 2: the only writer of State.YouTurn / Guidance / Vehicle / Section is
    // ApplyGpsCycleResult. Existing writes elsewhere, by file and count (#294 debt list).
    private static readonly Dictionary<string, int> KnownStateWrites = new(StringComparer.Ordinal)
    {
        ["MainViewModel.Simulator.cs"] = 2,       // sim sets lat/lon on the mirror
        ["MainViewModel.GpsHandling.cs"] = 1,     // roll mirror for the web UI
        ["MainViewModel.Commands.Track.cs"] = 4,  // snake sequence reset; A/B swap
        ["MainViewModel.cs"] = 1,                 // clears DisplayLine on field close
    };

    private static readonly Regex StateWrite =
        new(@"\bState\.(YouTurn|Guidance|Vehicle|Section)\.\w+\s*(=(?!=)|\+=|-=)", RegexOptions.Compiled);

    [Test]
    public void PipelineOwnedState_IsWrittenOnlyByApplyGpsCycleResult()
    {
        var dir = Path.Combine(RepoRoot(), "Shared", "AgOpenWeb.ViewModels");
        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in SourceFiles(dir))
        {
            var name = Path.GetFileName(file);
            if (name == "MainViewModel.ApplyResults.cs") continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var l = lines[i];
                if (l.TrimStart().StartsWith("//")) continue;
                // An event subscription or a value quoted inside a log string is not a write.
                if (l.Contains("PropertyChanged +=") || Regex.IsMatch(l, @"\bLog\w*\(")) continue;
                if (StateWrite.IsMatch(l))
                    (found.TryGetValue(name, out var list) ? list : found[name] = new()).Add($"{name}:{i + 1}  {l.Trim()}");
            }
        }

        var violations = new List<string>();
        foreach (var (file, sites) in found)
        {
            KnownStateWrites.TryGetValue(file, out var allowed);
            if (sites.Count > allowed)
                violations.Add($"{file}: {sites.Count} writes, {allowed} known\n    " + string.Join("\n    ", sites));
        }
        Assert.That(violations, Is.Empty,
            "A new write to pipeline-owned State.* outside ApplyGpsCycleResult. Push an intent " +
            "through IPipelineIntents and let the cycle's snapshot update the mirror " +
            "(CONTRIBUTING, Threading Model, rule 2):\n  " + string.Join("\n  ", violations));
    }

    // "Adding a new service that reads GPS/position: take *WorkingState as a parameter,
    // don't inject ApplicationState." The services that already do, by file.
    private static readonly HashSet<string> KnownApplicationStateConsumers = new(StringComparer.Ordinal)
    {
        "GpsPipelineService.cs", "AutoSteerService.cs", "SectionControlService.cs",
        "SmartWasCalibrationService.cs", "DebugDumpService.cs",
    };

    [Test]
    public void Services_DoNotTakeApplicationState_BeyondTheKnownOnes()
    {
        var dir = Path.Combine(RepoRoot(), "Shared", "AgOpenWeb.Services");
        var violations = new List<string>();
        foreach (var file in SourceFiles(dir))
        {
            var name = Path.GetFileName(file);
            if (KnownApplicationStateConsumers.Contains(name)) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (Regex.IsMatch(lines[i], @"\bApplicationState\s+\w+\s*[,)=]") && !lines[i].TrimStart().StartsWith("//"))
                    violations.Add($"{name}:{i + 1}  {lines[i].Trim()}");
        }
        Assert.That(violations, Is.Empty,
            "A service takes ApplicationState. Take the *WorkingState it needs as a parameter " +
            "instead (CONTRIBUTING, Threading Model, common patterns):\n  " + string.Join("\n  ", violations));
    }

    private static IEnumerable<string> SourceFiles(string dir) =>
        Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AgOpenWeb.sln")))
            dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "Could not locate repo root (AgOpenWeb.sln) from test base dir.");
        return dir!.FullName;
    }
}
