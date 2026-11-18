using System.Diagnostics;
using System.Drawing.Drawing2D;
using SuYanShot.Native;

namespace SuYanShot.Capture;

/// 顯示所有視窗, as on macOS: a full-screen overview of every listed window as a thumbnail, including covered ones,
/// with search. Click (or Enter on the first match) picks; Esc goes back to hover picking.
/// Thumbnails are captured one per timer tick on the UI thread, so the grid shows at once and the frozen desktop
/// (the PrintWindow fallback) is never touched from two threads.
internal sealed class WindowGrid : Form
{
    internal sealed record Entry(WindowInfo Info, string Title, string App)
    {
        public bool Matches(string query) =>
            query.Length == 0 || Title.Contains(query, StringComparison.OrdinalIgnoreCase) || App.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private readonly Action<WindowInfo> onPick;
    private readonly Action onBack;
    private readonly FrozenDesktop desktop;
    private readonly List<Tile> tiles;
    private readonly TextBox search;
    private readonly FlowLayoutPanel flow;
    private readonly System.Windows.Forms.Timer loader = new() { Interval = 1 };
    private int nextThumbnail;
    private bool finished;

    public WindowGrid(IReadOnlyList<WindowInfo> windows, FrozenDesktop desktop, Rectangle monitor, Action<WindowInfo> onPick, Action onBack)
    {
        this.desktop = desktop;
        this.onPick = onPick;
        this.onBack = onBack;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Bounds = monitor;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        BackColor = Color.FromArgb(24, 24, 27);
        Text = "拾光：所有視窗";

        var scale = NativeMethods.DpiOf(Screen.FromRectangle(monitor)) / 96f;
        int S(float v) => (int)Math.Round(v * scale);
        var font = new Font("Microsoft JhengHei UI", S(13), GraphicsUnit.Pixel);

        var top = new Panel { Dock = DockStyle.Top, Height = S(64), Padding = new Padding(S(24), S(16), S(24), S(8)) };
        var title = new Label { Text = "選擇視窗", AutoSize = true, ForeColor = Color.White, Font = new Font(font.FontFamily, S(16), FontStyle.Bold, GraphicsUnit.Pixel), Location = new Point(S(24), S(20)) };
        search = new TextBox
        {
            PlaceholderText = "搜尋視窗或程式名稱",
            Font = font,
            Width = S(320),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(48, 48, 52),
            ForeColor = Color.White,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        var hint = new Label { Text = "Esc 返回", AutoSize = true, ForeColor = Color.FromArgb(150, 150, 155), Font = font, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        top.Controls.AddRange(new Control[] { title, search, hint });
        top.Layout += (_, _) =>
        {
            hint.Location = new Point(top.Width - S(24) - hint.Width, S(22));
            search.Location = new Point(hint.Left - S(16) - search.Width, S(18));
        };

        flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(S(16)), BackColor = BackColor };
        tiles = windows.Select(w => new Tile(Describe(w), S(300), S(220), font, Pick)).ToList();
        flow.Controls.AddRange(tiles.Cast<Control>().ToArray());
        var empty = new Label { Text = "沒有可以選的視窗", AutoSize = true, ForeColor = Color.FromArgb(150, 150, 155), Font = font, Margin = new Padding(S(8)), Visible = tiles.Count == 0 };
        flow.Controls.Add(empty);

        Controls.Add(flow);
        Controls.Add(top);
        search.TextChanged += (_, _) =>
        {
            var query = search.Text.Trim();
            flow.SuspendLayout();
            foreach (var tile in tiles) tile.Visible = tile.Entry.Matches(query);
            empty.Visible = !tiles.Any(t => t.Visible);
            empty.Text = tiles.Count == 0 ? "沒有可以選的視窗" : "找不到符合的視窗";
            flow.ResumeLayout();
        };
        loader.Tick += (_, _) => LoadNextThumbnail();
    }

    internal static Entry Describe(WindowInfo window)
    {
        NativeMethods.GetWindowThreadProcessId(window.Handle, out var pid);
        string app;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            app = process.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } description ? description : process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            app = ""; // elevated or already-exited processes
        }
        var title = NativeMethods.WindowTitle(window.Handle);
        return new Entry(window, title.Length > 0 ? title : app, app);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.ExcludeFromCapture(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        NativeMethods.SetForegroundWindow(Handle);
        search.Focus();
        loader.Start();
    }

    private void LoadNextThumbnail()
    {
        if (finished || nextThumbnail >= tiles.Count)
        {
            loader.Stop();
            return;
        }
        var tile = tiles[nextThumbnail++];
        try
        {
            using var full = ScreenCapture.CaptureWindow(tile.Entry.Info.Handle, desktop);
            if (full != null) tile.SetThumbnail(Shrink(full, tile.ThumbnailSize));
        }
        catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException)
        {
            // A window that closed or refused to draw keeps its placeholder.
        }
    }

    internal static Bitmap Shrink(Bitmap source, Size box)
    {
        var ratio = Math.Min(1.0, Math.Min(box.Width / (double)source.Width, box.Height / (double)source.Height));
        var size = new Size(Math.Max(1, (int)(source.Width * ratio)), Math.Max(1, (int)(source.Height * ratio)));
        var result = new Bitmap(size.Width, size.Height);
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(source, new Rectangle(Point.Empty, size));
        return result;
    }

    private void Pick(WindowInfo window)
    {
        if (finished) return;
        finished = true;
        Close();
        onPick(window);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            if (search.Text.Length > 0) { search.Clear(); return; }
            Back();
        }
        else if (e.KeyCode == Keys.Enter && tiles.FirstOrDefault(t => t.Visible) is Tile first)
        {
            e.SuppressKeyPress = true;
            Pick(first.Entry.Info);
        }
    }

    private void Back()
    {
        if (finished) return;
        finished = true;
        Close();
        onBack();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) loader.Dispose();
        base.Dispose(disposing);
    }

    /// One window: thumbnail on top, title and app name below.
    private sealed class Tile : Control
    {
        public Entry Entry { get; }
        private readonly Font font;
        private readonly Action<WindowInfo> onPick;
        private Bitmap? thumbnail;
        private bool hovered;
        private readonly int textHeight;

        public Size ThumbnailSize => new(Width - Padding.Horizontal, Height - Padding.Vertical - textHeight);

        public Tile(Entry entry, int width, int height, Font font, Action<WindowInfo> onPick)
        {
            Entry = entry;
            this.font = font;
            this.onPick = onPick;
            textHeight = (int)(font.Height * 2.4);
            Size = new Size(width, height);
            Margin = new Padding(width / 24);
            Padding = new Padding(width / 30);
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
        }

        public void SetThumbnail(Bitmap image)
        {
            thumbnail?.Dispose();
            thumbnail = image;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) onPick(Entry.Info);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Color.Black);
            var card = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (var path = OverlayForm.RoundedRect(card, Padding.Left))
            {
                using var fill = new SolidBrush(hovered ? Color.FromArgb(60, 60, 66) : Color.FromArgb(38, 38, 42));
                g.FillPath(fill, path);
                if (hovered)
                {
                    using var border = new Pen(Color.FromArgb(10, 132, 255), 2);
                    g.DrawPath(border, path);
                }
            }

            var box = new Rectangle(Padding.Left, Padding.Top, ThumbnailSize.Width, ThumbnailSize.Height);
            if (thumbnail != null)
            {
                var x = box.X + (box.Width - thumbnail.Width) / 2;
                var y = box.Y + (box.Height - thumbnail.Height) / 2;
                g.DrawImage(thumbnail, x, y, thumbnail.Width, thumbnail.Height);
            }
            else
            {
                using var placeholder = new SolidBrush(Color.FromArgb(52, 52, 56));
                g.FillRectangle(placeholder, box);
            }

            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var titleRect = new RectangleF(Padding.Left, box.Bottom + font.Height * 0.3f, box.Width, font.Height * 1.1f);
            var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, Alignment = StringAlignment.Center };
            g.DrawString(Entry.Title.Length > 0 ? Entry.Title : "（無標題）", font, Brushes.White, titleRect, format);
            if (Entry.App.Length > 0 && Entry.App != Entry.Title)
            {
                using var muted = new SolidBrush(Color.FromArgb(150, 150, 155));
                g.DrawString(Entry.App, font, muted, new RectangleF(titleRect.X, titleRect.Bottom, titleRect.Width, titleRect.Height), format);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) thumbnail?.Dispose();
            base.Dispose(disposing);
        }
    }
}
