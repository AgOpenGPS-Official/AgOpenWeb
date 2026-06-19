// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
// Licensed under GNU GPL v3. See LICENSE.md.

using AgValoniaGPS.Models.Tram;

namespace AgValoniaGPS.ViewModels;

/// <summary>
/// MainViewModel partial: web/remote-client entry points for the Field Builder Tram tab
/// (Phase MT, Field Builder stage 3). Mirrors the native <c>FieldBuilderDialogPanel</c> tram
/// CRUD + edit handlers exactly — the browser only sends field edits; the host owns the tram
/// systems (<c>ConfigStore.Tram.Systems</c>), the line generation (<c>BuildTramLinesCommand</c>),
/// and persistence (<c>TramSystemFileService</c>).
/// </summary>
public partial class MainViewModel
{
    /// <summary>Add a tram system referencing the active track (or the boundary if none),
    /// mirroring native <c>AddTramSystem_Click</c>.</summary>
    public void RemoteAddTramSystem()
    {
        string refName = SelectedTrack?.Name ?? "Boundary";
        var sys = new TramSystem
        {
            Name = $"Tram {TramSystems.Count + 1} ({refName})",
            TramWidth = 24.0,
            PassCount = 1,
            Direction = TramDirection.Symmetric,
            Mode = TramSystemMode.TrackLine,
            ReferenceTrackName = SelectedTrack?.Name,
            ReferenceBoundaryIndex = SelectedTrack != null ? -1 : 0,
        };
        TramSystems.Add(sys);
        RebuildAndSaveTram();
    }

    /// <summary>Delete a tram system by index and rebuild.</summary>
    public void RemoteDeleteTramSystemAt(int index)
    {
        if (index < 0 || index >= TramSystems.Count) return;
        TramSystems.RemoveAt(index);
        RebuildAndSaveTram();
    }

    /// <summary>
    /// Set one field of a tram system and rebuild. Field/value mirror the native edit
    /// handlers: ref | width | offset | passes | enabled | mode | dir.
    /// </summary>
    public void RemoteSetTramField(int index, string field, string value)
    {
        if (index < 0 || index >= TramSystems.Count) return;
        var sys = TramSystems[index];
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        switch (field)
        {
            case "ref":
                if (value == "(Boundary)")
                {
                    sys.ReferenceTrackName = null;
                    sys.ReferenceBoundaryIndex = 0;
                }
                else
                {
                    sys.ReferenceTrackName = value;
                    sys.ReferenceBoundaryIndex = -1;
                }
                sys.Name = $"Tram {index + 1} ({(value == "(Boundary)" ? "Boundary" : value)})";
                break;
            case "width":
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, inv, out var w) && w > 0)
                    sys.TramWidth = w;
                break;
            case "offset":
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, inv, out var o))
                    sys.Offset = o;
                break;
            case "passes":
                if (int.TryParse(value, out var p))
                    sys.PassCount = System.Math.Max(0, p);
                break;
            case "enabled":
                sys.IsEnabled = value == "1";
                break;
            case "mode":
                sys.Mode = value == "1" ? TramSystemMode.Edge : TramSystemMode.TrackLine;
                break;
            case "dir":
                sys.Direction = value switch
                {
                    "1" => TramDirection.Left,
                    "2" => TramDirection.Right,
                    _ => TramDirection.Symmetric,
                };
                break;
            default:
                return;
        }
        RebuildAndSaveTram();
    }

    /// <summary>Rebuild the tram lines and persist the systems to the field directory
    /// (deleting the file when no systems remain). Mirrors native <c>RebuildTramLines</c>.</summary>
    private void RebuildAndSaveTram()
    {
        BuildTramLinesCommand?.Execute(null);
        OnPropertyChanged(nameof(TramLineCountDisplay));

        var field = State.Field.ActiveField;
        if (field == null) return;
        try
        {
            if (TramSystems.Count > 0)
                Services.Tram.TramSystemFileService.Save(field.DirectoryPath, TramSystems);
            else
            {
                var path = System.IO.Path.Combine(field.DirectoryPath, "TramSystems.json");
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
        }
        catch { /* save failure is non-critical */ }
    }
}
