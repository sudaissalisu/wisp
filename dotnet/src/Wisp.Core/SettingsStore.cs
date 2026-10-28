using System.Text.Json;

namespace Wisp.Core;

public sealed class AppSettings
{
    public string DeviceName { get; set; } = "";
    public string SaveDirectory { get; set; } = "";
    public bool AutoAcceptTrusted { get; set; }
    /// <summary>One of "system", "light", "dark".</summary>
    public string Theme { get; set; } = "dark";
    public bool SoundsEnabled { get; set; } = true;
    public bool NotificationsEnabled { get; set; } = true;
    /// <summary>Ask for biometric confirmation before accepting incoming files.</summary>
    public bool BiometricAccept { get; set; }
    /// <summary>Ask for biometric authentication when returning to the app.</summary>
    public bool BiometricUnlock { get; set; }
    public bool Onboarded { get; set; }
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }
    public string SkippedUpdateVersion { get; set; } = "";
    public string PromptedUpdateVersion { get; set; } = "";
}

public sealed class SettingsStore
{
    private readonly string _path;

    public SettingsStore(string path)
    {
        _path = path;
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(_path), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            return settings ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Settings persistence should never break transfers.
        }
    }
}
