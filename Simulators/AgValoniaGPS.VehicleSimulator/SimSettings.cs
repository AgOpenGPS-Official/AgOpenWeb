// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.IO;
using System.Text.Json;

namespace AgValoniaGPS.VehicleSimulator;

/// <summary>
/// Persists the operator's starting GPS pose so it survives restarts — re-entering
/// the coordinates on every launch was a pain. Stored as JSON under the user's
/// app-data directory; the ViewModel saves it whenever a pose value is edited while
/// the simulator is stopped (so live driving drift never overwrites the start point).
/// </summary>
public sealed class SimSettings
{
    public double Latitude { get; set; } = 42.0308;
    public double Longitude { get; set; } = -93.6319;
    public double Heading { get; set; }

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AgValoniaGPS");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "vehsim.json");
        }
    }

    public static SimSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<SimSettings>(File.ReadAllText(FilePath)) ?? new SimSettings();
        }
        catch { /* missing or corrupt → fall back to defaults */ }
        return new SimSettings();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
        catch { /* best-effort persistence; never crash the sim over it */ }
    }
}
