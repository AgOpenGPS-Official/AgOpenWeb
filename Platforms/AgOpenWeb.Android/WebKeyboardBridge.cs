// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using Android.Webkit;
using Java.Interop;

namespace AgOpenWeb.Android;

/// <summary>
/// A JS→native bridge added to the underlying android.webkit.WebView via
/// addJavascriptInterface (Avalonia's NativeWebView exposes no message bridge on Android).
/// The web app calls <c>window.agnative.hideKeyboard()</c> when a dialog closes; a WebView
/// input's JS blur() can't lower the Android soft keyboard — only InputMethodManager can —
/// so this routes to <see cref="MainActivity.HideSoftKeyboard"/>.
/// </summary>
public sealed class WebKeyboardBridge : Java.Lang.Object
{
    [JavascriptInterface]
    [Export("hideKeyboard")]
    public void HideKeyboard() => MainActivity.HideSoftKeyboard();
}
