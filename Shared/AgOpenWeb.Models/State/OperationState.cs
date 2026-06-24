namespace AgOpenWeb.Models.State;

/// <summary>
/// Operational-mode mirror for the right-nav toolbar. These flags are owned by the
/// MainViewModel (the toggle commands set them); they are mirrored here in lockstep
/// so View-free consumers — notably the web-UI projector — can read the operational
/// state without reaching into the ViewModel. (Projection pattern: the VM pushes,
/// state mirrors, the projector reads.)
/// </summary>
public sealed class OperationState
{
    /// <summary>Section master in AUTO (all sections follow coverage/boundary).</summary>
    public bool IsSectionAutoMaster { get; set; }

    /// <summary>Section master in MANUAL (all sections forced on).</summary>
    public bool IsSectionManualAll { get; set; }

    /// <summary>Contour/track mode toggle is on (the UI button — NOT the pipeline's
    /// GuidanceState.IsContourMode "currently driving a contour" flag).</summary>
    public bool IsContourOn { get; set; }

    /// <summary>Auto U-turn (YouTurn) arming is enabled.</summary>
    public bool IsYouTurnEnabled { get; set; }

    /// <summary>AutoSteer is engaged (the UI toggle — NOT the hardware-module flag
    /// on ConnectionState).</summary>
    public bool IsAutoSteerEngaged { get; set; }

    /// <summary>A track is available to steer to (else the AutoSteer button greys).</summary>
    public bool IsAutoSteerAvailable { get; set; }
}
