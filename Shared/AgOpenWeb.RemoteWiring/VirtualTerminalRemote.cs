// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.RemoteServer;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.RemoteWiring;

/// <summary>
/// The VT card's frames: the terminal's messages and pictures, relayed to every browser as the
/// terminal wrote them, on the broadcaster's tick and in order (a picture before the screen that
/// uses it). A browser that connects later gets the link state, every picture and the latest
/// of each message that describes state. Not started without a terminal (a head built without
/// one); the card then never shows.
/// </summary>
internal static class VirtualTerminalRemote
{
    private static readonly object _gate = new();
    private static readonly Dictionary<uint, byte[]> _images = new();
    private static readonly Dictionary<string, byte[]> _latest = new();
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

    private static void OnMessage(string json)
    {
        // Every message starts {"type":"NAME" (the terminal's contract). The ones that describe
        // state are kept for browsers that connect later.
        const string head = "{\"type\":\"";
        int end = json.StartsWith(head, StringComparison.Ordinal) ? json.IndexOf('"', head.Length) : -1;
        var type = end > 0 ? json[head.Length..end] : "";
        var frame = WireCodec.EncodeVt(json);
        lock (_gate)
        {
            if (type == "link" && SessionOf(json) is { } session && session != _session)
            {
                // A new terminal: the old one's pictures and screens are gone.
                _session = session;
                _images.Clear();
                _latest.Clear();
            }
            if (type is "link" or "screen" or "edit" or "workingSets" or "alarms")
                _latest[type] = frame;
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
            if (_latest.TryGetValue("link", out var link)) frames.Add(link);
            frames.AddRange(_images.Values);
            foreach (var type in new[] { "workingSets", "alarms", "screen", "edit" })
                if (_latest.TryGetValue(type, out var f)) frames.Add(f);
            return frames;
        }
    }
}
