// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Net.Sockets;
using System.Text;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Tests;

/// <summary>The pure parts of the NTRIP connection: the request, the reply header and the classification.</summary>
[TestFixture]
public class NtripProtocolTests
{
    private static NtripConfiguration Config(int port = 2101, string user = "u", string password = "p") => new()
    {
        CasterAddress = "caster.example.com", CasterPort = port, MountPoint = "MOUNT", Username = user, Password = password,
    };

    private static byte[] Bytes(string text) => Encoding.ASCII.GetBytes(text);

    // ── Request ──────────────────────────────────────────────────────────

    [Test]
    public void Ntrip20Request_CarriesHostVersionLoginAndPosition()
    {
        string request = NtripRequest.Build(Config(), NtripVersion.V2, "$GPGGA,1*00\r\n");

        Assert.That(request, Does.StartWith("GET /MOUNT HTTP/1.1\r\nHost: caster.example.com:2101\r\nNtrip-Version: Ntrip/2.0\r\n"));
        Assert.That(request, Does.Contain("User-Agent: NTRIP AgOpenWeb\r\n"));
        Assert.That(request, Does.Contain("Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("u:p")) + "\r\n"));
        Assert.That(request, Does.Contain("Ntrip-GGA: $GPGGA,1*00\r\n"), "the line break of the sentence is not repeated");
        Assert.That(request, Does.EndWith("\r\n\r\n"));
    }

    [Test]
    public void Ntrip10Request_HasNoHostVersionOrPosition()
    {
        string request = NtripRequest.Build(Config(), NtripVersion.V1, "$GPGGA,1*00");

        Assert.That(request, Does.StartWith("GET /MOUNT HTTP/1.0\r\nUser-Agent: NTRIP AgOpenWeb\r\n"));
        Assert.That(request, Does.Not.Contain("Host:").And.Not.Contain("Ntrip-Version").And.Not.Contain("Ntrip-GGA"));
    }

    [Test]
    public void ARequest_LeavesOutThePortOf80_AndTheLoginOfAnOpenCaster()
    {
        string request = NtripRequest.Build(Config(port: 80, user: "", password: ""), NtripVersion.V2, null);

        Assert.That(request, Does.Contain("Host: caster.example.com\r\n"));
        Assert.That(request, Does.Not.Contain("Authorization"));
    }

    [Test]
    public void APasswordWithLettersOutsideAscii_IsSentAsUtf8()
    {
        string request = NtripRequest.Build(Config(password: "žč"), NtripVersion.V1, null);

        Assert.That(request, Does.Contain(Convert.ToBase64String(Encoding.UTF8.GetBytes("u:žč"))));
    }

    // ── Reply header ─────────────────────────────────────────────────────

    [TestCase("HTTP/1.1 200 OK\r\nServer: x\r\n\r\nBODY", "HTTP/1.1 200 OK\r\nServer: x", 4)]
    [TestCase("HTTP/1.1 200 OK\n\nBODY", "HTTP/1.1 200 OK", 2)]
    public void TheHeaderEnds_AtTheBlankLine(string data, string header, int bodyStart)
    {
        Assert.That(NtripHeaderScanner.TryFindEnd(Bytes(data), out int length, out int body), Is.True);
        Assert.That(data[..length], Is.EqualTo(header));
        Assert.That(data[body..], Is.EqualTo("BODY"));
        Assert.That(body - length, Is.EqualTo(bodyStart));
    }

    [Test]
    public void AnIcyStatusLine_IsTheWholeHeader_WhenTheStreamFollowsAtOnce()
    {
        byte[] data = [.. Bytes("ICY 200 OK\r\n"), 0xD3, 0x00, 0x04];

        Assert.That(NtripHeaderScanner.TryFindEnd(data, out int length, out int body), Is.True);
        Assert.That(length, Is.EqualTo("ICY 200 OK".Length));
        Assert.That(body, Is.EqualTo("ICY 200 OK\r\n".Length));
    }

    [Test]
    public void AnIcyStatusLineAlone_WaitsForMoreData_ButCountsAsComplete_AfterThePause()
    {
        byte[] data = Bytes("ICY 200 OK\r\n");

        Assert.That(NtripHeaderScanner.TryFindEnd(data, out _, out _), Is.False);
        Assert.That(NtripHeaderScanner.IsIcyStatusLineOnly(data), Is.True);
    }

    [Test]
    public void AHeaderStillArriving_IsNotComplete()
    {
        Assert.That(NtripHeaderScanner.TryFindEnd(Bytes("HTTP/1.1 200 OK\r\nServer: x\r\n"), out _, out _), Is.False);
    }

    // ── Classification ───────────────────────────────────────────────────

    [TestCase("ICY 200 OK", NtripCategory.Accepted)]
    [TestCase("HTTP/1.1 200 OK", NtripCategory.Accepted)]
    [TestCase("HTTP/1.1 401 Unauthorized", NtripCategory.LoginRejected)]
    [TestCase("HTTP/1.1 403 Forbidden", NtripCategory.LoginRejected)]
    [TestCase("ERROR - Bad Password", NtripCategory.LoginRejected)]
    [TestCase("HTTP/1.1 404 Not Found", NtripCategory.UnknownMountpoint)]
    [TestCase("SOURCETABLE 200 OK", NtripCategory.UnknownMountpoint)]
    [TestCase("HTTP/1.1 429 Too Many Requests", NtripCategory.CasterBusy)]
    [TestCase("HTTP/1.1 503 Service Unavailable", NtripCategory.CasterBusy)]
    [TestCase("HTTP/1.1 500 Internal Server Error", NtripCategory.NotUnderstood)]
    [TestCase("", NtripCategory.NotUnderstood)]
    public void AReply_FallsInOneCategory(string header, NtripCategory expected)
    {
        Assert.That(NtripResponse.Classify(header).Category, Is.EqualTo(expected));
    }

    [TestCase("garbage", true)]
    [TestCase("HTTP/1.1 400 Bad Request", true)]
    [TestCase("HTTP/1.1 505 HTTP Version Not Supported", true)]
    [TestCase("HTTP/1.1 401 Unauthorized", false)]
    [TestCase("HTTP/1.1 404 Not Found", false)]
    [TestCase("HTTP/1.1 500 Internal Server Error", false)]
    [TestCase("SOURCETABLE 200 OK", false)]
    [TestCase("ERROR - Bad Password", false)]
    [TestCase("ICY 200 OK", false)]
    public void OnlyAReplyThatShowsNtrip20WasNotUnderstood_AsksForNtrip10(string header, bool asks)
    {
        Assert.That(NtripResponse.Classify(header).Ntrip2NotUnderstood, Is.EqualTo(asks));
    }

    [Test]
    public void ABadRequestThatAsksForAnEmailAddress_IsALoginProblem_NotAVersionProblem()
    {
        NtripOutcome outcome = NtripResponse.Classify("HTTP/1.1 400 Bad Request", Bytes("Please use your email address as user name"));

        Assert.That(outcome.Category, Is.EqualTo(NtripCategory.LoginRejected));
        Assert.That(outcome.Ntrip2NotUnderstood, Is.False);
    }

    [Test]
    public void ACasterStartingUp_IsBusy_NotARejection()
    {
        Assert.That(NtripResponse.Classify("HTTP/1.1 406 Not Acceptable", Bytes("Caster is starting up")).Category, Is.EqualTo(NtripCategory.CasterBusy));
    }

    [TestCase("HTTP/1.1 200 OK", "STR;MOUNT;Place")]
    [TestCase("ICY 200 OK", "STR;MOUNT;Place")]
    [TestCase("HTTP/1.1 200 OK\r\nContent-Type: gnss/sourcetable", "")]
    public void ASourceTableInPlaceOfAStream_IsAMissingMountPoint(string header, string body)
    {
        Assert.That(NtripResponse.Classify(header, Bytes(body)).Category, Is.EqualTo(NtripCategory.UnknownMountpoint));
    }

    [Test]
    public void AWebPage_IsNotACaster()
    {
        NtripOutcome outcome = NtripResponse.Classify("HTTP/1.1 200 OK", Bytes("<html>"));

        Assert.That(outcome.Category, Is.EqualTo(NtripCategory.NotUnderstood));
        Assert.That(outcome.Ntrip2NotUnderstood, Is.False, "NTRIP 1.0 would get the same page");
    }

    [Test]
    public void AChunkedReply_IsFlagged()
    {
        Assert.That(NtripResponse.Classify("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked").Chunked, Is.True);
        Assert.That(NtripResponse.Classify("HTTP/1.1 200 OK\r\nServer: x").Chunked, Is.False);
    }

    [TestCase(NtripCategory.LoginRejected, true)]
    [TestCase(NtripCategory.UnknownMountpoint, true)]
    [TestCase(NtripCategory.CasterBusy, false)]
    [TestCase(NtripCategory.CasterUnreachable, false)]
    [TestCase(NtripCategory.NotUnderstood, false)]
    public void OnlyALoginOrMountPointProblem_IsFinal(NtripCategory category, bool isFinal)
    {
        Assert.That(new NtripOutcome(category, "").IsFinal, Is.EqualTo(isFinal));
    }

    [TestCase(SocketError.ConnectionRefused, NtripCategory.CasterUnreachable)]
    [TestCase(SocketError.HostNotFound, NtripCategory.CasterUnreachable)]
    [TestCase(SocketError.TimedOut, NtripCategory.CasterUnreachable)]
    [TestCase(SocketError.NetworkUnreachable, NtripCategory.NoNetwork)]
    [TestCase(SocketError.NetworkDown, NtripCategory.NoNetwork)]
    public void ASocketError_FallsInOneCategory(SocketError error, NtripCategory expected)
    {
        Assert.That(ConnectionErrors.Classify(error, "host", 2101).Category, Is.EqualTo(expected));
    }
}
