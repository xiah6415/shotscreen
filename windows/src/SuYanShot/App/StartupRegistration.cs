using Microsoft.Win32;

namespace SuYanShot.App;

/// 登入時啟動: a per-user Run entry, no admin rights needed.
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Glean";
    /// The entry's name before the rename to 拾光; migrated by <see cref="RefreshPath"/>.
    private const string OldRunValue = "SuYanShot";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string || key?.GetValue(OldRunValue) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.DeleteValue(OldRunValue, throwOnMissingValue: false);
            if (value) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }

    /// Keeps an existing entry pointing at this exe (it moves when the exe is renamed or reinstalled elsewhere).
    public static void RefreshPath()
    {
        if (IsEnabled) IsEnabled = true;
    }
}
