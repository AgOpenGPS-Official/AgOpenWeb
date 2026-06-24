// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using Avalonia.Controls;

namespace AgOpenWeb.Views.Controls.Dialogs.Configuration;

/// <summary>
/// Shared numeric / text / color-picker input overlays for the configuration
/// dialogs. Inherits its ConfigurationViewModel DataContext from the hosting
/// dialog; only one overlay is visible at a time.
/// </summary>
public partial class ConfigInputOverlays : UserControl
{
    public ConfigInputOverlays()
    {
        InitializeComponent();
    }
}
