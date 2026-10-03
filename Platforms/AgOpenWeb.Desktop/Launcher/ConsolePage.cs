// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
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

using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Photino.NET;

namespace AgOpenWeb.Desktop.Launcher;

/// <summary>
/// The <c>--console</c> supervisor: a small window that starts/stops the in-process guidance
/// backend (<see cref="BackendHost"/>) and shows the LAN address other devices browse to. For the
/// Windows-centric audience who expect a program with buttons rather than a service, on a box run
/// purely as a server. The page is embedded HTML; it talks to this class over Photino's web-message
/// channel and receives its state as JSON once a second.
/// </summary>
internal sealed class ConsolePage
{
    private readonly PhotinoWindow _window;
    private readonly string[] _args;
    private BackendHost? _backend;
    private bool _busy, _closing, _autoOpen = true;
    private string? _error;
    private Timer? _timer;

    public ConsolePage(PhotinoWindow window, string[] args)
    {
        _window = window;
        _args = args;
    }

    public void Show()
    {
        _window
            .SetSize(420, 400)
            .Center()
            .SetResizable(false)
            .RegisterWebMessageReceivedHandler(OnWebMessage)
            .RegisterWindowClosingHandler((_, _) => LauncherEntry.StopThenClose(_window, _backend, ref _closing))
            .RegisterWindowCreatedHandler((_, _) => _timer = new Timer(_ => PushState(), null, 500, 1000))
            .LoadRawString(Html);
    }

    // ---- page → host ----

    private void OnWebMessage(object? sender, string message)
    {
        switch (message)
        {
            case "start": _ = StartAsync(); break;
            case "stop": _ = StopAsync(); break;
            case "open": LauncherEntry.OpenUrl(LanUrl()); break;
            case "copy": CopyToClipboard(LanUrl()); break;
            case "autoopen|1": _autoOpen = true; break;
            case "autoopen|0": _autoOpen = false; break;
            case "runonlogin|1": SetRunOnLogin(true); break;
            case "runonlogin|0": SetRunOnLogin(false); break;
        }
        PushState();
    }

    // ---- host → page ----

    private void PushState()
    {
        bool running = _backend is { IsRunning: true };
        int clients = _backend?.Server?.ClientCount ?? 0;
        var state = JsonSerializer.Serialize(new
        {
            running, busy = _busy, error = _error, url = LanUrl(), clients,
            autoOpen = _autoOpen, canRunOnLogin = OperatingSystem.IsWindows(), runOnLogin = RunOnLoginEnabled(),
        });
        try { _window.SendWebMessage(state); } catch { /* window going away */ }
    }

    // ---- lifecycle ----

    private async Task StartAsync()
    {
        if (_busy || _backend is { IsRunning: true }) return;
        _busy = true; _error = null; PushState();
        var backend = new BackendHost();
        try { await backend.StartAsync(_args); _backend = backend; }
        catch (Exception ex)
        {
            _error = LauncherEntry.LogStartFailure(ex);
            try { await backend.StopAsync(); } catch { /* best effort */ }
        }
        _busy = false; PushState();
        if (_error == null && _autoOpen) LauncherEntry.OpenUrl(LanUrl());
    }

    private async Task StopAsync()
    {
        if (_busy || _backend is not { IsRunning: true }) return;
        _busy = true; PushState();
        var backend = _backend;
        try { await backend.StopAsync(); } catch { /* UI still returns to Stopped */ }
        _backend = null;
        _busy = false; PushState();
    }

    // ---- helpers ----

    private string LanUrl()
    {
        int port = _backend?.Server?.Port ?? 5174;
        return $"http://{LanIp()}:{port}";
    }

    private static string LanIp()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                        return ua.Address.ToString();
            }
        }
        catch { /* fall through */ }
        return "localhost";
    }

    // The embedded page has no secure origin, so the browser clipboard API is unavailable; copy
    // through the OS tool instead.
    private static void CopyToClipboard(string text)
    {
        try
        {
            var psi = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd", "/c clip")
                    : OperatingSystem.IsMacOS() ? new ProcessStartInfo("pbcopy")
                    : new ProcessStartInfo("xclip", "-selection clipboard");
            psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardInput = true;
            using var p = Process.Start(psi);
            if (p == null) return;
            p.StandardInput.Write(text);
            p.StandardInput.Close();
            p.WaitForExit(2000);
        }
        catch { /* the URL is shown for manual entry */ }
    }

    // Run-on-login via reg.exe (Windows only) — package-free; the Run key launches the
    // launcher on user sign-in. Guarded so it is never touched off Windows.
    private static readonly string RunKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";

    private static void SetRunOnLogin(bool on)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var psi = on
                ? new ProcessStartInfo("reg", $"add \"{RunKey}\" /v AgOpenWeb /t REG_SZ /d \"\\\"{exe}\\\" --launcher --console\" /f")
                : new ProcessStartInfo("reg", $"delete \"{RunKey}\" /v AgOpenWeb /f");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process.Start(psi)?.WaitForExit(3000);
        }
        catch { /* a Run-key write failure must not crash the launcher */ }
    }

    private static bool RunOnLoginEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var psi = new ProcessStartInfo("reg", $"query \"{RunKey}\" /v AgOpenWeb")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(3000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // The supervisor page. Dark, one column, same look as the web UI's dialogs.
    private const string Html = """
<!doctype html><html><head><meta charset="utf-8"><title>AgOpenWeb</title><style>
html,body{margin:0;background:#0f1115;color:#e8edf3;font:14px system-ui,sans-serif;user-select:none}
body{padding:20px}h1{margin:0;font-size:22px}.sub{color:#9fb3cc;font-size:12px}
.row{display:flex;align-items:center;gap:8px;margin-top:14px}.dot{width:12px;height:12px;border-radius:50%;background:#ff5a5a}
.dot.on{background:#39FF6A}.dot.busy{background:#e0b341}#status{font-weight:600;font-size:15px}
.url{display:flex;gap:8px;margin-top:10px}.url code{flex:1;background:#1b2735;border-radius:4px;padding:8px 10px;font:13px Consolas,Menlo,monospace}
button{background:#2a3550;color:#fff;border:0;border-radius:6px;height:38px;font:inherit;cursor:pointer}button:disabled{opacity:.45;cursor:default}
button.accent{background:#2d7ff9}.url button{height:auto;padding:4px 10px}.btns{display:flex;flex-direction:column;gap:8px;margin-top:16px}
#clients{color:#9fb3cc;font-size:12px;margin-top:6px;min-height:14px}#error{color:#ff8a8a;margin-top:8px;white-space:pre-line}
label{display:flex;gap:8px;align-items:center;margin-top:8px}.opts{margin-top:14px}
</style></head><body>
<h1>AgOpenWeb</h1><div class="sub">Guidance host</div>
<div class="row"><div class="dot" id="dot"></div><div id="status">Stopped</div></div>
<div class="url"><code id="url">…</code><button id="copy" disabled>Copy</button></div>
<div id="clients"></div><div id="error"></div>
<div class="btns"><button class="accent" id="startstop">Start</button><button id="open" disabled>Open in Browser</button></div>
<div class="opts"><label><input type="checkbox" id="autoopen" checked> Open browser when started</label>
<label id="rolrow" hidden><input type="checkbox" id="runonlogin"> Start with Windows</label></div>
<script>
const send = m => window.external.sendMessage(m);
const $ = id => document.getElementById(id);
let running = false;
$('startstop').onclick = () => send(running ? 'stop' : 'start');
$('open').onclick = () => send('open');
$('copy').onclick = () => send('copy');
$('autoopen').onchange = e => send('autoopen|' + (e.target.checked ? 1 : 0));
$('runonlogin').onchange = e => send('runonlogin|' + (e.target.checked ? 1 : 0));
window.external.receiveMessage(msg => {
  const s = JSON.parse(msg); running = s.running;
  $('dot').className = 'dot' + (s.busy ? ' busy' : s.running ? ' on' : '');
  $('status').textContent = s.busy ? (s.running ? 'Stopping…' : 'Starting…') : (s.running ? 'Running' : 'Stopped');
  $('url').textContent = s.url;
  $('clients').textContent = s.running ? s.clients + ' browser' + (s.clients === 1 ? '' : 's') + ' connected' : '';
  $('error').textContent = s.error ? 'Failed: ' + s.error : '';
  $('startstop').textContent = s.running ? 'Stop' : 'Start'; $('startstop').disabled = s.busy;
  $('open').disabled = $('copy').disabled = !s.running;
  $('autoopen').checked = s.autoOpen; $('rolrow').hidden = !s.canRunOnLogin; $('runonlogin').checked = s.runOnLogin;
});
</script></body></html>
""";
}
