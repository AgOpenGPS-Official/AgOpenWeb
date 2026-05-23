// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

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
using AgValoniaGPS.Android.Services;
using AgValoniaGPS.Models;
using AgValoniaGPS.Services;
using AgValoniaGPS.Services.Interfaces;

namespace AgValoniaGPS.Android.Views;

/// <summary>
/// Android MainView with ViewModel - wires up map control to ViewModel commands
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

        Task.Run(() =>
        {
            try
            {
                var configService = App.Services.GetRequiredService<IConfigurationService>();
                configService.SaveAppSettings();

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
                    if (args.PixelsAlreadyLoaded)
                    {
                        _mapControl.MarkCoverageDirty();
                    }
                    else if (args.IsFullReload)
                    {
                        _mapControl.ClearCoveragePixels();
                        _mapControl.MarkCoverageFullRebuildNeeded();
                    }
                    else
                        _mapControl.MarkCoverageDirty();
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

        if (_viewModel.CurrentABCreationMode == ABCreationMode.DriveAB)
        {
            _viewModel.SetABPointCommand?.Execute(null);
        }
        else if (_viewModel.CurrentABCreationMode == ABCreationMode.DrawAB)
        {
            var mapPosition = new Position
            {
                Easting = e.Easting,
                Northing = e.Northing
            };
            _viewModel.SetABPointCommand?.Execute(mapPosition);
        }
        else if (_viewModel.CurrentABCreationMode == ABCreationMode.Curve)
        {
            _viewModel.SetABPointCommand?.Execute(null);
        }
        else if (_viewModel.CurrentABCreationMode == ABCreationMode.DrawCurve)
        {
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
        UpdateActiveTrack();
    }

    private void UpdateActiveTrack()
    {
        if (_mapControl != null && _viewModel != null)
        {
            var activeTrack = _viewModel.SavedTracks.FirstOrDefault(t => t.IsActive);
            _mapControl.SetActiveTrack(activeTrack);
        }
    }

    private void SyncInitialPositions()
    {
        if (_mapControl == null || _viewModel == null) return;

        double headingRadians = _viewModel.Heading * Math.PI / 180.0;
        _mapControl.SetVehiclePosition(_viewModel.Easting, _viewModel.Northing, headingRadians);

        var configStore = AgValoniaGPS.Models.Configuration.ConfigurationStore.Instance;
        double toolWidth = _viewModel.ToolWidth > 0 ? _viewModel.ToolWidth : configStore.ActualToolWidth;

        double toolX = _viewModel.ToolEasting;
        double toolY = _viewModel.ToolNorthing;
        double toolHeading = _viewModel.ToolHeadingRadians;
        double hitchX = _viewModel.HitchEasting;
        double hitchY = _viewModel.HitchNorthing;

        if (Math.Abs(toolX) < 0.001 && Math.Abs(toolY) < 0.001)
        {
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

        _mapControl.SetToolPosition(toolX, toolY, toolHeading, toolWidth, hitchX, hitchY);
        _mapControl.SetSectionStates(
            _viewModel.GetSectionStates(),
            _viewModel.GetSectionWidths(),
            _viewModel.NumSections,
            _viewModel.GetSectionButtonStates());
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_mapControl != null && _viewModel != null)
        {
            if (e.PropertyName?.StartsWith("Section") == true &&
                     (e.PropertyName.EndsWith("Active") || e.PropertyName.EndsWith("ColorCode")))
            {
                _mapControl.SetSectionStates(
                    _viewModel.GetSectionStates(),
                    _viewModel.GetSectionWidths(),
                    _viewModel.NumSections,
                    _viewModel.GetSectionButtonStates());
            }
            else if (e.PropertyName == nameof(MainViewModel.EnableABClickSelection))
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
}
