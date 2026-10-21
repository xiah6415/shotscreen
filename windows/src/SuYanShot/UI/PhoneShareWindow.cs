using SuYanShot.ClipboardHistory;

namespace SuYanShot.UI;

/// Starts phone sharing and shows the QR code to scan. Closing the window keeps sharing running;
/// it stops with the button or after 30 idle minutes (same as macOS).
internal sealed class PhoneShareWindow : Form
{
    private readonly PhoneShare share;
    private readonly PictureBox qr = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White, Margin = new Padding(0, 0, 0, 12) };
    private readonly Label message = new() { AutoSize = true, MaximumSize = new Size(340, 0), TextAlign = ContentAlignment.MiddleCenter, Anchor = AnchorStyles.None };
    private readonly TextBox url = new() { ReadOnly = true, BorderStyle = BorderStyle.None, TextAlign = HorizontalAlignment.Center, Width = 340, Font = new Font("Consolas", 8.5f) };
    private readonly Label status = new() { AutoSize = true, ForeColor = Color.SeaGreen, Anchor = AnchorStyles.None, Margin = new Padding(0, 6, 0, 6) };
    private readonly Button regenerate = new() { Text = "換一組網址", AutoSize = true };
    private readonly Button stop = new() { Text = "停止分享", AutoSize = true };
    private readonly Button start = new() { Text = "開始分享", AutoSize = true };
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 15_000 };
    private readonly ToolTip tips = new();

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
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        qr.Size = new Size(244, 244);
        qr.Anchor = AnchorStyles.None;
        url.Anchor = AnchorStyles.None;
        url.BackColor = BackColor;
        var note = new Label
        {
            Text = "只有同一個網路裡、拿到這個網址的裝置看得到。30 分鐘沒有使用會自動停止。",
            AutoSize = true, MaximumSize = new Size(340, 0), ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.None,
            Margin = new Padding(0, 6, 0, 12),
        };
        var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.None, WrapContents = false };
        buttons.Controls.AddRange(new Control[] { regenerate, stop, start });
        foreach (var control in new Control[] { qr, message, url, status, note, buttons }) layout.Controls.Add(control);
        Controls.Add(layout);

        tips.SetToolTip(regenerate, "舊的網址會立刻失效");
        regenerate.Click += (_, _) => share.RegenerateLink();
        stop.Click += (_, _) => share.Stop();
        start.Click += (_, _) => share.Start();
        share.Changed += Render;
        refresh.Tick += (_, _) => Render(); // keeps "上次連線 N 分鐘前" current
        refresh.Start();

        share.Start();
        Render();
    }

    private void Render()
    {
        if (IsDisposed) return;
        var running = share.IsRunning;
        if (share.Url is string link)
        {
            qr.Image?.Dispose();
            qr.Image = QrCode.Encode(link).ToBitmap(8);
            qr.Visible = url.Visible = status.Visible = true;
            message.Text = "手機和這台電腦連同一個 Wi-Fi，用相機掃描 QR code。";
            url.Text = link;
            status.Text = share.LastActivity is DateTimeOffset last ? $"分享中 · 手機上次連線 {ClipboardPanel.Age(last)}" : "分享中，等待手機連線";
        }
        else
        {
            qr.Visible = url.Visible = status.Visible = false;
            message.Text = share.Problem ?? (running ? "準備中…" : "已停止分享。");
        }
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
