using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Architectural guard: production view-model / view code must not write the
/// AppSettings DTO directly (<c>*.Settings.&lt;X&gt; = …</c>). Configuration
/// must flow through ConfigurationStore (then ConfigurationService.SaveAppSettings),
/// and persistent state through PersistentAppState. Direct DTO writes are the
/// clobber-risk pattern this whole remediation removed; this test stops it
/// regrowing. ConfigurationService/SettingsService (the sync layer) are the only
/// legitimate writers and live in the Services project, which is not scanned.
/// </summary>
[TestFixture]
public class NoBypassWritesTests
{
    // Properties that legitimately live ONLY in AppSettings (no ConfigurationStore
    // mirror) and are therefore safe to set on the DTO directly.
    private static readonly HashSet<string> Allowed = new()
    {
        "Language", // localization preference, read directly at startup
    };

    private static readonly Regex DtoWrite =
        new(@"\.Settings\.([A-Za-z_]\w*)\s*=(?!=)", RegexOptions.Compiled);

    [Test]
    public void NoDirectAppSettingsDtoWrites_InViewModelsOrViews()
    {
        var root = FindRepoRoot();
        var scanDirs = new[]
        {
            Path.Combine(root, "Shared", "AgOpenWeb.ViewModels"),
            Path.Combine(root, "Shared", "AgOpenWeb.Views"),
        };

        var violations = new List<string>();

        foreach (var dir in scanDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                    continue;

                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    foreach (Match m in DtoWrite.Matches(lines[i]))
                    {
                        var prop = m.Groups[1].Value;
                        if (Allowed.Contains(prop)) continue;
                        violations.Add($"{Path.GetFileName(file)}:{i + 1}  .Settings.{prop} =");
                    }
                }
            }
        }

        Assert.That(violations, Is.Empty,
            "Direct AppSettings DTO writes found. Route config through " +
            "ConfigStore + SaveAppSettings (or PersistentAppState for state), or add " +
            "the property to the Allowed set if it is genuinely DTO-only:\n  " +
            string.Join("\n  ", violations));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AgOpenWeb.sln")))
            dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "Could not locate repo root (AgOpenWeb.sln) from test base dir.");
        return dir!.FullName;
    }
}
