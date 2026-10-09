namespace AgOpenWeb.Models.Pipeline;
/// <summary>Immutable ownership transfer. Geometry and options must not be modified after enqueueing; native active flags remain UI-owned.</summary>
public sealed record IndividualRowsRequest(IReadOnlyList<Track.Track> Rows, bool AutoSelect, double CaptureDistanceMetres, int Selected);
