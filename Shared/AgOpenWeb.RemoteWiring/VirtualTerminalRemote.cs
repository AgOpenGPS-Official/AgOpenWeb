// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.RemoteServer;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.RemoteWiring;

/// <summary>
/// The VT cards' frames: the terminals' messages and pictures, relayed to every browser as the
/// terminals wrote them, each under its terminal, on the broadcaster's tick and in order (a
/// picture before the screen that uses it). A browser that connects later gets the link state,
/// every picture and the latest of each message that describes a terminal's state. Not started
/// without a terminal (a head built without one); the cards then never show.
/// </summary>
internal static class VirtualTerminalRemote
{
    private static readonly object _gate = new();
    private static readonly Dictionary<uint, byte[]> _images = new();
    private static readonly Dictionary<(int Terminal, string Type), byte[]> _latest = new();
    private static List<byte[]> _pending = new();
    private static int _session = -1;

    public static void Start(RemoteServerHost server, IVirtualTerminalService vt)
    {
        vt.Message += OnMessage;
        vt.Image += (id, w, h, rgba) =>
        {
            var frame = WireCodec.EncodeVtImage(id, w, h, rgba);
            lock (_gate)
            {
                _images[id] = frame;
                _pending.Add(frame);
            }
        };
        vt.InputRefused += () => server.ShowToast("Value not accepted");
        server.VtSeedProvider = Seed;
        server.VtFramesProvider = Drain;
        vt.Start();
    }

    private static void OnMessage(int terminal, string json)
    {
        // Every message starts {"type":"NAME" (the terminal's contract). The ones that describe
        // state are kept for browsers that connect later.
        const string head = "{\"type\":\"";
        int end = json.StartsWith(head, StringComparison.Ordinal) ? json.IndexOf('"', head.Length) : -1;
        var type = end > 0 ? json[head.Length..end] : "";
        var frame = WireCodec.EncodeVt(terminal, json);
        lock (_gate)
        {
            if (type == "link" && SessionOf(json) is { } session && session != _session)
            {
                // New terminals: the old ones' pictures and screens are gone.
                _session = session;
                _images.Clear();
                _latest.Clear();
            }
            if (type is "link" or "screen" or "edit" or "workingSets" or "alarms")
                _latest[(terminal, type)] = frame;
            _pending.Add(frame);
        }
    }

    private static int? SessionOf(string json)
    {
        const string key = "\"session\":";
        int i = json.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return null;
        i += key.Length;
        int n = 0, start = i;
        while (i < json.Length && char.IsAsciiDigit(json[i])) n = n * 10 + (json[i++] - '0');
        return i > start ? n : null;
    }

    private static IReadOnlyList<byte[]>? Drain()
    {
        lock (_gate)
        {
            if (_pending.Count == 0) return null;
            var frames = _pending;
            _pending = new List<byte[]>();
            return frames;
        }
    }

    private static IReadOnlyList<byte[]> Seed()
    {
        lock (_gate)
        {
            var frames = new List<byte[]>();
            if (_latest.TryGetValue((-1, "link"), out var link)) frames.Add(link);
            frames.AddRange(_images.Values);
            foreach (var terminal in _latest.Keys.Select(k => k.Terminal).Where(t => t >= 0).Distinct().Order())
                foreach (var type in new[] { "workingSets", "alarms", "screen", "edit" })
                    if (_latest.TryGetValue((terminal, type), out var f)) frames.Add(f);
            return frames;
        }
    }
}
