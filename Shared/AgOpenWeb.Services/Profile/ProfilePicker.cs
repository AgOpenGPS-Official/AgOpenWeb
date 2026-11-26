// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Profile;

/// <summary>
/// The Vehicle &amp; Tool picker (the web client's profile hub): load a vehicle and tool pair,
/// New (the live settings under a new name, which becomes the one in use), Delete, Rename and
/// Reset. Every refusal says why: the picker used to drop a delete of the profile in use, a
/// rename onto another profile or a load of a missing one without a word, write
/// <c>Puma 180/CVX</c> into a sub-folder and <c>../x</c> outside the Vehicles folder, and New
/// kept the old profile in use, so the next measurements went into it.
/// </summary>
public static class ProfilePicker
{
    /// <summary>
    /// Runs one of the picker's <c>profile.*</c> commands (arguments tab-separated). Returns
    /// what the operator must be told when it is refused or changes more than asked, or null.
    /// </summary>
    public static string? Apply(ConfigurationStore store, IConfigurationService cfg, string cmd, string arg)
    {
        var a = arg.Split('\t');
        string kind = a.Length > 0 && a[0] == "tool" ? "tool" : "vehicle";
        IReadOnlyList<string> Existing(string k) => k == "tool" ? cfg.GetAvailableToolProfiles() : cfg.GetAvailableProfiles();
        // A profile the picker lists, by its exact name; anything else (a name typed by
        // hand, a path) is not opened, deleted or renamed.
        string? Missing(string k, string name) =>
            Existing(k).Contains(name, StringComparer.Ordinal) ? null : $"There is no {k} profile named '{name}'";
        // The pair in use may not be saved yet (a first start): it stays loadable.
        string? Unknown(string k, string name) =>
            name == (k == "tool" ? store.ActiveToolProfileName : store.ActiveVehicleProfileName) ? null : Missing(k, name);
        string? Load(string vehicle, string tool)
        {
            if ((Unknown("vehicle", vehicle) ?? Unknown("tool", tool)) is { } missing) return missing;
            cfg.SaveProfiles(store.ActiveVehicleProfileName, store.ActiveToolProfileName);
            if (!cfg.LoadProfiles(vehicle, tool)) return $"The vehicle profile '{vehicle}' could not be read";
            // A combine takes only a front implement, its header (#111): the type picker keeps
            // to that, so a load does too.
            if (store.Vehicle.Type == VehicleType.Harvester && !store.Tool.IsToolFrontFixed)
            {
                store.Tool.SetToolType("front");
                return "Implement set to front: a combine takes only a front implement";
            }
            return null;
        }

        switch (cmd)
        {
            case "profile.load": // <vehicle>\t<tool> — save the outgoing pair, then load
                return a.Length == 2 ? Load(a[0], a[1]) : null;
            case "profile.new": // <kind>\t<name> — the live settings under a new name, which becomes the active one
            {
                if (a.Length != 2) return null;
                string name = a[1].Trim();
                if (ProfileNames.WhyNot(name) is { } bad) return bad;
                if (ProfileNames.Twin(Existing(kind), name) is { } twin)
                    return $"There is already a {kind} profile named '{twin}'";
                if (kind == "vehicle") cfg.SaveProfilesAs(name, store.ActiveToolProfileName);
                else cfg.SaveProfilesAs(store.ActiveVehicleProfileName, name);
                return null;
            }
            case "profile.delete": // <kind>\t<name>
            {
                if (a.Length != 2) return null;
                if (Missing(kind, a[1]) is { } missing) return missing;
                string active = kind == "vehicle" ? store.ActiveVehicleProfileName : store.ActiveToolProfileName;
                if (string.Equals(a[1], active, StringComparison.OrdinalIgnoreCase))
                    return $"The {kind} profile in use can't be deleted: load another one first";
                bool deleted = kind == "vehicle" ? cfg.DeleteVehicleProfile(a[1]) : cfg.DeleteToolProfile(a[1]);
                return deleted ? null : $"The {kind} profile '{a[1]}' could not be deleted";
            }
            case "profile.rename": // <kind>\t<old>\t<new>
            {
                if (a.Length != 3) return null;
                string name = a[2].Trim();
                if (Missing(kind, a[1]) is { } missing) return missing;
                if (ProfileNames.WhyNot(name) is { } bad) return bad;
                // A case-only rename of the same profile is fine; another profile's name is not.
                if (ProfileNames.Twin(Existing(kind).Where(n => n != a[1]), name) is { } twin)
                    return $"There is already a {kind} profile named '{twin}'";
                bool renamed = kind == "vehicle" ? cfg.RenameVehicleProfile(a[1], name) : cfg.RenameToolProfile(a[1], name);
                return renamed ? null : $"The {kind} profile '{a[1]}' could not be renamed";
            }
            case "profile.reset": // <kind> — reset only that column's Default profile (#111)
                cfg.CreateProfile("Default", vehicle: a[0] != "tool", tool: a[0] != "vehicle");
                return null;
            case "profile.configureVehicle": // <name> — make it active before the dialog opens
                return string.Equals(arg, store.ActiveVehicleProfileName, StringComparison.OrdinalIgnoreCase)
                    ? null : Load(arg, store.ActiveToolProfileName);
            case "profile.configureTool": // <name>
                return string.Equals(arg, store.ActiveToolProfileName, StringComparison.OrdinalIgnoreCase)
                    ? null : Load(store.ActiveVehicleProfileName, arg);
        }
        return null;
    }
}
