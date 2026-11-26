// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The MainViewModel members the rest of the app uses (the web wiring, the web host, the heads,
/// the tests and the simulators), each with its signature, held in
/// <c>TestData/MainViewModelSurface.txt</c>. The refactor in
/// <c>Plans/MAINVIEWMODEL_REFACTOR_PLAN.md</c> moves code out of the view model but keeps these
/// members as they are, so <c>RemoteServerWiring</c> does not change: a member gone or changed
/// fails here. A member nothing outside uses is not listed, so removing dead code does not touch
/// the file. When a change to the surface is wanted, the file is made again on purpose:
/// <c>AGOPENWEB_UPDATE_SURFACE=1 dotnet test --filter MainViewModelSurfaceTests</c>, and the
/// diff is reviewed with the change.
/// </summary>
[TestFixture]
public class MainViewModelSurfaceTests
{
    // Where the users of the view model are, from the repository root.
    private static readonly string[] UserFolders =
    {
        "Shared/AgOpenWeb.RemoteWiring",
        "Shared/AgOpenWeb.RemoteServer",
        "Platforms",
        "Tests",
        "Simulators",
    };

    [Test]
    public void The_members_the_rest_of_the_app_uses_are_unchanged()
    {
        string file = SurfaceFile();
        var current = Signatures(UsedNames());

        if (Environment.GetEnvironmentVariable("AGOPENWEB_UPDATE_SURFACE") == "1")
        {
            File.WriteAllLines(file, Header.Concat(current));
            Assert.Pass($"wrote {current.Count} signatures to {file}");
        }

        var expected = File.ReadAllLines(file).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        var now = Signatures(expected.Select(NameOf).ToHashSet()).ToHashSet();
        var missing = expected.Where(e => !now.Contains(e)).ToList();

        Assert.That(missing, Is.Empty,
            "MainViewModel members the app uses were removed or changed. If that is meant, make the file " +
            "again (AGOPENWEB_UPDATE_SURFACE=1) and review its diff. Now:\n" +
            string.Join("\n", missing.Select(m => "  " + m + "  ->  " +
                string.Join(" | ", now.Where(n => NameOf(n) == NameOf(m)).DefaultIfEmpty("(gone)")))));
    }

    private static readonly string[] Header =
    {
        "# The MainViewModel members the rest of the app uses, with their signatures.",
        "# Made by MainViewModelSurfaceTests (AGOPENWEB_UPDATE_SURFACE=1); do not edit by hand.",
    };

    private static string NameOf(string signature) => signature.Split(' ')[1].Split('(')[0];

    /// <summary>The public members declared on MainViewModel whose names another project reaches with <c>.Name</c>.</summary>
    private static HashSet<string> UsedNames()
    {
        string root = RepoRoot();
        var text = new System.Text.StringBuilder();
        foreach (var folder in UserFolders)
        {
            string dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    f.EndsWith(nameof(MainViewModelSurfaceTests) + ".cs")) continue;
                text.AppendLine(File.ReadAllText(f));
            }
        }
        var used = Regex.Matches(text.ToString(), @"\.([A-Z][A-Za-z0-9_]*)\b").Select(m => m.Groups[1].Value).ToHashSet();
        return PublicMembers().Select(m => m.Name).Where(used.Contains).ToHashSet();
    }

    private static IEnumerable<MemberInfo> PublicMembers() =>
        typeof(MainViewModel)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m is PropertyInfo or FieldInfo or EventInfo ||
                        (m is MethodInfo mi && !mi.IsSpecialName));

    /// <summary>One line per member (one per overload): kind, name, type or parameters, sorted.</summary>
    private static List<string> Signatures(ISet<string> names) =>
        PublicMembers().Where(m => names.Contains(m.Name)).Select(Signature).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();

    private static string Signature(MemberInfo m) => m switch
    {
        PropertyInfo p => $"property {p.Name} : {TypeName(p.PropertyType)}{(p.CanWrite && p.SetMethod!.IsPublic ? " { get; set; }" : " { get; }")}",
        FieldInfo f => $"field {f.Name} : {TypeName(f.FieldType)}",
        EventInfo e => $"event {e.Name} : {TypeName(e.EventHandlerType!)}",
        MethodInfo mi => $"method {mi.Name}({string.Join(", ", mi.GetParameters().Select(p => TypeName(p.ParameterType)))}) : {TypeName(mi.ReturnType)}",
        _ => $"member {m.Name}",
    };

    private static string TypeName(Type t)
    {
        if (Nullable.GetUnderlyingType(t) is { } u) return TypeName(u) + "?";
        if (t.IsArray) return TypeName(t.GetElementType()!) + "[]";
        if (!t.IsGenericType) return t.FullName ?? t.Name;
        string name = t.GetGenericTypeDefinition().FullName!;
        name = name[..name.IndexOf('`')];
        return $"{name}<{string.Join(", ", t.GetGenericArguments().Select(TypeName))}>";
    }

    private static string SurfaceFile([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "TestData", "MainViewModelSurface.txt");

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
