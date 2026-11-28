// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Windows.Input;
using AgOpenWeb.Models.Configuration;
using CommunityToolkit.Mvvm.Input;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// Network IO: module scan (PGN 202), per-module IP/subnet readout (from PGN 203
/// replies, surfaced via <c>State.Connections</c>), and the global subnet change
/// (PGN 201). Mirrors AgIO's FormUDP so the existing AiO board install base works
/// unchanged. The protocol bytes live in the UDP service; the VM only coordinates.
/// </summary>
public partial class MainViewModel
{
    // Set true once settings finish loading, so the persistent module-present
    // checkboxes save on user change but not during the initial load.
    private bool _configReady;

    /// <summary>
    /// Persistent module-present configuration (the Network IO checkboxes).
    /// A module shows present only when it is configured AND responding.
    /// Bound directly (ObservableObject); saved via the ConfigStore.Connections
    /// subscription in the constructor.
    /// </summary>
    public ConnectionConfig ConnectionConfig => ConfigStore.Connections;

    private int _subnetOctet1 = 192;
    public int SubnetOctet1
    {
        get => _subnetOctet1;
        set => SetProperty(ref _subnetOctet1, ClampOctet(value));
    }

    private int _subnetOctet2 = 168;
    public int SubnetOctet2
    {
        get => _subnetOctet2;
        set => SetProperty(ref _subnetOctet2, ClampOctet(value));
    }

    private int _subnetOctet3 = 5;
    public int SubnetOctet3
    {
        get => _subnetOctet3;
        set => SetProperty(ref _subnetOctet3, ClampOctet(value));
    }

    private static int ClampOctet(int v) => v < 0 ? 0 : (v > 255 ? 255 : v);

    private void InitializeNetworkIoCommands()
    {
        // When a scan reply reveals the modules' current /24, reflect it in the
        // entry fields so the operator edits from the live value (AgIO behaviour).
        State.Connections.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(State.Connections.ModuleSubnet)) return;
            var subnet = State.Connections.ModuleSubnet;
            if (string.IsNullOrEmpty(subnet)) return;
            var parts = subnet.Split('.');
            if (parts.Length != 3) return;
            if (int.TryParse(parts[0], out int a)) SubnetOctet1 = a;
            if (int.TryParse(parts[1], out int b)) SubnetOctet2 = b;
            if (int.TryParse(parts[2], out int c)) SubnetOctet3 = c;
        };
    }
}
