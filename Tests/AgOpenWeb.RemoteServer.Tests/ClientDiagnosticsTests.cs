// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.RemoteServer;
using NUnit.Framework;

namespace AgOpenWeb.RemoteServer.Tests;

/// <summary>
/// The dump's "Browsers" section: what each browser said about how it draws the map. A tablet
/// without a working GPU driver must stand out at once (Mesa's llvmpipe on an RK3568 Debian
/// image), so a slow map there isn't chased as an app problem.
/// </summary>
public class ClientDiagnosticsTests
{
    private static readonly DateTime T0 = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    private static string Report(string renderer, string gl = "webgl2") =>
        $"gl={gl}\trenderer={renderer}\tvendor=Mesa\tglVersion=WebGL 2.0\tmaxTexture=8192\tfps=11.4\tdrawMs=71.0 / 140.2\t" +
        "canvas=1920x1200\tdpr=1.00\tscreen=1920x1200\tskia=ready (Skia WebGL)\tua=Mozilla/5.0 (X11; Linux aarch64) AppleWebKit/605.1.15";

    [Test]
    public void A_software_renderer_is_flagged_and_every_value_is_listed()
    {
        var d = new ClientDiagnostics();
        var conn = Guid.NewGuid();
        d.Report(conn, Report("llvmpipe (LLVM 15.0.6, 128 bits)"), T0);

        var text = d.Summary(c => c == conn, T0.AddSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("Browser (in control, reported 5 s ago):"));
            Assert.That(text, Does.Contain("!! SOFTWARE RENDERING"));
            Assert.That(text, Does.Contain("GPU renderer: llvmpipe (LLVM 15.0.6, 128 bits)"));
            Assert.That(text, Does.Contain("Map frame rate: 11.4"));
            Assert.That(text, Does.Contain("Map draw time (ms, avg / max): 71.0 / 140.2"));
            Assert.That(text, Does.Contain("Max texture: 8192"));
            Assert.That(text, Does.Contain("User agent: Mozilla/5.0 (X11; Linux aarch64)"));
        });
    }

    [TestCase("Mali-G52 (Panfrost)", false)]
    [TestCase("ANGLE (ARM, Mali-G52, OpenGL ES 3.2)", false)]
    [TestCase("Apple GPU", false)]
    [TestCase("llvmpipe (LLVM 15.0.6, 128 bits)", true)]
    [TestCase("softpipe", true)]
    [TestCase("ANGLE (Google, Vulkan 1.3.0 (SwiftShader Device (Subzero)), SwiftShader driver)", true)]
    [TestCase("Microsoft Basic Render Driver", true)]
    public void Only_a_cpu_renderer_counts_as_software(string renderer, bool software)
    {
        var d = new ClientDiagnostics();
        d.Report(Guid.NewGuid(), Report(renderer), T0);
        Assert.That(d.Summary(_ => false, T0).Contains("SOFTWARE RENDERING"), Is.EqualTo(software));
    }

    [Test]
    public void No_webgl_is_flagged()
    {
        var d = new ClientDiagnostics();
        d.Report(Guid.NewGuid(), "gl=none\tskia=no WebGL surface\tua=x", T0);
        Assert.That(d.Summary(_ => false, T0), Does.Contain("!! NO WEBGL"));
    }

    [Test]
    public void A_browser_that_left_is_kept_with_when_and_a_newer_report_replaces_the_old()
    {
        var d = new ClientDiagnostics();
        var tablet = Guid.NewGuid();
        var phone = Guid.NewGuid();
        d.Report(tablet, Report("old"), T0);
        d.Report(tablet, Report("Mali-G52 (Panfrost)"), T0.AddSeconds(60));
        d.Report(phone, Report("Adreno (TM) 610"), T0.AddSeconds(60));
        d.Left(phone, T0.AddSeconds(120));

        var text = d.Summary(c => c == tablet, T0.AddSeconds(180));

        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Not.Contain("renderer: old"));
            Assert.That(text, Does.Contain("Browser (in control, reported 120 s ago)").Or.Contain("Browser (in control, reported 2 min ago)"));
            Assert.That(text, Does.Contain("Browser (left 60 s ago"));
            Assert.That(text.IndexOf("Mali-G52", StringComparison.Ordinal),
                Is.LessThan(text.IndexOf("Adreno", StringComparison.Ordinal)), "connected browsers first");
        });
    }

    [Test]
    public void Only_the_newest_browsers_that_left_are_kept_and_line_breaks_stay_out()
    {
        var d = new ClientDiagnostics();
        for (int i = 0; i < 10; i++)
        {
            var c = Guid.NewGuid();
            d.Report(c, $"gl=webgl2\trenderer=gpu{i}\nforged: line", T0.AddSeconds(i));
            d.Left(c, T0.AddSeconds(i));
        }
        var text = d.Summary(_ => false, T0.AddSeconds(20));
        Assert.Multiple(() =>
        {
            Assert.That(text.Split("Browser (").Length - 1, Is.EqualTo(6));
            Assert.That(text, Does.Contain("gpu9").And.Not.Contain("gpu3 "));
            Assert.That(text, Does.Not.Contain("\nforged"));
        });
    }

    [Test]
    public void Nothing_reported_gives_an_empty_section()
    {
        Assert.That(new ClientDiagnostics().Summary(_ => true), Is.Empty);
    }
}
