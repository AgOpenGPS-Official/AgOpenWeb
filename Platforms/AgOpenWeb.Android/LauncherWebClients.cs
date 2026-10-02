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
using Android.Content;
using Android.OS;
using Android.Webkit;

namespace AgOpenWeb.Android;

/// <summary>
/// Navigation policy and load results for the launcher's <see cref="WebView"/>: the local web
/// app stays in the view, anything else opens in the system browser, and a main-frame error is
/// reported as a failed load. (Through Avalonia's wrapper, Android's own "webpage not available"
/// page counted as a success — issue #73.)
/// </summary>
internal sealed class LauncherWebViewClient : WebViewClient
{
    private readonly int _port;
    private readonly Action<bool> _loadFinished;
    private bool _failed;

    public LauncherWebViewClient(int port, Action<bool> loadFinished)
    {
        _port = port;
        _loadFinished = loadFinished;
    }

    public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
    {
        var url = request?.Url;
        if (url == null || IsLocalApp(url)) return false;
        ExternalLinks.Open(view?.Context, url);
        return true;
    }

    private bool IsLocalApp(global::Android.Net.Uri url) =>
        (url.Host == "localhost" || url.Host == "127.0.0.1") && url.Port == _port;

    public override void OnPageStarted(WebView? view, string? url, global::Android.Graphics.Bitmap? favicon)
    {
        _failed = false;
        base.OnPageStarted(view, url, favicon);
    }

    public override void OnReceivedError(WebView? view, IWebResourceRequest? request, WebResourceError? error)
    {
        if (request?.IsForMainFrame == true)
        {
            _failed = true;
            Console.WriteLine($"[Launcher] load error {(int?)error?.ErrorCode}: {error?.Description}");
        }
        base.OnReceivedError(view, request, error);
    }

    public override void OnReceivedHttpError(WebView? view, IWebResourceRequest? request, WebResourceResponse? errorResponse)
    {
        if (request?.IsForMainFrame == true)
        {
            _failed = true;
            Console.WriteLine($"[Launcher] HTTP error {errorResponse?.StatusCode}");
        }
        base.OnReceivedHttpError(view, request, errorResponse);
    }

    // Also fires for the error page, after OnReceivedError.
    public override void OnPageFinished(WebView? view, string? url)
    {
        base.OnPageFinished(view, url);
        _loadFinished(!_failed);
    }
}

/// <summary>
/// <c>window.open(url, '_blank')</c> goes to the system browser, and the page's console goes to
/// logcat.
/// </summary>
internal sealed class LauncherWebChromeClient : WebChromeClient
{
    // The new window's URL isn't in the request: Android hands over an empty WebView and loads
    // the URL into it. Give it a throwaway one whose only job is to pass that URL on.
    public override bool OnCreateWindow(WebView? view, bool isDialog, bool isUserGesture, Message? resultMsg)
    {
        if (view?.Context == null || resultMsg?.Obj is not WebView.WebViewTransport transport) return false;
        var popup = new WebView(view.Context);
        popup.SetWebViewClient(new PopupClient());
        transport.WebView = popup;
        resultMsg.SendToTarget();
        return true;
    }

    public override bool OnConsoleMessage(ConsoleMessage? consoleMessage)
    {
        if (consoleMessage != null)
            Console.WriteLine($"[Web] {consoleMessage.InvokeMessageLevel()}: {consoleMessage.Message()} ({consoleMessage.SourceId()}:{consoleMessage.LineNumber()})");
        return true;
    }

    private sealed class PopupClient : WebViewClient
    {
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            if (request?.Url != null) ExternalLinks.Open(view?.Context, request.Url);
            view?.Destroy();
            return true;
        }
    }
}

internal static class ExternalLinks
{
    public static void Open(Context? context, global::Android.Net.Uri url)
    {
        if (context == null) return;
        try
        {
            var intent = new Intent(Intent.ActionView, url);
            intent.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Launcher] could not open {url}: {ex.Message}");
        }
    }
}
