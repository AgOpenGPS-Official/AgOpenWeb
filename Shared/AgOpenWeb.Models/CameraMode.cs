namespace AgOpenWeb.Models;

/// <summary>
/// Camera follow modes for the map view.
/// </summary>
public enum CameraMode
{
    /// <summary>Camera centered on vehicle, map oriented north-up.</summary>
    NorthUp,

    /// <summary>Camera centered on vehicle, map rotates so vehicle faces up.</summary>
    HeadingUp,

    /// <summary>Map-centric: camera holds position with smooth auto-pan when the
    /// vehicle approaches the screen edge. World stays fixed, tractor moves on it.</summary>
    Map,

    /// <summary>User has panned/rotated manually, camera does not follow vehicle.</summary>
    Free
}
