// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.Extensions.Logging;
using AgOpenWeb.Models.Ntrip;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Profile;
using AgOpenWeb.Services.Storage;

namespace AgOpenWeb.Services;

/// <summary>
/// Service for managing NTRIP profiles stored as JSON files
/// </summary>
public class NtripProfileService : INtripProfileService
{
    private readonly ISettingsService _settingsService;
    private readonly ILogger<NtripProfileService> _logger;
    private readonly List<NtripProfile> _profiles = new();
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string ProfilesDirectory { get; }

    public IReadOnlyList<NtripProfile> Profiles => _profiles.AsReadOnly();

    public NtripProfile? DefaultProfile => _profiles.FirstOrDefault(p => p.IsDefault);

    public event EventHandler? ProfilesChanged;

    public NtripProfileService(ISettingsService settingsService, ILogger<NtripProfileService> logger)
    {
        _settingsService = settingsService;
        _logger = logger;

        ProfilesDirectory = Path.Combine(AppDataRoot.Documents, "NtripProfiles");

        if (!Directory.Exists(ProfilesDirectory))
        {
            Directory.CreateDirectory(ProfilesDirectory);
        }
    }

    public NtripProfile? GetProfileForField(string fieldDirectoryName)
    {
        // First look for a profile that has this field in its associations
        var fieldProfile = _profiles.FirstOrDefault(p =>
            p.AssociatedFields.Contains(fieldDirectoryName, StringComparer.OrdinalIgnoreCase));

        if (fieldProfile != null)
            return fieldProfile;

        // Fall back to default profile
        return DefaultProfile;
    }

    public async Task LoadProfilesAsync()
    {
        _profiles.Clear();

        if (!Directory.Exists(ProfilesDirectory))
        {
            Directory.CreateDirectory(ProfilesDirectory);
        }

        var jsonFiles = Directory.GetFiles(ProfilesDirectory, "*.json");
        foreach (var file in jsonFiles)
        {
            // Crash-safe load: a damaged primary transparently recovers from its
            // .bak last-known-good copy. NTRIP recovery is silent + logged
            // (unlike vehicle/tool/settings, which prompt) since profiles load
            // asynchronously and there may be several.
            var result = AtomicJsonFile.Read<NtripProfile>(file, _jsonOptions);
            if (result.Loaded && result.Value != null)
            {
                if (result.Recovered)
                {
                    _logger.LogWarning(
                        "NTRIP profile '{File}' was damaged; recovered from backup (saved {Time}).",
                        file, result.BackupTimestamp);
                }
                result.Value.FilePath = file;
                _profiles.Add(result.Value);
            }
            else if (result.Outcome == LoadOutcome.CorruptNoBackup)
            {
                _logger.LogError("NTRIP profile '{File}' is damaged and has no usable backup.", file);
            }
        }

        // Migrate legacy settings if no profiles exist
        if (_profiles.Count == 0)
        {
            await MigrateLegacySettingsAsync();
        }

        await ImportAgIoCastersAsync();

        // Sort by name
        _profiles.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// AgOpenGPS's NTRIP caster, from AgIO's settings (<c>AgIO/&lt;name&gt;.XML</c>, copied into the
    /// data folder with the AgOpenGPS profiles): a profile for each caster and mount point not
    /// here yet. It becomes the default, and connects by itself, only when NTRIP was switched on
    /// in AgIO and there is no default yet. AgIO writes every field, so a farmer who never set
    /// NTRIP up has the caster AgIO ships with: that one is not imported. As with the AgOpenGPS
    /// profiles, the copied file is deleted once read, so a profile deleted later does not come back.
    /// </summary>
    private async Task ImportAgIoCastersAsync()
    {
        string agIo = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ProfilesDirectory)) ?? "", "AgIO");
        if (!Directory.Exists(agIo))
            return;
        var files = Directory.EnumerateFiles(agIo)
            .Where(f => string.Equals(Path.GetExtension(f), ".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        foreach (var file in files)
        {
            Dictionary<string, string> s;
            try { s = AgOpenGpsSettings.Read(file); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AgIO settings '{File}' could not be read", file);
                continue;
            }
            string Value(string key) => s.TryGetValue(key, out var v) ? v.Trim() : "";
            string host = Value("setNTRIP_casterURL");
            if (host.Length == 0) host = Value("setNTRIP_casterIP");
            string mount = Value("setNTRIP_mount");
            if (host.Length == 0 || mount.Length == 0)
                continue; // no caster set in AgIO
            int port = int.TryParse(Value("setNTRIP_casterPort"), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var p) && p > 0 ? p : 2101;
            bool isOn = bool.TryParse(Value("setNTRIP_isOn"), out var on) && on;
            string user = Value("setNTRIP_userName");
            string password = s.TryGetValue("setNTRIP_userPassword", out var pw) ? pw : "";

            bool known = _profiles.Any(x => string.Equals(x.CasterHost, host, StringComparison.OrdinalIgnoreCase)
                && x.CasterPort == port && string.Equals(x.MountPoint, mount, StringComparison.Ordinal));
            if (IsAgIoShippedCaster(host, port, mount, user, password, isOn))
            {
                _logger.LogInformation("AgIO's settings '{File}' hold the caster AgIO ships with, NTRIP off: not imported", file);
            }
            else if (!known)
            {
                string name = mount + " (AgIO)";
                for (int n = 2; _profiles.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
                    name = mount + " (AgIO " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
                var profile = new NtripProfile
                {
                    Name = name,
                    CasterHost = host,
                    CasterPort = port,
                    MountPoint = mount,
                    Username = user,
                    Password = password,
                    AutoConnectOnFieldLoad = isOn,
                    IsDefault = isOn && DefaultProfile == null,
                };
                await SaveProfileAsync(profile);
                _logger.LogInformation("Imported AgIO's NTRIP caster {Host}:{Port}/{Mount} as '{Name}'", host, port, mount, name);
            }
            try { File.Delete(file); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not delete the imported AgIO settings '{File}'", file); }
        }
    }

    /// <summary>
    /// The caster AgIO's settings start with (AgOpenGPS <c>AgIO/Source/Properties/Settings.cs</c>:
    /// <c>NTRIP.itsware.net</c>:2101, mount <c>SCSC</c>, no login, NTRIP off): nobody set it up.
    /// With NTRIP switched on or a login typed in, it is the farmer's caster.
    /// </summary>
    private static bool IsAgIoShippedCaster(string host, int port, string mount, string user, string password, bool isOn)
        => !isOn && user.Length == 0 && password.Length == 0 && port == 2101
           && string.Equals(mount, "SCSC", StringComparison.Ordinal)
           && (string.Equals(host, "NTRIP.itsware.net", StringComparison.OrdinalIgnoreCase)
               || string.Equals(host, "69.75.31.235", StringComparison.Ordinal));

    /// <summary>
    /// Migrates legacy NTRIP settings from appsettings.json to a default profile
    /// </summary>
    private async Task MigrateLegacySettingsAsync()
    {
        var settings = _settingsService.Settings;

        // Only migrate if there are legacy settings
        if (string.IsNullOrEmpty(settings.NtripCasterIp))
        {
            _logger.LogDebug("No legacy settings to migrate");
            return;
        }

        _logger.LogInformation("Migrating legacy NTRIP settings to default profile...");

        var defaultProfile = new NtripProfile
        {
            Name = "Default",
            CasterHost = settings.NtripCasterIp,
            CasterPort = settings.NtripCasterPort,
            MountPoint = settings.NtripMountPoint,
            Username = settings.NtripUsername,
            Password = settings.NtripPassword,
            AutoConnectOnFieldLoad = settings.NtripAutoConnect,
            IsDefault = true
        };

        await SaveProfileAsync(defaultProfile);
        _logger.LogInformation("Created default profile from legacy settings: {Host}:{Port}/{MountPoint}",
            defaultProfile.CasterHost, defaultProfile.CasterPort, defaultProfile.MountPoint);
    }

    public async Task SaveProfileAsync(NtripProfile profile)
    {
        // Ensure directory exists
        if (!Directory.Exists(ProfilesDirectory))
        {
            Directory.CreateDirectory(ProfilesDirectory);
        }

        // Generate file path if not set
        if (string.IsNullOrEmpty(profile.FilePath))
        {
            var safeFileName = SanitizeFileName(profile.Name);
            profile.FilePath = Path.Combine(ProfilesDirectory, $"{safeFileName}.json");
        }

        // If this profile is being set as default, clear default on others
        if (profile.IsDefault)
        {
            foreach (var p in _profiles.Where(p => p.Id != profile.Id && p.IsDefault))
            {
                p.IsDefault = false;
                await SaveProfileToFileAsync(p);
            }
        }

        await SaveProfileToFileAsync(profile);

        // Update in-memory list
        var existing = _profiles.FindIndex(p => p.Id == profile.Id);
        if (existing >= 0)
        {
            _profiles[existing] = profile;
        }
        else
        {
            _profiles.Add(profile);
            _profiles.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }

        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteProfileAsync(string profileId)
    {
        var profile = _profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile == null)
            return;

        // Delete file
        if (!string.IsNullOrEmpty(profile.FilePath) && File.Exists(profile.FilePath))
        {
            await Task.Run(() => File.Delete(profile.FilePath));
        }

        // Remove from in-memory list
        _profiles.Remove(profile);

        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetDefaultProfileAsync(string? profileId)
    {
        foreach (var profile in _profiles)
        {
            var shouldBeDefault = profile.Id == profileId;
            if (profile.IsDefault != shouldBeDefault)
            {
                profile.IsDefault = shouldBeDefault;
                await SaveProfileToFileAsync(profile);
            }
        }

        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    public NtripProfile CreateNewProfile(string name)
    {
        return new NtripProfile
        {
            Name = name,
            CasterPort = 2101,
            AutoConnectOnFieldLoad = true
        };
    }

    public IReadOnlyList<string> GetAvailableFields()
    {
        var fieldsDirectory = _settingsService.Settings.FieldsDirectory;
        if (string.IsNullOrEmpty(fieldsDirectory) || !Directory.Exists(fieldsDirectory))
            return Array.Empty<string>();

        return Directory.GetDirectories(fieldsDirectory)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .OrderBy(name => name)
            .ToList()!;
    }

    private Task SaveProfileToFileAsync(NtripProfile profile)
    {
        // Atomic write + .bak last-known-good (see AtomicJsonFile). These files
        // are tiny, so the synchronous write is negligible.
        AtomicJsonFile.WriteJson(profile.FilePath, profile, _jsonOptions);
        return Task.CompletedTask;
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName
            .Select(c => invalidChars.Contains(c) ? '_' : c)
            .ToArray());
        return string.IsNullOrEmpty(sanitized) ? "Profile" : sanitized;
    }
}
