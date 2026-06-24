// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using AgOpenWeb.Services.Audio;
using Android.Media;

namespace AgOpenWeb.Android.Services;

/// <summary>
/// Android audio service using Android.Media.MediaPlayer.
/// </summary>
public class AudioService : AudioServiceBase
{
    private MediaPlayer? _mediaPlayer;

    public AudioService(AgOpenWeb.Models.Configuration.ConfigurationStore configStore)
        : base(configStore) { }

    protected override void PlayFile(string filePath)
    {
        _mediaPlayer?.Release();
        _mediaPlayer = null;

        _mediaPlayer = new MediaPlayer();
        _mediaPlayer.SetDataSource(filePath);
        _mediaPlayer.Prepare();
        _mediaPlayer.Start();
        _mediaPlayer.Completion += (s, e) =>
        {
            _mediaPlayer?.Release();
            _mediaPlayer = null;
        };
    }
}
