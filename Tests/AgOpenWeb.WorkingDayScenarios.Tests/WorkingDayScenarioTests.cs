// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.WorkingDayScenarios.Tests;

/// <summary>
/// An eight-hour working day in one AgOpenWeb process, on the day's clock. Long (about 45
/// minutes at the default pace), so [Category("Scenario")]. Switches: AGOPENWEB_DAY_PACE (real ms
/// per 0.1 s fix: 8 = 12.5x, 100 = real time), AGOPENWEB_DAY_HOURS (8), AGOPENWEB_DAY_REPORT
/// (path.md), AGOPENWEB_DAY_CSV (the five-minute samples), AGOPENWEB_DAY_CENSUS=0 (no heap census).
/// </summary>
[TestFixture]
[Category("Scenario")]
[NonParallelizable]
public class WorkingDayScenarioTests
{
    [Test]
    [CancelAfter(36_000_000)]
    public async Task Working_day(CancellationToken ct)
    {
        string root = Path.Combine(Path.GetTempPath(), "aow-working-day-" + Guid.NewGuid().ToString("N")[..8]);
        var day = new Day(root);
        Exception? crash = null;
        try { await day.RunAsync().WaitAsync(ct); }
        catch (Exception ex) { crash = ex; day.Harness("the day stopped: " + ex.GetType().Name + ": " + ex.Message); TestContext.Out.WriteLine(ex.ToString()); }
        finally { await day.DisposeAsync(); }
        day.CountCensuses();

        var report = Report.Markdown(day);
        string path = Environment.GetEnvironmentVariable("AGOPENWEB_DAY_REPORT")
            ?? Path.Combine(TestContext.CurrentContext.WorkDirectory, "working-day-scenarios-report.md");
        await File.WriteAllTextAsync(path, report, CancellationToken.None);
        if (Environment.GetEnvironmentVariable("AGOPENWEB_DAY_CSV") is { Length: > 0 } csv)
            await File.WriteAllTextAsync(csv, Report.Csv(day), CancellationToken.None);
        TestContext.Out.WriteLine(report);
        TestContext.Out.WriteLine("Report: " + path);
        if (Environment.GetEnvironmentVariable("AGOPENWEB_DAY_KEEP") != "1")
            try { Directory.Delete(root, true); } catch { }

        Assert.That(day.HarnessProblems, Is.Empty, "the harness itself failed: " + string.Join("; ", day.HarnessProblems));
        Assert.That(crash, Is.Null);
    }
}
