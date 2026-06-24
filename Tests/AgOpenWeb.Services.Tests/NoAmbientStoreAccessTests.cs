using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Architectural guard (CONFIG_STATE_AUDIT §11.2): production service and
/// view-model code must not reach for the ambient singletons
/// <c>ConfigurationStore.Instance</c> or <c>ApplicationState.Instance</c>.
/// Both central stores are registered in DI and must be received by constructor
/// injection — "ambient, not injected" is the coupling smell §11 set out to
/// remove. The static <c>Instance</c>/<c>SetInstance</c> accessors survive only
/// as the seam for (a) framework-instantiated Avalonia Views, which the XAML
/// loader news up outside DI, and (b) test setup — so the Views project and the
/// Tests are deliberately NOT scanned. This test stops the static grab from
/// regrowing in the business logic.
/// </summary>
[TestFixture]
public class NoAmbientStoreAccessTests
{
    // `.Instance` access on the de-ambiented stores. (Does not match
    // `SetInstance`, nor `nameof(ConfigurationStore.X)` type references, nor
    // PersistentAppState.Instance — a separate persisted singleton out of scope.)
    private static readonly Regex AmbientAccess =
        new(@"\b(ConfigurationStore|ApplicationState)\.Instance\b", RegexOptions.Compiled);

    [Test]
    public void NoStaticInstanceAccess_InServicesOrViewModels()
    {
        var root = FindRepoRoot();
        var scanDirs = new[]
        {
            Path.Combine(root, "Shared", "AgOpenWeb.Services"),
            Path.Combine(root, "Shared", "AgOpenWeb.ViewModels"),
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
                    if (AmbientAccess.IsMatch(lines[i]))
                        violations.Add($"{Path.GetFileName(file)}:{i + 1}  {lines[i].Trim()}");
                }
            }
        }

        Assert.That(violations, Is.Empty,
            "Ambient store access found. Inject ConfigurationStore / ApplicationState " +
            "via the constructor instead of reading the static .Instance (the static seam " +
            "is reserved for framework-instantiated Views and test setup):\n  " +
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
