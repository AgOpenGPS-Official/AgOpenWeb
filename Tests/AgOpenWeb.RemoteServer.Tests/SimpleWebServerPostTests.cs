// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using AgOpenWeb.RemoteServer;
using NUnit.Framework;

namespace AgOpenWeb.RemoteServer.Tests;

/// <summary>POST routes on the embedded server (bug report attachments): the body and query
/// reach the handler, an oversize body is refused unread, and GET still works alongside.</summary>
[TestFixture]
public class SimpleWebServerPostTests
{
    private SimpleWebServer _server = null!;
    private HttpClient _http = null!;
    private SimpleWebServer.PostRequest? _seen;

    [SetUp]
    public async Task SetUp()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _server = new SimpleWebServer(port);
        _server.MapPost("/echo", 16, req =>
        {
            _seen = req;
            return Task.FromResult(SimpleWebServer.Response.Bytes(req.Body, "application/octet-stream"));
        });
        _server.MapGet("/hello", () => SimpleWebServer.Response.Text("hi", "text/plain"));
        await _server.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    [TearDown]
    public async Task TearDown()
    {
        _http.Dispose();
        await _server.StopAsync();
    }

    [Test]
    public async Task Post_BodyAndQuery_ReachTheHandler()
    {
        var res = await _http.PostAsync("/echo?zip=bugreport_a.zip&name=Screen%20shot+1.png",
            new ByteArrayContent(Encoding.ASCII.GetBytes("12345")));
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await res.Content.ReadAsStringAsync(), Is.EqualTo("12345"));
        Assert.That(_seen!.Query["zip"], Is.EqualTo("bugreport_a.zip"));
        Assert.That(_seen.Query["name"], Is.EqualTo("Screen shot 1.png"));
    }

    [Test]
    public async Task Post_OverTheLimit_Is413_AndTheServerCarriesOn()
    {
        var res = await _http.PostAsync("/echo", new ByteArrayContent(new byte[17]));
        Assert.That((int)res.StatusCode, Is.EqualTo(413));
        Assert.That(await _http.GetStringAsync("/hello"), Is.EqualTo("hi"));
    }

    [Test]
    public async Task Post_ToAGetRoute_Is405()
    {
        var res = await _http.PostAsync("/hello", new ByteArrayContent(new byte[1]));
        Assert.That((int)res.StatusCode, Is.EqualTo(405));
    }
}
