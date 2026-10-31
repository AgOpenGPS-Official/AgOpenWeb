// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace AgOpenWeb.Models;

/// <summary>
/// Edits of the web cards' bookmark list (AppSettings.WebCards, Plans/ADDON_WEB_CARDS_PLAN.md).
/// Each returns a new list, so a reader holding the old one is never disturbed, or null when the
/// edit is refused.
/// </summary>
public static class WebCardBookmarks
{
    public const int MaxCount = 20, MaxName = 40, MaxUrl = 500;

    /// <summary>The URL a bookmark may hold: http or https, with <c>{host}</c> allowed as the host.</summary>
    public static bool IsValidUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxUrl) return false;
        var probe = url.Replace("{host}", "aow-host", StringComparison.Ordinal);
        return Uri.TryCreate(probe, UriKind.Absolute, out var u)
            && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            && u.Host.Length > 0;
    }

    /// <summary>Replace the bookmark at <paramref name="index"/>, or add one when it is −1.</summary>
    public static List<WebCardBookmark>? Save(IReadOnlyList<WebCardBookmark> list, int index, string name, string url)
    {
        name = name.Trim(); url = url.Trim();
        if (name.Length == 0 || name.Length > MaxName || !IsValidUrl(url)) return null;
        if (index < -1 || index >= list.Count || (index == -1 && list.Count >= MaxCount)) return null;
        var copy = new List<WebCardBookmark>(list);
        var b = new WebCardBookmark { Name = name, Url = url };
        if (index == -1) copy.Add(b); else copy[index] = b;
        return copy;
    }

    public static List<WebCardBookmark>? Delete(IReadOnlyList<WebCardBookmark> list, int index)
    {
        if (index < 0 || index >= list.Count) return null;
        var copy = new List<WebCardBookmark>(list);
        copy.RemoveAt(index);
        return copy;
    }
}
