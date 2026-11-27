// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;

using AgOpenWeb.Models.State;
using CommunityToolkit.Mvvm.Input;

using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// Simulator commands - GPS simulation controls.
/// </summary>
public partial class MainViewModel
{
    private void InitializeSimulatorCommands()
    {
        ToggleSimulatorPanelCommand = new RelayCommand(() =>
        {
            IsSimulatorPanelVisible = !IsSimulatorPanelVisible;
        });

        ResetSimulatorCommand = new RelayCommand(() =>
        {
            _simulatorService.Reset();
            SimulatorSteerAngle = 0;
            SimulatorSpeedKph = 0; // start stationary at the reset position (property
                                   // setter mirrors State + refreshes the display)
            StatusMessage = "Simulator Reset";
        });

        ResetSteerAngleCommand = new RelayCommand(() =>
        {
            SimulatorSteerAngle = 0;
            StatusMessage = "Steer Angle Reset to 0";
        });

        SimulatorStopCommand = new RelayCommand(() =>
        {
            _simulatorService.IsAcceleratingForward = false;
            _simulatorService.IsAcceleratingBackward = false;
            _simulatorService.StepDistance = 0;
            _simulatorSpeedKph = 0;
            State.Simulator.SpeedKph = 0; // mirror for the web-UI projector (this path
                                          // bypasses the SimulatorSpeedKph setter)
            OnPropertyChanged(nameof(SimulatorSpeedKph));
            OnPropertyChanged(nameof(SimulatorSpeedDisplay));
            StatusMessage = "Sim: Stopped";
        });

        SimulatorReverseDirectionCommand = new RelayCommand(() =>
        {
            var newHeading = _simulatorService.HeadingRadians + Math.PI;
            if (newHeading > Math.PI * 2)
                newHeading -= Math.PI * 2;
            _simulatorService.SetHeading(newHeading);
            StatusMessage = "Sim: Direction Reversed";
        });

        SimulatorSteerLeftCommand = new RelayCommand(() =>
        {
            SimulatorSteerAngle -= 0.5;
            StatusMessage = $"Steer: {SimulatorSteerAngle:F1}";
        });

        SimulatorSteerRightCommand = new RelayCommand(() =>
        {
            SimulatorSteerAngle += 0.5;
            StatusMessage = $"Steer: {SimulatorSteerAngle:F1}";
        });

        SimulatorSpeedDownCommand = new RelayCommand(() =>
        {
            SimulatorSpeedKph -= 0.5;
            StatusMessage = $"Speed: {SimulatorSpeedKph:F1} kph";
        });

        SimulatorSpeedUpCommand = new RelayCommand(() =>
        {
            SimulatorSpeedKph += 0.5;
            StatusMessage = $"Speed: {SimulatorSpeedKph:F1} kph";
        });
    }
}
