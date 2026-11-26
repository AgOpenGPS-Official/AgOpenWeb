// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using AgOpenWeb.Models;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// A VRS caster builds its corrections for the position in our GGA. Without a fix this sent a
/// place in Kansas, so a caster in Slovakia gave nothing that helped or dropped us (#443).
/// </summary>
[TestFixture]
public class NtripGgaPositionTests
{
    private IGpsService _gps = null!;
    private NtripClientService _svc = null!;

    [SetUp]
    public void SetUp()
    {
        _gps = Substitute.For<IGpsService>();
        _svc = new NtripClientService(_gps, NullLogger<NtripClientService>.Instance);
    }

    [TearDown]
    public void TearDown() => _svc.Dispose();

    private static GpsData Fix(double lat, double lon) => new()
    {
        CurrentPosition = new Position { Latitude = lat, Longitude = lon, Altitude = 140 },
        FixQuality = 1,
        SatellitesInUse = 10,
    };

    // "4803.9299,N,01743.0781,E" → (48.0655, 17.7180)
    private static (double Lat, double Lon) Where(string gga)
    {
        var f = gga.Split(',');
        double Deg(string s, int d) => int.Parse(s[..d]) + double.Parse(s[d..], System.Globalization.CultureInfo.InvariantCulture) / 60;
        return (Deg(f[2], 2) * (f[3] == "S" ? -1 : 1), Deg(f[4], 3) * (f[5] == "W" ? -1 : 1));
    }

    [Test]
    public void No_fix_and_no_fallback_sends_nothing()
    {
        Assert.That(_svc.NextGgaSentence(new NtripConfiguration()), Is.Null);
    }

    [Test]
    public void No_fix_yet_sends_the_field_origin()
    {
        var gga = _svc.NextGgaSentence(new NtripConfiguration { FallbackPositionProvider = () => (48.0655, 17.718) });

        var (lat, lon) = Where(gga!);
        Assert.That(lat, Is.EqualTo(48.0655).Within(1e-4));
        Assert.That(lon, Is.EqualTo(17.718).Within(1e-4));
    }

    [Test]
    public void A_fix_wins_and_is_kept_when_it_goes()
    {
        var config = new NtripConfiguration { FallbackPositionProvider = () => (1.0, 1.0) };
        _gps.CurrentData.Returns(Fix(48.2, 17.1));
        Assert.That(Where(_svc.NextGgaSentence(config)!).Lat, Is.EqualTo(48.2).Within(1e-4));

        _gps.CurrentData.Returns(new GpsData()); // fix lost
        var (lat, lon) = Where(_svc.NextGgaSentence(config)!);
        Assert.That(lat, Is.EqualTo(48.2).Within(1e-4), "the last fix, not the fallback");
        Assert.That(lon, Is.EqualTo(17.1).Within(1e-4));
    }

    // Single or DGPS fails the steering check (MinFixQuality = RTK Fixed), so the pipeline marks
    // the fix invalid. The caster still needs this position: without it a VRS caster sends no
    // corrections, and the receiver never gets past single.
    [TestCase(1, TestName = "Single fix marked invalid still sends the receiver's position")]
    [TestCase(2, TestName = "DGPS fix marked invalid still sends the receiver's position")]
    public void A_fix_below_the_steering_minimum_is_still_sent(int fixQuality)
    {
        var fix = Fix(48.2, 17.1);
        fix.FixQuality = fixQuality;
        fix.IsValid = false; // as GpsFixQualityValidator leaves it
        _gps.CurrentData.Returns(fix);

        var gga = _svc.NextGgaSentence(new NtripConfiguration { FallbackPositionProvider = () => (1.0, 1.0) });

        var (lat, lon) = Where(gga!);
        Assert.That(lat, Is.EqualTo(48.2).Within(1e-4), "the receiver, not the field origin");
        Assert.That(lon, Is.EqualTo(17.1).Within(1e-4));
        Assert.That(gga!.Split(',')[6], Is.EqualTo(fixQuality.ToString()), "its own fix quality");
    }

    [Test]
    public void No_fix_at_a_position_falls_back()
    {
        var noFix = Fix(48.2, 17.1);
        noFix.FixQuality = 0; // the receiver repeats a position but has no fix
        _gps.CurrentData.Returns(noFix);

        var (lat, _) = Where(_svc.NextGgaSentence(new NtripConfiguration { FallbackPositionProvider = () => (1.0, 1.0) })!);
        Assert.That(lat, Is.EqualTo(1.0).Within(1e-4));
    }

    [Test]
    public void Credentials_are_utf8_and_sent_without_a_user_name()
    {
        var auth = NtripCredentials.BasicAuthorization("", "heslo-čľž!");
        Assert.That(auth, Does.StartWith("Basic "));
        Assert.That(Encoding.UTF8.GetString(Convert.FromBase64String(auth![6..])), Is.EqualTo(":heslo-čľž!"));
        Assert.That(NtripCredentials.BasicAuthorization("", ""), Is.Null, "an open caster gets no header");
    }
}
