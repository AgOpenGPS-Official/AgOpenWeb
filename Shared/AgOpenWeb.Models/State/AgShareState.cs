// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenWeb.Models.State;

/// <summary>One AgShare cloud field (the result of GetOwnFieldsAsync).</summary>
public sealed record AgShareCloudFieldInfo(string Id, string Name, double AreaHa);

/// <summary>
/// Runtime state for the remote (web) AgShare panels — the transient result of a cloud
/// action (test / fetch / upload / download), not persisted config. Written on the UI
/// thread by the host AgShare command handlers; projected on the AgShare wire frame so the
/// browser shows live status + the fetched cloud-field list. Settings (server / API key /
/// enabled) live in ConfigStore.Connections, not here.
/// </summary>
public class AgShareState : ObservableObject
{
    private string _status = string.Empty;
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    private bool _busy;
    public bool Busy { get => _busy; set => SetProperty(ref _busy, value); }

    private IReadOnlyList<AgShareCloudFieldInfo> _cloudFields = new List<AgShareCloudFieldInfo>();
    public IReadOnlyList<AgShareCloudFieldInfo> CloudFields
    {
        get => _cloudFields;
        set => SetProperty(ref _cloudFields, value);
    }
}
