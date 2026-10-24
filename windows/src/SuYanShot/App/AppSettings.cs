using System.Text.Json;
using System.Text.Json.Serialization;
using SuYanShot.ClipboardHistory;

namespace SuYanShot.App;

/// Preferences that are not shortcuts, in %APPDATA%\SuYanShot\settings.json (macOS keeps the same ones in UserDefaults).
/// The folder keeps its old name so history and settings carry over from before the rename to 拾光, the way the macOS
/// app kept its bundle id.
internal sealed class AppSettings
{
    public bool ClipboardEnabled { get; set; } = true;
    public int ClipboardLimit { get; set; } = 200;

    /// 手機分享 switch; sharing then starts with the app.
    public bool PhoneShareEnabled { get; set; }

    /// The `<name>.local` host name phones open.
    public string PhoneShareName { get; set; } = "glean";

    /// Only serve on networks in <see cref="PhoneShareHomeNetworks"/>.
    public bool PhoneShareHomeOnly { get; set; } = true;

    public List<NetworkIdentity> PhoneShareHomeNetworks { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SuYanShot", "settings.json");

    /// Where Save writes; the file it was loaded from.
    [JsonIgnore]
    public string FilePath { get; set; } = DefaultPath;

    public static AppSettings Load(string? path = null)
    {
        var file = path ?? DefaultPath;
        AppSettings settings;
        try
        {
            settings = File.Exists(file) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            settings = new();
        }
        settings.FilePath = file;
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}
