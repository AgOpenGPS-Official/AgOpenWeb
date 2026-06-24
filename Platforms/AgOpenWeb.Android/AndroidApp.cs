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
using Avalonia;
using Avalonia.Android;

namespace AgOpenWeb.Android;

[global::Android.App.Application]
public class AndroidApp : AvaloniaAndroidApplication<App>
{
    protected AndroidApp(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // Skia's default GPU resource cache is ~28 MB. Our coverage bitmap
        // alone is ~50 MB (5000x5000 Rgb565). At the default, the texture is
        // re-uploaded every frame, burning 20+ FPS on mobile. 192 MB fits the
        // coverage bitmap + its mipmap chain (~33% extra) + other textures
        // with comfortable headroom on 4 GB tablets.
        return base.CustomizeAppBuilder(builder)
            .LogToTrace()
            .With(new SkiaOptions
            {
                MaxGpuResourceSizeBytes = 192L * 1024 * 1024
            });
    }
}
