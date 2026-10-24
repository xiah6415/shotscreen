using System.Diagnostics;

namespace SuYanShot.Update;

/// Where 拾光 lives once installed, and the first-run "install to this PC" step: the Windows counterpart of dragging the
/// macOS app into Applications. Per-user, no administrator rights: %LOCALAPPDATA%\Programs\Glean\Glean.exe plus a Start
/// menu shortcut.
internal static class AppInstallation
{
    public const string ExeName = "Glean.exe";

    public static string InstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Glean");

    public static string InstalledExe => Path.Combine(InstallDirectory, ExeName);

    private static string StartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "拾光.lnk");

    public static string RunningExe => Environment.ProcessPath ?? "";

    public static bool IsInstalled => string.Equals(Path.GetFullPath(RunningExe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    /// `dotnet build` / `dotnet run` output: never offered for install or update.
    public static bool IsDevelopmentBuild =>
        RunningExe.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) ||
        RunningExe.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase);

    /// Run from Program.Main before anything else. Returns true when this process should exit (it handed over to the
    /// installed copy).
    public static bool OfferInstall()
    {
        if (IsInstalled || IsDevelopmentBuild) return false;
        var exists = File.Exists(InstalledExe);
        var answer = MessageBox.Show(
            exists ? "這台電腦已經安裝了拾光。要用這個版本取代它嗎？" : "要把拾光安裝到這台電腦嗎？\n\n會放在你的使用者資料夾，並加到開始功能表，不需要系統管理員權限。",
            "拾光", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return false;
        try
        {
            Install(RunningExe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"沒辦法安裝：{ex.Message}\n\n如果拾光正在執行，先從系統匣選單「結束拾光」再試一次。", "拾光", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true });
        return true;
    }

    internal static void Install(string sourceExe)
    {
        Directory.CreateDirectory(InstallDirectory);
        File.Copy(sourceExe, InstalledExe, overwrite: true);
        CreateShortcut(StartMenuShortcut, InstalledExe);
    }

    private static void CreateShortcut(string path, string target)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        var shortcut = shell.CreateShortcut(path);
        shortcut.TargetPath = target;
        shortcut.WorkingDirectory = Path.GetDirectoryName(target);
        shortcut.Description = "拾光：截圖、捲動截圖、標註和剪貼簿紀錄";
        shortcut.IconLocation = target + ",0";
        shortcut.Save();
    }

    /// After an update, the previous exe is left as Glean.exe.old (a running exe can be renamed but not deleted).
    public static void CleanUpAfterUpdate()
    {
        var old = RunningExe + ".old";
        for (var attempt = 0; attempt < 20 && File.Exists(old); attempt++)
        {
            try { File.Delete(old); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(250); }
        }
    }
}
