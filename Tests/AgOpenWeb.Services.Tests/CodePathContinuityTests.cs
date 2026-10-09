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
/// Architectural guards for code-path continuity: the repo has one way to do each of
/// these things, and a change that adds a second way should fail here before anyone
/// reads the diff. Each test names the existing path in its failure message. Like
/// <see cref="NoAmbientStoreAccessTests"/>, these scan the source tree, so they need no
/// project references and run in the ordinary test job.
///
/// What is pinned, and where the one way lives:
/// <list type="bullet">
/// <item>Project dependencies flow Models → Services → ViewModels / RemoteServer → RemoteWiring.</item>
/// <item>The web client is <c>index.html</c> + <c>app.js</c> + <c>transport.js</c> + <c>i18n.js</c>:
/// classic scripts, no modules, no workers, no extra script or stylesheet files.</item>
/// <item>Client → host is an <c>id|arg</c> text frame routed in <c>RemoteServerWiring</c>;
/// host → client is a binary frame from <c>WireCodec</c>. The hub never sends text.</item>
/// <item>Every command id the client sends has a handler on the host.</item>
/// <item>Files written to disk have names from one list (field, job, app and AgOpenGPS-import
/// formats). A new file name is a format decision, made here on purpose.</item>
/// <item>JSON is <c>System.Text.Json</c>; <c>Newtonsoft.Json</c> stays inside the AgShare client.</item>
/// <item>The host-side wiring runs on the host loop; it creates no timers or threads of its own.</item>
/// </list>
/// </summary>
[TestFixture]
public class CodePathContinuityTests
{
    // ── Project layering ──────────────────────────────────────────────────

    [Test]
    public void ProjectReferences_FlowOneWay()
    {
        var root = RepoRoot();
        var allowed = new Dictionary<string, string[]>
        {
            ["AgOpenWeb.Models"] = Array.Empty<string>(),
            ["AgOpenWeb.Services"] = new[] { "AgOpenWeb.Models" },
            ["AgOpenWeb.ViewModels"] = new[] { "AgOpenWeb.Models", "AgOpenWeb.Services" },
            ["AgOpenWeb.RemoteServer"] = new[] { "AgOpenWeb.Models", "AgOpenWeb.Services" },
            ["AgOpenWeb.RemoteWiring"] = new[] { "AgOpenWeb.Models", "AgOpenWeb.Services", "AgOpenWeb.ViewModels", "AgOpenWeb.RemoteServer" },
        };
        var violations = new List<string>();
        foreach (var (project, refs) in allowed)
        {
            var csproj = Path.Combine(root, "Shared", project, project + ".csproj");
            var actual = Regex.Matches(File.ReadAllText(csproj), @"ProjectReference Include=""[^""]*[\\/]([^\\/""]+)\.csproj""")
                .Select(m => m.Groups[1].Value).ToList();
            foreach (var r in actual.Where(r => !refs.Contains(r)))
                violations.Add($"{project} → {r}");
        }
        Assert.That(violations, Is.Empty,
            "A Shared project gained a reference outside the layering Models → Services → " +
            "ViewModels / RemoteServer → RemoteWiring. Put the code in the lower project instead:\n  " +
            string.Join("\n  ", violations));
    }

    // ── The web client ────────────────────────────────────────────────────

    private static readonly string[] ClientScripts = { "app.js", "transport.js", "i18n.js" };

    [Test]
    public void WebClient_IsIndexAppTransportI18n_NoModulesNoWorkers()
    {
        var www = Path.Combine(RepoRoot(), "Shared", "AgOpenWeb.RemoteServer", "wwwroot");
        var violations = new List<string>();

        // Script and stylesheet files outside vendor/: only the three scripts. Styles live in index.html.
        foreach (var file in Directory.EnumerateFiles(www, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(www, file).Replace('\\', '/');
            if (rel.StartsWith("vendor/")) continue;
            var ext = Path.GetExtension(rel);
            if ((ext == ".js" || ext == ".mjs" || ext == ".css") && !ClientScripts.Contains(rel))
                violations.Add($"{rel}: extra client file; the client is index.html + app.js + transport.js + i18n.js");
        }

        // index.html loads exactly those scripts, as classic scripts.
        var html = File.ReadAllText(Path.Combine(www, "index.html"));
        var tags = Regex.Matches(html, @"<script\b[^>]*>").Select(m => m.Value).ToList();
        foreach (var tag in tags)
        {
            if (tag.Contains("type=\"module\"")) violations.Add($"index.html: {tag} — module scripts are not used; app.js is a classic script");
            var src = Regex.Match(tag, @"src=""/?([^""]+)""").Groups[1].Value;
            if (src.Length > 0 && !src.StartsWith("vendor/") && !ClientScripts.Contains(src))
                violations.Add($"index.html: {tag} — not one of the client's scripts");
        }
        if (Regex.IsMatch(html, @"<link\b[^>]*rel=""stylesheet""[^>]*href=""/?(?!https?://)[^""]+"""))
            violations.Add("index.html: a local stylesheet link; styles live inline in index.html");

        // The scripts don't import or spawn anything.
        foreach (var name in ClientScripts)
        {
            var lines = File.ReadAllLines(Path.Combine(www, name));
            for (int i = 0; i < lines.Length; i++)
            {
                var l = lines[i].TrimStart();
                if (l.StartsWith("//") || l.StartsWith("*")) continue;
                if (Regex.IsMatch(l, @"^import\s|^export\s|\bimport\(|\bnew Worker\(|\bimportScripts\("))
                    violations.Add($"{name}:{i + 1}: {l.Trim()} — no modules or workers; add to app.js directly");
            }
        }

        Assert.That(violations, Is.Empty, string.Join("\n  ", violations));
    }

    // ── The wire ──────────────────────────────────────────────────────────

    [Test]
    public void WebSocket_HostSendsBinaryOnly_TextIsTheCommandFrame()
    {
        var hub = Path.Combine(RepoRoot(), "Shared", "AgOpenWeb.RemoteServer", "WebSocketHub.cs");
        var lines = File.ReadAllLines(hub);
        var textUses = lines.Select((l, i) => (l, i)).Where(x => x.l.Contains("WebSocketMessageType.Text")).ToList();
        Assert.That(textUses.Select(x => x.l.Trim()), Is.EqualTo(new[]
        {
            "if (res.MessageType == WebSocketMessageType.Text && res.Count > 0)",
        }), "WebSocketHub uses WebSocketMessageType.Text somewhere new. Host → client goes out " +
            "as binary frames encoded by WireCodec (add a DTO in Contracts.cs, encode it in " +
            "WireCodec.cs, decode it in transport.js); client → host is the one id|arg text frame.");

        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "Shared", "AgOpenWeb.RemoteServer"), "*.cs"))
            if (Path.GetFileName(file) != "WebSocketHub.cs")
                Assert.That(File.ReadAllText(file), Does.Not.Contain("WebSocketMessageType"),
                    $"{Path.GetFileName(file)} touches the WebSocket directly; only WebSocketHub does.");
    }

    [Test]
    public void EveryClientCommand_HasAHostHandler()
    {
        var root = RepoRoot();
        var www = Path.Combine(root, "Shared", "AgOpenWeb.RemoteServer", "wwwroot");
        var app = File.ReadAllText(Path.Combine(www, "app.js"));
        var html = File.ReadAllText(Path.Combine(www, "index.html"));

        // Command ids the client can send: literal ids in transport.send(...), data-cmd
        // attributes in the page and in app.js markup, and the hotkey → command table.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(app, @"transport\.send\([^)]*?'([a-z][A-Za-z0-9_.-]*\.[A-Za-z][A-Za-z0-9_.-]*)"))
            ids.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(html + app, @"data-cmd=\\?""([a-z][A-Za-z0-9_.-]*\.[A-Za-z][A-Za-z0-9_.-]*)"))
            ids.Add(m.Groups[1].Value);
        var hotkeys = Regex.Match(app, @"HOTKEY_TIER1\s*=\s*\{(.*?)\};", RegexOptions.Singleline);
        if (hotkeys.Success)
            foreach (Match m in Regex.Matches(hotkeys.Groups[1].Value, @"'([a-z][A-Za-z0-9_.-]*\.[A-Za-z][A-Za-z0-9_.-]*)'"))
                ids.Add(m.Groups[1].Value);
        Assert.That(ids.Count, Is.GreaterThan(100), "the extraction found too few command ids; the send() idiom changed?");

        // Handlers on the host: `case "id":` and `"id" => ...` in the wiring, plus the hub's
        // own control.* ids. config.set is a table of keys with its own switch.
        var host = string.Concat(Directory.EnumerateFiles(Path.Combine(root, "Shared", "AgOpenWeb.RemoteWiring"), "RemoteServerWiring*.cs")
            .Select(File.ReadAllText));
        var hub = File.ReadAllText(Path.Combine(root, "Shared", "AgOpenWeb.RemoteServer", "WebSocketHub.cs"));
        var handled = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(host + hub, @"(?:case\s+""|^\s*"")([A-Za-z0-9_.-]+)""\s*(?::|=>)", RegexOptions.Multiline))
            handled.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(host + hub, @"""([A-Za-z0-9_.-]+)""\s*=>"))
            handled.Add(m.Groups[1].Value);

        var unhandled = ids.Where(id => !handled.Contains(id.Split('|')[0]) && !id.StartsWith("control.")).OrderBy(x => x).ToList();
        Assert.That(unhandled, Is.Empty,
            "app.js / index.html send command ids with no handler in RemoteServerWiring. " +
            "Route them there (a `case` or a `\"id\" => vm.Command` arm); that is the only " +
            "client → host path:\n  " + string.Join("\n  ", unhandled));
    }

    // ── Files on disk ─────────────────────────────────────────────────────

    // Every data file name the Shared code writes or reads, by literal. A new name here is a
    // new on-disk format: add it with the plan or issue that decided it. Field data is one
    // field.geojson plus the named companions; AgOpenGPS names are import-only.
    private static readonly HashSet<string> KnownDataFiles = new(StringComparer.Ordinal)
    {
        // Field folder (Plans/Completed/FILE_FORMAT_MODERNIZATION_PLAN.md)
        "field.geojson", "background.png", "contours.geojson", "recorded-paths.geojson", "elevation.csv",
        "job.json", "screenshot.png", "manifest.json", "field.json", "doc.kml",
        "coverage_disp.bin", "coverage_detect.bin",
        // App data
        "appsettings.json", "appstate.json", "configuration.json", "runtime_state.json",
        "active_profile_name.txt", "user_notes.txt", "agshare.txt", "turn_path.json",
        "TramSystems.json", "TramConfig.json", "HeadlandSegments.json",
        ".AutoSteer.json", ".tool.xml", ".env.xml",
        // Bug report dump
        "gps_data_log.csv", "logs.txt", "system_info.txt", "ntrip_rtcm.txt",
        "appsettings_error.txt", "configuration_error.txt", "field_error.txt", "job_error.txt",
        "logs_error.txt", "save_error.txt", "screenshot_error.txt", "state_error.txt",
        "turn_path_error.txt", "gps_data_log_error.txt",
        // AgOpenGPS formats, imported once and deleted
        "Field.txt", "Boundary.txt", "Sections.txt", "RecPath.txt", "Elevation.txt", "Contour.txt",
        "TramLines.txt", "TrackLines.txt", "Headlines.txt", "Headland.txt", "Flags.txt",
        "BackPic.txt", "BackPic.png", "ActiveTrack.txt", "ABLines.txt",
    };

    [Test]
    public void DataFileNames_ComeFromOneList()
    {
        var root = RepoRoot();
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in new[] { "AgOpenWeb.Models", "AgOpenWeb.Services", "AgOpenWeb.ViewModels", "AgOpenWeb.RemoteServer", "AgOpenWeb.RemoteWiring" })
            foreach (var file in SourceFiles(Path.Combine(root, "Shared", project)))
            {
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                    foreach (Match m in Regex.Matches(lines[i], @"""([A-Za-z0-9_.-]+\.(?:json|geojson|csv|txt|png|bin|zip|log|kml|xml))"""))
                        found.TryAdd(m.Groups[1].Value, $"{Path.GetFileName(file)}:{i + 1}");
            }
        var unknown = found.Where(kv => !KnownDataFiles.Contains(kv.Key)).Select(kv => $"{kv.Key}  ({kv.Value})").ToList();
        Assert.That(unknown, Is.Empty,
            "New on-disk file names. Field data belongs in field.geojson (or one of its named " +
            "companions); if a new file is really needed, add it to KnownDataFiles with the " +
            "decision that introduced it:\n  " + string.Join("\n  ", unknown));
    }

    // ── One JSON library ──────────────────────────────────────────────────

    [Test]
    public void Newtonsoft_StaysInsideAgShare()
    {
        var root = RepoRoot();
        var violations = new List<string>();
        foreach (var project in new[] { "AgOpenWeb.Models", "AgOpenWeb.Services", "AgOpenWeb.ViewModels", "AgOpenWeb.RemoteServer", "AgOpenWeb.RemoteWiring" })
            foreach (var file in SourceFiles(Path.Combine(root, "Shared", project)))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.StartsWith("Shared/AgOpenWeb.Services/AgShare/")) continue;
                if (Regex.IsMatch(File.ReadAllText(file), @"\bNewtonsoft\b"))
                    violations.Add(rel);
            }
        Assert.That(violations, Is.Empty,
            "Newtonsoft.Json outside the AgShare client. New code uses System.Text.Json " +
            "(see DebugDumpService / the GeoJson services):\n  " + string.Join("\n  ", violations));
    }

    // ── No timers of their own in the wiring ──────────────────────────────

    [Test]
    public void RemoteWiring_CreatesNoTimersOrThreads()
    {
        var root = RepoRoot();
        var pattern = new Regex(@"\bnew\s+(?:System\.Threading\.)?Timer\s*\(|\bPeriodicTimer\s*\(|\bnew\s+Thread\s*\(|\bDispatcherTimer\b");
        var violations = new List<string>();
        foreach (var file in SourceFiles(Path.Combine(root, "Shared", "AgOpenWeb.RemoteWiring")))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (pattern.IsMatch(lines[i]) && !lines[i].TrimStart().StartsWith("//"))
                    violations.Add($"{Path.GetFileName(file)}:{i + 1}  {lines[i].Trim()}");
        }
        Assert.That(violations, Is.Empty,
            "RemoteWiring created a timer or thread. Periodic host work runs on the host loop " +
            "via IUiTimerFactory in MainViewModel; the client feed is MapBroadcaster's single " +
            "PeriodicTimer. Nothing else should tick on its own:\n  " + string.Join("\n  ", violations));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static IEnumerable<string> SourceFiles(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            : Enumerable.Empty<string>();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AgOpenWeb.sln")))
            dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "Could not locate repo root (AgOpenWeb.sln) from test base dir.");
        return dir!.FullName;
    }
}
