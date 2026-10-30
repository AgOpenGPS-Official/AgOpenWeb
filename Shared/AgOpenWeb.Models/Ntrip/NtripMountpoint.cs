// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Models.Ntrip;

/// <summary>One mount point of a caster's source table.</summary>
/// <param name="Name">The place or name the caster gives it.</param>
/// <param name="Systems">The satellite systems, for example "GPS+GLO+GAL".</param>
/// <param name="DistanceKm">From the receiver's position; NaN when either position is unknown.</param>
/// <param name="NeedsPosition">The caster wants the receiver's position (nearest base, virtual station).</param>
public sealed record NtripMountpoint(string Mount, string Name, string Format, string Systems, string Country,
    double DistanceKm, bool NeedsPosition);

/// <summary>
/// The mount points last asked for in the profile editor, nearest first. <paramref name="Seq"/> counts the
/// requests so a client can tell a new list from the old one; <paramref name="For"/> says which service the
/// list is for ("main" or "fallback"); <paramref name="Status"/> is a code: "loading", "partial" (only part of
/// the list arrived), "error:" and the reason it failed, or empty when the list was read.
/// </summary>
public sealed record NtripMountpointList(int Seq, string For, string Status, IReadOnlyList<NtripMountpoint> Items)
{
    public static readonly NtripMountpointList None = new(0, "", "", []);
}
