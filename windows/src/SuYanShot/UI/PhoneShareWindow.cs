using SuYanShot.ClipboardHistory;

namespace SuYanShot.UI;

/// Turns phone sharing on and shows the QR code to scan, plus the fixed addresses for iOS Shortcuts.
/// Closing the window keeps sharing on; 停止分享 (or the tray's 手機連線 switch) turns it off.
internal sealed class PhoneShareWindow : Form
{
    private const int ContentWidth = 380;

    private readonly PhoneShare share;
    private readonly PictureBox qr = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White, Margin = new Padding(0, 0, 0, 12) };
    private readonly Label message = new() { AutoSize = true, MaximumSize = new Size(ContentWidth, 0), Anchor = AnchorStyles.None };
    private readonly TextBox url = new() { ReadOnly = true, BorderStyle = BorderStyle.None, TextAlign = HorizontalAlignment.Center, Width = ContentWidth, Font = new Font("Consolas", 8.5f) };
    private readonly Label status = new() { AutoSize = true, ForeColor = Color.SeaGreen, Anchor = AnchorStyles.None, Margin = new Padding(0, 6, 0, 6) };
    private readonly Label shortcutsTitle = new() { Text = "iOS 捷徑用（「取得 URL 內容」動作）", AutoSize = true, Font = new Font("Microsoft JhengHei UI", 9.5f, FontStyle.Bold), Margin = new Padding(0, 10, 0, 4) };
    private readonly TextBox shortcuts = new() { ReadOnly = true, Multiline = true, WordWrap = false, ScrollBars = ScrollBars.Horizontal, Width = ContentWidth, Height = 96, Font = new Font("Consolas", 8.5f) };
    private readonly Button copyShortcuts = new() { Text = "複製捷徑網址", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button regenerate = new() { Text = "換一組網址", AutoSize = true };
    private readonly Button stop = new() { Text = "停止分享", AutoSize = true };
    private readonly Button start = new() { Text = "開始分享", AutoSize = true };
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 15_000 };
    private readonly ToolTip tips = new();
    private string? renderedUrl;

    public PhoneShareWindow(PhoneShare share)
    {
        this.share = share;
        Text = "在手機上開啟剪貼簿";
        Font = new Font("Microsoft JhengHei UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(24);

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ContentWidth));
        qr.Size = new Size(244, 244);
        qr.Anchor = AnchorStyles.None;
        url.Anchor = AnchorStyles.None;
        url.BackColor = BackColor;
        var note = new Label
        {
            Text = "網址會一直保持一樣，可以加到主畫面或設成捷徑。只有同一個網路裡、拿到這個網址的裝置看得到。" +
                   "電腦的 IP 變了網址也會跟著變，建議在路由器把這台電腦設成固定 IP（DHCP 保留）。",
            AutoSize = true, MaximumSize = new Size(ContentWidth, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 6, 0, 6),
        };
        var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.None, WrapContents = false, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.AddRange(new Control[] { regenerate, stop, start });
        foreach (var control in new Control[] { qr, message, url, status, note, shortcutsTitle, shortcuts, copyShortcuts, buttons })
            layout.Controls.Add(control);
        Controls.Add(layout);

        tips.SetToolTip(regenerate, "舊的網址和捷徑會立刻失效");
        regenerate.Click += (_, _) => share.RegenerateLink();
        stop.Click += (_, _) => share.IsEnabled = false;
        start.Click += (_, _) => share.IsEnabled = true;
        copyShortcuts.Click += (_, _) =>
        {
            System.Windows.Forms.Clipboard.SetText(shortcuts.Text);
            copyShortcuts.Text = "已複製";
        };
        share.Changed += Render;
        refresh.Tick += (_, _) => Render(); // keeps "上次連線 N 分鐘前" current
        refresh.Start();

        share.IsEnabled = true;
        Render();
    }

    private void Render()
    {
        if (IsDisposed) return;
        var running = share.IsRunning;
        if (share.Url is string link && share.BaseUrl is string baseUrl)
        {
            if (link != renderedUrl)
            {
                qr.Image?.Dispose();
                qr.Image = QrCode.Encode(link).ToBitmap(8);
                renderedUrl = link;
                copyShortcuts.Text = "複製捷徑網址";
            }
            message.Text = "手機和這台電腦連同一個 Wi-Fi，用相機掃描 QR code。";
            url.Text = link;
            status.Text = share.LastActivity is DateTimeOffset last ? $"分享中 · 手機上次連線 {ClipboardPanel.Age(last)}" : "分享中，等待手機連線";
            var t = Uri.EscapeDataString(share.Token);
            shortcuts.Text = string.Join("\r\n",
                $"傳文字到電腦（POST，內容放文字）：{baseUrl}/api/send?t={t}",
                $"傳照片到電腦（POST，內容放圖片檔）：{baseUrl}/api/send?t={t}",
                $"取得電腦上最新一筆文字（GET）：{baseUrl}/api/latest?t={t}",
                $"打開剪貼簿網頁：{link}");
        }
        else
        {
            renderedUrl = null;
            message.Text = share.Problem ?? (running ? "準備中…" : "已停止分享。");
        }
        foreach (var control in new Control[] { qr, url, status, shortcutsTitle, shortcuts, copyShortcuts })
            control.Visible = share.Url != null;
        regenerate.Visible = stop.Visible = running;
        start.Visible = !running;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        share.Changed -= Render;
        refresh.Dispose();
        qr.Image?.Dispose();
        base.OnFormClosed(e);
    }
}
