// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Services.Storage;

namespace AgOpenWeb.Services.Profile;

/// <summary>Which kind of profile a <see cref="ProfileFileProbe"/> describes.</summary>
public enum ProfileKind
{
    Vehicle,
    Tool,
}

/// <summary>
/// Corruption status of a single profile file, established by a read-only
/// probe (no store mutation, no quarantine) so the UI can decide whether to
/// prompt before loading.
/// </summary>
public sealed class ProfileFileProbe
{
    public required ProfileKind Kind { get; init; }

    /// <summary>Human-readable label for the prompt, e.g. <c>vehicle 'JohnDeere'</c>.</summary>
    public required string Label { get; init; }

    public required LoadOutcome Outcome { get; init; }

    /// <summary>Last-write time of the recoverable backup, when <see cref="Outcome"/> is <see cref="LoadOutcome.RecoveredFromBackup"/>.</summary>
    public DateTime? BackupTimestamp { get; init; }
}

/// <summary>
/// Aggregate corruption status for a vehicle+tool pair (the files behind one
/// <see cref="Interfaces.IConfigurationService.LoadProfiles"/> call).
/// </summary>
public sealed class ProfileLoadProbe
{
    public IReadOnlyList<ProfileFileProbe> Files { get; init; } = Array.Empty<ProfileFileProbe>();

    /// <summary>At least one file was damaged but recoverable from its backup.</summary>
    public bool AnyRecovered => Files.Any(f => f.Outcome == LoadOutcome.RecoveredFromBackup);

    /// <summary>At least one file was damaged with no usable backup.</summary>
    public bool AnyUnrecoverable => Files.Any(f => f.Outcome == LoadOutcome.CorruptNoBackup);

    /// <summary>Whether the caller should surface a recovery prompt before/after loading.</summary>
    public bool NeedsPrompt => AnyRecovered || AnyUnrecoverable;

    /// <summary>Only the damaged files (the ones worth mentioning in a prompt).</summary>
    public IEnumerable<ProfileFileProbe> Damaged =>
        Files.Where(f => f.Outcome is LoadOutcome.RecoveredFromBackup or LoadOutcome.CorruptNoBackup);
}
