using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MoveCopyScrap.Services;

/// <summary>
/// Preferences that outlive a session, in %LOCALAPPDATA%\MoveCopyScrap\settings.json.
///
/// Deliberately tiny and forgiving: a missing, unreadable or half-written file just means
/// defaults. Nothing here is worth interrupting the user over, so every failure is
/// swallowed and logged.
/// </summary>
public sealed class AppSettings
{
    /// <summary>"Cover" or "Fit" - how a picture is scaled in fill-window mode.</summary>
    [JsonPropertyName("fillStyle")]
    public string FillStyle { get; set; } = "Cover";
}

public static class SettingsStore
{
    public static string StateDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "MoveCopyScrap");

    public static string FilePath => Path.Combine(StateDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            string json = File.ReadAllText(FilePath, Encoding.UTF8);
            return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)
                   ?? new AppSettings();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] load failed: {ex.Message}");
            return new AppSettings();
        }
    }

    /// <summary>Writes atomically, so an interrupted save leaves the previous file intact.</summary>
    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(StateDirectory);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings),
                              Encoding.UTF8);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] save failed: {ex.Message}");
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext
{
}
