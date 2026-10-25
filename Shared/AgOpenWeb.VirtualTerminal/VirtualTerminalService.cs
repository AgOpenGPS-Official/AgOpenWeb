// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json.Serialization;
using AgIsoStack;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.VirtualTerminal;

/// <summary>
/// The VT card's terminal: AgIsoStack's VirtualTerminal on the implement bus, reached through
/// an AOW link gateway (AppSettings.VtGateway, TCP port 2323 unless given). Pools the implements
/// ask to keep are stored under VtPools in the data folder, by NAME and version label. The
/// terminal and the link each run their own thread (the library's); this adds the held repeat
/// of a key that is down.
/// </summary>
public sealed class VirtualTerminalService : IVirtualTerminalService
{
    private readonly ISettingsService _settings;
    private readonly ILogger<VirtualTerminalService>? _log;

    private readonly object _gate = new();      // the fields below
    private readonly object _connecting = new(); // one reconnect at a time
    private AgIsoStack.VirtualTerminal? _vt;
    private AowLink? _link;
    private KeyRepeater? _keys;
    private int _session;
    private string _gateway = "", _state = "off", _why = "", _gatewayName = "", _bus = "";
    private double _rtt = double.NaN;
    private long _linkSentAt;

    public event Action<string>? Message;
    public event Action<uint, int, int, byte[]>? Image;
    public event Action? InputRefused;

    public VirtualTerminalService(ISettingsService settings, ILogger<VirtualTerminalService>? log = null)
    {
        _settings = settings;
        _log = log;
    }

    public void Start() => Task.Run(() => Connect(_settings.Settings.VtGateway));

    public void SetGateway(string gateway)
    {
        gateway = gateway.Trim();
        _settings.Settings.VtGateway = gateway;
        _settings.Save();
        Task.Run(() => Connect(gateway)); // stopping the old terminal waits for a pool parsing
    }

    public void Key(bool button, ushort objectId, VtKeyAction action)
    {
        var keys = Keys();
        if (keys is null) return;
        if (action == VtKeyAction.Press) keys.Press(button, objectId);
        else keys.Release(button, objectId, aborted: action == VtKeyAction.Abort);
    }

    public void Select(ushort objectId) => Call(vt => vt.SelectAsync(objectId, open: true));
    public void SetNumber(ushort objectId, uint value) => Call(vt => vt.SetNumberAsync(objectId, value), Entered);
    public void SetString(ushort objectId, string text) => Call(vt => vt.SetStringAsync(objectId, text), Entered);
    public void Escape() => Call(vt => vt.EscapeAsync());
    public void Activate(ulong name) => Call(vt => vt.ActivateAsync(name));
    public void AbortKeys() => Keys()?.AbortAll();

    private KeyRepeater? Keys() { lock (_gate) return _keys; }

    private void Entered(InputResult r)
    {
        if (r == InputResult.Invalid) InputRefused?.Invoke();
    }

    private void Call<T>(Func<AgIsoStack.VirtualTerminal, Task<T>> call, Action<T>? then = null)
    {
        AgIsoStack.VirtualTerminal? vt;
        lock (_gate) vt = _vt;
        if (vt is not null) Observe(call(vt), then);
    }

    private void Observe<T>(Task<T> call, Action<T>? then = null) =>
        call.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) then?.Invoke(t.Result);
            else _log?.LogDebug("VT call failed: {Error}", t.Exception?.GetBaseException().Message);
        }, TaskScheduler.Default);

    // ── The terminal and its link ──────────────────────────────────────────────────────────

    private void Connect(string gateway)
    {
        lock (_connecting)
        {
            AgIsoStack.VirtualTerminal? oldVt;
            AowLink? oldLink;
            KeyRepeater? oldKeys;
            lock (_gate)
            {
                oldVt = _vt; oldLink = _link; oldKeys = _keys;
                _vt = null; _link = null; _keys = null;
            }
            // Keys released first, then the link, then the terminal (the library's order).
            oldKeys?.AbortAll();
            oldLink?.Stop();
            oldVt?.Stop();

            lock (_gate)
            {
                _session++;
                _gateway = gateway;
                _state = gateway.Length == 0 ? "off" : "connecting";
                _why = _gatewayName = _bus = "";
                _rtt = double.NaN;
            }
            PublishLink(force: true);
            if (gateway.Length == 0) return;

            var host = gateway;
            int port = AowLink.DefaultPort;
            int colon = gateway.LastIndexOf(':');
            if (colon > 0 && int.TryParse(gateway[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
            {
                host = gateway[..colon];
                port = p;
            }

            try
            {
                var link = new AowLink(host, port, name: "AgOpenWeb");
                var vt = new AgIsoStack.VirtualTerminal(new VirtualTerminalOptions
                {
                    Send = link.Send,
                    Store = new FilePoolStore(Path.Combine(AppDataRoot.Documents, "VtPools")),
                    Log = (level, text) => _log?.Log(level >= 3 ? LogLevel.Error : level == 2 ? LogLevel.Warning : LogLevel.Debug,
                        "{Text}", text),
                });
                vt.Attach(link);
                vt.Message += json => Message?.Invoke(json);
                vt.Image += img => Image?.Invoke(img.Id, img.Width, img.Height, img.Rgba);
                vt.HandlerFailed += e => _log?.LogWarning(e, "VT handler failed");
                link.HandlerFailed += e => _log?.LogWarning(e, "VT link handler failed");
                link.Connected += info => SetLink("connected", "", info.Name);
                link.Disconnected += (why, text) => SetLink("connecting", text.Length > 0 ? $"{why}: {text}" : why.ToString(), null);
                link.RoundTrip += ms => { lock (_gate) _rtt = ms; PublishLink(force: false); };
                link.StatusReceived += st => { lock (_gate) _bus = st.State.ToString(); PublishLink(force: false); };

                vt.Start();
                link.Start();
                lock (_gate) { _vt = vt; _link = link; _keys = new KeyRepeater(vt, Observe); }
            }
            catch (Exception e)
            {
                // No native library for this runtime, another terminal in the process, ...
                _log?.LogError(e, "VT could not start");
                SetLink("failed", e.GetBaseException().Message, null);
            }
        }
    }

    private void SetLink(string state, string why, string? gatewayName)
    {
        lock (_gate)
        {
            _state = state;
            _why = why;
            if (gatewayName is not null) _gatewayName = gatewayName;
            if (state != "connected") _rtt = double.NaN;
        }
        PublishLink(force: true);
    }

    // The link message: on any change of state, and with the round trip and the bus state at
    // most every 2 s (the gateway reports both every second). "session" changes with every new
    // terminal: the card then drops the old one's pictures and screens.
    private void PublishLink(bool force)
    {
        string json;
        lock (_gate)
        {
            long now = Environment.TickCount64;
            if (!force && now - _linkSentAt < 2000) return;
            _linkSentAt = now;
            json = System.Text.Json.JsonSerializer.Serialize(new LinkMessage("link", _session, _gateway, _state, _why,
                _gatewayName, double.IsNaN(_rtt) ? null : Math.Round(_rtt, 1), _bus), LinkJsonContext.Default.LinkMessage);
        }
        Message?.Invoke(json);
    }

    /// <summary>A key's press, a held about every 200 ms while it is down (ISO 11783-6), and its
    /// release or abort. From AgIsoStack-dotnet's integration guide.</summary>
    private sealed class KeyRepeater(AgIsoStack.VirtualTerminal vt, Action<Task<bool>, Action<bool>?> observe)
    {
        private sealed class Down { public Timer? Timer; }
        private readonly Dictionary<(bool Button, ushort Id), Down> _down = new();

        private void Send((bool Button, ushort Id) key, KeyState state) =>
            observe(key.Button ? vt.ButtonAsync(key.Id, state) : vt.SoftKeyAsync(key.Id, state), null);

        public void Press(bool button, ushort id)
        {
            var key = (button, id);
            lock (_down)
            {
                if (_down.ContainsKey(key)) return;
                var d = new Down();
                _down[key] = d;
                Send(key, KeyState.Pressed);
                d.Timer = new Timer(_ =>
                {
                    lock (_down)
                        if (_down.TryGetValue(key, out var now) && now == d) Send(key, KeyState.Held);
                }, null, 200, 200);
            }
        }

        public void Release(bool button, ushort id, bool aborted)
        {
            lock (_down)
            {
                if (!_down.Remove((button, id), out var d)) return;
                d.Timer?.Dispose();
                Send((button, id), aborted ? KeyState.Aborted : KeyState.Released);
            }
        }

        public void AbortAll()
        {
            lock (_down)
            {
                foreach (var (key, d) in _down)
                {
                    d.Timer?.Dispose();
                    Send(key, KeyState.Aborted);
                }
                _down.Clear();
            }
        }
    }
}

internal sealed record LinkMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("session")] int Session,
    [property: JsonPropertyName("gateway")] string Gateway,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("why")] string Why,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("rtt")] double? Rtt,
    [property: JsonPropertyName("bus")] string Bus);

[JsonSerializable(typeof(LinkMessage))]
internal sealed partial class LinkJsonContext : JsonSerializerContext;
