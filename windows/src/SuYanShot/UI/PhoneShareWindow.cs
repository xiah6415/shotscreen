using SuYanShot.ClipboardHistory;

namespace SuYanShot.UI;

/// 手機分享設定, as on macOS: the on/off switch, the fixed address (with QR code), password, signed-in devices,
/// the local name, and which networks sharing may run on.
internal sealed class PhoneShareWindow : Form
{
    private const int ContentWidth = 420;

    private readonly PhoneShare share;
    private readonly CheckBox enabled = new() { Text = "手機分享", AutoSize = true, Font = new Font("Microsoft JhengHei UI", 10.5f, FontStyle.Bold) };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(ContentWidth, 0), Margin = new Padding(0, 4, 0, 8) };

    private readonly TableLayoutPanel address = new() { AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 8) };
    private readonly PictureBox qr = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White, Size = new Size(140, 140), Margin = new Padding(0, 0, 14, 0) };
    private readonly TextBox url = new() { ReadOnly = true, BorderStyle = BorderStyle.None, Width = 250, Font = new Font("Consolas", 11f, FontStyle.Bold) };
    private readonly Label ipFallback = new() { AutoSize = true, MaximumSize = new Size(260, 0), ForeColor = SystemColors.GrayText };
    private string? renderedUrl;

    private readonly Label passwordTitle = Heading("");
    private readonly TextBox password = new() { UseSystemPasswordChar = true, Width = 200, PlaceholderText = $"新密碼（至少 {PhoneShareAuth.MinimumPasswordLength} 個字）" };
    private readonly TextBox confirmation = new() { UseSystemPasswordChar = true, Width = 200, PlaceholderText = "再輸入一次" };
    private readonly Button savePassword = new() { AutoSize = true };
    private readonly Label passwordMessage = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left };
    private readonly Label passwordNote = Note("更改密碼後，所有裝置都要重新登入。");

    private readonly FlowLayoutPanel devices = Column();
    private readonly TextBox nameBox = new() { Width = 120, Font = new Font("Consolas", 10f) };
    private readonly Button applyName = new() { Text = "套用", AutoSize = true };
    private readonly CheckBox homeOnly = new() { Text = "只在允許的網路開放", AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
    private readonly FlowLayoutPanel networks = Column();
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 15_000 };
    private bool rendering;

    public PhoneShareWindow(PhoneShare share)
    {
        this.share = share;
        Text = "手機分享設定";
        Icon = AppIcon.Window;
        Font = new Font("Microsoft JhengHei UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScroll = true;
        ClientSize = new Size(ContentWidth + 48, 640);

        var layout = Column();
        layout.Padding = new Padding(22);

        layout.Controls.Add(enabled);
        layout.Controls.Add(Note("打開後，拾光啟動時會自動開始分享。"));
        layout.Controls.Add(status);

        var addressText = Column();
        addressText.Controls.Add(Heading("在手機上打開"));
        addressText.Controls.Add(url);
        addressText.Controls.Add(Note("每台電腦上的拾光都用這個網址，所以換電腦或給別人用，同一個捷徑都能用。用 Safari 打開後，按「分享 → 加入主畫面」就會變成一個 App。", 260));
        addressText.Controls.Add(ipFallback);
        address.Controls.Add(qr, 0, 0);
        address.Controls.Add(addressText, 1, 0);
        layout.Controls.Add(address);

        layout.Controls.Add(Divider());
        layout.Controls.Add(passwordTitle);
        var passwordRow = Row();
        passwordRow.Controls.AddRange(new Control[] { password, confirmation });
        layout.Controls.Add(passwordRow);
        var saveRow = Row();
        saveRow.Controls.AddRange(new Control[] { savePassword, passwordMessage });
        layout.Controls.Add(saveRow);
        layout.Controls.Add(passwordNote);

        layout.Controls.Add(Divider());
        layout.Controls.Add(devices);

        layout.Controls.Add(Divider());
        layout.Controls.Add(Heading("網址名稱"));
        var nameRow = Row();
        nameRow.Controls.Add(new Label { Text = "http://", AutoSize = true, Font = nameBox.Font, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 0) });
        nameRow.Controls.Add(nameBox);
        nameRow.Controls.Add(new Label { Text = $".local:{PhoneShare.DefaultPort}", AutoSize = true, Font = nameBox.Font, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) });
        nameRow.Controls.Add(applyName);
        layout.Controls.Add(nameRow);
        layout.Controls.Add(Note("維持預設的 glean，換電腦時捷徑才不用改。同一個網路裡有兩台電腦都開著時，第二台會自動改用 glean-2。"));

        layout.Controls.Add(Divider());
        layout.Controls.Add(homeOnly);
        layout.Controls.Add(networks);

        layout.Controls.Add(Divider());
        layout.Controls.Add(Note("連線沒有加密，所以只建議在自己家裡、信任的網路使用。手機能看到剪貼簿紀錄，也能傳文字或照片到這台電腦。" +
                                 "第一次開始分享時，Windows 防火牆會詢問是否允許拾光連線，請允許「私人網路」。"));
        Controls.Add(layout);

        enabled.CheckedChanged += (_, _) => { if (!rendering) share.SetEnabled(enabled.Checked); };
        homeOnly.CheckedChanged += (_, _) => { if (!rendering) share.SetRestrictToHomeNetworks(homeOnly.Checked); };
        savePassword.Click += (_, _) => SavePassword();
        nameBox.TextChanged += (_, _) => applyName.Enabled = LocalNameAdvertiser.Sanitize(nameBox.Text) != share.LocalName;
        applyName.Click += (_, _) =>
        {
            share.SetLocalName(nameBox.Text);
            nameBox.Text = share.LocalName;
        };
        nameBox.Text = share.LocalName;
        share.Changed += Render;
        refresh.Tick += (_, _) => Render(); // keeps "上次使用 N 分鐘前" current
        refresh.Start();
        Render();
    }

    private void SavePassword()
    {
        if (password.Text.Length < PhoneShareAuth.MinimumPasswordLength) { passwordMessage.Text = "密碼太短了"; return; }
        if (password.Text != confirmation.Text) { passwordMessage.Text = "兩次輸入的不一樣"; return; }
        share.SetPassword(password.Text);
        password.Clear();
        confirmation.Clear();
        passwordMessage.Text = "已儲存";
    }

    private void Render()
    {
        if (IsDisposed) return;
        rendering = true;
        SuspendLayout();
        enabled.Checked = share.IsEnabled;
        (status.Text, status.ForeColor) = share.Status switch
        {
            PhoneShareStatus.Off => ("已關閉", SystemColors.GrayText),
            PhoneShareStatus.NeedsPassword => ("先在下面設定密碼，才會開始分享。", Color.DarkOrange),
            PhoneShareStatus.NoNetwork => ("沒有連上網路，等連上後會自動開始。", Color.DarkOrange),
            PhoneShareStatus.OtherNetwork => ("目前的網路不在允許清單，已暫停分享。回到允許的網路會自動恢復。", Color.DarkOrange),
            PhoneShareStatus.Starting => ("啟動中…", SystemColors.GrayText),
            PhoneShareStatus.Running => (share.LastActivity is DateTimeOffset last ? $"分享中 · 手機上次使用 {ClipboardPanel.Age(last)}" : "分享中，等待手機連線", Color.SeaGreen),
            _ => ($"無法開始分享：{share.FailureMessage}", Color.Firebrick),
        };

        // Prefer the .local name; until it is published (or if it can't be), show the IP address instead.
        var link = share.IsServing ? share.Url ?? share.IpUrl : null;
        address.Visible = link != null;
        if (link != null)
        {
            if (link != renderedUrl)
            {
                qr.Image?.Dispose();
                qr.Image = QrCode.Encode(link).ToBitmap(6);
                renderedUrl = link;
            }
            url.Text = link;
            ipFallback.Text = share.Url != null && share.IpUrl != null ? $"打不開的話改用：{share.IpUrl}"
                : share.NameProblem != null ? $"無法發布 {share.LocalName}.local（{share.NameProblem}），先用 IP 位址。" : "";
        }

        passwordTitle.Text = share.Auth.HasPassword ? "更改密碼" : "設定密碼";
        savePassword.Text = passwordTitle.Text;
        passwordNote.Visible = share.Auth.HasPassword;

        devices.Controls.Clear();
        devices.Visible = share.Auth.Devices.Count > 0;
        if (devices.Visible)
        {
            var header = Row();
            header.Controls.Add(Heading("已登入的裝置"));
            var signOutAll = new Button { Text = "全部登出", AutoSize = true, Margin = new Padding(16, 0, 0, 0) };
            signOutAll.Click += (_, _) => share.Auth.RevokeAll();
            header.Controls.Add(signOutAll);
            devices.Controls.Add(header);
            foreach (var device in share.Auth.Devices)
            {
                var row = Row();
                row.Controls.Add(new Label { Text = $"📱 {device.Name}　最近使用 {ClipboardPanel.Age(device.LastSeen)}", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) });
                var remove = new Button { Text = "移除", AutoSize = true };
                var id = device.Id;
                remove.Click += (_, _) => share.Auth.Revoke(id);
                row.Controls.Add(remove);
                devices.Controls.Add(row);
            }
        }

        applyName.Enabled = LocalNameAdvertiser.Sanitize(nameBox.Text) != share.LocalName;
        homeOnly.Checked = share.RestrictToHomeNetworks;
        networks.Controls.Clear();
        networks.Visible = share.RestrictToHomeNetworks;
        if (networks.Visible)
        {
            foreach (var network in share.HomeNetworks)
            {
                var row = Row();
                var current = network.Id == share.CurrentNetwork?.Id;
                row.Controls.Add(new Label { Text = network.Label + (current ? "（目前）" : ""), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) });
                var remove = new Button { Text = "移除", AutoSize = true };
                var id = network.Id;
                remove.Click += (_, _) => share.RemoveNetwork(id);
                row.Controls.Add(remove);
                networks.Controls.Add(row);
            }
            if (share.CurrentNetwork is NetworkIdentity now && !share.HomeNetworks.Any(n => n.Id == now.Id))
            {
                var allow = new Button { Text = $"允許目前的網路（{now.Label}）", AutoSize = true };
                allow.Click += (_, _) => share.AllowCurrentNetwork();
                networks.Controls.Add(allow);
            }
            networks.Controls.Add(Note("以路由器辨認網路。在咖啡廳、公司等其他網路會自動暫停分享。"));
        }
        ResumeLayout();
        rendering = false;
    }

    private static FlowLayoutPanel Column() => new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };

    private static FlowLayoutPanel Row() => new() { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };

    private static Label Heading(string text) => new() { Text = text, AutoSize = true, Font = new Font("Microsoft JhengHei UI", 9.5f, FontStyle.Bold), Margin = new Padding(0, 4, 0, 4) };

    private static Label Note(string text, int width = ContentWidth) =>
        new() { Text = text, AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 0, 4) };

    private static Label Divider() => new() { AutoSize = false, Height = 1, Width = ContentWidth, BorderStyle = BorderStyle.Fixed3D, Margin = new Padding(0, 10, 0, 10) };

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        share.Changed -= Render;
        refresh.Dispose();
        qr.Image?.Dispose();
        base.OnFormClosed(e);
    }
}
