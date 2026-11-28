// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using AgOpenWeb.Models.Headland;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Threading;

namespace AgOpenWeb.Services.Headland;

/// <summary>
/// The open field's headland segments: the lines its headland is built from (Plans/
/// MAINVIEWMODEL_REFACTOR_PLAN.md item 3). Building the headland from them is
/// <see cref="HeadlandSegmentBuilder"/>'s; selecting one, the preview, and showing and saving them
/// are the view model's. An undo of their edits is #978.
///
/// State: <see cref="Segments"/>. Owned by the host loop: written there only (checked in debug
/// builds), read there and by the web projection on the host loop. Lives as long as the backend;
/// a field's opening and closing clear it and load the field's.
/// </summary>
public sealed class FieldHeadland
{
    private readonly IUiDispatcher _host;

    public FieldHeadland(IUiDispatcher host) => _host = host;

    public ObservableCollection<HeadlandSegment> Segments { get; } = new();

    public void Add(HeadlandSegment segment)
    {
        OwnerThread.Assert(_host, "Headland segments");
        Segments.Add(segment);
    }

    public bool Remove(HeadlandSegment segment)
    {
        OwnerThread.Assert(_host, "Headland segments");
        return Segments.Remove(segment);
    }

    public void RemoveAt(int index)
    {
        OwnerThread.Assert(_host, "Headland segments");
        Segments.RemoveAt(index);
    }

    public void Clear()
    {
        OwnerThread.Assert(_host, "Headland segments");
        Segments.Clear();
    }
}
