// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Profile;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Guard: every setting in <see cref="ConfigurationStore"/> is saved with the vehicle or
/// tool profile, or is listed below with where it lives instead. Roll calibration (#248)
/// and the whole Machine tab were added to the store and its UI but never to a profile,
/// so they reset on every restart; a new setting that repeats that fails here.
/// </summary>
[TestFixture]
public class ProfilePersistenceGuardTests
{
    /// <summary>Sections kept somewhere other than the vehicle and tool profiles.</summary>
    private static readonly Dictionary<string, string> SectionsStoredElsewhere = new()
    {
        ["Display"] = "AppSettings (ConfigurationService)",
        ["Simulator"] = "AppSettings / PersistentAppState",
        ["Hotkeys"] = "AppSettings.HotkeyBindings",
        ["AutoSteer"] = "<vehicle>.autosteer file (ConfigurationService.SaveAutoSteerConfig)",
        ["Tram"] = "the field's tram file (TramConfigFileService); pass count is Guidance.TramPasses",
    };

    /// <summary>Single settings not in a profile, and why.</summary>
    private static readonly Dictionary<string, string> SettingsStoredElsewhere = new()
    {
        ["Vehicle.Name"] = "the profile's file name",
        // App-wide, in AppSettings: one NTRIP / AgShare / module setup per installation.
        ["Connections.NtripCasterHost"] = "AppSettings",
        ["Connections.NtripCasterPort"] = "AppSettings",
        ["Connections.NtripMountPoint"] = "AppSettings",
        ["Connections.NtripUsername"] = "AppSettings",
        ["Connections.NtripPassword"] = "AppSettings",
        ["Connections.NtripAutoConnect"] = "AppSettings",
        ["Connections.AgShareServer"] = "AppSettings",
        ["Connections.AgShareApiKey"] = "AppSettings",
        ["Connections.AgShareEnabled"] = "AppSettings",
        ["Connections.GpsUpdateRate"] = "AppSettings",
        ["Connections.UseRtk"] = "AppSettings",
        ["Connections.IsGpsConfigured"] = "AppSettings",
        ["Connections.IsImuConfigured"] = "AppSettings",
        ["Connections.IsAutoSteerConfigured"] = "AppSettings",
        ["Connections.IsMachineConfigured"] = "AppSettings",
        // Unused: no control in the web client and nothing reads them at runtime.
        ["Connections.HeadingSource"] = "unused",
        ["Connections.MinFixQuality"] = "unused",
        ["Connections.MaxDifferentialAge"] = "unused",
        ["Connections.MaxHdop"] = "unused",
        ["Ahrs.FusionWeight"] = "unused (the live one is Connections.HeadingFusionWeight)",
        ["Ahrs.ForwardCompensation"] = "unused",
        ["Ahrs.ReverseCompensation"] = "unused",
        ["Ahrs.IsAutoSteerAuto"] = "unused",
        ["Ahrs.IsDualAsIMU"] = "unused",
    };

    [Test]
    public void EverySetting_SurvivesAProfileSaveAndLoad_OrIsListedAsStoredElsewhere()
    {
        string root = Path.Combine(Path.GetTempPath(), "aow-guard-" + Guid.NewGuid().ToString("N"));
        string vehicles = Path.Combine(root, "Vehicles"), tools = Path.Combine(root, "Tools");
        Directory.CreateDirectory(vehicles);
        Directory.CreateDirectory(tools);
        try
        {
            var src = new ConfigurationStore();
            var def = new ConfigurationStore();
            var settings = Settings(src).ToList();
            foreach (var (_, owner, prop) in settings)
                prop.SetValue(owner, Changed(prop.GetValue(owner), prop.PropertyType));

            VehicleProfileJsonService.Save(vehicles, "Guard", src);
            ToolProfileJsonService.Save(tools, "Guard", src);
            var dst = new ConfigurationStore();
            Assert.That(VehicleProfileJsonService.Load(vehicles, "Guard", dst), Is.True);
            Assert.That(ToolProfileJsonService.Load(tools, "Guard", dst), Is.True);

            var lost = new List<string>();
            var listedButSaved = new List<string>();
            foreach (var (name, owner, prop) in settings)
            {
                string section = name[..name.IndexOf('.')];
                object sectionOf(ConfigurationStore s) => typeof(ConfigurationStore).GetProperty(section)!.GetValue(s)!;
                string changed = Text(prop.GetValue(owner));
                if (changed == Text(prop.GetValue(sectionOf(def)))) continue;   // setter clamped it back
                bool survived = changed == Text(prop.GetValue(sectionOf(dst)));
                bool listed = SettingsStoredElsewhere.ContainsKey(name);
                if (!survived && !listed) lost.Add(name);
                if (survived && listed) listedButSaved.Add(name);
            }

            Assert.Multiple(() =>
            {
                Assert.That(lost, Is.Empty,
                    "not saved with the vehicle or tool profile: add them to VehicleProfileJsonService / "
                    + "ToolProfileJsonService, or list them in this test with where they are stored");
                Assert.That(listedButSaved, Is.Empty, "listed as stored elsewhere, but a profile saves them");
            });
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // Every publicly settable bool / number / string / enum of every profile-backed section.
    private static IEnumerable<(string Name, object Owner, PropertyInfo Prop)> Settings(ConfigurationStore store)
    {
        foreach (var section in typeof(ConfigurationStore).GetProperties()
                     .Where(p => p.PropertyType.IsClass && p.PropertyType.Namespace == typeof(ConfigurationStore).Namespace
                                 && p.GetIndexParameters().Length == 0 && p.Name != nameof(ConfigurationStore.Instance)))
        {
            if (SectionsStoredElsewhere.ContainsKey(section.Name)) continue;
            object owner = section.GetValue(store)!;
            foreach (var p in owner.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var t = p.PropertyType;
                if (p.GetSetMethod() == null || p.GetIndexParameters().Length > 0) continue;
                if (t == typeof(bool) || t == typeof(int) || t == typeof(double) || t == typeof(string) || t.IsEnum)
                    yield return ($"{section.Name}.{p.Name}", owner, p);
            }
        }
    }

    private static object Changed(object? value, Type type)
    {
        if (type == typeof(bool)) return !(bool)value!;
        if (type == typeof(int)) return (int)value! + 1;
        if (type == typeof(double)) return (double)value! + 0.25;
        if (type == typeof(string)) return (string?)value + "x";
        var values = Enum.GetValues(type);
        return values.GetValue((Array.IndexOf(values, value) + 1) % values.Length)!;
    }

    private static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
}
