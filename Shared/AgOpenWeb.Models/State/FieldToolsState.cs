namespace AgOpenWeb.Models.State;

/// <summary>
/// Bottom-nav field-tools mirror for the web-UI projector (Phase 8). These toggle
/// states are owned by the MainViewModel (the IsHeadlandOn / IsAutoTrackEnabled /
/// UTurnSkipRows / IsUTurnSkipRowsEnabled setters); they are mirrored here in lockstep
/// so the View-free projector can read them without reaching into the ViewModel.
/// (Projection pattern: the VM pushes, state mirrors, the projector reads.) Two related
/// values are NOT here — they live in ConfigurationStore and the projector reads them
/// directly: tram display mode (Tram.DisplayMode) and section-control-in-headland
/// (Tool.IsHeadlandSectionControl).
/// </summary>
public sealed class FieldToolsState
{
    /// <summary>Headland zone is active (green line shown; gates section auto-off).</summary>
    public bool IsHeadlandOn { get; set; }

    /// <summary>Auto track-select (snap to the closest track) is enabled.</summary>
    public bool IsAutoTrackEnabled { get; set; }

    /// <summary>Rows to skip on each U-turn (0–9).</summary>
    public int UTurnSkipRows { get; set; }

    /// <summary>U-turn skip-rows is enabled.</summary>
    public bool IsUTurnSkipRowsEnabled { get; set; }
}
