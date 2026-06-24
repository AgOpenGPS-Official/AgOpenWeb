using System;
using System.IO;
using System.Text.Json;
using AgOpenWeb.Models;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Storage;

namespace AgOpenWeb.IntegrationTests;

/// <summary>
/// Test-isolated settings service that redirects all data I/O to a temp directory.
/// Never touches the user's real Documents/AgOpenWeb/ directory.
/// </summary>
public class TestSettingsService : ISettingsService
{
    private readonly string _basePath;
    private readonly string _settingsFilePath;

    public AppSettings Settings { get; private set; }

    public LoadOutcome LastLoadStatus { get; private set; } = LoadOutcome.Missing;

    public DateTime? RecoveredBackupTime { get; private set; }

    public event EventHandler<AppSettings>? SettingsLoaded;
    public event EventHandler<AppSettings>? SettingsSaved;

    /// <summary>
    /// Create a test settings service rooted at the given base path.
    /// The base path should contain appsettings.json and a Fields/ subdirectory.
    /// </summary>
    public TestSettingsService(string basePath)
    {
        _basePath = basePath;
        _settingsFilePath = Path.Combine(_basePath, "appsettings.json");
        Settings = new AppSettings();
    }

    public bool Load()
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                Settings = new AppSettings
                {
                    FieldsDirectory = Path.Combine(_basePath, "Fields")
                };
                return false;
            }

            var json = File.ReadAllText(_settingsFilePath);
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
            };

            var loaded = JsonSerializer.Deserialize<AppSettings>(json, options);
            if (loaded != null)
            {
                Settings = loaded;

                // Override fields directory to point at our test data
                Settings.FieldsDirectory = Path.Combine(_basePath, "Fields");

                SettingsLoaded?.Invoke(this, Settings);
                return true;
            }

            return false;
        }
        catch
        {
            Settings = new AppSettings
            {
                FieldsDirectory = Path.Combine(_basePath, "Fields")
            };
            return false;
        }
    }

    public bool Save()
    {
        // No-op: integration tests should not persist state back
        SettingsSaved?.Invoke(this, Settings);
        return true;
    }

    public void ResetToDefaults()
    {
        Settings = new AppSettings
        {
            FieldsDirectory = Path.Combine(_basePath, "Fields")
        };
    }

    public string GetSettingsFilePath() => _settingsFilePath;
}
