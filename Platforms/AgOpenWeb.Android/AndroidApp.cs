// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

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
