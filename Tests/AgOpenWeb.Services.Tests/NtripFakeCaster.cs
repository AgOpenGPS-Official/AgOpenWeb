// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// A caster on the loopback address. For each connection it reads the request, records it and runs
/// the script, which answers however the test needs (<paramref name="connection"/> counts from 1).
/// </summary>
internal sealed class NtripFakeCaster : IDisposable
{
    public delegate Task Script(int connection, string request, NetworkStream stream);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private int _accepted;

    public ConcurrentQueue<string> Requests { get; } = new();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int Accepted => Volatile.Read(ref _accepted);

    public NtripFakeCaster(Script script)
    {
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { return; }
                int connection = Interlocked.Increment(ref _accepted);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            NetworkStream stream = client.GetStream();
                            string request = await ReadRequestAsync(stream);
                            Requests.Enqueue(request);
                            await script(connection, request, stream);
                        }
                        catch
                        {
                            // The client went away.
                        }
                    }
                });
            }
        });
    }

    private static async Task<string> ReadRequestAsync(NetworkStream stream)
    {
        var request = new StringBuilder();
        var buffer = new byte[1024];
        while (!request.ToString().Contains("\r\n\r\n"))
        {
            int n = await stream.ReadAsync(buffer);
            if (n == 0) break;
            request.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }
        return request.ToString();
    }

    public static async Task SendAsync(NetworkStream stream, string text) =>
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text));

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
