// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenWeb.Models.State;

/// <summary>
/// Active field state — boundaries, tracks, headlands, drift, local plane.
///
/// <para>
/// <b>Thread ownership (§0 invariant, Phase E close):</b>
/// Every property is written on the UI thread. No service writes here
/// directly — the cycle worker emits cycle-produced values (e.g.
/// <see cref="HeadlandProximityDistance"/>, <see cref="LocalPlane"/>) on
/// <c>GpsCycleResult</c> and <c>MainViewModel.ApplyGpsCycleResult</c>
/// commits them on the UI thread. See <c>Plans/threading_model.svg</c>
/// and the Phase E plan for the full contract.
/// </para>
///
/// <para>Reader / writer table:</para>
/// <list type="table">
///   <listheader><term>Property</term><description>Written by</description></listheader>
///   <item><term>ActiveField / FieldsRootDirectory</term>            <description>UI — field open/close commands</description></item>
///   <item><term>CurrentBoundary</term>                              <description>UI — boundary load/edit commands</description></item>
///   <item><term>Tracks / ActiveTrack</term>                         <description>UI — track load + selection</description></item>
///   <item><term>HeadlandLine / HeadlandDistance</term>              <description>UI — headland load/build commands</description></item>
///   <item><term>HeadlandProximityDistance / …Warning</term>         <description>UI mirror of cycle output in <c>ApplyGpsCycleResult</c></description></item>
///   <item><term>OriginLatitude / OriginLongitude</term>             <description>UI — <c>SetFieldOrigin</c></description></item>
///   <item><term>DriftEasting / DriftNorthing</term>                 <description>UI — offset-fix / reset-drift commands</description></item>
///   <item><term>LocalPlane</term>                                   <description>UI — <c>SetFieldOrigin</c>; or <c>ApplyGpsCycleResult</c> committing the cycle's <c>FirstFixLocalPlane</c> auto-create (Phase E E1)</description></item>
/// </list>
///
/// <para>The cycle worker <i>reads</i> <c>LocalPlane</c> for coord
/// conversion (<c>GpsPipelineService.ProcessCycle</c>,
/// <c>AutoSteerService.ProcessGpsBuffer</c>). All other fields the cycle
/// needs (boundary, headland, drift) are pushed via lock-protected setters
/// on <c>IGpsPipelineService</c>.</para>
/// </summary>
public class FieldState : ObservableObject
{
    private Field? _activeField;
    public Field? ActiveField
    {
        get => _activeField;
        set
        {
            SetProperty(ref _activeField, value);
            OnPropertyChanged(nameof(HasActiveField));
            OnPropertyChanged(nameof(FieldName));
        }
    }

    public bool HasActiveField => ActiveField != null;
    public string FieldName => ActiveField?.Name ?? "No Field";

    // Field directory
    private string _fieldsRootDirectory = string.Empty;
    public string FieldsRootDirectory
    {
        get => _fieldsRootDirectory;
        set => SetProperty(ref _fieldsRootDirectory, value);
    }

    // Boundary — the field's active boundary. Services (guidance, section
    // control) read this directly; it is the runtime SoT (CONFIG_STATE_AUDIT §12.1).
    private Boundary? _currentBoundary;
    public Boundary? CurrentBoundary
    {
        get => _currentBoundary;
        set => SetProperty(ref _currentBoundary, value);
    }

    // Tracks (unified Track model)
    public ObservableCollection<Track.Track> Tracks { get; } = new();

    // Field flags (markers) — render snapshot the VM's UpdateFlagsOnMap pushes here
    // so View-free consumers (the web-UI projector) can read them. Field-local metres
    // + the flag's display colour as hex.
    public IReadOnlyList<FlagMarker> Flags { get; set; } = System.Array.Empty<FlagMarker>();

    private Track.Track? _activeTrack;
    public Track.Track? ActiveTrack
    {
        get => _activeTrack;
        set => SetProperty(ref _activeTrack, value);
    }

    public bool HasActiveTrack => ActiveTrack != null;

    // Headlands
    private List<Vec3>? _headlandLine;
    public List<Vec3>? HeadlandLine
    {
        get => _headlandLine;
        set
        {
            SetProperty(ref _headlandLine, value);
            OnPropertyChanged(nameof(HasHeadland));
        }
    }

    private double _headlandDistance;
    public double HeadlandDistance
    {
        get => _headlandDistance;
        set => SetProperty(ref _headlandDistance, value);
    }

    /// <summary>
    /// Distance from tool pivot to nearest headland boundary (meters).
    /// null when no headland exists or distance not computed.
    /// </summary>
    private double? _headlandProximityDistance;
    public double? HeadlandProximityDistance
    {
        get => _headlandProximityDistance;
        set => SetProperty(ref _headlandProximityDistance, value);
    }

    /// <summary>
    /// Whether headland proximity warning is active (heading toward boundary within threshold).
    /// </summary>
    private bool _headlandProximityWarning;
    public bool HeadlandProximityWarning
    {
        get => _headlandProximityWarning;
        set => SetProperty(ref _headlandProximityWarning, value);
    }

    public bool HasHeadland => HeadlandLine != null && HeadlandLine.Count > 0;

    // Field origin (local plane reference)
    private double _originLatitude;
    public double OriginLatitude
    {
        get => _originLatitude;
        set => SetProperty(ref _originLatitude, value);
    }

    private double _originLongitude;
    public double OriginLongitude
    {
        get => _originLongitude;
        set => SetProperty(ref _originLongitude, value);
    }

    // GPS drift compensation (offset fix)
    private double _driftNorthing;
    public double DriftNorthing
    {
        get => _driftNorthing;
        set => SetProperty(ref _driftNorthing, value);
    }

    private double _driftEasting;
    public double DriftEasting
    {
        get => _driftEasting;
        set => SetProperty(ref _driftEasting, value);
    }

    // Local plane for coordinate conversion
    private LocalPlane? _localPlane;
    public LocalPlane? LocalPlane
    {
        get => _localPlane;
        set => SetProperty(ref _localPlane, value);
    }

    // Background imagery (BackPic.png) placement in field-local meters, mirrored
    // from the VM's LoadBackgroundImage (which also pushes it to the map control).
    // View-independent consumers (remote/web map) read this; null when none.
    private FieldImagery? _imagery;
    public FieldImagery? Imagery
    {
        get => _imagery;
        set => SetProperty(ref _imagery, value);
    }

    public void Reset()
    {
        ActiveField = null;
        CurrentBoundary = null;
        Tracks.Clear();
        ActiveTrack = null;
        HeadlandLine = null;
        HeadlandDistance = 0;
        HeadlandProximityDistance = null;
        HeadlandProximityWarning = false;
        DriftNorthing = DriftEasting = 0;
        OriginLatitude = OriginLongitude = 0;
        LocalPlane = null;
        Imagery = null;
    }
}

/// <summary>Background-imagery placement in field-local meters (the rectangle the
/// PNG at <paramref name="Path"/> covers).</summary>
public record FieldImagery(string Path, double MinE, double MinN, double MaxE, double MaxN);
