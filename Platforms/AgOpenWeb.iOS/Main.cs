// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using UIKit;

namespace AgOpenWeb.iOS;

public class Application
{
    static void Main(string[] args)
    {
        try
        {
            Console.WriteLine("[Main] Starting UIApplication.Main...");
            UIApplication.Main(args, null, typeof(AppDelegate));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Main] FATAL: {ex}");
            throw;
        }
    }
}
