using System.Text.Json;

namespace SuYanShot.App;

/// Preferences that are not shortcuts, in %APPDATA%\SuYanShot\settings.json (macOS keeps the same ones in UserDefaults).
internal sealed class AppSettings
{
    public bool ClipboardEnabled { get; set; } = true;
    public int ClipboardLimit { get; set; } = 200;

    /// 手機連線 switch, plus the port and token that keep the phone link the same across restarts.
    public bool PhoneShareEnabled { get; set; }
    public int PhoneSharePort { get; set; }
    public string? PhoneShareToken { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SuYanShot", "settings.json");

    /// Where Save writes; the file it was loaded from.
    [System.Text.Json.Serialization.JsonIgnore]
    public string FilePath { get; init; } = DefaultPath;

    public static AppSettings Load(string? path = null)
    {
        var file = path ?? DefaultPath;
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), Json) is AppSettings loaded)
                return new AppSettings
                {
                    FilePath = file,
                    ClipboardEnabled = loaded.ClipboardEnabled,
                    ClipboardLimit = loaded.ClipboardLimit,
                    PhoneShareEnabled = loaded.PhoneShareEnabled,
                    PhoneSharePort = loaded.PhoneSharePort,
                    PhoneShareToken = loaded.PhoneShareToken,
                };
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }
        return new AppSettings { FilePath = file };
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}
