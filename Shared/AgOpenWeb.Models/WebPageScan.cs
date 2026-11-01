// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace AgOpenWeb.Models;

/// <summary>A web page found by the web cards' "Search the network": its title (empty when it has
/// none) and its address.</summary>
public sealed record FoundWebPage(string Title, string Url);

/// <summary>
/// The last "Search the network" from the web cards' bookmark editor (Plans/ADD_ON_CARDS_PLAN.md).
/// Status is "" (none yet), "searching" or "done"; Networks the networks searched ("192.168.5.0/24"),
/// for the editor to say where it looked. Not kept: a restart forgets it.
/// </summary>
public sealed record WebPageScan(int Seq, string Status, string Networks, IReadOnlyList<FoundWebPage> Pages)
{
    public static readonly WebPageScan None = new(0, "", "", []);
}
