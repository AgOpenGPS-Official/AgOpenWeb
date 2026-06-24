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
    private AVAudioPlayer? _currentPlayer;

    public AudioService(AgOpenWeb.Models.Configuration.ConfigurationStore configStore)
        : base(configStore) { }

    protected override void PlayFile(string filePath)
    {
        _currentPlayer?.Dispose();
        _currentPlayer = null;

        var url = NSUrl.FromFilename(filePath);
        if (url == null) return;

        _currentPlayer = AVAudioPlayer.FromUrl(url, out var error);
        if (error != null)
        {
            System.Diagnostics.Debug.WriteLine($"[Audio] iOS AVAudioPlayer error: {error.LocalizedDescription}");
            return;
        }

        _currentPlayer?.Play();
    }
}
