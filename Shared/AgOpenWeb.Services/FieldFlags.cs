// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AgOpenWeb.Models;

namespace AgOpenWeb.Services;

/// <summary>
/// The open field's marker flags: the list, its numbering and the edits to it. Showing and
/// saving them is the caller's job.
/// </summary>
public sealed class FieldFlags
{
    private int _nextId = 1;

    public ObservableCollection<Flag> Items { get; } = new();

    /// <summary>Add a flag; without a colour, the first one no flag has yet (then in turn).</summary>
    public Flag Add(double easting, double northing, FlagColor? color = null)
    {
        var id = _nextId++;
        var flag = new Flag(easting, northing, color ?? NextAutoColor(), id, $"Flag {id}");
        Items.Add(flag);
        return flag;
    }

    public bool Rename(int index, string name)
    {
        if (!Has(index) || string.IsNullOrWhiteSpace(name)) return false;
        Items[index].Name = name.Trim();
        return true;
    }

    public bool SetColor(int index, FlagColor color)
    {
        if (!Has(index)) return false;
        Items[index].FlagColor = color;
        return true;
    }

    /// <summary>The flag removed, or null when there is none at <paramref name="index"/>.</summary>
    public Flag? RemoveAt(int index)
    {
        if (!Has(index)) return null;
        var flag = Items[index];
        Items.RemoveAt(index);
        return flag;
    }

    /// <summary>Replace the list (a field opened or closed); numbering goes on after its highest.</summary>
    public void Replace(IEnumerable<Flag> flags)
    {
        Items.Clear();
        foreach (var f in flags) Items.Add(f);
        _nextId = Items.Count == 0 ? 1 : Items.Max(f => f.UniqueNumber) + 1;
    }

    private bool Has(int index) => index >= 0 && index < Items.Count;

    private FlagColor NextAutoColor()
    {
        var all = Enum.GetValues<FlagColor>();
        var used = Items.Select(f => f.FlagColor).ToHashSet();
        foreach (var c in all)
            if (!used.Contains(c)) return c;
        return all[Items.Count % all.Length];
    }
}
