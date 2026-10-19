// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace AgOpenWeb.Models.Ntrip;

/// <summary>
/// NTRIP profile containing caster settings and field associations.
/// Profiles are stored as JSON files in Documents/AgOpenWeb/NtripProfiles/.
/// </summary>
public class NtripProfile
{
    /// <summary>
    /// Unique identifier for this profile (GUID)
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// User-friendly name for this profile
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// NTRIP caster host address
    /// </summary>
    public string CasterHost { get; set; } = string.Empty;

    /// <summary>
    /// NTRIP caster port (typically 2101)
    /// </summary>
    public int CasterPort { get; set; } = 2101;

    /// <summary>
    /// NTRIP mount point
    /// </summary>
    public string MountPoint { get; set; } = string.Empty;

    /// <summary>
    /// Username for NTRIP authentication (optional)
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Password for NTRIP authentication (optional)
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// List of field directory names associated with this profile.
    /// When a field in this list is loaded, this profile will be used.
    /// </summary>
    public List<string> AssociatedFields { get; set; } = new();

    /// <summary>
    /// If true, automatically connect to NTRIP when an associated field is loaded
    /// </summary>
    public bool AutoConnectOnFieldLoad { get; set; } = true;

    /// <summary>
    /// If true, this is the default profile used for fields without a specific association
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Full path to the profile file (set by service after loading)
    /// </summary>
    public string FilePath { get; set; } = string.Empty;
}
