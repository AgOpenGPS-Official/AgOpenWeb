// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Operations;

// The settings reader map: for every key the web client can set (`config.set|key:value`, one
// `case` of RemoteServerWiring.ApplyConfigSet), what the case writes in the configuration store
// and the code that reads it, found by the compiler (every getter call, every use of a store
// method), not by name. A read inside the store itself (a method packing a bit, a property
// computed from another) is followed to whoever uses that, until the code leaves the store.
//
//   dotnet run --project Tools/settings-readers                 print the map
//   dotnet run --project Tools/settings-readers -- --write      write Tools/settings-readers/settings-readers.txt
//   dotnet run --project Tools/settings-readers -- --check      exit 1 when that file is not the map of the code,
//                                                               or a setting is read by no app or page code
MSBuildLocator.RegisterDefaults();
return await ReaderMap.RunAsync(args);

static class ReaderMap
{
    const string Switch = "ApplyConfigSet";
    const string StoreNamespace = "AgOpenWeb.Models.Configuration";

    // Where a read ends, by the type it is in. Saving and restoring a setting, sending it to the
    // page and routing it are kinds of their own: a setting read only by those does nothing.
    static readonly string[] SaveTypes =
    {
        "ConfigurationService", "SettingsService", "AgOpenGpsSettings", "AgOpenGpsProfileImport",
        "VehicleProfileService", "ToolProfileService", "PersistentStateService", "AppSettings",
        "ProfileJsonServiceV1", "VehicleProfileJsonService", "ToolProfileJsonService", "HitchMigration",
    };
    static readonly string[] PageTypes = { "SceneProjector", "WireCodec" };
    static readonly string[] Bookkeeping = { "ConfigurationStore.HasUnsavedChanges" };
    // The bug report dump lists the settings; it does not act on them.
    static readonly string[] ReportTypes = { "DebugDumpService" };

    public static async Task<int> RunAsync(string[] args)
    {
        string repo = FindRepo();
        string mapPath = Path.Combine(repo, "Tools", "settings-readers", "settings-readers.txt");
        string project = Path.Combine(repo, "Shared", "AgOpenWeb.RemoteWiring", "AgOpenWeb.RemoteWiring.csproj");

        using var workspace = MSBuildWorkspace.Create(new Dictionary<string, string> { ["Configuration"] = "Debug" });
        var failures = new List<string>();
        workspace.RegisterWorkspaceFailedHandler(e => { if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure) failures.Add(e.Diagnostic.Message); });
        await workspace.OpenProjectAsync(project);
        var solution = workspace.CurrentSolution;

        // A map built from code the compiler could not read would be wrong without saying so.
        foreach (var p in solution.Projects)
        {
            var c = await p.GetCompilationAsync() ?? throw new InvalidOperationException("no compilation for " + p.Name);
            var errors = c.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                foreach (var f in failures) Console.Error.WriteLine("workspace: " + f);
                foreach (var e in errors.Take(20)) Console.Error.WriteLine(p.Name + ": " + e);
                Console.Error.WriteLine(p.Name + ": " + errors.Count.ToString(CultureInfo.InvariantCulture) + " compile error(s); the map needs code that compiles");
                return 2;
            }
        }

        var cases = await ReadSwitchAsync(solution);
        var map = new Mapper(solution);
        var page = new Page(Path.Combine(repo, "Shared", "AgOpenWeb.RemoteServer", "wwwroot"));
        var sb = new StringBuilder();
        var silent = new List<string>();
        var body = new StringBuilder();

        // The store's bookkeeping (the unsaved-changes flag MarkChanged sets) is not the setting:
        // listed once, and not what makes a key act.
        var shared = cases.SelectMany(c => c.Writes).Where(w => Bookkeeping.Contains(Name(w)))
            .ToHashSet(SymbolEqualityComparer.Default);

        foreach (var (key, allWrites, calls) in cases.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            body.Append(key).Append('\n');
            var writes = allWrites.Where(w => !shared.Contains(w)).Distinct(SymbolEqualityComparer.Default).Select(w => w!).ToList();
            if (writes.Count == 0)
            {
                body.Append("  command  ").Append(calls.Count == 0 ? "(writes nothing in the store)" : string.Join(", ", calls.Distinct().Order(StringComparer.Ordinal))).Append("\n\n");
                continue;
            }
            body.Append("  writes   ").Append(string.Join(", ", writes.Select(Name).Distinct().Order(StringComparer.Ordinal))).Append('\n');
            var ends = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var w in writes) foreach (var e in await map.EndsAsync(w)) ends.Add(e);
            var save = new SortedSet<string>(StringComparer.Ordinal);
            var fields = new SortedSet<string>(StringComparer.Ordinal);
            bool acts = false;
            foreach (var e in ends)
            {
                if (e.StartsWith("save ", StringComparison.Ordinal)) { save.Add(e[5..].Trim().Split('.')[0]); continue; }
                if (e.StartsWith("app ", StringComparison.Ordinal)) acts = true;
                int arrow = e.IndexOf(" -> ", StringComparison.Ordinal);
                if (e.StartsWith("page ", StringComparison.Ordinal) && arrow > 0) fields.Add(e[(arrow + 4)..].Split(' ')[0]);
                body.Append("  ").Append(e).Append('\n');
            }
            foreach (var f in fields)
            {
                var (uses, panels) = page.Uses(f);
                body.Append("  js       ").Append(page.JsName(f)).Append(": ")
                    .Append(!page.Decoded(f) ? "not decoded by transport.js under this name" : uses.Count > 0 ? string.Join(", ", uses) : "used by no script")
                    .Append(panels.Count > 0 ? "; shown in " + string.Join(", ", panels) : "").Append('\n');
                if (uses.Count > 0) acts = true;
            }
            if (save.Count > 0) body.Append("  save     ").Append(string.Join(", ", save)).Append('\n');
            if (!acts) silent.Add(key);
            body.Append('\n');
        }

        sb.Append("# The settings reader map: Tools/settings-readers (README.md). Generated; do not edit.\n");
        sb.Append("# app = read by AgOpenWeb's own code; page = sent to the web client; save = saved/restored only;\n");
        sb.Append("# report = the bug report dump; routing = read by config.set itself; via = the store's own members it passes through.\n");
        sb.Append("# Store bookkeeping, left out below: " + string.Join(", ", Bookkeeping) + "\n\n");
        sb.Append("# Settings no app or page code reads (" + silent.Count.ToString(CultureInfo.InvariantCulture) + "):\n");
        foreach (var s in silent) sb.Append("#   ").Append(s).Append('\n');
        sb.Append('\n').Append(body);
        string text = sb.ToString();

        if (args.Contains("--write")) { File.WriteAllText(mapPath, text); Console.WriteLine("wrote " + mapPath); }
        else if (args.Contains("--check"))
        {
            string old = File.Exists(mapPath) ? File.ReadAllText(mapPath).ReplaceLineEndings("\n") : "";
            if (old == text)
            {
                if (silent.Count > 0)
                {
                    Console.WriteLine("settings no app or page code reads: " + string.Join(", ", silent));
                    return 1;
                }
                Console.WriteLine("settings-readers.txt is the map of the code; every setting is read");
                return 0;
            }
            var a = old.Split('\n'); var b = text.Split('\n');
            foreach (var l in a.Except(b)) Console.WriteLine("- " + l);
            foreach (var l in b.Except(a)) Console.WriteLine("+ " + l);
            Console.WriteLine("settings-readers.txt is out of date: dotnet run --project Tools/settings-readers -- --write, and look at what changed");
            return 1;
        }
        else Console.Write(text);
        return 0;
    }

    /// <summary>Each case of the switch: its keys, the store members it writes (directly or in a
    /// store method it calls) and, for a command, the other methods it calls.</summary>
    static async Task<List<(string Key, List<ISymbol> Writes, List<string> Calls)>> ReadSwitchAsync(Solution solution)
    {
        foreach (var project in solution.Projects.Where(p => p.Name == "AgOpenWeb.RemoteWiring"))
        foreach (var doc in project.Documents)
        {
            var root = await doc.GetSyntaxRootAsync();
            var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == Switch);
            if (method == null) continue;
            var model = (await doc.GetSemanticModelAsync())!;
            var sw = method.DescendantNodes().OfType<SwitchStatementSyntax>().First();
            var list = new List<(string, List<ISymbol>, List<string>)>();
            foreach (var section in sw.Sections)
            {
                var writes = new List<ISymbol>();
                var calls = new List<string>();
                var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
                CollectWrites(section, model, solution, writes, calls, seen, depth: 0);
                foreach (var label in section.Labels.OfType<CaseSwitchLabelSyntax>())
                    if (label.Value is LiteralExpressionSyntax lit && lit.Token.Value is string key)
                        list.Add((key, writes, calls));
            }
            return list;
        }
        throw new InvalidOperationException(Switch + " not found in AgOpenWeb.RemoteWiring");
    }

    static void CollectWrites(SyntaxNode node, SemanticModel model, Solution solution, List<ISymbol> writes, List<string> calls, HashSet<IMethodSymbol> seen, int depth)
    {
        foreach (var n in node.DescendantNodes())
        {
            if (n is AssignmentExpressionSyntax a)
            {
                var target = a.Left;
                while (target is ElementAccessExpressionSyntax ea) target = ea.Expression;
                if (model.GetSymbolInfo(target).Symbol is { } s && (s is IPropertySymbol || s is IFieldSymbol) && InStore(s.ContainingType))
                    writes.Add(s);
            }
            else if (n is InvocationExpressionSyntax inv && model.GetSymbolInfo(inv).Symbol is IMethodSymbol m)
            {
                if (m.MethodKind == MethodKind.LocalFunction) continue;
                if (InStore(m.ContainingType))
                {
                    // A store method (ApplySectionCount, SetSectionWidth): what it writes, one
                    // level of store calls deeper at a time.
                    var def = m.OriginalDefinition;
                    if (depth < 3 && seen.Add(def))
                        foreach (var r in def.DeclaringSyntaxReferences)
                        {
                            var tree = r.SyntaxTree;
                            var doc = solution.GetDocument(tree);
                            var mm = doc?.GetSemanticModelAsync().Result;
                            if (mm != null) CollectWrites(r.GetSyntax(), mm, solution, writes, calls, seen, depth + 1);
                        }
                }
                else if (depth == 0) calls.Add(m.ContainingType.Name + "." + m.Name);
            }
        }
    }

    static bool InStore(INamedTypeSymbol? t) =>
        t != null && (t.ContainingNamespace?.ToDisplayString() ?? "").StartsWith(StoreNamespace, StringComparison.Ordinal);

    internal static string Name(ISymbol s) => s.ContainingType.Name + "." + s.Name;

    internal static string Id(ISymbol s) => s.OriginalDefinition.GetDocumentationCommentId() ?? s.ToDisplayString();

    static string FindRepo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "AgOpenWeb.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("AgOpenWeb.sln not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The web client's side of a field the host sends: its name as transport.js decodes
    /// it, and the scripts that use it. The scripts are untyped, so this one step goes by name
    /// (`.name`), not by the compiler.</summary>
    sealed class Page(string wwwroot)
    {
        // These show a setting's own value in the settings panels (index.html: the controls'
        // data-key attributes); that is not the setting acting.
        static readonly string[] Panels = { "settings-panels.js", "config-panels.js", "cfg-controls.js", "index.html" };
        private readonly List<(string File, string Text)> _scripts = Directory.GetFiles(Path.Combine(wwwroot, "js"), "*.js")
            .Append(Path.Combine(wwwroot, "index.html"))
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f))).OrderBy(s => s.Item1, StringComparer.Ordinal).ToList();
        private readonly string _transport = File.ReadAllText(Path.Combine(wwwroot, "transport.js"));

        public string JsName(string field) => char.ToLowerInvariant(field[0]) + field[1..];

        public bool Decoded(string field) =>
            System.Text.RegularExpressions.Regex.IsMatch(_transport, @"\b" + JsName(field) + @"\s*:");

        public (List<string> Uses, List<string> Panels) Uses(string field)
        {
            string js = JsName(field);
            var uses = new List<string>(); var panels = new List<string>();
            if (!Decoded(field)) return (uses, panels);
            var dot = new System.Text.RegularExpressions.Regex(@"\." + js + @"\b");
            foreach (var (file, text) in _scripts)
                if (dot.IsMatch(text)) (Panels.Contains(file) ? panels : uses).Add(file);
            return (uses, panels);
        }
    }

    /// <summary>Follows a store member to the code outside the store that reads it.</summary>
    sealed class Mapper(Solution solution)
    {
        private readonly Dictionary<ISymbol, List<(ISymbol Member, bool Read, string? Into)>> _refs = new(SymbolEqualityComparer.Default);

        public async Task<SortedSet<string>> EndsAsync(ISymbol start)
        {
            var ends = new SortedSet<string>(StringComparer.Ordinal);
            // By documentation ID: the same member seen from two projects is two symbols to Roslyn.
            var seen = new HashSet<string>(StringComparer.Ordinal) { Id(start) };
            var queue = new Queue<(ISymbol Symbol, string Via)>();
            queue.Enqueue((start, ""));
            // The value also lives in the fields the setter writes (SetProperty(ref _x, value)), and
            // a member may read the field, not the property (CoverageMarginMeters => _coverageMargin / 100).
            foreach (var f in await BackingFieldsAsync(start))
                if (seen.Add(Id(f))) queue.Enqueue((f, Name(f)));
            while (queue.Count > 0)
            {
                var (sym, via) = queue.Dequeue();
                foreach (var (member, read, into) in await ReferencesAsync(sym))
                {
                    if (!read) continue;
                    // A field or property initializer's enclosing symbol is the type itself.
                    var type = member as INamedTypeSymbol ?? member.ContainingType;
                    if (type == null) continue;
                    if (InStore(type))
                    {
                        // Inside the store: whoever uses this member (its property, the method).
                        if (member is INamedTypeSymbol) continue;
                        var next = member is IMethodSymbol { AssociatedSymbol: IPropertySymbol p } ? p : member;
                        if (seen.Add(Id(next))) queue.Enqueue((next, via.Length == 0 ? Name(next) : via + " > " + Name(next)));
                        continue;
                    }
                    string kind = member.Name == Switch || (type.Name == "RemoteServerWiring" && member.Name == "ConfigSet") ? "routing"
                        : SaveTypes.Contains(type.Name) || type.Name.EndsWith("Dto", StringComparison.Ordinal) ? "save"
                        : PageTypes.Contains(type.Name) ? "page"
                        : ReportTypes.Contains(type.Name) ? "report"
                        : "app";
                    ends.Add((kind + "      ")[..9] + type.Name + (member is INamedTypeSymbol ? " (initializer)" : "." + MemberName(member))
                        + (kind == "page" && into != null ? " -> " + into : "")
                        + (via.Length > 0 && kind != "save" ? "  (via " + via + ")" : ""));
                }
            }
            return ends;
        }

        private async Task<List<IFieldSymbol>> BackingFieldsAsync(ISymbol symbol)
        {
            var fields = new List<IFieldSymbol>();
            if (symbol is not IPropertySymbol { SetMethod: { } setter }) return fields;
            foreach (var r in setter.DeclaringSyntaxReferences)
            {
                var doc = solution.GetDocument(r.SyntaxTree);
                var model = doc == null ? null : await doc.GetSemanticModelAsync();
                if (model == null) continue;
                foreach (var id in (await r.GetSyntaxAsync()).DescendantNodes().OfType<IdentifierNameSyntax>())
                    if (model.GetSymbolInfo(id).Symbol is IFieldSymbol f && SymbolEqualityComparer.Default.Equals(f.ContainingType, symbol.ContainingType)
                        && !IsRead(id, model))
                        fields.Add(f);
            }
            return fields;
        }

        private static string MemberName(ISymbol m) => m switch
        {
            IMethodSymbol { AssociatedSymbol: { } p } => p.Name,
            IMethodSymbol { MethodKind: MethodKind.Constructor } => "ctor",
            _ => m.Name,
        };

        /// <summary>Every place that uses <paramref name="symbol"/>: the member it is in (a lambda's
        /// or local function's own member) and whether it reads it (a getter call, a method call)
        /// or only writes it.</summary>
        private async Task<List<(ISymbol, bool, string?)>> ReferencesAsync(ISymbol symbol)
        {
            if (_refs.TryGetValue(symbol, out var cached)) return cached;
            var list = new List<(ISymbol, bool, string?)>();
            // In file and position order: SymbolFinder's order varies from run to run, and the
            // first path to reach a reader is the one the map shows.
            var locations = (await SymbolFinder.FindReferencesAsync(symbol, solution))
                .SelectMany(f => f.Locations)
                .OrderBy(l => l.Document.FilePath, StringComparer.Ordinal).ThenBy(l => l.Location.SourceSpan.Start);
            foreach (var loc in locations)
            {
                if (loc.IsImplicit) continue;
                var model = await loc.Document.GetSemanticModelAsync();
                var root = await loc.Document.GetSyntaxRootAsync();
                if (model == null || root == null) continue;
                var node = root.FindNode(loc.Location.SourceSpan, getInnermostNodeForTie: true);
                var member = model.GetEnclosingSymbol(loc.Location.SourceSpan.Start);
                while (member is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction } || member is ILocalSymbol)
                    member = member.ContainingSymbol;
                if (member == null) continue;
                bool read = symbol is IMethodSymbol || IsRead(node, model);
                if (symbol is not IMethodSymbol && IsNameOf(node)) continue;
                list.Add((member, read, Into(node, model)));
            }
            _refs[symbol] = list;
            return list;
        }

        /// <summary>The field a read value is put in: the parameter it is passed as (a positional DTO's
        /// constructor), or the property an initializer sets with it; through a conversion or a
        /// conditional (`x ? 1 : 0`).</summary>
        private static string? Into(SyntaxNode node, SemanticModel model)
        {
            SyntaxNode expr = node.Parent is MemberAccessExpressionSyntax ma && ma.Name == node ? ma : node;
            var op = model.GetOperation(expr);
            for (var cur = op; cur != null; cur = cur.Parent)
            {
                switch (cur.Parent)
                {
                    case IArgumentOperation arg:
                        return arg.Parameter?.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ? arg.Parameter.Name : null;
                    case ISimpleAssignmentOperation s when s.Value == cur && s.Target is IPropertyReferenceOperation p: return p.Property.Name;
                    case IConversionOperation or IConditionalOperation or IParenthesizedOperation or IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals }:
                        continue;
                    default: return null;
                }
            }
            return null;
        }

        private static bool IsNameOf(SyntaxNode node) =>
            node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault() is { Expression: IdentifierNameSyntax { Identifier.Text: "nameof" } };

        /// <summary>A getter call: not the target of an assignment, an increment, or a ref/out argument
        /// (SetProperty(ref _field, value) writes).</summary>
        private static bool IsRead(SyntaxNode node, SemanticModel model)
        {
            SyntaxNode expr = node;
            if (expr.Parent is MemberAccessExpressionSyntax ma && ma.Name == expr) expr = ma;
            else if (expr.Parent is MemberBindingExpressionSyntax mb) expr = mb.Parent is ConditionalAccessExpressionSyntax ? mb : mb;
            var op = model.GetOperation(expr);
            if (op == null) return true;
            var cur = op;
            while (cur.Parent is IArrayElementReferenceOperation or IPropertyReferenceOperation { Property.IsIndexer: true } && ((cur.Parent is IArrayElementReferenceOperation ae && ae.ArrayReference == cur) || (cur.Parent is IPropertyReferenceOperation pr && pr.Instance == cur)))
                cur = cur.Parent!;
            return cur.Parent switch
            {
                ISimpleAssignmentOperation s when s.Target == cur => false,
                ICompoundAssignmentOperation c when c.Target == cur => false,
                IIncrementOrDecrementOperation => false,
                IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out } => false,
                _ => true,
            };
        }
    }
}
