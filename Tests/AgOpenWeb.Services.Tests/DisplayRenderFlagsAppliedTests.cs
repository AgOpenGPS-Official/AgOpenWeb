using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Architectural guard for config/state audit §13 (the "apply gap"): a
/// <c>ConfigStore.Display.*</c> flag can persist correctly and be wired to a UI
/// toggle yet do nothing because the live renderer never reads it. The reflection
/// shadow-guard (StateShadowGuardTests) catches dead duplicate state, but it
/// cannot catch a flag that is simply never applied to the map.
///
/// This test asserts every render-affecting Display flag is actually read by the
/// Skia map control (i.e. flows into the MapRenderState / draw path). If you add a
/// new Display flag that should change what is drawn, wire it in SkiaMapControl
/// (read it from <c>displayCfg.</c> in SendStateToHandlerNow, or in a partial) and
/// add it here; if a flag is genuinely non-visual, it does not belong in this list.
/// </summary>
[TestFixture]
public class DisplayRenderFlagsAppliedTests
{
    // DisplayConfig properties whose whole purpose is to change what the map draws.
    // Each MUST be read somewhere in the SkiaMapControl source.
    private static readonly string[] RenderAffectingDisplayFlags =
    {
        "GridVisible",
        "SvennArrowVisible",
        "HeadlandDistanceVisible",
        "ExtraGuidelines",
        "ExtraGuidelinesCount",
        "FieldTextureVisible",
        "FieldTextureMoveable",
        "LineSmoothEnabled",
        "DisplayResolutionMultiplier",
    };

    [Test]
    public void EveryRenderAffectingDisplayFlag_IsReadByTheMapControl()
    {
        var root = FindRepoRoot();
        var controlsDir = Path.Combine(root, "Shared", "AgOpenWeb.Views", "Controls");
        Assert.That(Directory.Exists(controlsDir), Is.True, $"Missing {controlsDir}");

        // Combine every SkiaMapControl source file (the control + its partials).
        var source = string.Concat(
            Directory.EnumerateFiles(controlsDir, "SkiaMapControl*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .Select(File.ReadAllText));

        Assert.That(source, Is.Not.Empty, "Could not read SkiaMapControl source.");

        var unwired = RenderAffectingDisplayFlags
            .Where(flag => !source.Contains("." + flag))
            .ToList();

        Assert.That(unwired, Is.Empty,
            "These Display.* flags are render-affecting but the map control never " +
            "reads them — they persist and toggle in the UI but change nothing on " +
            "screen (config/state audit §13 apply gap). Wire each into SkiaMapControl:\n  "
            + string.Join("\n  ", unwired));
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
