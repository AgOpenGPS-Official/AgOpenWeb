// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Services;

/// <summary>
/// Reads a stream with a time limit per call. A read that times out keeps running and the next
/// call waits for that same read, so two reads are never active on one stream.
/// </summary>
internal sealed class TimedReader
{
    private readonly Stream _stream;
    private Task<int>? _pending;

    public byte[] Buffer { get; } = new byte[16384];

    public TimedReader(Stream stream) => _stream = stream;

    /// <summary>Bytes read into <see cref="Buffer"/>, 0 when the other side closed, -1 on timeout.</summary>
    public async Task<int> ReadAsync(TimeSpan timeout, CancellationToken ct)
    {
        _pending ??= _stream.ReadAsync(Buffer, 0, Buffer.Length, CancellationToken.None);
        if (timeout < TimeSpan.Zero) timeout = TimeSpan.Zero;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task done = await Task.WhenAny(_pending, Task.Delay(timeout, linked.Token)).ConfigureAwait(false);
        if (done != _pending)
        {
            ct.ThrowIfCancellationRequested();
            return -1;
        }
        linked.Cancel();
        Task<int> read = _pending;
        _pending = null;
        return await read.ConfigureAwait(false);
    }

    /// <summary>Observe a read that fails after the stream was closed, so nothing goes unobserved.</summary>
    public void Abandon()
    {
        _pending?.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        _pending = null;
    }
}
