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
        // macOS uses ⌘⇧V, but Ctrl+Shift+V is "paste as plain text" in browsers and Office, so it stays free here.
        [ShortcutAction.Clipboard] = "Ctrl+Alt+V",
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
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

    /// Assigns (or with null, clears) one action and saves. A combination already used by another action is taken from it,
    /// like the macOS settings window.
    public Dictionary<ShortcutAction, KeyShortcut> Set(ShortcutAction action, KeyShortcut? shortcut)
    {
        var shortcuts = Load();
        if (shortcut is KeyShortcut value)
        {
            foreach (var other in shortcuts.Where(pair => pair.Value == value && pair.Key != action).Select(pair => pair.Key).ToList())
                shortcuts.Remove(other);
            shortcuts[action] = value;
        }
        else
        {
            shortcuts.Remove(action);
        }
        Save(shortcuts);
        return shortcuts;
    }

    public Dictionary<ShortcutAction, KeyShortcut> ResetToDefaults()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
        return Load();
    }

    /// Writes every action, so cleared ones are stored as "" rather than falling back to their default.
    public void Save(IReadOnlyDictionary<ShortcutAction, KeyShortcut> shortcuts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var data = Enum.GetValues<ShortcutAction>()
            .ToDictionary(action => action.ToString(), action => shortcuts.TryGetValue(action, out var s) ? s.ToString() : "");
        File.WriteAllText(FilePath, JsonSerializer.Serialize(data, WriteOptions));
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
