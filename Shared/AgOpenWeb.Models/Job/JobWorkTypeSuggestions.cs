// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace AgOpenWeb.Models.Job;

/// <summary>
/// Seed suggestions surfaced by the Work Type autocomplete on the
/// New-Job mini-form. <see cref="Job.WorkType"/> is free-text (Decision #1)
/// and operators can type anything; this list just primes the dropdown
/// before any prior jobs exist.
/// </summary>
public static class JobWorkTypeSuggestions
{
    public static readonly IReadOnlyList<string> Seed = new[]
    {
        "fertilizing",
        "spraying",
        "seeding",
        "cultivating",
        "tillage",
        "harvesting"
    };
}
