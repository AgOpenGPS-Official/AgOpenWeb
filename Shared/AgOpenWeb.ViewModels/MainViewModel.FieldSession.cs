// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Job;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// The field session (Plans/MAINVIEWMODEL_REFACTOR_PLAN.md, item 6): what opening a field does and
/// what closing it does, each in one declared order with the reason for each place. The steps are
/// in MainViewModel.FieldSteps.cs, each domain's open beside its close. OpenFieldCoreAsync runs the
/// open steps inside its busy overlay and error handling; ClearFieldState runs the close steps,
/// after CloseFieldAsync has saved the field.
/// </summary>
public partial class MainViewModel
{
    /// <summary>What one opening of a field passes from step to step.</summary>
    private sealed class FieldOpening(string fieldPath, string fieldName)
    {
        public string FieldPath { get; } = fieldPath;
        public string FieldName { get; } = fieldName;
        /// <summary>The field as read from its folder; null when it could not be read.</summary>
        public Field? LoadedField { get; set; }
        public Boundary? Boundary { get; set; }
        /// <summary>The field made active (the one read, or a new one when it could not be).</summary>
        public Field Field { get; set; } = null!;
        public Job? ActiveJob { get; set; }
    }

    private sealed record OpenStep(string Name, string Why, Func<FieldOpening, Task> Run);
    private sealed record CloseStep(string Name, string Why, Action Run);

    private IReadOnlyList<OpenStep>? _openSteps;
    private IReadOnlyList<CloseStep>? _closeSteps;

    /// <summary>Opening a field, in this order. The previous field is closed before these run.</summary>
    private IReadOnlyList<OpenStep> OpenSteps => _openSteps ??= new OpenStep[]
    {
        new("busy", "the overlay is drawn before the field is read", OpenShowBusy),
        new("open", "the field counts as open for everything loaded below", OpenMarkFieldOpen),
        new("field file", "the origin, before anything placed by it", OpenLoadField),
        new("boundary", "needs the field read", OpenBoundary),
        new("imagery", "placed over the boundary", OpenImagery),
        new("active field", "the field read, with its origin and boundary, made the active one: the close-save writes back what was read", OpenActiveField),
        new("headland", "needs the active field", OpenHeadland),
        new("tracks", "needs the active field", OpenTracks),
        new("recorded path", "the field's path in use", OpenRecordedPath),
        new("flags", "the field's own, none carried over (#107)", OpenFlags),
        new("contours", "the field's strips (#110)", OpenContours),
        new("job", "before any coverage: coverage lives in the job's folder", OpenJob),
        new("coverage", "the job's", OpenCoverage),
        new("coverage autosave", "once coverage is loaded", OpenCoverageAutosave),
        new("tram lines", "built from the systems and the track loaded above (checklist 6.10)", OpenTram),
        new("NTRIP", "the field's own profile, when it has one", OpenNtrip),
        new("elevation log", "on or off as set", OpenElevationLog),
        new("last field", "remembered for the next start", OpenRememberField),
        new("simulator", "moved to the field's origin", OpenSimulator),
        new("settle", "GPS events through before the field is announced", OpenSettle),
        new("announce", "the field is loaded: subscribers told, the operator told", OpenAnnounce),
    };

    /// <summary>Closing a field, in this order, after CloseFieldAsync has saved it.</summary>
    private IReadOnlyList<CloseStep> CloseSteps => _closeSteps ??= new CloseStep[]
    {
        new("coverage autosave", "first: a tick can't fire in the middle of the close", CloseCoverageAutosave),
        new("AutoSteer", "off before the track, boundary and U-turn go: the module kept the last steer angle", CloseAutoSteer),
        new("recorded path", "before the sections: a replay left driving puts the Auto master back on (#956)", CloseRecordedPath),
        new("sections", "off, those on by hand too: with no field there is no section bar (#749)", CloseSections),
        new("recordings", "a boundary or line being recorded belongs to this field (#625)", CloseRecordings),
        new("contours", "saved and exported while the field is still there (#110)", CloseContours),
        new("closed", "the field no longer counts as open for what is cleared below", CloseMarkFieldClosed),
        new("boundary", "", CloseBoundary),
        new("headland", "", CloseHeadland),
        new("imagery", "or the closed field's picture stays on screen (#309)", CloseImagery),
        new("tracks", "", CloseTracks),
        new("U-turn", "", CloseYouTurn),
        new("tram lines", "or the map draws them until the next open (#310)", CloseTram),
        new("flags", "not saved: the field is already closed (#107)", CloseFlags),
        new("coverage", "", CloseCoverage),
        new("active field", "last: the field service lets go of the field", CloseActiveField),
    };
}
