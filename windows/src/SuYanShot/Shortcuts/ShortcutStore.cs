using System.Text.Encodings.Web;
using System.Text.Json;

namespace SuYanShot.Shortcuts;

/// Persists shortcuts to %APPDATA%\SuYanShot\shortcuts.json as { "Region": "Ctrl+Shift+1", ... }.
/// An action saved as "" means the user cleared it; a missing action gets its default.
internal sealed class ShortcutStore
{
    /// ⌘⇧1–4 on macOS become Ctrl+Shift+1–4 here.
    public static readonly IReadOnlyDictionary<ShortcutAction, string> Defaults = new Dictionary<ShortcutAction, string>
    {
        [ShortcutAction.Region] = "Ctrl+Shift+1",
        [ShortcutAction.Window] = "Ctrl+Shift+2",
        [ShortcutAction.FullScreen] = "Ctrl+Shift+3",
        [ShortcutAction.Delayed] = "Ctrl+Shift+4",
    };

    public string FilePath { get; }

    public ShortcutStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SuYanShot", "shortcuts.json");
    }

    public Dictionary<ShortcutAction, KeyShortcut> Load()
    {
        var saved = ReadFile();
        var result = new Dictionary<ShortcutAction, KeyShortcut>();
        foreach (var action in Enum.GetValues<ShortcutAction>())
        {
            var text = saved.TryGetValue(action.ToString(), out var value) ? value : Defaults[action];
            if (KeyShortcut.TryParse(text, out var shortcut) && !result.ContainsValue(shortcut))
                result[action] = shortcut;
        }
        return result;
    }

    /// Writes the defaults on first run so the user has a file to edit.
    public void EnsureFileExists()
    {
        if (File.Exists(FilePath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var defaults = Defaults.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    private Dictionary<string, string> ReadFile()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }
}
