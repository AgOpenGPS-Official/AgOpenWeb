// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using NSubstitute;
using NUnit.Framework;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Findings of the field scenario harness's first nightly run (Tests/AgOpenWeb.FieldScenarios.Tests):
/// the field's origin read as "no GPS", NaN or A = B in a track edited on the map, tracks, flags
/// and tram systems made with no field open, tram values out of range or naming no track, and
/// tracks sharing a name (tram systems and the last used track find a track by name).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class FieldScenarioFindingsTests
{
    private static readonly Wgs84 Origin = new(52.0, 5.0);
    private MainViewModelBuilder _b = null!;
    private MainViewModel _vm = null!;
    private List<string> _failures = null!;

    [SetUp]
    public void SetUp()
    {
        AgOpenWeb.Models.Configuration.ConfigurationStore.SetInstance(new AgOpenWeb.Models.Configuration.ConfigurationStore());
        var config = AgOpenWeb.Models.Configuration.ConfigurationStore.Instance;
        _b = new MainViewModelBuilder
        {
            TramLineService = new AgOpenWeb.Services.Tram.TramLineService(new AgOpenWeb.Services.TramLineOffsetService(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AgOpenWeb.Services.Tram.TramLineService>.Instance, config),
        };
        _vm = _b.Build();
        _failures = new List<string>();
        _vm.FailureReported += _failures.Add;
    }

    [TearDown]
    public void TearDown() =>
        AgOpenWeb.Models.Configuration.ConfigurationStore.SetInstance(new AgOpenWeb.Models.Configuration.ConfigurationStore());

    private void OpenField()
    {
        _vm.IsFieldOpen = true;
        _vm.State.Field.LocalPlane = new LocalPlane(Origin, new SharedFieldProperties());
    }

    // --- the field's origin is a position ---

    [Test]
    public void APlus_at_the_fields_origin_with_GPS_coming_in_makes_the_line()
    {
        OpenField();
        _b.GpsService.IsGpsDataOk().Returns(true);
        _vm.State.Vehicle.Easting = 0; // standing where the field was just created
        _vm.State.Vehicle.Northing = 0;
        _vm.State.Vehicle.Heading = 90;

        _vm.CreateAPlusLine(null);

        Assert.That(_vm.SavedTracks, Has.Count.EqualTo(1));
        Assert.That(_failures, Is.Empty);
    }

    [Test]
    public void APlus_with_no_GPS_and_no_position_is_still_refused()
    {
        OpenField();
        _b.GpsService.IsGpsDataOk().Returns(false);

        _vm.CreateAPlusLine(null);

        Assert.That(_vm.SavedTracks, Is.Empty);
        Assert.That(_failures, Has.Some.Contains("No GPS position"));
    }

    [Test]
    public void A_flag_at_the_fields_origin_with_GPS_coming_in_is_placed()
    {
        OpenField();
        _b.GpsService.IsGpsDataOk().Returns(true);

        _vm.PlaceRedFlagCommand!.Execute(null);

        Assert.That(_vm.Flags, Has.Count.EqualTo(1));
    }

    // --- a track edited on the map ---

    private void TwoPointTrack()
    {
        OpenField();
        _vm.CreateLatLonABLine(52.0, 5.0, 52.001, 5.0);
        Assert.That(_vm.SavedTracks, Has.Count.EqualTo(1));
    }

    [Test]
    public void A_track_edit_with_a_point_that_is_no_number_changes_nothing()
    {
        TwoPointTrack();
        var before = _vm.SavedTracks[0].Points.ToList();

        _vm.RemoteSaveTrackEdit(0, new List<(double, double)> { (double.NaN, double.NaN), (10, 10) });

        Assert.That(_vm.SavedTracks[0].Points, Is.EqualTo(before));
        Assert.That(_failures, Has.Some.Contains("a point has no position"));
    }

    [Test]
    public void A_track_edit_with_A_and_B_on_one_spot_changes_nothing()
    {
        TwoPointTrack();
        var before = _vm.SavedTracks[0].Points.ToList();

        _vm.RemoteSaveTrackEdit(0, new List<(double, double)> { (10, 10), (10.2, 10) });

        Assert.That(_vm.SavedTracks[0].Points, Is.EqualTo(before));
        Assert.That(_failures, Has.Some.Contains("at least 1 m apart"));
    }

    // --- nothing that belongs to a field is made with none open ---

    [Test]
    public void With_no_field_open_no_track_flag_or_tram_system_is_kept()
    {
        _b.GpsService.IsGpsDataOk().Returns(true);
        _vm.State.Vehicle.Easting = 5;
        _vm.State.Vehicle.Northing = 5;
        _vm.State.Field.LocalPlane = new LocalPlane(Origin, new SharedFieldProperties()); // a GPS origin, but no field

        _vm.CreateAPlusLine(null);
        _vm.CreateLatLonABLine(52.0, 5.0, 52.001, 5.0);
        _vm.PlaceFlagAtWorldPosition(10, 10);
        _vm.PlaceRedFlagCommand!.Execute(null);
        _vm.RemoteAddTramSystem();

        Assert.Multiple(() =>
        {
            Assert.That(_vm.SavedTracks, Is.Empty);
            Assert.That(_vm.Flags, Is.Empty);
            Assert.That(_vm.TramSystems, Is.Empty);
            Assert.That(_failures, Has.Count.EqualTo(5).And.All.EqualTo("Open a field first"));
        });
    }

    // --- tram values ---

    [TestCase("width", "Infinity", "Tram width must be a number")]
    [TestCase("width", "0", "Tram width must be")]
    [TestCase("width", "-1", "Tram width must be")]
    [TestCase("width", "abc", "Tram width must be a number")]
    [TestCase("passes", "-3", "Tram passes must be")]
    [TestCase("passes", "1.5", "Tram passes must be a whole number")]
    [TestCase("ref", "No such line", "There is no track called 'No such line'")]
    [TestCase("offset", "NaN", "Tram offset must be a number")]
    public void A_tram_value_out_of_range_or_naming_no_track_is_refused(string field, string value, string why)
    {
        TwoPointTrack();
        _vm.SelectedTrack = _vm.SavedTracks[0];
        _vm.RemoteAddTramSystem();
        var sys = _vm.TramSystems[0];
        var (width, passes, reference) = (sys.TramWidth, sys.PassCount, sys.ReferenceTrackName);

        _vm.RemoteSetTramField(0, field, value);

        Assert.Multiple(() =>
        {
            Assert.That(_failures, Has.Some.StartsWith(why));
            Assert.That(sys.TramWidth, Is.EqualTo(width));
            Assert.That(sys.PassCount, Is.EqualTo(passes));
            Assert.That(sys.ReferenceTrackName, Is.EqualTo(reference));
        });
    }

    [Test]
    public void A_tram_value_in_range_is_taken()
    {
        TwoPointTrack();
        _vm.RemoteAddTramSystem();

        _vm.RemoteSetTramField(0, "width", "18");
        _vm.RemoteSetTramField(0, "passes", "2");

        Assert.That(_vm.TramSystems[0].TramWidth, Is.EqualTo(18));
        Assert.That(_vm.TramSystems[0].PassCount, Is.EqualTo(2));
        Assert.That(_failures, Is.Empty);
    }

    [Test]
    public void Zero_passes_on_a_track_tram_is_all_passes()
    {
        // #675: the editor says "Passes (0 = all)" for a track, and 0 was refused as out of range.
        TwoPointTrack();
        _vm.SelectedTrack = _vm.SavedTracks[0];
        _vm.RemoteAddTramSystem();

        _vm.RemoteSetTramField(0, "passes", "0");

        Assert.That(_vm.TramSystems[0].PassCount, Is.EqualTo(0));
        Assert.That(_failures, Is.Empty);
    }

    [Test]
    public void Zero_passes_on_a_boundary_tram_is_refused()
    {
        TwoPointTrack();
        _vm.SelectedTrack = null;
        _vm.RemoteAddTramSystem(); // no track selected: the boundary
        Assume.That(_vm.TramSystems[0].ReferenceBoundaryIndex, Is.GreaterThanOrEqualTo(0));

        _vm.RemoteSetTramField(0, "passes", "0");

        Assert.That(_vm.TramSystems[0].PassCount, Is.EqualTo(1));
        Assert.That(_failures, Has.Some.StartsWith("Tram passes must be"));
    }

    [Test]
    public void A_new_tram_system_takes_the_tools_tram_width()
    {
        // #675: it was a fixed 24 m whatever the tool's tram width.
        AgOpenWeb.Models.Configuration.ConfigurationStore.Instance.Tram.TramWidth = 18;
        TwoPointTrack();
        _vm.SelectedTrack = _vm.SavedTracks[0];

        _vm.RemoteAddTramSystem();

        Assert.That(_vm.TramSystems[0].TramWidth, Is.EqualTo(18));
    }

    // --- one name, one track ---

    [Test]
    public void Two_tracks_made_with_one_name_get_two_names()
    {
        OpenField();
        _vm.CreateLatLonHeadingLine(52.0, 5.0, 120);
        _vm.CreateLatLonHeadingLine(52.0005, 5.0, 120);
        _vm.CreateLatLonHeadingLine(52.001, 5.0, 120);

        Assert.That(_vm.SavedTracks.Select(t => t.Name).ToArray(),
            Is.EqualTo(new[] { "A+ 120°", "A+ 120° (2)", "A+ 120° (3)" }));
    }

    [Test]
    public void A_rename_onto_another_tracks_name_is_refused_and_says_so()
    {
        OpenField();
        _vm.CreateLatLonHeadingLine(52.0, 5.0, 10);
        _vm.CreateLatLonHeadingLine(52.0, 5.0, 20);

        _vm.RenameTrackAt(1, _vm.SavedTracks[0].Name);
        _vm.RenameTrackAt(1, "   ");

        Assert.Multiple(() =>
        {
            Assert.That(_vm.SavedTracks[1].Name, Is.EqualTo("A+ 20°"));
            Assert.That(_failures, Has.Some.EqualTo("There is already a track called 'A+ 10°'"));
            Assert.That(_failures, Has.Some.EqualTo("Type a name for the track"));
        });
    }
}
