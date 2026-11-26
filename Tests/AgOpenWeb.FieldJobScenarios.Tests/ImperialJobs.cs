// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>
/// The units pass: the careful job (J1) once in metric and once by an operator who works in feet
/// and inches, and once by one who types centimetres into the inch boxes. AgOpenWeb keeps and
/// sends everything in metric; imperial lives only in the web client, which converts what is
/// typed before it goes to the host. So the imperial operator types every setting that has a unit
/// into the web client's own box (the real page, in headless Chromium, its units switched to
/// imperial in App Settings), and the headland in the field builder's box; settings without a
/// unit (types, counts, switches) go as config.set, as the page's buttons send them.
/// </summary>
public static class ImperialJobs
{
    public static IEnumerable<JobSpec> Jobs()
    {
        var reference = new MetricReference();
        yield return new JobSpec("U0", "Olival, 8 m sprayer, metric (the reference)",
            "J1 as the field jobs run it: every setting in metres and centimetres. What it measures is the reference for U1.",
            "Olival", "AB 159 deg", Machines.Puma, Machines.Sprayer8, CarefulOperator.Puma)
        { ExtraJudge = reference.Capture };
        var careful = new ImperialOperator(reference, careless: false);
        yield return new JobSpec("U1", "Olival, 8 m sprayer, typed in feet and inches",
            "The same job by an operator who works in imperial: App Settings > Imperial, then every length typed in the web client's own boxes in feet (two decimals) and the section widths in inches (one decimal), as the boxes show them; the headland 52.49 ft in the field builder. The job must come out as the metric one.",
            "Olival", "AB 159 deg", Machines.Puma, Machines.Sprayer8, CarefulOperator.Puma)
        { TypeSettings = careful.TypeSettingsAsync, MakeHeadland = careful.MakeHeadlandAsync, BeforeStart = careful.CloseAsync, ExtraJudge = careful.Judge };
        var careless = new ImperialOperator(reference, careless: true);
        yield return new JobSpec("U2", "Olival, 8 m sprayer, centimetres typed into the inch boxes",
            "The screen in imperial, everything typed right except the section widths: the operator thinks in centimetres and types 100 in each 'Section widths (in)' box. What does AgOpenWeb keep, what does the machine module get, and does anything on the screen question an 8 m boom becoming 20 m?",
            "Olival", "AB 159 deg", Machines.Puma, Machines.Sprayer8, CarefulOperator.Puma)
        {
            TypeSettings = careless.TypeSettingsAsync, MakeHeadland = careless.MakeHeadlandAsync, BeforeStart = careless.CloseAsync, ExtraJudge = careless.Judge,
            // An 8 m boom AgOpenWeb takes for 20.32 m: the passes 20 m apart, the map painting 20 m.
            Expected = new[] { "MISSED-STRIPS", "FIELD-LEFT-UNDONE", "MAP-SHOWS-UNSPRAYED", "APP-AREA-OFF", "SPRAYED-NOT-ON-MAP", "TOOL-POSITION-OFF", "SPRAYED-IN-HEADLAND", "DOUBLE-SPRAYED", "VALVES-NOT-FOLLOWING" },
        };
    }

    /// <summary>U0's outcome, for U1 to compare with.</summary>
    public sealed class MetricReference
    {
        public bool Have;
        public double Unsprayed, HeadlandHa, Spacing;
        public int Turns;
        public ushort[] ModuleCm = Array.Empty<ushort>();

        public void Capture(Scenario sc, JobRun r, Action<Severity, string, string> flag)
        {
            Have = true;
            Unsprayed = r.UnsprayedShare();
            Turns = r.Turns.Count;
            HeadlandHa = r.Work == null ? 0 : Ring.Area(r.Work) / 1e4;
            Spacing = r.Spacing;
            ModuleCm = r.Module.Flash.SectionCm.Take(r.Module.Flash.ToolSections).ToArray();
            sc.Row.Metrics.Add(("Reference for U1: left unsprayed / U-turns / pass spacing / headland area / module section widths",
                JobRun.F($"{Unsprayed * 100:0.0} % / {Turns} / {Spacing:0.000} m / {HeadlandHa:0.000} ha / {string.Join(" ", ModuleCm)} cm")));
        }
    }

    /// <summary>One value the operator typed into a box with a unit.</summary>
    private sealed record Entry(string Key, string Box, string Unit, double Metric, string Typed)
    {
        public double? Stored;     // what AgOpenWeb then holds (metric), from its config frame
        public string Shows = "";  // what the box shows when it is filled again from that
    }

    /// <summary>
    /// The operator at the web client in imperial (or, careless, with centimetres typed into the
    /// inch boxes). One per job: the browser stays open from the settings to the headland, and is
    /// closed before the job starts (the run itself is driven as every field job is).
    /// </summary>
    public sealed class ImperialOperator : IAsyncDisposable
    {
        private const double FtPerM = 3.280839895, CmPerIn = 2.54;
        private readonly MetricReference _reference;
        private readonly bool _careless;
        private readonly List<Entry> _entries = new();
        private readonly List<string> _pageErrors = new();
        /// <summary>The settings sent as config.set because the page has no box with a unit for them.</summary>
        private readonly List<string> _rawKeys = new();

        /// <summary>A setting whose name says it is a length (a heading or angle offset is not).</summary>
        private static bool LooksLikeLength(string key) =>
            System.Text.RegularExpressions.Regex.IsMatch(key, "(length|width|height|distance|radius|extension|overlap|spacing|wheelbase|hitch|antennaPivot|antennaOffset|tool\\.offset)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            && !System.Text.RegularExpressions.Regex.IsMatch(key, "(heading|angle|deg)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        private IPlaywright? _pw;
        private IBrowser? _browser;
        private IPage? _page;
        private string _headlandOffered = "", _headlandTyped = "";
        private double _headlandSentM = double.NaN, _headlandOfferedM = double.NaN;
        private bool _imperialOn;

        public ImperialOperator(MetricReference reference, bool careless) { _reference = reference; _careless = careless; }

        private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

        /// <summary>What the operator types for a metric value in a box of this unit: the box's own
        /// precision (feet to two decimals, inches to one), as it shows values.</summary>
        private static string Imperial(double metric, string unit) => unit switch
        {
            "m" => (metric * FtPerM).ToString("0.00", CultureInfo.InvariantCulture),
            "cm" => (metric / CmPerIn).ToString("0.0", CultureInfo.InvariantCulture),
            "kmh" => (metric * 0.621371192).ToString("0.0", CultureInfo.InvariantCulture),
            _ => metric.ToString(CultureInfo.InvariantCulture),
        };

        private static string UnitName(string unit) => unit switch { "m" => "ft", "cm" => "in", "kmh" => "mph", _ => unit };

        /// <summary>How far the stored metric value may be from the metric one for the precision the
        /// operator typed it with (half the last digit, plus the client's own rounding to 1 mm).</summary>
        private static double Tolerance(string unit) => unit switch
        {
            "m" => 0.005 / FtPerM + 0.0011,
            "cm" => 0.05 * CmPerIn + 0.011,
            "kmh" => 0.05 / 0.621371192 + 0.011,
            _ => 1e-6,
        };

        public async Task<bool> TypeSettingsAsync(JobRun r, IReadOnlyList<string> settings)
        {
            r.Owned.Add(this);
            try
            {
                _pw = await Playwright.CreateAsync();
                var opts = new BrowserTypeLaunchOptions { Headless = true };
                var exe = Environment.GetEnvironmentVariable("AGOPENWEB_CHROMIUM");
                if (!string.IsNullOrEmpty(exe)) opts.ExecutablePath = exe;
                _browser = await _pw.Chromium.LaunchAsync(opts);
                _page = await _browser.NewPageAsync(new BrowserNewPageOptions { ViewportSize = new ViewportSize { Width = 1280, Height = 800 }, Locale = "en-US" });
                _page.PageError += (_, e) => { lock (_pageErrors) _pageErrors.Add("page error: " + e); };
                _page.Console += (_, m) => { if (m.Type == "error" && !m.Text.StartsWith("Failed to load resource", StringComparison.Ordinal)) lock (_pageErrors) _pageErrors.Add("console: " + m.Text); };
                await _page.GotoAsync(F($"http://127.0.0.1:{r.Rig.Server.Port}/"), new PageGotoOptions { Timeout = 30000 });
                await _page.WaitForFunctionAsync("() => typeof iHoldControl !== 'undefined' && iHoldControl === true && typeof config !== 'undefined' && !!config && typeof statusBar !== 'undefined' && !!statusBar",
                    null, new PageWaitForFunctionOptions { Timeout = 30000, PollingInterval = 100 });
            }
            catch (Exception ex)
            {
                r.Harness("the web client did not open in headless Chromium (Playwright; AGOPENWEB_CHROMIUM names a browser): " + ex.Message.Split('\n')[0]);
                return false;
            }
            r.S.Step("web client open in headless Chromium, holding control");

            // App Settings > Imperial (the segment button sends units:imperial; the status frame turns the page).
            await TapAsync("#appsettings .ln-segbtn[data-units=\"imperial\"]");
            try
            {
                await _page.WaitForFunctionAsync("() => typeof isMetric === 'function' && !isMetric()", null, new PageWaitForFunctionOptions { Timeout = 10000, PollingInterval = 100 });
                _imperialOn = true;
                r.S.Step("App Settings > Imperial: the page shows feet and inches");
            }
            catch (TimeoutException)
            {
                r.Harness("the page did not turn imperial after App Settings > Imperial");
                return false;
            }

            var sectionWidths = new List<(int Idx, double Cm)>();
            foreach (var kv in settings)
            {
                int colon = kv.IndexOf(':');
                string key = kv[..colon], val = kv[(colon + 1)..];
                if (key == "tool.sectionWidth")
                {
                    var p = val.Split(',');
                    sectionWidths.Add((int.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture)));
                    continue;
                }
                string? unit = await _page.EvaluateAsync<string?>(@"key => { const i = document.querySelector('.cfg-num[data-key=""' + key + '""][data-unit]'); return i ? i.dataset.unit : null; }", key);
                if (unit == null || !double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double metric))
                {
                    // No unit: the page's buttons and lists send these as they are. A length sent
                    // this way would skip the imperial path the job is here to test.
                    _rawKeys.Add(key);
                    if (LooksLikeLength(key))
                    {
                        r.Harness(F($"{key} ({val}) looks like a length but has no box with a unit on the page: it would go in metric, not typed in imperial"));
                        return false;
                    }
                    await _page.EvaluateAsync("c => transport.send(c)", "config.set|" + kv);
                    await Task.Delay(30);
                    continue;
                }
                string typed = Imperial(metric, unit);
                await TypeAsync(F($".cfg-num[data-key=\"{key}\"][data-unit]"), typed);
                _entries.Add(new Entry(key, key, unit, metric, typed));
            }

            // Section widths: Vehicle > Tool, one box per section, built for the section count the host echoed.
            if (sectionWidths.Count > 0)
            {
                int n = sectionWidths.Count;
                try
                {
                    await _page.WaitForFunctionAsync("n => config && config.tool && config.tool.numSections === n", n, new PageWaitForFunctionOptions { Timeout = 10000, PollingInterval = 100 });
                }
                catch (TimeoutException) { r.Harness(F($"the page never showed {n} sections")); return false; }
                await _page.EvaluateAsync("() => lnOpen('toolcfg', 'ln-vehicle', () => populateToolCfg(true))");
                foreach (var (idx, cm) in sectionWidths)
                {
                    string typed = _careless ? cm.ToString("0.###", CultureInfo.InvariantCulture) : Imperial(cm, "cm");
                    string sel = F($"#tc-sectionwidths input[data-idx=\"{idx}\"]");
                    if (!await TypeAsync(sel, typed)) { r.Harness(F($"no box for section {idx + 1} in Vehicle > Tool")); return false; }
                    _entries.Add(new Entry(F($"tool.sectionWidth[{idx}]"), sel, "cm", cm, typed));
                }
            }

            await r.Rig.DrainAsync();
            await WaitConfigSettledAsync();   // the config frames that echo the last values
            await ReadBackAsync();
            r.S.Step(F($"typed {_entries.Count} values with a unit in their boxes ({string.Join(", ", _entries.Take(4).Select(e => F($"{e.Key} {e.Typed} {UnitName(e.Unit)}")))}, ...)"));
            return true;
        }

        /// <summary>Type into a box as a person does: focus, the text, then leave it (its change handler sends it).</summary>
        private async Task<bool> TypeAsync(string selector, string text)
        {
            bool ok = await _page!.EvaluateAsync<bool>(@"([sel, text]) => {
                const inp = document.querySelector(sel); if (!inp) return false;
                inp.focus(); inp.value = text;
                inp.dispatchEvent(new Event('input', { bubbles: true }));
                inp.dispatchEvent(new Event('change', { bubbles: true }));
                inp.blur(); return true; }", new object[] { selector, text });
            await Task.Delay(40);
            return ok;
        }

        /// <summary>A finger's tap on a button: press and release. The page drops a press from a
        /// pointer it never saw released (its ghost-press filter, js/map-input.js), so a press
        /// alone works only every second time.</summary>
        private async Task<bool> TapAsync(string selector)
        {
            bool ok = await _page!.EvaluateAsync<bool>(@"(sel) => {
                const b = document.querySelector(sel); if (!b) return false;
                const o = { bubbles: true, cancelable: true, pointerId: 7, pointerType: 'touch', isPrimary: true };
                b.dispatchEvent(new PointerEvent('pointerdown', o));
                b.dispatchEvent(new PointerEvent('pointerup', o));
                return true; }", selector);
            await Task.Delay(40);
            return ok;
        }

        /// <summary>Wait until the page's copy of AgOpenWeb's settings has not changed for a second (at most 10 s).</summary>
        private async Task WaitConfigSettledAsync()
        {
            string last = "";
            var still = System.Diagnostics.Stopwatch.StartNew();
            var all = System.Diagnostics.Stopwatch.StartNew();
            while (all.ElapsedMilliseconds < 10000)
            {
                string now = await _page!.EvaluateAsync<string>("() => JSON.stringify(config)");
                if (now != last) { last = now; still.Restart(); }
                else if (still.ElapsedMilliseconds >= 1000) return;
                await Task.Delay(200);
            }
        }

        /// <summary>What AgOpenWeb holds for each value (its config frame) and what the box then shows.</summary>
        private async Task ReadBackAsync()
        {
            await _page!.EvaluateAsync("() => { if (typeof populateToolCfg === 'function') populateToolCfg(true); }");
            foreach (var e in _entries)
            {
                var res = await _page.EvaluateAsync<JsonElement>(@"([key, box]) => {
                    const m = /^tool\.sectionWidth\[(\d+)\]$/.exec(key);
                    const inp = document.querySelector(m ? box : box.startsWith('#') ? box : '.cfg-num[data-key=""' + box + '""][data-unit]');
                    const v = m ? (config.tool.sectionWidths || [])[+m[1]] : cfgGet(key);
                    if (typeof v !== 'number' || !inp) return null;
                    writeUnitInput(inp, v);
                    return { v, shows: String(inp.value) }; }", new object[] { e.Key, e.Box });
                if (res.ValueKind != JsonValueKind.Object) continue;
                e.Stored = res.GetProperty("v").GetDouble();
                e.Shows = res.GetProperty("shows").GetString() ?? "";
            }
        }

        public async Task<bool> MakeHeadlandAsync(JobRun r)
        {
            if (_page == null) return false;
            string want = Imperial(r.Spec.HeadlandM, "m");
            // Field builder > Headland > Add: the box offers two tool widths; the operator types the
            // headland in feet and taps Whole Boundary.
            await TapAsync("#fb-hl-add");
            var offer = await _page.EvaluateAsync<JsonElement>(@"() => { const b = document.getElementById('fb-hl-newoff');
                return { text: b.value, m: b.value === '' ? null : readUnitInput(b) }; }");
            _headlandOffered = offer.GetProperty("text").GetString() ?? "";
            if (offer.GetProperty("m").ValueKind == JsonValueKind.Number) _headlandOfferedM = offer.GetProperty("m").GetDouble();
            await TypeAsync("#fb-hl-newoff", want);
            _headlandTyped = want;
            _headlandSentM = await _page.EvaluateAsync<double>("() => readUnitInput(document.getElementById('fb-hl-newoff'))");
            await TapAsync("#fb-addhl .fb-addbtn[data-fbhl=\"whole\"]");
            await r.Rig.DrainAsync();
            r.S.Step(F($"Field builder > Headland > Add: the box offered '{_headlandOffered}' ft (two tool widths); typed {want} ft, Whole Boundary"));
            return true;
        }

        /// <summary>The operator is done at the screen: the browser closes before the job starts.</summary>
        public async Task CloseAsync(JobRun r)
        {
            if (_page != null)
            {
                await ReadBackAsync();
                lock (_pageErrors) foreach (var e in _pageErrors.Distinct().Take(5)) r.S.Note("web client: " + e);
            }
            await DisposeAsync();
            await r.Rig.DrainAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try { if (_browser != null) await _browser.CloseAsync(); } catch { }
            _browser = null; _page = null;
            _pw?.Dispose(); _pw = null;
        }

        public void Judge(Scenario sc, JobRun r, Action<Severity, string, string> flag)
        {
            if (!_imperialOn) return;
            // The table: each value as typed, as AgOpenWeb keeps it, as the box shows it again.
            foreach (var e in _entries)
                sc.Row.Metrics.Add((F($"{e.Key}: wanted / typed / kept / the box then shows"),
                    F($"{e.Metric:0.###} {(e.Unit == "cm" ? "cm" : e.Unit == "kmh" ? "km/h" : "m")} / {e.Typed} {UnitName(e.Unit)} / {(e.Stored is double s ? s.ToString("0.####", CultureInfo.InvariantCulture) : "nothing")} / {e.Shows}")));

            sc.Row.Metrics.Add(("Sent as config.set, no box with a unit on the page (types, counts, switches, angles)",
                _rawKeys.Count == 0 ? "none" : string.Join(", ", _rawKeys.Distinct())));
            var lost = _entries.Where(e => e.Stored == null).ToList();
            if (lost.Count > 0)
                flag(Severity.Bug, "IMPERIAL-VALUE-LOST", F($"{lost.Count} value(s) typed in imperial never reached AgOpenWeb: {string.Join(", ", lost.Take(5).Select(e => F($"{e.Key} ({e.Typed} {UnitName(e.Unit)})")))}."));

            if (!_careless)
            {
                var off = _entries.Where(e => e.Stored is double s && Math.Abs(s - e.Metric) > Tolerance(e.Unit)).ToList();
                if (off.Count > 0)
                    flag(Severity.Bug, "IMPERIAL-VALUE-OFF", F($"{off.Count} of {_entries.Count} values typed in feet and inches were kept as another length than the metric one: {string.Join("; ", off.Take(5).Select(e => F($"{e.Key} {e.Typed} {UnitName(e.Unit)} kept as {e.Stored:0.####}, wanted {e.Metric:0.###}")))}."));
                else sc.Ok(F($"all {_entries.Count} values typed in feet and inches were kept as the metric ones, within what was typed (worst length {_entries.Where(e => e.Stored != null && e.Unit is "m" or "cm").Select(e => Math.Abs(e.Stored!.Value - e.Metric) / (e.Unit == "cm" ? 100 : 1)).DefaultIfEmpty(0).Max() * 1000:0.0} mm{(_entries.Any(e => e.Stored != null && e.Unit == "kmh") ? F($", worst speed {_entries.Where(e => e.Stored != null && e.Unit == "kmh").Max(e => Math.Abs(e.Stored!.Value - e.Metric)):0.00} km/h") : "")})"));
                var drift = _entries.Where(e => e.Stored != null && !SameNumber(e.Shows, e.Typed)).ToList();
                if (drift.Count > 0)
                    flag(Severity.Bug, "IMPERIAL-BOX-CHANGES", F($"{drift.Count} box(es) show another number than was typed once AgOpenWeb echoes it: {string.Join("; ", drift.Take(5).Select(e => F($"{e.Key} typed {e.Typed}, shows {e.Shows}")))}."));
                else sc.Ok("every box shows what was typed once AgOpenWeb has echoed it");
            }

            // The field builder's offer: two tool widths, in feet. The width is the one AgOpenWeb keeps:
            // a 1 m section typed as 39.4 in (the box's step) is 100.076 cm, so the boom is 8.006 m.
            if (!_careless)
            {
                var sectionsKept = _entries.Where(e => e.Key.StartsWith("tool.sectionWidth[", StringComparison.Ordinal) && e.Stored != null).ToList();
                double twoWidths = 2 * (sectionsKept.Count == r.Spec.T.Sections ? sectionsKept.Sum(e => e.Stored!.Value) / 100.0 : r.Spec.T.Width);
                if (double.IsNaN(_headlandOfferedM))
                    flag(Severity.Bug, "IMPERIAL-VALUE-OFF", F($"Field builder > Headland > Add offered nothing ('{_headlandOffered}'), wanted two tool widths ({twoWidths * FtPerM:0.00} ft)."));
                else if (Math.Abs(_headlandOfferedM - twoWidths) > Tolerance("m"))
                    flag(Severity.Bug, "IMPERIAL-VALUE-OFF", F($"Field builder > Headland > Add offered {_headlandOffered} ft ({_headlandOfferedM:0.###} m), wanted two tool widths, {twoWidths * FtPerM:0.00} ft ({twoWidths:0.###} m)."));
                else sc.Ok(F($"Field builder > Headland > Add offered {_headlandOffered} ft, two tool widths"));
            }

            // The headland, typed in feet in the field builder.
            if (!double.IsNaN(_headlandSentM))
            {
                double wantM = r.Spec.HeadlandM;
                if (Math.Abs(_headlandSentM - wantM) > Tolerance("m"))
                    flag(Severity.Bug, "IMPERIAL-VALUE-OFF", F($"the headland typed as {_headlandTyped} ft in the field builder went to AgOpenWeb as {_headlandSentM:0.###} m, wanted {wantM:0.##} m."));
                else sc.Ok(F($"the headland typed as {_headlandTyped} ft went to AgOpenWeb as {_headlandSentM:0.###} m"));
            }

            // What the machine module got (PGN 235, whole cm) against what AgOpenWeb keeps.
            var flash = r.Module.Flash;
            var kept = _entries.Where(e => e.Key.StartsWith("tool.sectionWidth[", StringComparison.Ordinal)).Select(e => e.Stored ?? double.NaN).ToList();
            var module = flash.SectionCm.Take(Math.Min((int)flash.ToolSections, 16)).ToList();
            sc.Row.Metrics.Add(("Section widths AgOpenWeb keeps / the module got (PGN 235)", F($"{string.Join(" ", kept.Select(k => k.ToString("0.###", CultureInfo.InvariantCulture)))} cm / {string.Join(" ", module)} cm")));
            if (module.Count != kept.Count || module.Zip(kept).Any(p => Math.Abs(p.First - p.Second) > 1.0))
                flag(Severity.Inconsistency, "MODULE-WIDTHS-DIFFER", F($"the machine module's section widths (PGN 235: {string.Join(" ", module)} cm) are not the ones AgOpenWeb keeps ({string.Join(" ", kept.Select(k => k.ToString("0.#", CultureInfo.InvariantCulture)))} cm)."));

            if (_careless)
            {
                double totalM = kept.Where(k => !double.IsNaN(k)).Sum() / 100.0;
                string words = string.Join(" | ", r.Said.Where(s => s.Contains("width", StringComparison.OrdinalIgnoreCase)).Take(3));
                sc.Row.Metrics.Add(("Typed into each 'Section widths (in)' box / kept / implement width / module got", F($"{_entries.FirstOrDefault(e => e.Key.StartsWith("tool.sectionWidth", StringComparison.Ordinal))?.Typed ?? "?"} in / {(kept.Count > 0 ? kept[0] : double.NaN):0.#} cm / {totalM:0.00} m / {(module.Count > 0 ? module[0] : 0)} cm")));
                if (totalM > r.Spec.T.Width * 1.5)
                    flag(Severity.Improvement, "WRONG-UNIT-ACCEPTED", F($"centimetres typed into the inch boxes (100 for a 1 m section) went in as {(kept.Count > 0 ? kept[0] : 0):0.#} cm a section: the {r.Spec.T.Width:0} m boom became {totalM:0.00} m, the machine module got {(module.Count > 0 ? module[0] : 0)} cm a section, and nothing on the screen questioned a boom {totalM / r.Spec.T.Width:0.0} times as wide as the profile's{(words.Length > 0 ? " (said: " + words + ")" : "")}. Operator error; a check on a width that jumps (or the unit shown beside the number in the box) is an enhancement."));
                return;
            }

            // The same job as the metric one?
            if (!_reference.Have) { sc.Note("no metric run (U0) in this run to compare the job with"); return; }
            double unsprayed = r.UnsprayedShare();
            double headlandHa = r.Work == null ? 0 : Ring.Area(r.Work) / 1e4;
            sc.Row.Metrics.Add(("Against the metric run (U0): left unsprayed / U-turns / pass spacing / headland area",
                F($"{unsprayed * 100:0.0} % vs {_reference.Unsprayed * 100:0.0} % / {r.Turns.Count} vs {_reference.Turns} / {r.Spacing:0.000} vs {_reference.Spacing:0.000} m / {headlandHa:0.000} vs {_reference.HeadlandHa:0.000} ha")));
            var diffs = new List<string>();
            if (Math.Abs(unsprayed - _reference.Unsprayed) > 0.005) diffs.Add(F($"{unsprayed * 100:0.0} % left unsprayed against {_reference.Unsprayed * 100:0.0} %"));
            if (Math.Abs(r.Turns.Count - _reference.Turns) > 1) diffs.Add(F($"{r.Turns.Count} U-turns against {_reference.Turns}"));
            if (Math.Abs(r.Spacing - _reference.Spacing) > 0.01) diffs.Add(F($"passes {r.Spacing:0.000} m apart against {_reference.Spacing:0.000} m"));
            if (Math.Abs(headlandHa - _reference.HeadlandHa) > 0.005) diffs.Add(F($"{headlandHa:0.000} ha inside the headland against {_reference.HeadlandHa:0.000} ha"));
            if (!module.SequenceEqual(_reference.ModuleCm)) diffs.Add(F($"the module got {string.Join(" ", module)} cm against {string.Join(" ", _reference.ModuleCm)} cm"));
            if (diffs.Count > 0)
                flag(Severity.Bug, "IMPERIAL-JOB-DIFFERS", "the job typed in feet and inches did not come out as the metric one: " + string.Join("; ", diffs) + ".");
            else sc.Ok("the job typed in feet and inches came out as the metric one (unsprayed share, U-turns, pass spacing, headland, the module's section widths)");
        }

        private static bool SameNumber(string a, string b) =>
            double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && Math.Abs(x - y) < 1e-9;
    }
}
