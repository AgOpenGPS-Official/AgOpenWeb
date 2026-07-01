// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Audio;

/// <summary>
/// Audio service for the web-UI architecture. The host backend never plays sound
/// locally — a headless server box has no speaker, and on Linux the old native
/// path shelled out to <c>aplay</c>, which floods the journal with ALSA errors
/// when there's no sound card. Instead this applies the per-sound config gating
/// and raises <see cref="EffectTriggered"/>; <c>RemoteServerHost</c> forwards the
/// effect to connected web clients, which play the matching .wav. The client
/// (a browser, or the embedded WebView in a launcher) is where the operator —
/// and the speaker — actually is, so alerts sound in every deployment.
/// </summary>
public sealed class WebClientAudioService : IAudioService
{
    private readonly ConfigurationStore _configStore;

    public WebClientAudioService(ConfigurationStore configStore) => _configStore = configStore;

    /// <inheritdoc/>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Raised on the caller's thread when a sound effect passes the global enable
    /// flag and its per-sound config toggle. The RemoteServer subscribes and
    /// broadcasts the effect to clients.
    /// </summary>
    public event Action<SoundEffect>? EffectTriggered;

    /// <inheritdoc/>
    public void Play(SoundEffect sound)
    {
        if (!IsEnabled) return;

        // Per-sound config toggles — identical gating to the old native players,
        // kept here so the host decides what's audible and the client just plays
        // whatever it's told (one source of truth for the toggles).
        var display = _configStore.Display;
        switch (sound)
        {
            case SoundEffect.AutoSteerOn:
            case SoundEffect.AutoSteerOff:
                if (!display.AutoSteerSound) return;
                break;
            case SoundEffect.UTurnTooClose:
                if (!display.UTurnSound) return;
                break;
            case SoundEffect.HydraulicLiftUp:
            case SoundEffect.HydraulicLiftDown:
                if (!display.HydraulicSound) return;
                break;
            case SoundEffect.SectionOn:
            case SoundEffect.SectionOff:
                if (!display.SectionsSound) return;
                break;
        }

        EffectTriggered?.Invoke(sound);
    }
}
