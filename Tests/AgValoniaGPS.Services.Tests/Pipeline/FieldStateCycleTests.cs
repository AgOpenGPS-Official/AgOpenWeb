// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Linq;
using System.Reflection;
using AgValoniaGPS.Models;
using AgValoniaGPS.Services.Interfaces;

namespace AgValoniaGPS.Services.Tests.Pipeline;

/// <summary>
/// End-of-Phase-E locks for the <c>FieldState</c> / <c>LocalPlane</c>
/// read/write boundary. Mirrors the reflection-based guards added for
/// YouTurn (Phase C C9) and Guidance (Phase D D10).
///
/// The cycle worker reads <see cref="LocalPlane"/> from
/// <c>ApplicationState.Field.LocalPlane</c> for coordinate conversion; it
/// must not accept a LocalPlane from the UI thread through
/// <see cref="IGpsPipelineService"/>. Auto-create flows from the cycle
/// to the UI via <c>GpsCycleResult.FirstFixLocalPlane</c>, not the
/// other way around.
/// </summary>
[TestFixture]
public class FieldStateCycleTests
{
    [Test]
    public void IGpsPipelineService_has_no_direct_LocalPlane_writethrough_methods()
    {
        var disallowed = typeof(IGpsPipelineService).GetMethods()
            .Where(m =>
                (m.Name.StartsWith("Set") || m.Name.StartsWith("Push") || m.Name.StartsWith("Apply"))
                && m.GetParameters().Any(p =>
                    p.ParameterType == typeof(LocalPlane)
                    || p.ParameterType == typeof(LocalPlane).MakeByRefType()))
            .Select(m => m.Name)
            .ToList();

        Assert.That(disallowed, Is.Empty,
            "IGpsPipelineService must not accept a LocalPlane from the UI thread. "
            + "The cycle auto-creates one when needed and emits it via "
            + "GpsCycleResult.FirstFixLocalPlane for UI commit. Offending method(s): "
            + string.Join(", ", disallowed));
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
