// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using Android.Runtime;

namespace AgOpenWeb.Android;

[global::Android.App.Application(UsesCleartextTraffic = true)]
public class AndroidApp : global::Android.App.Application
{
    // internal set: the foreground BackendService's AndroidBackendHost owns the DI provider and
    // publishes it here so lookups from the Activity (save-on-background) resolve.
    public static IServiceProvider? Services { get; internal set; }

    protected AndroidApp(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    public override void OnCreate()
    {
        base.OnCreate();

        // The one point Android guarantees runs before any Activity or Service in this
        // process — cold start via the launcher icon and a sticky BackendService restart
        // (which can happen without MainActivity ever running) both go through here first.
        // Must happen before anything reads AppDataRoot.Documents.
        AgOpenWeb.Android.Services.AndroidDataRoot.Initialize(this);
    }
}
