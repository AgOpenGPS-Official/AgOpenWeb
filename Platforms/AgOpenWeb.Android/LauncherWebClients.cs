// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using Android.App;
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
/// <c>window.open(url, '_blank')</c> goes to the system browser, the page's console goes to
/// logcat, and <c>&lt;input type=file&gt;</c> opens the system picker (bug report attachments):
/// a WebView shows none of its own.
/// </summary>
internal sealed class LauncherWebChromeClient : WebChromeClient
{
    public const int FileChooserRequest = 4711;

    private readonly Activity _activity;
    private IValueCallback? _fileCallback;

    public LauncherWebChromeClient(Activity activity) => _activity = activity;

    public override bool OnShowFileChooser(WebView? webView, IValueCallback? filePathCallback, FileChooserParams? fileChooserParams)
    {
        _fileCallback?.OnReceiveValue(null); // a picker left open: the page must hear back once
        _fileCallback = filePathCallback;
        try
        {
            var intent = fileChooserParams?.CreateIntent() ?? new Intent(Intent.ActionGetContent).SetType("*/*");
            intent.AddCategory(Intent.CategoryOpenable);
            if (fileChooserParams?.Mode == ChromeFileChooserMode.OpenMultiple)
                intent.PutExtra(Intent.ExtraAllowMultiple, true);
            _activity.StartActivityForResult(Intent.CreateChooser(intent, (string?)null), FileChooserRequest);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Launcher] no file picker: {ex.Message}");
            _fileCallback = null;
            return false;
        }
    }

    /// <summary>From <see cref="MainActivity.OnActivityResult"/>: hand the picked files to the page.</summary>
    public void OnFileChosen(Result resultCode, Intent? data)
    {
        var callback = _fileCallback;
        _fileCallback = null;
        if (callback == null) return;
        global::Android.Net.Uri[]? uris = null;
        if (resultCode == Result.Ok && data != null)
        {
            if (data.ClipData is { ItemCount: > 0 } clip)
            {
                uris = new global::Android.Net.Uri[clip.ItemCount];
                for (int i = 0; i < clip.ItemCount; i++) uris[i] = clip.GetItemAt(i)!.Uri!;
            }
            else if (data.Data != null) uris = new[] { data.Data };
        }
        callback.OnReceiveValue(uris); // null = cancelled; the page must always hear back
    }

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
