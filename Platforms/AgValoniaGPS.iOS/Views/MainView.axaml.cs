// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
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
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using AgValoniaGPS.ViewModels;
using AgValoniaGPS.Views.Controls;
using AgValoniaGPS.iOS.Services;
using AgValoniaGPS.Models;
using AgValoniaGPS.Services;
using AgValoniaGPS.Services.Interfaces;

namespace AgValoniaGPS.iOS.Views;

/// <summary>
/// iOS MainView with ViewModel - wires up map control to ViewModel commands
/// </summary>
public partial class MainView : UserControl
{
    private SkiaMapControl? _mapControl;
    private MainViewModel? _viewModel;

    public MainView()
    {
        System.Diagnostics.Debug.WriteLine("[MainView] Constructor starting...");
        InitializeComponent();
        System.Diagnostics.Debug.WriteLine("[MainView] InitializeComponent completed.");

        _mapControl = this.FindControl<SkiaMapControl>("MapControl");

        WireChartPanelDrag("SteerChartPanel");
        WireChartPanelDrag("HeadingChartPanel");
        WireChartPanelDrag("XTEChartPanel");
        // SimulatorBarPanel is a fixed layout child of the bottom stack — no drag/positioning needed.

        this.PropertyChanged += MainView_PropertyChanged;
        this.Unloaded += MainView_Unloaded;
    }

    private void MainView_Unloaded(object? sender, RoutedEventArgs e)
    {
        if (App.Services == null) return;

        if (_viewModel != null)
        {
            var display = AgValoniaGPS.Models.Configuration.ConfigurationStore.Instance.Display;
            display.GridVisible = _viewModel.IsGridOn;
        }

        // Save on background thread — never block the UI thread on app close
        Task.Run(() =>
        {
            try
            {
                var configService = App.Services.GetRequiredService<IConfigurationService>();
                configService.SaveAppSettings();
                App.Services.GetRequiredService<IPersistentStateService>().Save();

                var fieldService = App.Services.GetRequiredService<IFieldService>();
                var coverageService = App.Services.GetRequiredService<ICoverageMapService>();
                if (fieldService.ActiveField != null && !string.IsNullOrEmpty(fieldService.ActiveField.DirectoryPath))
                {
                    coverageService.SaveToFile(fieldService.ActiveField.DirectoryPath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Save] Error saving on close: {ex.Message}");
            }
        });
    }

    private void MainView_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // Panels are now anchored via alignment - no constraint logic needed
    }

    public MainView(MainViewModel viewModel, MapService mapService, ICoverageMapService coverageService) : this()
    {
        System.Diagnostics.Debug.WriteLine("[MainView] Setting DataContext to MainViewModel...");
        DataContext = viewModel;
        _viewModel = viewModel;

        if (_mapControl != null)
        {
            mapService.RegisterMapControl(_mapControl);

            viewModel.ScreenshotProvider = () =>
                AgValoniaGPS.Views.ScreenshotHelper.CaptureScreenshotPng(this);

            _mapControl.MapClicked += OnMapClicked;
            _mapControl.UserPanned += () => _viewModel?.OnUserPan();

            coverageService.CoverageUpdated += (sender, args) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    CoverageRefreshDispatcher.Apply(_mapControl, args.IsFullReload);
                    _viewModel?.RefreshCoverageStatistics();
                });
            };

            _mapControl.SetCoverageBitmapProviders(
                coverageService.GetCoverageBounds,
                (cellSize, minE, maxE, minN, maxN) => coverageService.GetCoverageBitmapCells(cellSize, minE, maxE, minN, maxN),
                coverageService.GetNewCoverageBitmapCells);
            _mapControl.MarkCoverageDirty();

            _mapControl.FpsUpdated += fps =>
            {
                if (_viewModel != null)
                    _viewModel.CurrentFps = fps;
            };
        }

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.SavedTracks.CollectionChanged += SavedTracks_CollectionChanged;
        UpdateActiveTrack();

        // Push initial state to the map control. VM.LoadSettings ran before
        // MapControl was registered, so map-targeted calls were no-ops then.
        // Re-push here so the camera mode and pitch match the loaded settings.
        if (_mapControl != null)
        {
            _mapControl.Set3DMode(!viewModel.Is2DMode);
            double pitchRadians = (90.0 + viewModel.CameraPitch) * Math.PI / 180.0;
            _mapControl.SetPitchAbsolute(pitchRadians);
            _mapControl.CameraFollowMode = viewModel.CameraMode switch
            {
                AgValoniaGPS.Models.CameraMode.NorthUp => 0,
                AgValoniaGPS.Models.CameraMode.HeadingUp => 1,
                AgValoniaGPS.Models.CameraMode.Free => 2,
                _ => 3,
            };
        }

        // Fire missing OnPropertyChanged for properties ConfigurationService
        // loaded directly into the backing field.
        viewModel.NotifyDisplayLabelsAfterStartup();

        System.Diagnostics.Debug.WriteLine("[MainView] DataContext set.");
    }

    private void OnMapClicked(object? sender, MapClickEventArgs e)
    {
        if (_viewModel == null) return;

        if (_viewModel.IsPlaceFlagOnClickMode)
        {
            _viewModel.PlaceFlagAtWorldPosition(e.Easting, e.Northing);
            return;
        }

        // For DriveAB mode, we use current GPS position (not the clicked position)
        // For DrawAB mode, we use the clicked map position
        // For Curve mode, tap finishes recording
        if (_viewModel.CurrentABCreationMode == ABCreationMode.DriveAB)
        {
            // In DriveAB mode, any tap triggers setting the point at current GPS position
            _viewModel.SetABPointCommand?.Execute(null);
        }
        else if (_viewModel.CurrentABCreationMode == ABCreationMode.DrawAB)
        {
            // In DrawAB mode, pass the clicked map coordinates
            var mapPosition = new Position
            {
                Easting = e.Easting,
                Northing = e.Northing
            };
            _viewModel.SetABPointCommand?.Execute(mapPosition);
        }
        else if (_viewModel.CurrentABCreationMode == ABCreationMode.Curve)
        {
            // In Curve mode, tap finishes recording
            _viewModel.SetABPointCommand?.Execute(null);
        }
        else if (_viewModel.CurrentABCreationMode == ABCreationMode.DrawCurve)
        {
            // In DrawCurve mode, pass the clicked map coordinates to add a point
            var mapPosition = new Position
            {
                Easting = e.Easting,
                Northing = e.Northing
            };
            _viewModel.SetABPointCommand?.Execute(mapPosition);
        }
    }

    private void SavedTracks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // When a new track is added, show the most recently active one
        UpdateActiveTrack();
    }

    private void UpdateActiveTrack()
    {
        if (_mapControl != null && _viewModel != null)
        {
            // Only show track on map if explicitly active (no fallback)
            var activeTrack = _viewModel.SavedTracks.FirstOrDefault(t => t.IsActive);
            _mapControl.SetActiveTrack(activeTrack);
        }
    }

    private void SyncInitialPositions()
    {
        if (_mapControl == null || _viewModel == null) return;

        // Sync vehicle position
        double headingRadians = _viewModel.Heading * Math.PI / 180.0;
        _mapControl.SetVehiclePosition(_viewModel.Easting, _viewModel.Northing, headingRadians);

        // Get tool config (ViewModel's tool values are 0 until first simulator update)
        var configStore = AgValoniaGPS.Models.Configuration.ConfigurationStore.Instance;
        double toolWidth = _viewModel.ToolWidth > 0 ? _viewModel.ToolWidth : configStore.ActualToolWidth;

        // Calculate tool position from vehicle position if not yet set
        double toolX = _viewModel.ToolEasting;
        double toolY = _viewModel.ToolNorthing;
        double toolHeading = _viewModel.ToolHeadingRadians;
        double hitchX = _viewModel.HitchEasting;
        double hitchY = _viewModel.HitchNorthing;

        if (Math.Abs(toolX) < 0.001 && Math.Abs(toolY) < 0.001)
        {
            // Tool position not yet calculated - compute from vehicle position
            var tool = configStore.Tool;
            double hitchDist = tool.IsToolRearFixed || tool.IsToolTrailing || tool.IsToolTBT
                ? -Math.Abs(tool.HitchLength)
                : Math.Abs(tool.HitchLength);

            hitchX = _viewModel.Easting + Math.Sin(headingRadians) * hitchDist;
            hitchY = _viewModel.Northing + Math.Cos(headingRadians) * hitchDist;
            toolX = hitchX;
            toolY = hitchY;
            toolHeading = headingRadians;
        }

        // Sync tool position and section states
        _mapControl.SetToolPosition(toolX, toolY, toolHeading, toolWidth, hitchX, hitchY);
        _mapControl.SetSectionStates(
            _viewModel.GetSectionStates(),
            _viewModel.GetSectionWidths(),
            _viewModel.NumSections,
            _viewModel.GetSectionButtonStates());
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Vehicle/tool/hitch positions are pushed atomically via
        // _mapService.SetAllPositions(...) at the end of ApplyGpsCycleResult,
        // so we must NOT also push them per-property here. Doing both caused
        // visible tool/hitch oscillation: SetVehiclePosition moves the camera
        // and triggers a render with stale tool state, then SetAllPositions
        // snaps the tool forward — once per GPS tick.
        if (_mapControl != null && _viewModel != null)
        {
            // Section state/layout is pushed to the map directly by the
            // ViewModel (MainViewModel.UpdateSectionStates) and every GPS cycle
            // by ApplyGpsCycleResult, so there is no per-property section bridge.
            if (e.PropertyName == nameof(MainViewModel.EnableABClickSelection))
            {
                _mapControl.EnableClickSelection = _viewModel.EnableABClickSelection;
            }
            else if (e.PropertyName == nameof(MainViewModel.Is2DMode))
            {
                _mapControl.Set3DMode(!_viewModel.Is2DMode);
            }
            else if (e.PropertyName == nameof(MainViewModel.CameraPitch))
            {
                double pitchRadians = (90.0 + _viewModel.CameraPitch) * Math.PI / 180.0;
                _mapControl.SetPitchAbsolute(pitchRadians);
            }
            else if (e.PropertyName == nameof(MainViewModel.IsSimulatorEnabled))
            {
                if (_viewModel.IsSimulatorEnabled)
                {
                    _ = Task.Delay(300).ContinueWith(_ =>
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(SyncInitialPositions);
                    });
                }
            }
            else if (e.PropertyName == nameof(MainViewModel.PendingPointA))
            {
                _mapControl.SetPendingPointA(_viewModel.PendingPointA);
            }
            else if (e.PropertyName == nameof(MainViewModel.CrossTrackError))
            {
                bool hasGuidance = _viewModel.HasActiveTrack
                    || _viewModel.IsContourModeOn
                    || _viewModel.State.RecordedPath.IsDrivingRecordedPath;
                LightBarPanel?.Update(
                    _viewModel.CrossTrackError / 100.0,
                    _viewModel.SimulatorSteerAngle,
                    hasGuidance, false);
            }
        }
    }

    private void WireChartPanelDrag(string panelName)
    {
        var panel = this.FindControl<Control>(panelName);
        if (panel == null) return;

        // Chart panels expose DragMoved as an event via reflection-free duck typing
        var dragMovedEvent = panel.GetType().GetEvent("DragMoved");
        if (dragMovedEvent != null)
        {
            dragMovedEvent.AddEventHandler(panel, new EventHandler<Vector>((_, delta) =>
            {
                double newLeft = Canvas.GetLeft(panel) + delta.X;
                double newTop = Canvas.GetTop(panel) + delta.Y;
                double maxLeft = Math.Max(0, Bounds.Width - panel.Bounds.Width);
                double maxTop = Math.Max(0, Bounds.Height - panel.Bounds.Height);
                Canvas.SetLeft(panel, Math.Clamp(newLeft, 0, maxLeft));
                Canvas.SetTop(panel, Math.Clamp(newTop, 0, maxTop));
            }));
        }
    }

    // Section control is now anchored (no drag needed)

}
