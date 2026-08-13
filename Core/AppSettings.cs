using System.IO;
using System.Text.Json;

namespace ClaudeSessions;

public enum ThemePreference { System, Light, Dark }

/// <summary>Small, best-effort user settings persisted across launches.</summary>
public sealed class AppSettings
{
    /// <summary>Loaded once at startup — one user, one settings file.</summary>
    public static AppSettings Current { get; } = Load();

    public ThemePreference Theme { get; set; } = ThemePreference.System;

    // Roaming, not Local: build.sh installs to (and wipes) %LOCALAPPDATA%\ClaudeSessions.
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeSessions", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch
        {
            // Best-effort — a failed save just means the next launch falls back to System.
        }
    }
}
