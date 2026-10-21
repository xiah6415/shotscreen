using System.Text.Json;

namespace SuYanShot.App;

/// Preferences that are not shortcuts, in %APPDATA%\SuYanShot\settings.json (macOS keeps the same ones in UserDefaults).
internal sealed class AppSettings
{
    public bool ClipboardEnabled { get; set; } = true;
    public int ClipboardLimit { get; set; } = 200;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SuYanShot", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            if (File.Exists(file)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), Json) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }
        return new();
    }

    public void Save(string? path = null)
    {
        var file = path ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, Json));
    }
}
