// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Ntrip;

namespace AgOpenWeb.Services.Tests;

/// <summary>The fallback block of the profile editor's save command (<c>ntrip.save</c>).</summary>
[TestFixture]
public class NtripProfileFieldsTests
{
    private const string Mask = "••••••••";

    private static string[] Save(params string[] fallback) =>
        new[] { "id", "name", "host", "2101", "MOUNT", "user", "pass", "1", "1", "" }.Concat(fallback).ToArray();

    private static NtripFallback Existing() => new()
    {
        CasterHost = "old.caster", CasterPort = 2102, MountPoint = "OLD", Username = "olduser", Password = "oldsecret",
        AutoSwitch = true, SwitchAfterFailures = 4,
    };

    [Test]
    public void ACommandWithoutTheBlock_KeepsTheFallbackTheProfileHas()
    {
        NtripFallback existing = Existing();

        Assert.That(NtripProfileFields.FallbackFrom(Save(), 10, existing, Mask), Is.SameAs(existing));
        Assert.That(NtripProfileFields.FallbackFrom(Save(), 10, null, Mask), Is.Null);
    }

    [Test]
    public void ANotPresentBlock_RemovesTheFallback()
    {
        Assert.That(NtripProfileFields.FallbackFrom(Save("0", "", "2101", "", "", "", "0", "2"), 10, Existing(), Mask), Is.Null);
    }

    [Test]
    public void ABlock_BecomesTheFallback()
    {
        NtripFallback fallback = NtripProfileFields.FallbackFrom(Save("1", " new.caster ", "2103", " NEW ", "nu", "np", "1", "3"), 10, null, Mask)!;

        Assert.That(fallback.CasterHost, Is.EqualTo("new.caster"));
        Assert.That(fallback.CasterPort, Is.EqualTo(2103));
        Assert.That(fallback.MountPoint, Is.EqualTo("NEW"));
        Assert.That(fallback.Username, Is.EqualTo("nu"));
        Assert.That(fallback.Password, Is.EqualTo("np"));
        Assert.That(fallback.AutoSwitch, Is.True);
        Assert.That(fallback.SwitchAfterFailures, Is.EqualTo(3));
        Assert.That(fallback.IsComplete, Is.True);
    }

    [Test]
    public void TheMask_StandsForTheStoredPassword_AndANewOneReplacesIt()
    {
        Assert.That(NtripProfileFields.FallbackFrom(Save("1", "h", "2101", "M", "u", Mask, "0", "2"), 10, Existing(), Mask)!.Password, Is.EqualTo("oldsecret"));
        Assert.That(NtripProfileFields.FallbackFrom(Save("1", "h", "2101", "M", "u", "fresh", "0", "2"), 10, Existing(), Mask)!.Password, Is.EqualTo("fresh"));
        Assert.That(NtripProfileFields.FallbackFrom(Save("1", "h", "2101", "M", "u", Mask, "0", "2"), 10, null, Mask)!.Password, Is.Empty, "no stored password to keep");
    }

    [TestCase("", 2101)]
    [TestCase("0", 2101)]
    [TestCase("abc", 2101)]
    [TestCase("2105", 2105)]
    public void ABadPort_BecomesTheDefault(string port, int expected)
    {
        Assert.That(NtripProfileFields.FallbackFrom(Save("1", "h", port, "M", "", "", "0", "2"), 10, null, Mask)!.CasterPort, Is.EqualTo(expected));
    }

    [TestCase("", 2)]
    [TestCase("0", 2)]
    [TestCase("-3", 2)]
    [TestCase("abc", 2)]
    [TestCase("5", 5)]
    public void ABadAttemptCount_BecomesTwo(string after, int expected)
    {
        Assert.That(NtripProfileFields.FallbackFrom(Save("1", "h", "2101", "M", "", "", "1", after), 10, null, Mask)!.SwitchAfterFailures, Is.EqualTo(expected));
    }

    [Test]
    public void AShortBlock_IsReadAsFarAsItGoes()
    {
        NtripFallback fallback = NtripProfileFields.FallbackFrom(Save("1", "h"), 10, null, Mask)!;

        Assert.That(fallback.CasterHost, Is.EqualTo("h"));
        Assert.That(fallback.CasterPort, Is.EqualTo(2101));
        Assert.That(fallback.AutoSwitch, Is.False);
        Assert.That(fallback.IsComplete, Is.False, "no mount point: not enough to connect");
    }
}
