using SuYanShot.Native;

namespace SuYanShot.Shortcuts;

internal enum ShortcutAction
{
    Region,
    Window,
    FullScreen,
    Delayed,
}

/// A global hotkey such as "Ctrl+Shift+1". Requires Ctrl, Alt or Win unless the key is F1–F24 (same rule as macOS).
internal readonly record struct KeyShortcut(Keys Key, bool Control, bool Alt, bool Shift, bool Win)
{
    public uint Modifiers =>
        (Control ? NativeMethods.MOD_CONTROL : 0) | (Alt ? NativeMethods.MOD_ALT : 0) |
        (Shift ? NativeMethods.MOD_SHIFT : 0) | (Win ? NativeMethods.MOD_WIN : 0);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Control) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out KeyShortcut shortcut)
    {
        shortcut = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        bool control = false, alt = false, shift = false, win = false;
        Keys? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": control = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "win" or "windows": win = true; break;
                default:
                    if (key != null || ParseKey(raw) is not Keys parsed) return false;
                    key = parsed;
                    break;
            }
        }
        if (key is not Keys k) return false;
        var isFunctionKey = k is >= Keys.F1 and <= Keys.F24;
        if (!isFunctionKey && !control && !alt && !win) return false;
        shortcut = new KeyShortcut(k, control, alt, shift, win);
        return true;
    }

    private static Keys? ParseKey(string name)
    {
        if (name.Length == 1 && char.IsAsciiDigit(name[0])) return Keys.D0 + (name[0] - '0');
        if (name.Length == 1 && char.IsAsciiLetter(name[0])) return Keys.A + (char.ToUpperInvariant(name[0]) - 'A');
        if (name.Equals("PrintScreen", StringComparison.OrdinalIgnoreCase) || name.Equals("PrtSc", StringComparison.OrdinalIgnoreCase))
            return Keys.PrintScreen;
        return Enum.TryParse(name, ignoreCase: true, out Keys parsed) && parsed is >= Keys.F1 and <= Keys.F24 ? parsed : null;
    }

    private static string KeyName(Keys key) => key switch
    {
        >= Keys.D0 and <= Keys.D9 => ((char)('0' + (key - Keys.D0))).ToString(),
        Keys.PrintScreen => "PrintScreen",
        _ => key.ToString(),
    };
}
