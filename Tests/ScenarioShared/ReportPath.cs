// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

// Linked into every scenario harness (<Compile Include="..\ScenarioShared\ReportPath.cs" />).

using System;
using System.IO;
using NUnit.Framework;

/// <summary>
/// Where a harness writes its report. The variable (AGOPENWEB_SCENARIO_REPORT, the working day's
/// AGOPENWEB_DAY_REPORT) names the report's file, or a folder (ending in a slash, or one that
/// exists) that the report goes into under its own name; unset, the report goes to the test's
/// work folder under its own name. A folder keeps the reports of a project with several fixtures
/// apart: the field job harness has six, and with one file named for all of them the last fixture
/// run overwrote the others, so a Runner comparison read a report without the jobs it was run for
/// (2026-11-13).
/// </summary>
public static class ReportPath
{
    public static string For(string reportName, string variable = "AGOPENWEB_SCENARIO_REPORT")
    {
        string? v = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(v))
            return Path.Combine(TestContext.CurrentContext.WorkDirectory, reportName);
        if (v.EndsWith('/') || v.EndsWith('\\') || Directory.Exists(v))
        {
            Directory.CreateDirectory(v);
            return Path.Combine(v, reportName);
        }
        return v;
    }
}
