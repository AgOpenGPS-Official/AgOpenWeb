// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgOpenWeb.Services.Audio;
using NetCoreAudio;

namespace AgOpenWeb.Desktop.Services;

/// <summary>
/// Desktop audio service using NetCoreAudio (Windows, macOS, Linux).
/// </summary>
public class AudioService : AudioServiceBase
{
    private readonly Player _player = new();

    public AudioService(AgOpenWeb.Models.Configuration.ConfigurationStore configStore)
        : base(configStore) { }

    protected override void PlayFile(string filePath)
    {
        // Fire and forget - don't await, don't block
        _ = _player.Play(filePath);
    }
}
