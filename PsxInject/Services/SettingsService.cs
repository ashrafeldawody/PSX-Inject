using System.IO;
using System.Text.Json;
using PsxInject.Models;

namespace PsxInject.Services;

public static class SettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PsxInject");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    /// <summary>
    /// Legacy settings path from when the project was named PsxDownloadHelper.
    /// On first launch we copy any existing settings.json forward so users
    /// don't lose state across the rename.
    /// </summary>
    private static readonly string LegacySettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PsxDownloadHelper");

    private static readonly string LegacySettingsPath = Path.Combine(LegacySettingsDir, "settings.json");

    static SettingsService()
    {
        try
        {
            if (!File.Exists(SettingsPath) && File.Exists(LegacySettingsPath))
            {
                Directory.CreateDirectory(SettingsDir);
                File.Copy(LegacySettingsPath, SettingsPath, overwrite: false);
            }
        }
        catch
        {
            // Best-effort — we'll just start with defaults if the copy failed.
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private const int CurrentSettingsVersion = 3;

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings
                {
                    DataDirectory = AppSettings.DefaultDataDirectory,
                    SettingsVersion = CurrentSettingsVersion
                };
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();

            if (string.IsNullOrWhiteSpace(settings.DataDirectory))
                settings.DataDirectory = AppSettings.DefaultDataDirectory;

            // One-time migration for users on older defaults.
            if (settings.SettingsVersion < CurrentSettingsVersion)
            {
                Migrate(settings, settings.SettingsVersion);
                settings.SettingsVersion = CurrentSettingsVersion;
                Save(settings);
            }

            return settings;
        }
        catch
        {
            return new AppSettings
            {
                DataDirectory = AppSettings.DefaultDataDirectory,
                SettingsVersion = CurrentSettingsVersion
            };
        }
    }

    private static void Migrate(AppSettings settings, int fromVersion)
    {
        // v0/v1 → v2: auto-start default flipped from true to false.
        if (fromVersion < 2)
        {
            settings.AutoStartServer = false;
        }

        // v2 → v3: default port flipped from 8085 to 8080. Migrate users still
        // sitting on the old default; preserve any other explicit choice.
        if (fromVersion < 3 && settings.Port == 8085)
        {
            settings.Port = 8080;
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] Save failed: {ex.Message}");
        }
    }
}
