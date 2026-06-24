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
using Avalonia;
using Avalonia.Controls;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace AgOpenWeb.Views.Controls.Panels;

public partial class HeadingChartPanel : UserControl
{
    private bool _configured;

    public event EventHandler<Vector>? DragMoved;

    public static IServiceProvider? ServiceProvider { get; set; }

    public HeadingChartPanel()
    {
        InitializeComponent();

        var fp = this.FindControl<FloatingPanel>("FP");
        if (fp != null)
            fp.DragMoved += (s, delta) => DragMoved?.Invoke(this, delta);

        PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(IsVisible) && e.NewValue is true && !_configured)
            {
                var chartData = ServiceProvider?.GetService<IChartDataService>();
                if (chartData != null) ConfigureChart(chartData);
            }
        };
    }

    public void ConfigureChart(IChartDataService chartData)
    {
        if (_configured) return;
        _configured = true;

        var chart = this.FindControl<ChartControl>("HeadingChart");
        if (chart == null) return;

        chart.Configure(
            title: "Heading",
            yAxisLabel: "deg",
            minY: 0,
            maxY: 360,
            gridStepY: 45,
            timeWindow: chartData.TimeWindowSeconds,
            currentTimeProvider: () => chartData.CurrentTime,
            autoScaleY: true);

        chart.AddSeries(chartData.HeadingError);
        chart.AddSeries(chartData.ImuHeading);
        chart.AddSeries(chartData.GpsHeading);
    }
}
