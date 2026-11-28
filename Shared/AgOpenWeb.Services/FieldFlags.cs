// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Threading;

namespace AgOpenWeb.Services;

/// <summary>
/// The open field's marker flags: the list, its numbering and the edits to it. Showing and
/// saving them is the caller's job (the view model's, Plans/MAINVIEWMODEL_REFACTOR_PLAN.md item 1).
/// From Richard Klasens' FieldFlags (#953).
///
/// State: <see cref="Items"/> and the next number. Owned by the host loop: written there only
/// (checked in debug builds), read there, and copied for the page by the view model
/// (<c>State.Field.Flags</c>). Lives as long as the backend; a field's opening and closing
/// replace the list (<see cref="Replace"/>).
/// </summary>
public sealed class FieldFlags
{
    private readonly IUiDispatcher _host;
    private int _nextId = 1;

    public FieldFlags(IUiDispatcher host) => _host = host;

    public ObservableCollection<Flag> Items { get; } = new();

    /// <summary>Add a flag; without a colour, the first one no flag has yet (then in turn).</summary>
    public Flag Add(double easting, double northing, FlagColor? color = null)
    {
        OwnerThread.Assert(_host, "Flags");
        var id = _nextId++;
        var flag = new Flag(easting, northing, color ?? NextAutoColor(), id, $"Flag {id}");
        Items.Add(flag);
        return flag;
    }

    public bool Rename(int index, string name)
    {
        OwnerThread.Assert(_host, "Flags");
        if (!Has(index) || string.IsNullOrWhiteSpace(name)) return false;
        Items[index].Name = name.Trim();
        return true;
    }

    public bool SetColor(int index, FlagColor color)
    {
        OwnerThread.Assert(_host, "Flags");
        if (!Has(index)) return false;
        Items[index].FlagColor = color;
        return true;
    }

    /// <summary>The flag removed, or null when there is none at <paramref name="index"/>.</summary>
    public Flag? RemoveAt(int index)
    {
        OwnerThread.Assert(_host, "Flags");
        if (!Has(index)) return null;
        var flag = Items[index];
        Items.Remove(flag);
        return flag;
    }

    /// <summary>Replace the list (a field opened or closed, all deleted); numbering goes on after
    /// its highest, from 1 when it is empty.</summary>
    public void Replace(IEnumerable<Flag> flags)
    {
        OwnerThread.Assert(_host, "Flags");
        Items.Clear();
        foreach (var f in flags) Items.Add(f);
        _nextId = Items.Count == 0 ? 1 : Items.Max(f => f.UniqueNumber) + 1;
    }

    private bool Has(int index) => index >= 0 && index < Items.Count;

    private FlagColor NextAutoColor()
    {
        var allColors = Enum.GetValues<FlagColor>();
        var usedColors = Items.Select(f => f.FlagColor).ToHashSet();
        foreach (var c in allColors)
            if (!usedColors.Contains(c)) return c;
        return allColors[Items.Count % allColors.Length];
    }
}
