// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading.Tasks;

using AgOpenWeb.Models.Timing;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.Logging;

using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// MainViewModel partial class containing NTRIP connection management.
/// Handles NTRIP caster connections, RTCM data reception, and profile management.
/// </summary>
public partial class MainViewModel
{
    #region NTRIP Fields

    private bool _isNtripConnected;
    private string _ntripStatus = "Not Connected";
    private ulong _ntripBytesReceived;
    private string _ntripCasterAddress = "rtk2go.com";
    private int _ntripCasterPort = 2101;
    private string _ntripMountPoint = "";
    private string _ntripUsername = "";
    private string _ntripPassword = "";
    private NtripFallbackConfiguration? _ntripFallback;   // of the profile in use; null in the profile-per-field mode
    private (string Host, int Port, string Mount)? _ntripFallbackFor;   // the main service that fallback belongs to

    #endregion

    #region NTRIP Properties

    public bool IsNtripConnected
    {
        get => _isNtripConnected;
        set => SetProperty(ref _isNtripConnected, value);
    }

    public string NtripStatus
    {
        get => _ntripStatus;
        set => SetProperty(ref _ntripStatus, value);
    }

    public string NtripBytesReceived
    {
        get => $"{(_ntripBytesReceived / 1024):N0} KB";
    }

    public string NtripCasterAddress
    {
        get => _ntripCasterAddress;
        set => SetProperty(ref _ntripCasterAddress, value);
    }

    public int NtripCasterPort
    {
        get => _ntripCasterPort;
        set => SetProperty(ref _ntripCasterPort, value);
    }

    public string NtripMountPoint
    {
        get => _ntripMountPoint;
        set => SetProperty(ref _ntripMountPoint, value);
    }

    public string NtripUsername
    {
        get => _ntripUsername;
        set => SetProperty(ref _ntripUsername, value);
    }

    public string NtripPassword
    {
        get => _ntripPassword;
        set => SetProperty(ref _ntripPassword, value);
    }

    #endregion

    #region NTRIP Connection Methods

    public async Task ConnectToNtripAsync()
    {
        try
        {
            var config = new NtripConfiguration
            {
                CasterAddress = NtripCasterAddress,
                CasterPort = NtripCasterPort,
                MountPoint = NtripMountPoint,
                Username = NtripUsername,
                Password = NtripPassword,
                // The modules' /24 as discovered (followed live), else the Network IO
                // subnet (default 192.168.5) — AgIO sends to its subnet setting.
                SubnetAddress = $"{SubnetOctet1}.{SubnetOctet2}.{SubnetOctet3}",
                SubnetProvider = _udpService.GetActiveModuleSubnet,
                // Unicast to the module the position sentences come from, unless the
                // operator asked for the broadcast (Network IO).
                GpsModuleAddressProvider = () => _udpService.GetGpsSourceAddress(),
                BroadcastOnly = () => ConfigStore.Connections.RtcmBroadcast,
                UdpForwardPort = 2233,
                GgaIntervalSeconds = 10,
                // Before the first fix a VRS caster still needs to know roughly where we are.
                FallbackPositionProvider = () => State.Field.OriginLatitude != 0 || State.Field.OriginLongitude != 0
                    ? (State.Field.OriginLatitude, State.Field.OriginLongitude)
                    : null,
                UseManualPosition = false,
                // Only for the main service it was made for: a caster typed in by hand has none.
                Fallback = _ntripFallbackFor is { } key && key.Host == NtripCasterAddress && key.Port == NtripCasterPort && key.Mount == NtripMountPoint
                    ? _ntripFallback : null,
            };

            await _ntripService.ConnectAsync(config);
        }
        catch (Exception ex)
        {
            NtripStatus = $"Error: {ex.Message}";
        }
    }

    public async Task DisconnectFromNtripAsync()
    {
        await _ntripService.DisconnectAsync();
    }

    /// <summary>
    /// The NTRIP on/off box in Network IO (#278). Off drops the connection and ends the
    /// retries; on connects as a start-up or a field open would, with the open field's
    /// profile or the default one.
    /// </summary>
    private void OnNtripEnabledChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Models.Configuration.ConnectionConfig.NtripEnabled))
            _ = ApplyNtripEnabledAsync(ConfigStore.Connections.NtripEnabled);
        else if (e.PropertyName == nameof(Models.Configuration.ConnectionConfig.NtripProfilePerField))
            _ = ApplyNtripModeAsync();
    }

    /// <summary>
    /// The mode changed while running. Profile per field: the open field's profile. One profile with
    /// a fallback: the default profile (with its fallback). Nothing is deleted either way.
    /// </summary>
    private async Task ApplyNtripModeAsync()
    {
        try
        {
            if (!ConfigStore.Connections.NtripEnabled || !_ntripService.IsActive) return;

            var field = State.Field.ActiveField?.Name;
            var profile = ConfigStore.Connections.NtripProfilePerField && !string.IsNullOrEmpty(field)
                ? _ntripProfileService.GetProfileForField(field)
                : _ntripProfileService.DefaultProfile;
            if (profile == null || !profile.AutoConnectOnFieldLoad) return;

            // The same caster can still need a new session: the mode decides whether the fallback is part of it.
            if (IsNtripConnectedTo(profile) && SameFallback(FallbackFor(profile), _ntripFallback)) return;
            await SwitchToNtripProfileAsync(profile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying the NTRIP mode");
        }
    }

    /// <summary>Switch to the main or the fallback service of the connected profile, at any time.</summary>
    public Task SwitchNtripServiceAsync(NtripService service) => _ntripService.SwitchToAsync(service);

    private async Task ApplyNtripEnabledAsync(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                if (_ntripService.IsActive) await _ntripService.DisconnectAsync();
                return;
            }
            var field = State.Field.ActiveField?.Name;
            if (!string.IsNullOrEmpty(field)) await HandleNtripProfileForFieldAsync(field);
            // Switched on by hand: connect whatever the profile's auto-connect says.
            if (!_ntripService.IsActive) await ConnectDefaultNtripProfileOnStartupAsync(explicitRequest: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error switching NTRIP {State}", enabled ? "on" : "off");
        }
    }

    /// <summary>
    /// Loads NTRIP profiles then, if a default profile exists with auto-connect
    /// enabled, connects to it at startup. Keeps corrections flowing to the GPS
    /// from app launch so there is no wait when a field is opened.
    /// </summary>
    private async Task LoadProfilesThenAutoConnectAsync()
    {
        try
        {
            await _ntripProfileService.LoadProfilesAsync();
            ShowNtripModeNoticeOnce();
            await ConnectDefaultNtripProfileOnStartupAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading NTRIP profiles / startup auto-connect");
        }
    }

    /// <summary>
    /// Connects to the default NTRIP profile at startup (if one is set and its
    /// auto-connect is enabled, and we are not already connected). A field-loaded
    /// later that has its own profile will switch via <see cref="HandleNtripProfileForFieldAsync"/>.
    /// </summary>
    private async Task ConnectDefaultNtripProfileOnStartupAsync(bool explicitRequest = false)
    {
        if (!ConfigStore.Connections.NtripEnabled)
        {
            _logger.LogDebug("NTRIP is switched off; skipping startup auto-connect");
            return;
        }

        var profile = _ntripProfileService.DefaultProfile;
        if (profile == null)
        {
            _logger.LogDebug("No default NTRIP profile; skipping startup auto-connect");
            return;
        }

        if (!explicitRequest && !profile.AutoConnectOnFieldLoad)
        {
            _logger.LogDebug("Default NTRIP profile '{ProfileName}' has auto-connect disabled", profile.Name);
            return;
        }

        if (_ntripService.IsActive)
            return;

        _logger.LogInformation("Connecting default NTRIP profile '{ProfileName}' ({Reason})", profile.Name,
            explicitRequest ? "NTRIP switched on" : "startup auto-connect");
        await SwitchToNtripProfileAsync(profile);
    }

    /// <summary>
    /// A profile was just created. If it is the default one and nothing is connected, start it now:
    /// otherwise the first profile would do nothing until a restart or a field load.
    /// </summary>
    public async Task ConnectNewNtripProfileAsync(Models.Ntrip.NtripProfile profile)
    {
        try
        {
            if (!ConfigStore.Connections.NtripEnabled || _ntripService.IsActive || !profile.IsDefault) return;
            await SwitchToNtripProfileAsync(profile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error connecting the new NTRIP profile");
        }
    }

    /// <summary>
    /// After a field closes: corrections keep flowing, as they do at startup with no field
    /// open (#285). Only when the field connected a profile other than the default does
    /// this switch back to the default one; nothing starts if NTRIP is off or stopped.
    /// </summary>
    public async Task RestoreDefaultNtripProfileAsync()
    {
        try
        {
            if (!ConfigStore.Connections.NtripEnabled || !ConfigStore.Connections.NtripProfilePerField || !_ntripService.IsActive) return;

            var profile = _ntripProfileService.DefaultProfile;
            if (profile == null || !profile.AutoConnectOnFieldLoad || IsNtripConnectedTo(profile)) return;

            _logger.LogInformation("Field closed; returning to the default NTRIP profile '{ProfileName}'", profile.Name);
            await SwitchToNtripProfileAsync(profile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error returning to the default NTRIP profile");
        }
    }

    /// <summary>Connected to this profile's caster, port and mount point.</summary>
    private bool IsNtripConnectedTo(Models.Ntrip.NtripProfile profile) =>
        _ntripService.IsConnected &&
        profile.CasterHost == NtripCasterAddress &&
        profile.CasterPort == NtripCasterPort &&
        profile.MountPoint == NtripMountPoint;

    /// <summary>Drop the current session, if any, and connect with this profile.</summary>
    private async Task SwitchToNtripProfileAsync(Models.Ntrip.NtripProfile profile)
    {
        if (_ntripService.IsActive)
        {
            _logger.LogDebug("Disconnecting from current NTRIP caster");
            await _ntripService.DisconnectAsync();
        }

        // Reflect the profile in the display props.
        NtripCasterAddress = profile.CasterHost;
        NtripCasterPort = profile.CasterPort;
        NtripMountPoint = profile.MountPoint;
        NtripUsername = profile.Username;
        NtripPassword = profile.Password;
        UseFallbackOf(profile);

        await ConnectToNtripAsync();
    }

    private void UseFallbackOf(Models.Ntrip.NtripProfile profile)
    {
        _ntripFallback = FallbackFor(profile);
        _ntripFallbackFor = (profile.CasterHost, profile.CasterPort, profile.MountPoint);
    }

    private static bool SameFallback(NtripFallbackConfiguration? a, NtripFallbackConfiguration? b) =>
        a == null || b == null
            ? a == b
            : a.CasterAddress == b.CasterAddress && a.CasterPort == b.CasterPort && a.MountPoint == b.MountPoint
                && a.Username == b.Username && a.Password == b.Password
                && a.AutoSwitch == b.AutoSwitch && a.SwitchAfterFailures == b.SwitchAfterFailures;

    /// <summary>The profile's fallback service; none in the profile-per-field mode, which uses the main service only.</summary>
    private NtripFallbackConfiguration? FallbackFor(Models.Ntrip.NtripProfile profile) =>
        ConfigStore.Connections.NtripProfilePerField || profile.Fallback is not { IsComplete: true } f
            ? null
            : new NtripFallbackConfiguration
            {
                CasterAddress = f.CasterHost, CasterPort = f.CasterPort, MountPoint = f.MountPoint,
                Username = f.Username, Password = f.Password,
                AutoSwitch = f.AutoSwitch, SwitchAfterFailures = f.SwitchAfterFailures,
            };

    /// <summary>
    /// After an NTRIP profile is edited: if we're connected with its old caster settings and
    /// the connection settings changed, reconnect with the new ones (#111).
    /// </summary>
    public async Task ReconnectNtripIfConnectedAsync(Models.Ntrip.NtripProfile before, Models.Ntrip.NtripProfile after)
    {
        try
        {
            if (!_ntripService.IsActive) return;
            bool wasThisOne = before.CasterHost == NtripCasterAddress
                && before.CasterPort == NtripCasterPort && before.MountPoint == NtripMountPoint;
            if (!wasThisOne) return;
            bool changed = before.CasterHost != after.CasterHost || before.CasterPort != after.CasterPort
                || before.MountPoint != after.MountPoint || before.Username != after.Username
                || before.Password != after.Password || !SameFallback(before.Fallback, after.Fallback);
            if (!changed) return;

            await _ntripService.DisconnectAsync();
            NtripCasterAddress = after.CasterHost;
            NtripCasterPort = after.CasterPort;
            NtripMountPoint = after.MountPoint;
            NtripUsername = after.Username;
            NtripPassword = after.Password;
            UseFallbackOf(after);
            _logger.LogInformation("NTRIP profile '{ProfileName}' changed while connected; reconnecting", after.Name);
            await ConnectToNtripAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reconnecting NTRIP after a profile change");
        }
    }

    private static bool SameFallback(Models.Ntrip.NtripFallback? a, Models.Ntrip.NtripFallback? b) =>
        a == null || b == null
            ? a == b
            : a.CasterHost == b.CasterHost && a.CasterPort == b.CasterPort && a.MountPoint == b.MountPoint
                && a.Username == b.Username && a.Password == b.Password
                && a.AutoSwitch == b.AutoSwitch && a.SwitchAfterFailures == b.SwitchAfterFailures;

    /// <summary>
    /// Handles NTRIP profile connection when a field is loaded.
    /// Checks for field-specific profile or falls back to default profile.
    /// </summary>
    private async Task HandleNtripProfileForFieldAsync(string fieldName)
    {
        try
        {
            if (!ConfigStore.Connections.NtripEnabled)
            {
                _logger.LogDebug("NTRIP is switched off; not connecting for field '{FieldName}'", fieldName);
                return;
            }

            // One profile with a fallback is in use: a field load changes nothing.
            if (!ConfigStore.Connections.NtripProfilePerField)
            {
                _logger.LogDebug("NTRIP uses one profile; not switching for field '{FieldName}'", fieldName);
                return;
            }

            var profile = _ntripProfileService.GetProfileForField(fieldName);

            if (profile == null)
            {
                _logger.LogDebug("No NTRIP profile found for field '{FieldName}' (no default set)", fieldName);
                return;
            }

            if (!profile.AutoConnectOnFieldLoad)
            {
                _logger.LogDebug("NTRIP profile '{ProfileName}' has auto-connect disabled", profile.Name);
                return;
            }

            // Already connected to this exact caster (e.g. the default connected at
            // startup, and this field uses the default) — leave it alone so
            // corrections keep flowing without a disconnect/reconnect blip.
            if (IsNtripConnectedTo(profile))
            {
                _logger.LogDebug("NTRIP already connected to '{ProfileName}' caster; keeping it", profile.Name);
                return;
            }

            _logger.LogInformation("Connecting to NTRIP profile '{ProfileName}' for field '{FieldName}'", profile.Name, fieldName);
            await SwitchToNtripProfileAsync(profile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling NTRIP profile for field '{FieldName}'", fieldName);
        }
    }

    #endregion

    #region NTRIP Event Handlers

    private void OnNtripConnectionChanged(object? sender, NtripConnectionEventArgs e)
    {
        // Marshal to UI thread (use Invoke for synchronous execution to avoid modal dialog issues)
        if (_dispatcher.CheckAccess())
        {
            UpdateNtripConnectionProperties(e);
        }
        else
        {
            _dispatcher.Post(() => UpdateNtripConnectionProperties(e));
        }
    }

    private void UpdateNtripConnectionProperties(NtripConnectionEventArgs e)
    {
        // Update centralized state
        State.Connections.IsNtripConnected = e.IsConnected;
        State.Connections.NtripStatus = e.Message ?? (e.IsConnected ? "Connected" : "Not Connected");

        State.Connections.NtripUsingFallback = _ntripService.Using == NtripService.Fallback;
        State.Connections.NtripMainIsBack = _ntripService.MainIsBack;

        // Legacy property updates
        IsNtripConnected = e.IsConnected;
        NtripStatus = e.Message ?? (e.IsConnected ? "Connected" : "Not Connected");
    }

    private void OnNtripNotice(object? sender, NtripNoticeEventArgs e)
    {
        if (_dispatcher.CheckAccess()) ApplyNtripNotice(e);
        else _dispatcher.Post(() => ApplyNtripNotice(e));
    }

    // A switch or note is told once and then fades from the card, so an old one does not stay.
    private const double NtripNoticeSeconds = 5 * 60;
    private long _ntripNoticeAt;

    private void SetNtripNotice(string text)
    {
        State.Connections.NtripNotice = text;
        _ntripNoticeAt = Clock.Current.GetTimestamp();
    }

    internal void ExpireNtripNotice()
    {
        if (State.Connections.NtripNotice.Length == 0) return;
        if (Clock.Current.ElapsedSeconds(_ntripNoticeAt, Clock.Current.GetTimestamp()) > NtripNoticeSeconds)
            State.Connections.NtripNotice = "";
    }

    /// <summary>A switch between the NTRIP services, to show as a short notification on the web.</summary>
    public event Action<string>? NtripNoticeReported;

    private void ApplyNtripNotice(NtripNoticeEventArgs e)
    {
        SetNtripNotice(e.Message);
        State.Connections.NtripUsingFallback = _ntripService.Using == NtripService.Fallback;
        State.Connections.NtripMainIsBack = _ntripService.MainIsBack;

        if (e.Kind != NtripNoticeKind.MainBack)
        {
            NtripNoticeReported?.Invoke(e.Message);
            return;
        }

        // The main service answers again: ask, never switch back by itself. Not over another question
        // that is waiting; the card in Network IO still offers the switch.
        if (IsPromptPending) return;
        ShowConfirmationDialog("Main service is back", e.Message + " Switch back to it? GPS may lose RTK and the position may shift a little.",
            "Switch back", "Stay on fallback", () =>
            {
                // Only while it is still true: the operator may have switched, or the main service may have dropped again.
                if (_ntripService.Using == NtripService.Fallback && _ntripService.MainIsBack) _ = SwitchNtripServiceAsync(NtripService.Main);
            });
    }

    /// <summary>
    /// Once, for an installation whose profiles were tied to fields before NTRIP got a fallback: say that one
    /// profile with a fallback is now the default and the field links are kept (Plans/NTRIP_MAIN_FALLBACK_PLAN.md).
    /// </summary>
    internal void ShowNtripModeNoticeOnce()
    {
        if (!_dispatcher.CheckAccess()) { _dispatcher.Post(ShowNtripModeNoticeOnce); return; }   // settings and state are the UI thread's
        var connections = ConfigStore.Connections;
        if (connections.NtripModeNoticeShown) return;
        connections.NtripModeNoticeShown = true;
        _configurationService.SaveAppSettings();
        if (connections.NtripProfilePerField || !_ntripProfileService.Profiles.Any(p => p.AssociatedFields.Count > 0)) return;

        const string text = "NTRIP now uses one profile with a fallback service. Your profiles keep their fields: turn on \"Profile per field\" in Network IO to use them.";
        SetNtripNotice(text);
        NtripNoticeReported?.Invoke(text);
    }

    private void OnRtcmDataReceived(object? sender, RtcmDataReceivedEventArgs e)
    {
        if (_dispatcher.CheckAccess())
        {
            UpdateNtripDataProperties();
        }
        else
        {
            _dispatcher.Post(() => UpdateNtripDataProperties());
        }
    }

    private void UpdateNtripDataProperties()
    {
        // Update centralized state
        State.Connections.NtripBytesReceived = _ntripService.TotalBytesReceived;
        var (destination, unicast) = _ntripService.RtcmDestination;
        State.Connections.NtripRtcmDestination = destination ?? "";
        State.Connections.NtripRtcmUnicast = unicast;

        // Legacy property updates
        _ntripBytesReceived = _ntripService.TotalBytesReceived;
        OnPropertyChanged(nameof(NtripBytesReceived));
    }

    #endregion
}
