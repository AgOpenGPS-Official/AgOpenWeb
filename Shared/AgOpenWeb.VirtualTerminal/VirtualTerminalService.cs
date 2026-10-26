// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json.Serialization;
using SharpIsoTerm;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.VirtualTerminal;

/// <summary>
/// The VT cards' terminals: SharpIsoTerm's terminal stack on the implement bus, one to four
/// terminals (AppSettings.VtCount; as displays in one cab, each implement choosing one), reached
/// through an AOW link gateway (AppSettings.VtGateway, TCP port 2323 unless given) or an slcan
/// adapter plugged into this device (AppSettings.VtAdapter, its serial port; it wins when set).
/// Gateways announcing themselves on the network are listed for the gear (AowLinkBrowser); one
/// picked by name (AppSettings.VtGatewayName) is followed to a new address. Pools the
/// implements ask to keep are stored under VtPools in the data folder, by NAME and version
/// label, one store for all the terminals. The stack and the link each run their own thread
/// (the library's); this adds the held repeat of a key that is down.
/// </summary>
public sealed class VirtualTerminalService : IVirtualTerminalService, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly ILogger<VirtualTerminalService>? _log;

    private readonly object _gate = new();      // the fields below
    private readonly object _connecting = new(); // one reconnect at a time
    private VirtualTerminalStack? _stack;
    private AowLink? _link;
    private SlcanLink? _adapter;
    private KeyRepeater? _keys;
    private int _session, _count = 1;
    private bool _disposed;
    private string _gateway = "", _adapterPort = "", _state = "off", _why = "", _gatewayName = "", _bus = "";
    private string[] _ports = [];
    private readonly AowLinkBrowser _browser = new();
    private FoundGateway[] _gateways = [];
    private double _rtt = double.NaN;
    private long _linkSentAt;

    public event Action<int, string>? Message;
    public event Action<uint, int, int, byte[]>? Image;
    public event Action? InputRefused;

    public VirtualTerminalService(ISettingsService settings, ILogger<VirtualTerminalService>? log = null)
    {
        _settings = settings;
        _log = log;
    }

    public void Start()
    {
        _browser.Changed += OnGatewaysChanged;
        _browser.HandlerFailed += e => _log?.LogWarning(e, "VT gateway list handler failed");
        _browser.Start();
        Reconnect();
    }

    public void SetGateway(string gateway, string name = "")
    {
        _settings.Settings.VtGateway = gateway.Trim();
        _settings.Settings.VtGatewayName = gateway.Trim().Length == 0 ? "" : name.Trim();
        _settings.Settings.VtAdapter = "";
        _settings.Save();
        Reconnect();
    }

    public void SetAdapter(string port)
    {
        _settings.Settings.VtAdapter = port.Trim();
        _settings.Save();
        Reconnect();
    }

    public void SetCount(int count)
    {
        count = Math.Clamp(count, 1, 4);
        if (count == _settings.Settings.VtCount) return;
        _settings.Settings.VtCount = count;
        _settings.Save();
        Reconnect();
    }

    public void RefreshPorts() => Task.Run(() =>
    {
        _browser.Refresh();
        var ports = ListPorts();
        lock (_gate) _ports = ports;
        PublishLink(force: true);
    });

    private string[] ListPorts()
    {
        try { return SerialPorts.List().ToArray(); }
        catch (Exception e)
        {
            _log?.LogDebug("VT serial ports: {Error}", e.Message);
            return [];
        }
    }

    private static string Address(AowLinkGateway g) =>
        g.Port == AowLink.DefaultPort ? g.Host : g.Host + ":" + g.Port.ToString(CultureInfo.InvariantCulture);

    // The gateways on the network changed. The one picked by name, seen at another address while
    // the terminal isn't connected: follow it there; seen at its address under a new name: rename.
    private void OnGatewaysChanged(IReadOnlyList<AowLinkGateway> list)
    {
        string state;
        lock (_gate)
        {
            _gateways = list.Select(g => new FoundGateway(g.Name, Address(g))).ToArray();
            state = _state;
        }
        var s = _settings.Settings;
        // Renamed on its own page: the same gateway at the same address, under a new name.
        var renamed = s.VtGatewayName.Length > 0 && s.VtAdapter.Length == 0 && list.All(g => g.Name != s.VtGatewayName)
            ? list.FirstOrDefault(g => Address(g) == s.VtGateway) : null;
        if (renamed is not null)
        {
            _log?.LogInformation("VT gateway {Old} is now called {Name}", s.VtGatewayName, renamed.Name);
            s.VtGatewayName = renamed.Name;
            _settings.Save();
        }
        var moved = s.VtGatewayName.Length > 0 && s.VtAdapter.Length == 0 && state != "connected"
            ? list.FirstOrDefault(g => g.Name == s.VtGatewayName && Address(g) != s.VtGateway) : null;
        if (moved is not null)
        {
            _log?.LogInformation("VT gateway {Name} is now at {Address}", moved.Name, Address(moved));
            s.VtGateway = Address(moved);
            _settings.Save();
            Reconnect();
        }
        else PublishLink(force: true);
    }

    // On a worker: stopping the old stack waits for a pool parsing.
    private void Reconnect()
    {
        var s = _settings.Settings;
        string gateway = s.VtGateway, adapter = s.VtAdapter;
        int count = s.VtCount;
        Task.Run(() => Connect(gateway, adapter, count));
    }

    public void Key(int terminal, bool button, ushort objectId, VtKeyAction action)
    {
        var keys = Keys();
        if (keys is null) return;
        if (action == VtKeyAction.Press) keys.Press(terminal, button, objectId);
        else keys.Release(terminal, button, objectId, aborted: action == VtKeyAction.Abort);
    }

    public void Select(int terminal, ushort objectId) => Call(terminal, vt => vt.SelectAsync(objectId, open: true));
    public void SetNumber(int terminal, ushort objectId, uint value) => Call(terminal, vt => vt.SetNumberAsync(objectId, value), Entered);
    public void SetString(int terminal, ushort objectId, string text) => Call(terminal, vt => vt.SetStringAsync(objectId, text), Entered);
    public void Escape(int terminal) => Call(terminal, vt => vt.EscapeAsync());
    public void Activate(int terminal, ulong name) => Call(terminal, vt => vt.ActivateAsync(name));
    public void AbortKeys() => Keys()?.AbortAll();

    private KeyRepeater? Keys() { lock (_gate) return _keys; }

    private void Entered(InputResult r)
    {
        if (r == InputResult.Invalid) InputRefused?.Invoke();
    }

    private void Call<T>(int terminal, Func<SharpIsoTerm.VirtualTerminal, Task<T>> call, Action<T>? then = null)
    {
        VirtualTerminalStack? stack;
        lock (_gate) stack = _stack;
        if (stack is not null && terminal >= 0 && terminal < stack.Terminals.Count) Observe(call(stack.Terminals[terminal]), then);
    }

    private void Observe<T>(Task<T> call, Action<T>? then = null) =>
        call.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) then?.Invoke(t.Result);
            else _log?.LogDebug("VT call failed: {Error}", t.Exception?.GetBaseException().Message);
        }, TaskScheduler.Default);

    // ── The terminals and their link ───────────────────────────────────────────────────────

    private void Connect(string gateway, string adapter, int count)
    {
        lock (_connecting)
        {
            VirtualTerminalStack? oldStack;
            AowLink? oldLink;
            SlcanLink? oldAdapter;
            KeyRepeater? oldKeys;
            lock (_gate)
            {
                oldStack = _stack; oldLink = _link; oldAdapter = _adapter; oldKeys = _keys;
                _stack = null; _link = null; _adapter = null; _keys = null;
            }
            // Keys released first, then the link, then the terminals (the library's order).
            oldKeys?.AbortAll();
            oldLink?.Stop();
            oldAdapter?.Stop();
            oldStack?.Stop();
            if (_disposed) return;

            count = Math.Clamp(count, 1, 4);
            var ports = ListPorts();
            lock (_gate)
            {
                _session++;
                _count = count;
                _gateway = gateway;
                _adapterPort = adapter;
                _ports = ports;
                _state = gateway.Length == 0 && adapter.Length == 0 ? "off" : "connecting";
                _why = _gatewayName = _bus = "";
                _rtt = double.NaN;
            }
            PublishLink(force: true);
            if (adapter.Length > 0) ConnectAdapter(adapter, count);
            else if (gateway.Length > 0) ConnectGateway(gateway, count);
        }
    }

    /// <summary>Stops the terminals and their link: the backend is stopping. On Android and iOS
    /// the process outlives the backend, and the library allows one stack per process.</summary>
    public void Dispose()
    {
        _disposed = true;
        _browser.Stop();
        Connect("", "", 1);
    }

    private VirtualTerminalStack NewStack(FrameSender send, int count, byte packetsPerCts = 0)
    {
        var stack = new VirtualTerminalStack(new VirtualTerminalOptions
        {
            Send = send,
            PacketsPerCts = packetsPerCts,
            Store = new FilePoolStore(Path.Combine(AppDataRoot.Documents, "VtPools")),
            Count = count,
            PhysicalKeys = 12, // two columns of six, as newer terminals have
            Log = (level, text) => _log?.Log(level >= 3 ? LogLevel.Error : level == 2 ? LogLevel.Warning : LogLevel.Debug,
                "{Text}", text),
        });
        foreach (var vt in stack.Terminals)
        {
            int index = vt.Index;
            vt.Message += json => Message?.Invoke(index, json);
            vt.Image += img => Image?.Invoke(img.Id, img.Width, img.Height, img.Rgba);
        }
        stack.HandlerFailed += e => _log?.LogWarning(e, "VT handler failed");
        return stack;
    }

    // An slcan adapter on this device: no round trip or bus state to show; "connecting" while it
    // isn't there, with why (not plugged in, waiting for permission).
    private void ConnectAdapter(string adapter, int count)
    {
        try
        {
            var link = new SlcanLink(adapter);
            // An implement's upload 16 frames at a time, not 255: a USB adapter's reads (Android's
            // most) fall behind a long burst on a full bus, and a frame lost aborts the upload.
            var stack = NewStack(link.Send, count, packetsPerCts: 16);
            stack.Attach(link);
            link.HandlerFailed += e => _log?.LogWarning(e, "VT adapter handler failed");
            link.Connected += info => SetLink("connected", "", info);
            link.Disconnected += (why, text) => SetLink("connecting", text.Length > 0 ? $"{why}: {text}" : why.ToString(), null);
            link.OpenFailed += why => SetLink("connecting", why, null);
            stack.Start();
            link.Start();
            lock (_gate) { _stack = stack; _adapter = link; _keys = new KeyRepeater(stack, Observe); }
        }
        catch (Exception e)
        {
            // No native library for this runtime, another stack in the process, ...
            _log?.LogError(e, "VT could not start");
            SetLink("failed", e.GetBaseException().Message, null);
        }
    }

    private void ConnectGateway(string gateway, int count)
    {
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
            var link = new AowLink(host, port, name: "AgOpenWeb on " + DeviceName());
            var stack = NewStack(link.Send, count);
            stack.Attach(link);
            link.HandlerFailed += e => _log?.LogWarning(e, "VT link handler failed");
            link.Connected += info => SetLink("connected", "", info.Name);
            // Down, or a try that never came up (refused while another terminal has the gateway: said
            // by its name, "in use by AgOpenWeb on ..."). Tries repeat every 250 ms on a gateway that
            // isn't there, so only a change is sent on.
            void Down(LinkCloseReason why, string text)
            {
                var (state, said) = why == LinkCloseReason.Busy
                    ? ("busy", text.StartsWith("in use by ", StringComparison.Ordinal) ? text[10..] : text)
                    : ("connecting", text.Length > 0 ? $"{why}: {text}" : why.ToString());
                lock (_gate) if (_state == state && _why == said) return;
                SetLink(state, said, null);
            }
            link.Disconnected += Down;
            link.ConnectFailed += Down;
            link.RoundTrip += ms => { lock (_gate) _rtt = ms; PublishLink(force: false); };
            link.StatusReceived += st => { lock (_gate) _bus = st.State.ToString(); PublishLink(force: false); };

            stack.Start();
            link.Start();
            lock (_gate) { _stack = stack; _link = link; _keys = new KeyRepeater(stack, Observe); }
        }
        catch (Exception e)
        {
            // No native library for this runtime, another stack in the process, ...
            _log?.LogError(e, "VT could not start");
            SetLink("failed", e.GetBaseException().Message, null);
        }
    }

    // This device, for the gateway's page and for another terminal refused while this one has it.
    private static string DeviceName()
    {
        try
        {
#if ANDROID
            return Android.OS.Build.Model ?? "Android";
#elif IOS
            return UIKit.UIDevice.CurrentDevice.Name;
#else
            return Environment.MachineName;
#endif
        }
        catch (Exception)
        {
            return "this device";
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
    // stack: the cards then drop the old one's pictures and screens. "count": the terminals, one
    // card each. "adapter": the serial port when an adapter on this device is used instead of the
    // gateway; "ports": the serial ports there are, for choosing one; "adapters": false where an
    // app can't reach a USB adapter (iOS), so the gear offers only the gateway. "gatewayName": the
    // gateway's announced name when it was picked from "gateways", the ones found on the network.
    private void PublishLink(bool force)
    {
        string json;
        lock (_gate)
        {
            long now = Environment.TickCount64;
            if (!force && now - _linkSentAt < 2000) return;
            _linkSentAt = now;
            json = System.Text.Json.JsonSerializer.Serialize(new LinkMessage("link", _session, _count, _gateway,
                _settings.Settings.VtGatewayName, _gateways, _adapterPort, _ports, !OperatingSystem.IsIOS(),
                _state, _why, _gatewayName, double.IsNaN(_rtt) ? null : Math.Round(_rtt, 1), _bus), LinkJsonContext.Default.LinkMessage);
        }
        Message?.Invoke(-1, json);
    }

    /// <summary>A key's press, a held about every 200 ms while it is down (ISO 11783-6), and its
    /// release or abort. From SharpIsoTerm's integration guide.</summary>
    private sealed class KeyRepeater(VirtualTerminalStack stack, Action<Task<bool>, Action<bool>?> observe)
    {
        private sealed class Down { public Timer? Timer; }
        private readonly Dictionary<(int Terminal, bool Button, ushort Id), Down> _down = new();

        private void Send((int Terminal, bool Button, ushort Id) key, KeyState state)
        {
            if (key.Terminal < 0 || key.Terminal >= stack.Terminals.Count) return;
            var vt = stack.Terminals[key.Terminal];
            observe(key.Button ? vt.ButtonAsync(key.Id, state) : vt.SoftKeyAsync(key.Id, state), null);
        }

        public void Press(int terminal, bool button, ushort id)
        {
            var key = (terminal, button, id);
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

        public void Release(int terminal, bool button, ushort id, bool aborted)
        {
            lock (_down)
            {
                if (!_down.Remove((terminal, button, id), out var d)) return;
                d.Timer?.Dispose();
                Send((terminal, button, id), aborted ? KeyState.Aborted : KeyState.Released);
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
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("gateway")] string Gateway,
    [property: JsonPropertyName("gatewayName")] string GatewayName,
    [property: JsonPropertyName("gateways")] FoundGateway[] Gateways,
    [property: JsonPropertyName("adapter")] string Adapter,
    [property: JsonPropertyName("ports")] string[] Ports,
    [property: JsonPropertyName("adapters")] bool Adapters,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("why")] string Why,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("rtt")] double? Rtt,
    [property: JsonPropertyName("bus")] string Bus);

/// <summary>A gateway announcing itself on the network: its name and its address.</summary>
internal sealed record FoundGateway(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("address")] string Address);

[JsonSerializable(typeof(LinkMessage))]
internal sealed partial class LinkJsonContext : JsonSerializerContext;
