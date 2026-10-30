// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Services.Tests;

/// <summary>A caster's list of mount points: reading the text, and asking for it as the client does.</summary>
[TestFixture]
public class NtripSourcetableTests
{
    private const string Table =
        "SOURCETABLE 200 OK\r\nServer: Test\r\nContent-Type: gnss/sourcetable\r\n\r\n" +
        "CAS;caster.example.com;2101;Test caster;Op;0;NLD;52.0;5.0;\r\n" +
        "NET;NET1;Op;B;N;;;;\r\n" +
        "STR;FAR;Far place;RTCM 3.2;1004(1);2;GPS;NET1;NLD;53.50;6.50;0;1;gen;none;B;N;9600;\r\n" +
        "STR;NEAR;Near place;RTCM 3.2;1004(1);2;GPS+GLO;NET1;NLD;52.01;5.01;1;0;gen;none;B;N;9600;\r\n" +
        "STR;NOPOS;No position;RTCM 3.3;1004(1);2;GPS;NET1;DEU;0;0;0;0;gen;none;B;N;9600;\r\n" +
        "STR;;broken line without a name;\r\n" +
        "garbage\r\n" +
        "ENDSOURCETABLE\r\n";

    // ── Reading the text ─────────────────────────────────────────────────

    [Test]
    public void OnlyTheStrLines_BecomeMountPoints()
    {
        var list = NtripSourcetable.Parse(Table);

        Assert.That(list.Select(m => m.Mount), Is.EqualTo(new[] { "FAR", "NEAR", "NOPOS" }), "by name without a position");
        var near = list.Single(m => m.Mount == "NEAR");
        Assert.That(near.Name, Is.EqualTo("Near place"));
        Assert.That(near.Format, Is.EqualTo("RTCM 3.2"));
        Assert.That(near.Systems, Is.EqualTo("GPS+GLO"));
        Assert.That(near.Country, Is.EqualTo("NLD"));
        Assert.That(near.NeedsPosition, Is.True);
        Assert.That(list.Single(m => m.Mount == "FAR").NeedsPosition, Is.False);
    }

    [Test]
    public void WithAPosition_TheNearestComesFirst_AndThoseWithoutAPositionLast()
    {
        var list = NtripSourcetable.Parse(Table, (52.0, 5.0));

        Assert.That(list.Select(m => m.Mount), Is.EqualTo(new[] { "NEAR", "FAR", "NOPOS" }));
        Assert.That(list[0].DistanceKm, Is.EqualTo(1.4).Within(0.2));
        Assert.That(list[1].DistanceKm, Is.GreaterThan(150));
        Assert.That(double.IsNaN(list[2].DistanceKm), Is.True);
    }

    [Test]
    public void ADistance_IsGreatCircle()
    {
        Assert.That(NtripSourcetable.DistanceKm(52, 5, 52, 5), Is.Zero);
        Assert.That(NtripSourcetable.DistanceKm(0, 0, 0, 1), Is.EqualTo(111.2).Within(0.5));
    }

    [TestCase("")]
    [TestCase("nothing here")]
    [TestCase("CAS;a;b\r\nNET;a;b\r\nENDSOURCETABLE")]
    public void TextWithoutStrLines_HasNoMountPoints(string text)
    {
        Assert.That(NtripSourcetable.Parse(text), Is.Empty);
    }

    // ── Asking the caster ────────────────────────────────────────────────

    private static string FirstLine(NtripFakeCaster caster) => caster.Requests.First().Split("\r\n")[0];

    [Test]
    public async Task TheListIsAskedForWithoutAMountPoint_AndReadAsItArrives()
    {
        using var caster = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s, Table));

        SourcetableResult result = await NtripSourcetable.FetchAsync("127.0.0.1", caster.Port, "u", "p", (52.0, 5.0), new NtripTimeouts(), default);

        Assert.That(result.Error, Is.Null);
        Assert.That(result.Mountpoints.Select(m => m.Mount), Is.EqualTo(new[] { "NEAR", "FAR", "NOPOS" }));
        Assert.That(FirstLine(caster), Is.EqualTo("GET / HTTP/1.1"));
        Assert.That(caster.Requests.First(), Does.Contain("Authorization: Basic "));
    }

    [Test]
    public async Task ATableThatArrivesInPieces_IsReadToTheEnd()
    {
        using var caster = new NtripFakeCaster(async (_, _, s) =>
        {
            await NtripFakeCaster.SendAsync(s, Table[..60]);
            await Task.Delay(100);
            await NtripFakeCaster.SendAsync(s, Table[60..200]);
            await Task.Delay(100);
            await NtripFakeCaster.SendAsync(s, Table[200..]);
        });

        SourcetableResult result = await NtripSourcetable.FetchAsync("127.0.0.1", caster.Port, "", "", null, new NtripTimeouts(), default);

        Assert.That(result.Mountpoints, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task ABigTableSentWithItsHeader_IsNotTakenForAnEndlessHeader()
    {
        // Big networks send hundreds of lines at once, right behind a small header (kadaster, centipede).
        var big = new System.Text.StringBuilder("HTTP/1.1 200 OK\r\nContent-Type: gnss/sourcetable\r\nConnection: close\r\n\r\n");
        for (int i = 0; i < 800; i++)
            big.Append($"STR;MOUNT{i:000};Place {i};RTCM 3.2;1004(1),1005(10),1077(1);2;GPS+GLO;NET;NLD;52.{i % 90:00};5.0;0;0;gen;none;B;N;9600;\r\n");
        big.Append("ENDSOURCETABLE\r\n");
        Assert.That(big.Length, Is.GreaterThan(60_000), "more than a read and the header cap");
        using var caster = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s, big.ToString()));

        SourcetableResult result = await NtripSourcetable.FetchAsync("127.0.0.1", caster.Port, "", "", null, new NtripTimeouts(), default);

        Assert.That(result.Error, Is.Null);
        Assert.That(result.Mountpoints, Has.Count.EqualTo(800));
    }

    private static byte[] Bytes(string text, System.Text.Encoding encoding) => encoding.GetBytes(text);

    [Test]
    public async Task ACharacterSplitBetweenTwoReads_IsKept()
    {
        byte[] all = Bytes("SOURCETABLE 200 OK\r\nContent-Type: gnss/sourcetable\r\n\r\nSTR;ZUERICH;Zürich Nord;RTCM 3.2;;2;GPS;NET;CHE;47.4;8.5;0;0;gen;none;B;N;9600;\r\nENDSOURCETABLE\r\n",
            System.Text.Encoding.UTF8);
        int split = Array.IndexOf(all, (byte)0xC3) + 1;   // between the two bytes of "ü"
        using var caster = new NtripFakeCaster(async (_, _, s) =>
        {
            await s.WriteAsync(all.AsMemory(0, split));
            await Task.Delay(150);
            await s.WriteAsync(all.AsMemory(split));
        });

        SourcetableResult result = await NtripSourcetable.FetchAsync("127.0.0.1", caster.Port, "", "", null, new NtripTimeouts(), default);

        Assert.That(result.Mountpoints.Single().Name, Is.EqualTo("Zürich Nord"));
    }

    [Test]
    public async Task ALatin1Table_IsReadToo()
    {
        byte[] all = Bytes("SOURCETABLE 200 OK\r\nContent-Type: gnss/sourcetable\r\n\r\nSTR;KOELN;Köln;RTCM 3.2;;2;GPS;NET;DEU;50.9;7.0;0;0;gen;none;B;N;9600;\r\nENDSOURCETABLE\r\n",
            System.Text.Encoding.Latin1);
        using var caster = new NtripFakeCaster((_, _, s) => s.WriteAsync(all).AsTask());

        SourcetableResult result = await NtripSourcetable.FetchAsync("127.0.0.1", caster.Port, "", "", null, new NtripTimeouts(), default);

        Assert.That(result.Mountpoints.Single().Name, Is.EqualTo("Köln"));
    }

    [Test]
    public async Task ATableCutOffInTheMiddleOfALine_KeepsTheWholeLines_AndSaysItIsPartial()
    {
        using var caster = new NtripFakeCaster(async (_, _, s) =>
            await NtripFakeCaster.SendAsync(s, Table[..Table.IndexOf("STR;NOPOS")] + "STR;NOP"));   // then the caster closes

        SourcetableResult result = await NtripSourcetable.FetchAsync("127.0.0.1", caster.Port, "", "", null, new NtripTimeouts(), default);

        Assert.That(result.Mountpoints.Select(m => m.Mount), Is.EqualTo(new[] { "FAR", "NEAR" }), "the cut line is not a mount point");
        Assert.That(result.Partial, Is.True);
        Assert.That(result.Error, Is.Null);
    }

    [Test]
    public async Task ACasterThatGoesSilentMidTable_GivesWhatArrived()
    {
        using var caster = new NtripFakeCaster(async (_, _, s) =>
        {
            await NtripFakeCaster.SendAsync(s, Table[..Table.IndexOf("STR;NEAR")]);
            await Task.Delay(5000);
        });
        var timeouts = new NtripTimeouts { Header = TimeSpan.FromMilliseconds(400) };

        SourcetableResult result = await NtripSourcetable.FetchAsync("127.0.0.1", caster.Port, "", "", null, timeouts, default);

        Assert.That(result.Mountpoints.Select(m => m.Mount), Is.EqualTo(new[] { "FAR" }));
        Assert.That(result.Partial, Is.True);
    }

    [Test]
    public async Task AWholeTable_IsNotPartial()
    {
        using var caster = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s, Table));

        SourcetableResult result = await NtripSourcetable.FetchAsync("127.0.0.1", caster.Port, "", "", null, new NtripTimeouts(), default);

        Assert.That(result.Partial, Is.False);
    }

    [Test]
    public async Task AHeaderThatNeverEnds_IsStillRefused()
    {
        using var caster = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s, new string('X', 9 * 1024)));

        SourcetableResult result = await NtripSourcetable.FetchAsync("127.0.0.1", caster.Port, "", "", null, new NtripTimeouts(), default);

        Assert.That(result.Mountpoints, Is.Empty);
        Assert.That(result.Error, Does.Contain("invalid reply"));
    }

    [Test]
    public async Task AChunkedTable_IsDecoded()
    {
        string body = Table[(Table.IndexOf("\r\n\r\n") + 4)..];
        using var caster = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s,
            "HTTP/1.1 200 OK\r\nContent-Type: gnss/sourcetable\r\nTransfer-Encoding: chunked\r\n\r\n" +
            body.Length.ToString("X") + "\r\n" + body + "\r\n0\r\n\r\n"));

        SourcetableResult result = await NtripSourcetable.FetchAsync("127.0.0.1", caster.Port, "", "", null, new NtripTimeouts(), default);

        Assert.That(result.Mountpoints.Select(m => m.Mount), Is.EqualTo(new[] { "FAR", "NEAR", "NOPOS" }));
    }

    [Test]
    public async Task ACasterThatNeedsAnNtrip1Request_GetsOne()
    {
        using var caster = new NtripFakeCaster((_, request, s) => request.StartsWith("GET / HTTP/1.1")
            ? NtripFakeCaster.SendAsync(s, "garbage that is no reply\r\n\r\n")
            : NtripFakeCaster.SendAsync(s, Table));

        SourcetableResult result = await NtripSourcetable.FetchAsync("127.0.0.1", caster.Port, "", "", null, new NtripTimeouts(), default);

        Assert.That(result.Mountpoints, Has.Count.EqualTo(3));
        Assert.That(caster.Requests.Select(r => r.Split("\r\n")[0]), Is.EqualTo(new[] { "GET / HTTP/1.1", "GET / HTTP/1.0" }));
    }

    [Test]
    public async Task AWrongLogin_SaysSo_InsteadOfAnEmptyList()
    {
        using var caster = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s, "HTTP/1.1 401 Unauthorized\r\n\r\n"));

        SourcetableResult result = await NtripSourcetable.FetchAsync("127.0.0.1", caster.Port, "u", "wrong", null, new NtripTimeouts(), default);

        Assert.That(result.Mountpoints, Is.Empty);
        Assert.That(result.Error, Does.Contain("Authentication failed"));
    }

    [Test]
    public async Task ACasterThatAnswersWithAStream_IsNotAList()
    {
        using var caster = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s, "ICY 200 OK\r\n\r\n"));
        var timeouts = new NtripTimeouts { Header = TimeSpan.FromMilliseconds(300) };

        SourcetableResult result = await NtripSourcetable.FetchAsync("127.0.0.1", caster.Port, "", "", null, timeouts, default);

        Assert.That(result.Mountpoints, Is.Empty);
        Assert.That(result.Error, Is.Not.Null);
    }

    [Test]
    public async Task ANoCaster_SaysSo()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        SourcetableResult result = await NtripSourcetable.FetchAsync("127.0.0.1", port, "", "", null, new NtripTimeouts(), default);

        Assert.That(result.Mountpoints, Is.Empty);
        Assert.That(result.Error, Does.Contain("Cannot reach"));
    }
}
