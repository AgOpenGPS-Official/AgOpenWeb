// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// A field's outer boundary replaced is in the log, with what the field had before. On a
/// tester's field two obstacles driven round with the field's button each became the outer
/// boundary (an 11.6 ha field 0.09 ha on the screen), and the bug report's log had no line to say
/// so: it was read from the headland being saved right after each recording.
/// </summary>
[TestFixture]
public class OuterBoundaryReplacedLogTests
{
    private static BoundaryPolygon Square(double e, double n, double side)
    {
        var p = new BoundaryPolygon();
        p.Points.Add(new BoundaryPoint(e, n, 0));
        p.Points.Add(new BoundaryPoint(e + side, n, 0));
        p.Points.Add(new BoundaryPoint(e + side, n + side, 0));
        p.Points.Add(new BoundaryPoint(e, n + side, 0));
        p.UpdateBounds();
        return p;
    }

    private static (MainViewModel Vm, MainViewModelBuilder B, ListLogger Log, Boundary Boundary) Field(bool withOuter)
    {
        var log = new ListLogger();
        var b = new MainViewModelBuilder { Logger = log };
        b.SettingsService.Settings.Returns(new AppSettings { FieldsDirectory = "/tmp/agtest" });
        var boundary = new Boundary();
        if (withOuter) boundary.OuterBoundary = Square(0, 0, 300);   // 9 ha
        b.FieldService.LoadField(Arg.Any<string>()).Returns(_ => new Field { Name = "F", DirectoryPath = "/tmp/agtest/F", Boundary = boundary });
        var vm = b.Build();
        vm.ActiveField = new Field { Name = "F", DirectoryPath = "/tmp/agtest/F" };
        vm.IsFieldOpen = true;
        return (vm, b, log, boundary);
    }

    [Test]
    public void A_boundary_drawn_on_the_map_over_the_field_s_own_is_logged_with_both_sizes()
    {
        var (vm, _, log, _) = Field(withOuter: true);

        vm.RemoteCreateBoundaryFromMapPoints(new[] { (10.0, 10.0), (30.0, 10.0), (30.0, 30.0), (10.0, 30.0) });

        Assert.That(log.Lines, Has.Some.EqualTo(
            "[Boundary] Outer boundary replaced by a drawing on the map: was 4 points, 9.00 ha; now 4 points, 0.04 ha"));
    }

    [Test]
    public void A_drive_around_recording_saved_over_the_field_s_own_is_logged()
    {
        var (vm, b, log, _) = Field(withOuter: true);
        b.BoundaryRecordingService.CurrentBoundaryType.Returns(BoundaryType.Outer);
        b.BoundaryRecordingService.StopRecording().Returns(Square(100, 100, 20));

        vm.StopBoundaryRecordingCommand!.Execute(null);

        Assert.That(log.Lines, Has.Some.EqualTo(
            "[Boundary] Outer boundary replaced by a drive-around recording: was 4 points, 9.00 ha; now 4 points, 0.04 ha"));
    }

    [Test]
    public void A_field_s_first_outer_boundary_is_logged_as_made()
    {
        var (vm, _, log, _) = Field(withOuter: false);

        vm.RemoteCreateBoundaryFromMapPoints(new[] { (0.0, 0.0), (100.0, 0.0), (100.0, 100.0), (0.0, 100.0) });

        Assert.Multiple(() =>
        {
            Assert.That(log.Lines, Has.Some.EqualTo("[Boundary] Outer boundary made by a drawing on the map: 4 points, 1.00 ha"));
            Assert.That(log.Lines, Has.None.Contains("replaced"));
        });
    }

    [Test]
    public void An_obstacle_recorded_leaves_the_outer_boundary_and_is_not_logged_as_one()
    {
        var (vm, b, log, boundary) = Field(withOuter: true);
        var outer = boundary.OuterBoundary;
        b.BoundaryRecordingService.CurrentBoundaryType.Returns(BoundaryType.Inner);
        b.BoundaryRecordingService.StopRecording().Returns(Square(100, 100, 20));

        vm.StopBoundaryRecordingCommand!.Execute(null);

        Assert.Multiple(() =>
        {
            Assert.That(boundary.OuterBoundary, Is.SameAs(outer));
            Assert.That(log.Lines, Has.None.StartsWith("[Boundary] Outer boundary"));
        });
    }

    private sealed class ListLogger : ILogger<MainViewModel>
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }
}
