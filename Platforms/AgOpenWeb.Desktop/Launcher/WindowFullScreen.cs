// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Photino.NET;

namespace AgOpenWeb.Desktop.Launcher;

/// <summary>
/// The launcher window's fullscreen (#498): the status bar's button and App Settings › Start
/// Fullscreen. The page can't make the window fullscreen itself (the web view ignores the HTML
/// Fullscreen API), so it asks the launcher. Call on the window's thread.
/// <list type="bullet">
///   <item>Windows: the window loses its frame and covers its whole monitor, taskbar included, and
///   gets its frame and place back after (maximized stays maximized). Photino's SetFullScreen
///   sizes a maximized window to the work area, so this is done here.</item>
///   <item>macOS: the window's own fullscreen (toggleFullScreen:, as the green button), its state
///   read from the window, so leaving by the green button or the menu is seen too. Photino's
///   fullscreen moves the web view to a screen of its own and never brought it back, and its
///   FullScreen getter always answers false there (Photino.NET 4.0.16).</item>
///   <item>Linux: Photino's native fullscreen, called directly: PhotinoWindow.SetFullScreen skips
///   the call when its getter already shows the value.</item>
/// </list>
/// </summary>
internal sealed class WindowFullScreen
{
    private readonly PhotinoWindow _window;
    private bool _on;
    private long _savedStyle;
    private WINDOWPLACEMENT _savedPlacement;

    public WindowFullScreen(PhotinoWindow window) => _window = window;

    public bool IsOn => OperatingSystem.IsMacOS() ? MacIsFullScreen() : _on;

    public void Set(bool on)
    {
        if (on == IsOn) return;
        if (OperatingSystem.IsMacOS())
        {
            var w = MacWindow();
            if (w != IntPtr.Zero) objc_msgSend(w, sel_registerName("toggleFullScreen:"), IntPtr.Zero);
            return;
        }
        _on = on;
        if (!OperatingSystem.IsWindows())
        {
            if (NativeInstance?.GetValue(_window) is IntPtr p && p != IntPtr.Zero)
                Photino_SetFullScreen(p, on);
            else
                _window.SetFullScreen(on);
            return;
        }

        var h = _window.WindowHandle;
        if (on)
        {
            _savedStyle = GetWindowLongPtr(h, GWL_STYLE).ToInt64();
            _savedPlacement.length = Marshal.SizeOf<WINDOWPLACEMENT>();
            GetWindowPlacement(h, ref _savedPlacement);
            // A maximized window keeps its maximized (work area) size; restore it first.
            if (IsZoomed(h)) ShowWindow(h, SW_RESTORE);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST), ref mi);
            SetWindowLongPtr(h, GWL_STYLE, new IntPtr(_savedStyle & ~WS_OVERLAPPEDWINDOW));
            var r = mi.rcMonitor;
            SetWindowPos(h, HWND_TOP, r.left, r.top, r.right - r.left, r.bottom - r.top,
                SWP_NOOWNERZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        }
        else
        {
            SetWindowLongPtr(h, GWL_STYLE, new IntPtr(_savedStyle));
            SetWindowPlacement(h, ref _savedPlacement); // its place, and maximized if it was
            SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
        }
    }

    // ---- macOS: the app's window through the Objective-C runtime ----

    private const ulong NSWindowStyleMaskFullScreen = 1UL << 14;

    private static IntPtr MacWindow()
    {
        var app = objc_msgSend(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
        var w = objc_msgSend(app, sel_registerName("mainWindow"));
        if (w != IntPtr.Zero) return w;
        var windows = objc_msgSend(app, sel_registerName("windows")); // not focused yet (Start Fullscreen)
        return objc_msgSend_ulong(windows, sel_registerName("count")) > 0
            ? objc_msgSend(windows, sel_registerName("objectAtIndex:"), 0UL)
            : IntPtr.Zero;
    }

    private static bool MacIsFullScreen()
    {
        var w = MacWindow();
        return w != IntPtr.Zero && (objc_msgSend_ulong(w, sel_registerName("styleMask")) & NSWindowStyleMaskFullScreen) != 0;
    }

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC, CharSet = CharSet.Ansi, BestFitMapping = false)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC, CharSet = CharSet.Ansi, BestFitMapping = false)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC)] private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC)] private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, IntPtr arg);
    [DllImport(ObjC)] private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, ulong arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern ulong objc_msgSend_ulong(IntPtr receiver, IntPtr selector);

    // ---- Linux: Photino's native call ----

    private static readonly FieldInfo? NativeInstance =
        typeof(PhotinoWindow).GetField("_nativeInstance", BindingFlags.NonPublic | BindingFlags.Instance);

    [DllImport("Photino.Native", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Photino_SetFullScreen(IntPtr instance, [MarshalAs(UnmanagedType.I1)] bool fullScreen);

    private const int GWL_STYLE = -16;
    private const long WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const int SW_RESTORE = 9;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004,
        SWP_FRAMECHANGED = 0x0020, SWP_SHOWWINDOW = 0x0040, SWP_NOOWNERZORDER = 0x0200;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length, flags, showCmd;
        public POINT ptMinPosition, ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
