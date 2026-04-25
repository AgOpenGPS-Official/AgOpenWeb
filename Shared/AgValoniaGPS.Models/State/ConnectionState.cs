// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
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

using CommunityToolkit.Mvvm.ComponentModel;

namespace AgValoniaGPS.Models.State;

/// <summary>
/// Connection status for all external systems (GPS, NTRIP, AutoSteer/Machine/IMU modules).
///
/// <para>
/// <b>Thread ownership (§0 invariant, Phase F close):</b>
/// Every property is written on the UI thread. No service writes here
/// directly — communication services (<c>NtripClientService</c>,
/// <c>UdpCommunicationService</c>) raise events from their own background
/// threads; the ViewModel's handlers check
/// <c>Dispatcher.UIThread.CheckAccess()</c> and <c>Post</c> if needed
/// before touching <c>State.Connections</c>. The hello-timer polling
/// in <c>MainViewModel</c> starts on the UI thread and its <c>await</c>
/// continuations stay on the UI thread via Avalonia's
/// <c>SynchronizationContext</c>, so <c>State.Connections</c> writes
/// there are also UI-thread.
/// </para>
///
/// <para>Reader / writer table:</para>
/// <list type="table">
///   <listheader><term>Property</term><description>Written by</description></listheader>
///   <item><term>IsGpsConnected / IsGpsDataOk</term>                     <description>UI — <c>MainViewModel</c> hello-timer (awaited loop)</description></item>
///   <item><term>IsAutoSteerDataOk / IsMachineDataOk / IsImuDataOk</term><description>UI — same hello-timer</description></item>
///   <item><term>IsAutoSteerConnected / IsMachineConnected / IsImuConnected</term><description>UI — same hello-timer (connection vs data-flow distinction)</description></item>
///   <item><term>IsAutoSteerEngaged</term>                               <description>UI — <c>ToggleAutoSteerCommand</c></description></item>
///   <item><term>IsNtripConnected / NtripStatus</term>                   <description>UI — <c>OnNtripConnectionChanged</c> handler (Dispatcher-Posted)</description></item>
///   <item><term>NtripBytesReceived</term>                               <description>UI — <c>OnRtcmDataReceived</c> handler (Dispatcher-Posted)</description></item>
/// </list>
///
/// <para>Services raise events and expose polling APIs — they never
/// reference this type. See <c>Plans/threading_model.svg</c> for the
/// full data-flow contract.</para>
/// </summary>
public class ConnectionState : ObservableObject
{
    // GPS
    private bool _isGpsConnected;
    public bool IsGpsConnected
    {
        get => _isGpsConnected;
        set => SetProperty(ref _isGpsConnected, value);
    }

    private bool _isGpsDataOk;
    public bool IsGpsDataOk
    {
        get => _isGpsDataOk;
        set => SetProperty(ref _isGpsDataOk, value);
    }

    // NTRIP
    private bool _isNtripConnected;
    public bool IsNtripConnected
    {
        get => _isNtripConnected;
        set => SetProperty(ref _isNtripConnected, value);
    }

    private string _ntripStatus = "Not Connected";
    public string NtripStatus
    {
        get => _ntripStatus;
        set => SetProperty(ref _ntripStatus, value);
    }

    private ulong _ntripBytesReceived;
    public ulong NtripBytesReceived
    {
        get => _ntripBytesReceived;
        set => SetProperty(ref _ntripBytesReceived, value);
    }

    // AutoSteer module
    private bool _isAutoSteerConnected;
    public bool IsAutoSteerConnected
    {
        get => _isAutoSteerConnected;
        set => SetProperty(ref _isAutoSteerConnected, value);
    }

    private bool _isAutoSteerDataOk;
    public bool IsAutoSteerDataOk
    {
        get => _isAutoSteerDataOk;
        set => SetProperty(ref _isAutoSteerDataOk, value);
    }

    private bool _isAutoSteerEngaged;
    public bool IsAutoSteerEngaged
    {
        get => _isAutoSteerEngaged;
        set => SetProperty(ref _isAutoSteerEngaged, value);
    }

    private string? _autoSteerIpAddress;
    public string? AutoSteerIpAddress
    {
        get => _autoSteerIpAddress;
        set => SetProperty(ref _autoSteerIpAddress, value);
    }

    // Machine module
    private bool _isMachineConnected;
    public bool IsMachineConnected
    {
        get => _isMachineConnected;
        set => SetProperty(ref _isMachineConnected, value);
    }

    private bool _isMachineDataOk;
    public bool IsMachineDataOk
    {
        get => _isMachineDataOk;
        set => SetProperty(ref _isMachineDataOk, value);
    }

    private string? _machineIpAddress;
    public string? MachineIpAddress
    {
        get => _machineIpAddress;
        set => SetProperty(ref _machineIpAddress, value);
    }

    // IMU
    private bool _isImuConnected;
    public bool IsImuConnected
    {
        get => _isImuConnected;
        set => SetProperty(ref _isImuConnected, value);
    }

    private bool _isImuDataOk;
    public bool IsImuDataOk
    {
        get => _isImuDataOk;
        set => SetProperty(ref _isImuDataOk, value);
    }

    private string? _imuIpAddress;
    public string? ImuIpAddress
    {
        get => _imuIpAddress;
        set => SetProperty(ref _imuIpAddress, value);
    }

    // Overall status
    public bool IsFullyConnected =>
        IsGpsConnected && IsAutoSteerConnected && IsMachineConnected;

    public string OverallStatus
    {
        get
        {
            if (!IsGpsConnected) return "No GPS";
            if (!IsAutoSteerConnected) return "No AutoSteer";
            if (!IsMachineConnected) return "No Machine";
            if (!IsNtripConnected) return "No RTK";
            return "Connected";
        }
    }

    public void Reset()
    {
        // Connection state typically persists, but provide reset for full app restart
        IsGpsConnected = IsGpsDataOk = false;
        IsNtripConnected = false;
        NtripStatus = "Not Connected";
        NtripBytesReceived = 0;
        IsAutoSteerConnected = IsAutoSteerDataOk = IsAutoSteerEngaged = false;
        IsMachineConnected = IsMachineDataOk = false;
        IsImuConnected = IsImuDataOk = false;
        AutoSteerIpAddress = MachineIpAddress = ImuIpAddress = null;
    }
}
