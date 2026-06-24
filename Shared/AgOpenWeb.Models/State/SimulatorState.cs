namespace AgOpenWeb.Models.State;

/// <summary>
/// Simulator-panel mirror for the web-UI projector (Phase 6). These values are owned
/// by the MainViewModel (the IsSimulatorEnabled / SimulatorSteerAngle / SimulatorSpeedKph
/// / IsSimulatorSpeed10x setters); they are mirrored here in lockstep so the View-free
/// projector can read the simulator readouts without reaching into the ViewModel.
/// (Projection pattern: the VM pushes, state mirrors, the projector reads.)
/// </summary>
public sealed class SimulatorState
{
    /// <summary>Simulator is the active GPS source.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Steering angle in degrees (slider value, −40..40, 0.5° steps).</summary>
    public double SteerAngle { get; set; }

    /// <summary>RAW speed in kph (the slider value, BEFORE the 10× multiplier).
    /// The client applies the multiplier + unit formatting for display.</summary>
    public double SpeedKph { get; set; }

    /// <summary>10× speed multiplier toggle (for testing large fields).</summary>
    public bool Is10x { get; set; }
}
