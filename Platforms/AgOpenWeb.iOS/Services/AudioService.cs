// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using AgOpenWeb.Services.Audio;
using AVFoundation;
using Foundation;

namespace AgOpenWeb.iOS.Services;

/// <summary>
/// iOS audio service using AVFoundation.AVAudioPlayer.
/// </summary>
public class AudioService : AudioServiceBase
{
    // Players kept alive until they finish (else GC stops the sound mid-play). One per play so
    // sounds overlap: a single shared player cut sounds off — section on/off can fire several
    // times in quick succession, and disposing the still-playing instance dropped them.
    private readonly System.Collections.Generic.List<AVAudioPlayer> _active = new();

    public AudioService(AgOpenWeb.Models.Configuration.ConfigurationStore configStore)
        : base(configStore) { }

    protected override void PlayFile(string filePath)
    {
        var url = NSUrl.FromFilename(filePath);
        if (url == null) return;

        var player = AVAudioPlayer.FromUrl(url, out var error);
        if (error != null || player == null)
        {
            System.Diagnostics.Debug.WriteLine($"[Audio] iOS AVAudioPlayer error: {error?.LocalizedDescription}");
            player?.Dispose();
            return;
        }

        player.FinishedPlaying += (_, _) =>
        {
            lock (_active) _active.Remove(player);
            player.Dispose();
        };
        lock (_active) _active.Add(player);
        player.Play();
    }
}
