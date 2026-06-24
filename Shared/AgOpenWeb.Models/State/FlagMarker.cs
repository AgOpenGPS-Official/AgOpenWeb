namespace AgOpenWeb.Models.State;

/// <summary>
/// A field flag reduced to what the map needs to draw it: field-local position
/// (metres) plus the display colour as a hex string. Built from <c>Flag</c> by the
/// VM's <c>UpdateFlagsOnMap</c> and stored on <see cref="FieldState.Flags"/> so the
/// View-free web-UI projector can read flags without touching the ViewModel.
/// </summary>
public readonly record struct FlagMarker(double Easting, double Northing, string ColorHex, string Name);
