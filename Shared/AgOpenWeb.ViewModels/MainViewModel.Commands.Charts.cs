// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using CommunityToolkit.Mvvm.Input;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// Chart panel commands - toggle diagnostic chart panels.
/// </summary>
public partial class MainViewModel
{
    private void InitializeChartCommands()
    {
        // Start chart data collection immediately so users see recent
        // history when opening a chart mid-session. The overhead is
        // minimal (a few hundred data points in rolling buffers).
        _chartDataService.Start();

        ToggleSteerChartPanelCommand = new RelayCommand(() =>
        {
            IsSteerChartPanelVisible = !IsSteerChartPanelVisible;
        });

        ToggleHeadingChartPanelCommand = new RelayCommand(() =>
        {
            IsHeadingChartPanelVisible = !IsHeadingChartPanelVisible;
        });

        ToggleXTEChartPanelCommand = new RelayCommand(() =>
        {
            IsXTEChartPanelVisible = !IsXTEChartPanelVisible;
        });
    }
}
