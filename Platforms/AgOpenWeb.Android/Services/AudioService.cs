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
    public AudioService(AgOpenWeb.Models.Configuration.ConfigurationStore configStore)
        : base(configStore) { }

    protected override void PlayFile(string filePath)
    {
        // One self-releasing player per sound. A single shared MediaPlayer cut sounds off:
        // section on/off can fire several times in quick succession, and releasing the still-
        // playing instance before starting the next dropped them inconsistently. Each play now
        // gets its own player so they overlap, and PrepareAsync keeps the host control loop
        // (which raises these) from blocking on prepare. Callbacks arrive on the main looper
        // (the host-loop thread has none), so cleanup is safe.
        var player = new MediaPlayer();
        void Release() { try { player.Release(); } catch { /* already gone */ } }
        player.Completion += (_, _) => Release();
        player.Error += (_, e) => { e.Handled = true; Release(); };
        player.Prepared += (_, _) => { try { player.Start(); } catch { Release(); } };
        try
        {
            player.SetDataSource(filePath);
            player.PrepareAsync();
        }
        catch
        {
            Release();
        }
    }
}
