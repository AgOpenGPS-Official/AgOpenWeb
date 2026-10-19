// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenWeb.Models.State;

/// <summary>
/// Central application state container.
/// Single source of truth for ALL runtime state.
/// Singleton, Observable, Injectable.
/// </summary>
public class ApplicationState : ObservableObject
{
    private static ApplicationState? _instance;

    /// <summary>
    /// Singleton instance for static access (use DI when possible)
    /// </summary>
    public static ApplicationState Instance => _instance ??= new ApplicationState();

    /// <summary>
    /// Create a new ApplicationState (primarily for DI registration)
    /// </summary>
    public ApplicationState()
    {
        _instance = this;
    }

    // Domain state objects
    public VehicleState Vehicle { get; } = new();
    public GuidanceState Guidance { get; } = new();
    public ConnectionState Connections { get; } = new();
    public FieldState Field { get; } = new();
    public YouTurnState YouTurn { get; } = new();
    public OperationState Operation { get; } = new(); // right-nav toolbar mode mirror (web-UI)
    public SimulatorState Simulator { get; } = new(); // sim-panel readout mirror (web-UI)
    public FieldToolsState FieldTools { get; } = new(); // bottom-nav toggle mirror (web-UI)
    public AgShareState AgShare { get; } = new(); // remote AgShare action status/results (web-UI)
    public string BugReportStatus { get; set; } = string.Empty; // remote bug-report submit result (web-UI)
    public RecordedPathState RecordedPath { get; } = new();
    public BoundaryRecState BoundaryRec { get; } = new();
    public UIState UI { get; } = new();

    // Global events
    public event EventHandler? StateReset;

    /// <summary>
    /// Reset all state (e.g., when closing a field)
    /// </summary>
    public void Reset()
    {
        Vehicle.Reset();
        Guidance.Reset();
        YouTurn.Reset();
        RecordedPath.Reset();
        BoundaryRec.Reset();
        // Field and Connections typically persist across field changes
        StateReset?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Full reset including field (e.g., app restart)
    /// </summary>
    public void ResetAll()
    {
        Reset();
        Field.Reset();
        Connections.Reset();
    }
}
