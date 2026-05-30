// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System.Windows.Input;
using AgValoniaGPS.Models.Configuration;
using CommunityToolkit.Mvvm.Input;

namespace AgValoniaGPS.ViewModels;

/// <summary>
/// Network IO: module scan (PGN 202), per-module IP/subnet readout (from PGN 203
/// replies, surfaced via <c>State.Connections</c>), and the global subnet change
/// (PGN 201). Mirrors AgIO's FormUDP so the existing AiO board install base works
/// unchanged. The protocol bytes live in the UDP service; the VM only coordinates.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Broadcast PGN 202 and ask every module to report its IP/subnet.</summary>
    public ICommand? ScanModulesCommand { get; private set; }

    /// <summary>Confirm, then broadcast PGN 201 to set the /24 on all modules.</summary>
    public ICommand? SendSubnetCommand { get; private set; }

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

    /// <summary>
    /// The host's own IPv4 addresses (one per up NIC), newline-separated, so the
    /// operator can see which subnet the host is on and match the modules to it.
    /// </summary>
    public string HostIpAddressesText
    {
        get
        {
            var ips = _udpService.GetLocalIpAddresses();
            return ips.Count == 0 ? "—" : string.Join("\n", ips);
        }
    }

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
        ScanModulesCommand = new RelayCommand(() =>
        {
            _udpService.ScanModules();
            OnPropertyChanged(nameof(HostIpAddressesText));
            StatusMessage = "Scanning network for modules…";
        });

        SendSubnetCommand = new RelayCommand(() =>
        {
            int o1 = SubnetOctet1, o2 = SubnetOctet2, o3 = SubnetOctet3;
            ShowConfirmationDialog(
                "Change Module Subnet",
                $"Set ALL connected modules to subnet {o1}.{o2}.{o3}.x and restart them?\n\n" +
                "This changes every module at once (there is no per-module setting).",
                () =>
                {
                    _udpService.SetModuleSubnet((byte)o1, (byte)o2, (byte)o3);
                    StatusMessage = $"Sent subnet {o1}.{o2}.{o3}.x to all modules";
                });
        });

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
