// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;

namespace AgOpenWeb.Services;

/// <summary>
/// Where the desktop launcher's window goes (#571), as AgOpenGPS keeps it (setWindow_Size,
/// setWindow_Location, setWindow_Maximized): the restored size and place and whether it was
/// maximized, saved when the window closes and used at the next start. The restored bounds are
/// what Restore (the middle title-bar button) returns to; with none set the window collapsed to
/// its title bar. No Photino here, so it can be tested; the launcher does the wiring.
/// </summary>
public sealed record WindowPlacement(int Left, int Top, int Width, int Height, bool Maximized)
{
    public const int DefaultWidth = 1280, DefaultHeight = 800;
    public const int MinWidth = 640, MinHeight = 480;

    /// <summary>How much of the title bar (its top 40 px) must be on a screen to grab it: else the default.</summary>
    private const int TitleBarHeight = 40, MinVisibleWidth = 120, MinVisibleHeight = 20;

    public const string FileName = "window.json";

    public Rectangle Bounds => new(Left, Top, Width, Height);

    /// <summary>
    /// The first start: 1280 × 800 (or the screen, if smaller) centred on the main screen's work
    /// area, maximized, as the launcher has always opened.
    /// </summary>
    public static WindowPlacement Default(Rectangle workArea)
    {
        int w = Math.Min(DefaultWidth, workArea.Width), h = Math.Min(DefaultHeight, workArea.Height);
        return new WindowPlacement(
            workArea.Left + (workArea.Width - w) / 2,
            workArea.Top + (workArea.Height - h) / 2,
            w, h, Maximized: true);
    }

    /// <summary>
    /// The placement to open with: the saved one when its title bar is on one of the screens
    /// (sized down to that screen if it has shrunk), else the default on the main screen with the
    /// saved maximized state (a monitor unplugged, a resolution changed). No screens known: the
    /// saved one as it is, or the default on a 1920 × 1080 screen.
    /// </summary>
    public static WindowPlacement Fit(WindowPlacement? saved, IReadOnlyList<Rectangle> workAreas)
    {
        var main = workAreas.Count > 0 ? workAreas[0] : new Rectangle(0, 0, 1920, 1080);
        if (saved == null || saved.Width < MinWidth || saved.Height < MinHeight)
            return saved == null ? Default(main) : Default(main) with { Maximized = saved.Maximized };
        if (workAreas.Count == 0) return saved;

        var titleBar = new Rectangle(saved.Left, saved.Top, saved.Width, TitleBarHeight);
        foreach (var area in workAreas)
        {
            var seen = Rectangle.Intersect(titleBar, area);
            if (seen.Width < MinVisibleWidth || seen.Height < MinVisibleHeight) continue;
            int w = Math.Min(saved.Width, area.Width), h = Math.Min(saved.Height, area.Height);
            int left = Math.Clamp(saved.Left, area.Left, area.Right - w);
            int top = Math.Clamp(saved.Top, area.Top, area.Bottom - h);
            return saved with { Left = left, Top = top, Width = w, Height = h };
        }
        return Default(main) with { Maximized = saved.Maximized };
    }

    /// <summary>The saved placement in <paramref name="directory"/>, or null (none yet, unreadable).</summary>
    public static WindowPlacement? Load(string directory)
    {
        try
        {
            var path = Path.Combine(directory, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Saves it in <paramref name="directory"/>; a failure only costs the next start its place.</summary>
    public void Save(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, FileName), JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[launcher] Window placement not saved: {ex.Message}");
        }
    }
}
