using System.Diagnostics;
using SuYanShot.App;

namespace SuYanShot.Update;

/// Update checks and prompts, as on macOS: a quiet check shortly after launch and then daily (skipping a version the user
/// chose 略過這個版本 for), and 檢查更新… in the tray menu, which always answers. Installing needs the app to be the
/// installed copy (not a development build).
internal sealed class UpdateController : IDisposable
{
    private readonly AppSettings settings;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 60 * 60 * 1000 };
    private bool checking;

    /// Asked before installing; returns false to postpone (for example while a capture is in progress).
    public Func<bool> CanInstallNow { get; set; } = () => true;

    /// Ends the app after the new version has been started.
    public Action Exit { get; set; } = Application.Exit;

    public UpdateController(AppSettings settings)
    {
        this.settings = settings;
        timer.Tick += (_, _) => CheckIfDue();
    }

    public bool AutomaticallyChecks
    {
        get => settings.UpdateAutomaticallyChecks;
        set
        {
            settings.UpdateAutomaticallyChecks = value;
            settings.Save();
        }
    }

    /// Starts the schedule: a first check after 10 seconds, then hourly ticks that check once a day.
    public void Start()
    {
        var first = new System.Windows.Forms.Timer { Interval = 10_000 };
        first.Tick += (_, _) =>
        {
            first.Dispose();
            CheckIfDue();
        };
        first.Start();
        timer.Start();
    }

    private void CheckIfDue()
    {
        if (!AutomaticallyChecks || AppInstallation.IsDevelopmentBuild) return;
        if (settings.UpdateLastCheck is DateTimeOffset last && DateTimeOffset.Now - last < TimeSpan.FromHours(20)) return;
        _ = Check(userInitiated: false);
    }

    /// 檢查更新…: always answers, including "already up to date" and errors.
    public void CheckNow() => _ = Check(userInitiated: true);

    private async Task Check(bool userInitiated)
    {
        if (checking) return;
        checking = true;
        try
        {
            ReleaseInfo? release;
            try
            {
                release = await new UpdateChecker(settings.UpdateFeedUrl).LatestReleaseAsync();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                if (userInitiated) Inform("沒辦法檢查更新", $"{ex.Message}\n請確認網路連線後再試一次。");
                return;
            }
            settings.UpdateLastCheck = DateTimeOffset.Now;
            settings.Save();
            var newer = release != null && release.ExeUrl != null && AppVersion.IsNewer(release.Version, AppVersion.Current);
            if (!newer)
            {
                if (userInitiated) Inform("拾光已經是最新版本", $"目前版本 {AppVersion.Current}。");
                return;
            }
            if (!userInitiated && settings.UpdateSkippedVersion == release!.Version) return;
            Offer(release!);
        }
        finally
        {
            checking = false;
        }
    }

    private void Offer(ReleaseInfo release)
    {
        var update = new TaskDialogButton("更新");
        var later = new TaskDialogButton("稍後");
        var skip = new TaskDialogButton("略過這個版本");
        var page = new TaskDialogPage
        {
            Caption = "更新拾光",
            Heading = $"拾光 {release.Version} 推出了",
            Text = $"你目前用的是 {AppVersion.Current}。更新會下載新版、取代目前的拾光，然後重新打開。",
            Icon = TaskDialogIcon.Information,
            Buttons = { update, later, skip },
            DefaultButton = update,
            Expander = string.IsNullOrWhiteSpace(release.Notes) ? null : new TaskDialogExpander(release.Notes) { CollapsedButtonText = "這個版本的更新內容", ExpandedButtonText = "收起" },
        };
        var choice = TaskDialog.ShowDialog(page);
        if (choice == skip)
        {
            settings.UpdateSkippedVersion = release.Version;
            settings.Save();
        }
        else if (choice == update)
        {
            _ = Install(release);
        }
    }

    private async Task Install(ReleaseInfo release)
    {
        if (!AppInstallation.IsInstalled)
        {
            Fallback(release, "目前執行的不是安裝好的拾光（開始功能表裡的那一個），沒辦法自動更新。");
            return;
        }
        if (!CanInstallNow())
        {
            Inform("正在截圖，先不更新", "完成後再從系統匣選單的「檢查更新…」更新。");
            return;
        }
        using var progress = new ProgressWindow($"正在下載拾光 {release.Version}…");
        progress.Show();
        string prepared;
        try
        {
            prepared = await new UpdateInstaller().PrepareAsync(release);
        }
        catch (Exception ex) when (ex is UpdateInstallException or HttpRequestException or TaskCanceledException or IOException)
        {
            progress.Close();
            Fallback(release, ex.Message);
            return;
        }
        try
        {
            new UpdateInstaller().Install(prepared);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            progress.Close();
            Fallback(release, ex.Message);
            return;
        }
        Exit();
    }

    private static void Fallback(ReleaseInfo release, string reason)
    {
        var open = new TaskDialogButton("打開下載頁面");
        var page = new TaskDialogPage
        {
            Caption = "更新拾光",
            Heading = "沒辦法自動更新",
            Text = $"{reason}\n\n可以到下載頁面手動下載 Glean-{release.Version}-win-x64.exe，打開後選擇安裝，就會取代舊的。",
            Icon = TaskDialogIcon.Warning,
            Buttons = { open, TaskDialogButton.Cancel },
        };
        if (TaskDialog.ShowDialog(page) == open) Process.Start(new ProcessStartInfo(release.PageUrl) { UseShellExecute = true });
    }

    private static void Inform(string heading, string text) =>
        TaskDialog.ShowDialog(new TaskDialogPage { Caption = "拾光", Heading = heading, Text = text, Buttons = { TaskDialogButton.OK } });

    public void Dispose() => timer.Dispose();

    /// A small "downloading…" window with an indeterminate bar.
    private sealed class ProgressWindow : Form
    {
        public ProgressWindow(string text)
        {
            Text = "更新拾光";
            Icon = UI.AppIcon.Window;
            Font = new Font("Microsoft JhengHei UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = ControlBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(360, 90);
            Controls.Add(new Label { Text = text, AutoSize = true, Location = new Point(16, 16) });
            Controls.Add(new ProgressBar { Style = ProgressBarStyle.Marquee, Location = new Point(16, 48), Width = 328 });
        }
    }
}
