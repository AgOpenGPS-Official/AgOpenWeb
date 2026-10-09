using AgOpenWeb.Models.Pipeline;
using NativeTrack = AgOpenWeb.Models.Track.Track;
namespace AgOpenWeb.Services.SavedRows;
/// <summary>Cycle-worker-owned selector. Finite actual paths, bidirectional capture, hysteresis.</summary>
public sealed class IndividualRowsGuidance
{
    private readonly SavedRowSelector selector = new();
    private IndividualRowsRequest? request;
    private SavedRowTracks? data;
    public void Configure(IndividualRowsRequest value)
    {
        request = value;
        data = new SavedRowTracks { AutoSelect=value.AutoSelect, CaptureDistanceMetres=value.CaptureDistanceMetres,
            Rows=value.Rows.Select(t=>new SavedRowTrack {Points=t.Points.Select(p=>new SavedRowPoint(p.Easting,p.Northing,p.Heading)).ToList()}).ToList() };
        selector.Reset(value.Selected);
    }
    public NativeTrack? Update(double e, double n, double heading, double seconds, bool positionValid)
    {
        if (!positionValid || request==null || data==null) {selector.Reset(selector.Selected);return null;}
        var match=selector.Update(data,e,n,heading,seconds);
        return match==null ? null : request.Rows[match.RowIndex];
    }
}
