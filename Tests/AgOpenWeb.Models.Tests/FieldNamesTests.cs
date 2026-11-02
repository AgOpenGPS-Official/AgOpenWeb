// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;

namespace AgOpenWeb.Models.Tests;

/// <summary>Checklist 7.1-7.4: a field's name is one folder name on every head.</summary>
[TestFixture]
public class FieldNamesTests
{
    [TestCase("North 40", "North 40")]
    [TestCase("  Feld Süd  ", "Feld Süd")]
    [TestCase("Back (2026) #2 & co.", "Back (2026) #2 & co")]   // trailing dot dropped, as Windows would
    [TestCase("Con Hill", "Con Hill")]                          // only the bare device names are Windows'
    public void AcceptedNames(string raw, string expected)
    {
        Assert.That(FieldNames.TryCheck(raw, out var name, out var why), Is.True, why);
        Assert.That(name, Is.EqualTo(expected));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("..")]
    [TestCase("../Vehicles")]            // would land outside the Fields folder
    [TestCase("North/South")]
    [TestCase("North\\South")]
    [TestCase("C:")]
    [TestCase("What?")]
    [TestCase("A|B")]
    [TestCase("tab\there")]
    [TestCase("con")]
    [TestCase("LPT1.old")]
    public void RefusedNames(string raw)
    {
        Assert.That(FieldNames.TryCheck(raw, out _, out var why), Is.False);
        Assert.That(why, Is.Not.Empty, "the operator is told why");
    }
}
