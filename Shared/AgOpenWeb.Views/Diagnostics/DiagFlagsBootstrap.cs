// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using Avalonia;
using Avalonia.Media;
using AgOpenWeb.Models.Diagnostics;

namespace AgOpenWeb.Views.Diagnostics;

/// <summary>
/// Avalonia-specific diagnostic bootstrap. Reads flags from
/// <see cref="DiagFlags"/> and applies UI-framework-level overrides
/// (resource swaps, etc.). Called once from each platform's App.
/// </summary>
public static class DiagFlagsBootstrap
{
    public static void ApplyAtStartup(Application app)
    {
        DiagFlags.LogInitState();

        if (DiagFlags.PanelsOpaque)
        {
            var opaque = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
            app.Resources["SystemControlBackgroundChromeMediumBrush"] = opaque;
            Console.WriteLine(
                "[DiagFlags] panels_opaque: overrode SystemControlBackgroundChromeMediumBrush with solid #2A2A2A");
        }
    }
}
