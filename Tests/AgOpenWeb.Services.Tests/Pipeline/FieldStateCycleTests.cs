// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Linq;
using System.Reflection;
using AgOpenWeb.Models;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Tests.Pipeline;

/// <summary>
/// End-of-Phase-E locks for the <c>FieldState</c> / <c>LocalPlane</c>
/// read/write boundary. Mirrors the reflection-based guards added for
/// YouTurn (Phase C C9) and Guidance (Phase D D10).
///
/// The cycle worker converts against the UI's committed <see cref="LocalPlane"/>,
/// which the ViewModel pushes through <c>SetLocalPlane</c> whenever
/// <c>State.Field.LocalPlane</c> changes (the pipeline reads no
/// <c>ApplicationState</c>). That is the one way a plane enters the cycle;
/// auto-create flows from the cycle to the UI via
/// <c>GpsCycleResult.FirstFixLocalPlane</c> and comes back through the same push.
/// </summary>
[TestFixture]
public class FieldStateCycleTests
{
    [Test]
    public void IGpsPipelineService_takes_a_LocalPlane_only_through_SetLocalPlane()
    {
        var takingAPlane = typeof(IGpsPipelineService).GetMethods()
            .Where(m => m.GetParameters().Any(p =>
                p.ParameterType == typeof(LocalPlane)
                || p.ParameterType == typeof(LocalPlane).MakeByRefType()))
            .Select(m => m.Name)
            .ToList();

        Assert.That(takingAPlane, Is.EquivalentTo(new[] { nameof(IGpsPipelineService.SetLocalPlane) }),
            "SetLocalPlane is the committed plane coming back from State.Field.LocalPlane. "
            + "No other entry point hands the cycle a plane: auto-create flows cycle → UI via "
            + "GpsCycleResult.FirstFixLocalPlane and returns through that one push. Found: "
            + string.Join(", ", takingAPlane));
    }

    [Test]
    public void GpsCycleResult_FirstFixLocalPlane_defaults_null()
    {
        var result = new Models.State.GpsCycleResult();
        Assert.That(result.FirstFixLocalPlane, Is.Null,
            "FirstFixLocalPlane is non-null only on the single cycle a plane is "
            + "auto-created — any null initializer regression would make every "
            + "result look like a first-fix signal and flood the UI commit point.");
    }
}
