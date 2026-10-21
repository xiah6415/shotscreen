using System.Drawing.Drawing2D;
using System.Drawing.Text;
using SuYanShot.Capture;
using SuYanShot.ClipboardHistory;
using SuYanShot.Native;

namespace SuYanShot.UI;

/// The clipboard history panel along the bottom of the screen, like the macOS one (and Paste):
/// search, filter chips (全部 · 常用 · 文字 · 圖片 · 檔案 · tags · ＋), and a row of cards.
/// ← → select · Tab / Shift+Tab change filter · Enter pastes · Shift+Enter plain text · Ctrl+1–9 quick paste ·
/// Ctrl+P 常用 · Ctrl+Delete deletes · Esc clears the search, then closes. Clicking elsewhere closes it.
internal sealed class ClipboardPanel : Form
{
    private abstract record Filter
    {
        public sealed record All : Filter;
        public sealed record Favorites : Filter;
        public sealed record Text : Filter;
        public sealed record Image : Filter;
        public sealed record File : Filter;
        public sealed record Tag(Guid Id) : Filter;

        public bool Matches(ClipboardItem item) => this switch
        {
            Favorites => item.Pinned,
            Text => item.Kind.IsText(),
            Image => item.Kind == ClipboardKind.Image,
            File => item.Kind == ClipboardKind.File,
            Tag tag => item.Tags.Contains(tag.Id),
            _ => true,
        };
    }

    private readonly record struct Chip(Filter? Filter, string Title, Color? Color, RectangleF Bounds);

    private static readonly Color Background = Color.FromArgb(246, 30, 30, 32);
    private static readonly Color CardColor = Color.FromArgb(44, 44, 47);
    private static readonly Color Accent = Color.FromArgb(10, 132, 255);
    private static readonly Color Muted = Color.FromArgb(152, 152, 159);

    private readonly ClipboardStore store;
    private readonly Action<ClipboardItem, bool, IntPtr> paste;
    private readonly TextBox search;
    private readonly IntPtr previousWindow;
    private readonly List<Chip> chips = new();
    private readonly List<(ClipboardItem Item, RectangleF Bounds)> cardRects = new();
    private List<ClipboardItem> visible = new();
    private Filter filter = new Filter.All();
    private int selected;
    private float scroll;
    private bool modalOpen;

    private float S(float value) => value * DeviceDpi / 96f;

    private float CardWidth => S(230);
    private float CardGap => S(14);
    private float TopBarHeight => S(52);

    public ClipboardPanel(ClipboardStore store, Action<ClipboardItem, bool, IntPtr> paste)
    {
        this.store = store;
        this.paste = paste;
        previousWindow = GetForegroundWindow();

        var screen = ScreenCapture.ScreenUnderCursor();
        var scale = NativeMethods.DpiOf(screen) / 96f;
        var area = screen.WorkingArea;
        var height = (int)(330 * scale);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Bounds = new Rectangle(area.X, area.Bottom - height, area.Width, height);
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        BackColor = Background;
        Text = "剪貼簿紀錄";

        search = new TextBox
        {
            PlaceholderText = "搜尋",
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(58, 58, 61),
            ForeColor = Color.White,
            // Pixel units: this window does no automatic DPI scaling, so sizes follow the monitor it opens on.
            Font = new Font("Microsoft JhengHei UI", 14f * scale, GraphicsUnit.Pixel),
            Location = new Point((int)(16 * scale), (int)(14 * scale)),
            Width = (int)(240 * scale),
        };
        search.TextChanged += (_, _) => { selected = 0; scroll = 0; Reload(); };
        Controls.Add(search);

        store.Changed += OnStoreChanged;
        Reload();
    }

    public void ShowPanel()
    {
        Show();
        Activate();
        NativeMethods.SetForegroundWindow(Handle);
        search.Focus();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.ExcludeFromCapture(Handle);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!modalOpen) Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        store.Changed -= OnStoreChanged;
        base.OnFormClosed(e);
    }

    private void OnStoreChanged()
    {
        if (!IsDisposed) Reload();
    }

    private void Reload()
    {
        var query = search.Text.Trim();
        if (filter is Filter.Tag tag && store.Tag(tag.Id) == null) filter = new Filter.All();
        visible = store.Items.Where(filter.Matches).Where(item => query.Length == 0 || Matches(item, query)).ToList();
        selected = Math.Clamp(selected, 0, Math.Max(0, visible.Count - 1));
        Invalidate();
    }

    private bool Matches(ClipboardItem item, string query)
    {
        var tagNames = string.Join(" ", item.Tags.Select(id => store.Tag(id)?.Name ?? ""));
        return $"{item.SearchText} {tagNames} {item.SourceAppName}".Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    // MARK: Keyboard

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        var modifiers = keyData & Keys.Modifiers;
        switch (key)
        {
            case Keys.Escape when modifiers == Keys.None:
                if (search.TextLength > 0) search.Clear(); else Close();
                return true;
            case Keys.Left or Keys.Right when modifiers == Keys.None:
                Select(selected + (key == Keys.Left ? -1 : 1));
                return true;
            case Keys.Tab:
                CycleFilter(modifiers == Keys.Shift ? -1 : 1);
                return true;
            case Keys.Enter:
                PasteSelected(plainText: modifiers == Keys.Shift);
                return true;
            case >= Keys.D1 and <= Keys.D9 when modifiers == Keys.Control:
                var index = key - Keys.D1;
                if (index < visible.Count) Paste(visible[index], plainText: false);
                return true;
            case Keys.P when modifiers == Keys.Control:
                if (Current is ClipboardItem pinned) store.TogglePin(pinned.Id);
                return true;
            case Keys.Delete or Keys.Back when modifiers == Keys.Control:
                if (Current is ClipboardItem doomed) store.Delete(doomed.Id);
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private ClipboardItem? Current => selected < visible.Count ? visible[selected] : null;

    private void Select(int index)
    {
        if (visible.Count == 0) return;
        selected = Math.Clamp(index, 0, visible.Count - 1);
        // Keep the selected card on screen.
        var left = S(16) + selected * (CardWidth + CardGap);
        if (left - scroll < S(16)) scroll = left - S(16);
        if (left + CardWidth - scroll > Width - S(16)) scroll = left + CardWidth - Width + S(16);
        Invalidate();
    }

    private void CycleFilter(int step)
    {
        var filters = AllFilters().ToList();
        var current = filters.FindIndex(f => f == filter);
        filter = filters[(current + step + filters.Count) % filters.Count];
        selected = 0;
        scroll = 0;
        Reload();
    }

    private IEnumerable<Filter> AllFilters()
    {
        yield return new Filter.All();
        yield return new Filter.Favorites();
        yield return new Filter.Text();
        yield return new Filter.Image();
        yield return new Filter.File();
        foreach (var tag in store.Tags) yield return new Filter.Tag(tag.Id);
    }

    private void PasteSelected(bool plainText)
    {
        if (Current is ClipboardItem item) Paste(item, plainText);
    }

    private void Paste(ClipboardItem item, bool plainText)
    {
        modalOpen = true; // closing ourselves; do not react to the deactivate that follows
        Close();
        paste(item, plainText, previousWindow);
    }

    // MARK: Mouse

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        var max = Math.Max(0, S(16) * 2 + visible.Count * (CardWidth + CardGap) - CardGap - Width);
        scroll = Math.Clamp(scroll - e.Delta, 0, max);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var chip = chips.FirstOrDefault(c => c.Bounds.Contains(e.Location));
        if (chip.Bounds != RectangleF.Empty)
        {
            if (chip.Filter == null)
            {
                if (e.Button == MouseButtons.Left) NewTag(assignTo: null);
            }
            else if (e.Button == MouseButtons.Left)
            {
                filter = chip.Filter;
                selected = 0;
                scroll = 0;
                Reload();
            }
            else if (e.Button == MouseButtons.Right && chip.Filter is Filter.Tag tag)
            {
                ShowTagMenu(tag.Id, e.Location);
            }
            return;
        }
        var card = cardRects.FindIndex(c => c.Bounds.Contains(e.Location));
        if (card < 0) return;
        selected = visible.IndexOf(cardRects[card].Item);
        Invalidate();
        if (e.Button == MouseButtons.Right) ShowCardMenu(cardRects[card].Item, e.Location);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && cardRects.Any(c => c.Bounds.Contains(e.Location))) PasteSelected(plainText: false);
    }

    private void ShowCardMenu(ClipboardItem item, Point location)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("貼上", null, (_, _) => Paste(item, plainText: false));
        if (item.Kind == ClipboardKind.RichText) menu.Items.Add("貼上純文字", null, (_, _) => Paste(item, plainText: true));
        menu.Items.Add(item.Pinned ? "移除常用" : "加入常用", null, (_, _) => store.TogglePin(item.Id));
        var tagsMenu = new ToolStripMenuItem("標籤");
        foreach (var tag in store.Tags)
        {
            var tagId = tag.Id;
            tagsMenu.DropDownItems.Add(new ToolStripMenuItem(tag.Name, Swatch(tag.Color), (_, _) => store.ToggleTag(tagId, item.Id)) { Checked = item.Tags.Contains(tagId) });
        }
        if (store.Tags.Count > 0) tagsMenu.DropDownItems.Add(new ToolStripSeparator());
        tagsMenu.DropDownItems.Add("新增標籤…", null, (_, _) => NewTag(assignTo: item.Id));
        menu.Items.Add(tagsMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("刪除", null, (_, _) => store.Delete(item.Id));
        ShowMenu(menu, location);
    }

    private void ShowTagMenu(Guid tagId, Point location)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("重新命名…", null, (_, _) =>
        {
            if (Ask("重新命名標籤", store.Tag(tagId)?.Name ?? "") is string name) store.RenameTag(tagId, name);
        });
        menu.Items.Add("刪除標籤", null, (_, _) => store.DeleteTag(tagId));
        ShowMenu(menu, location);
    }

    /// Menus take focus from the panel; keep it open while they are up.
    private void ShowMenu(ContextMenuStrip menu, Point location)
    {
        modalOpen = true;
        menu.Closed += (_, _) => BeginInvoke(() =>
        {
            modalOpen = false;
            if (!IsDisposed) { Activate(); search.Focus(); }
        });
        menu.Show(this, location);
    }

    private void NewTag(Guid? assignTo)
    {
        if (Ask("新增標籤", "") is string name) store.CreateTag(name, assignTo);
    }

    private string? Ask(string title, string initial)
    {
        modalOpen = true;
        try
        {
            using var dialog = new TextPrompt(title, initial);
            return dialog.ShowDialog(this) == DialogResult.OK ? dialog.Value : null;
        }
        finally
        {
            modalOpen = false;
            if (!IsDisposed) { Activate(); search.Focus(); }
        }
    }

    private static Bitmap Swatch(Color color)
    {
        var bitmap = new Bitmap(12, 12);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, 1, 1, 10, 10);
        return bitmap;
    }

    // MARK: Drawing

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        using var font = new Font("Microsoft JhengHei UI", S(12), GraphicsUnit.Pixel);
        using var small = new Font("Microsoft JhengHei UI", S(11), GraphicsUnit.Pixel);
        using var bold = new Font("Microsoft JhengHei UI", S(11), FontStyle.Bold, GraphicsUnit.Pixel);

        DrawChips(g, font);
        DrawCards(g, font, small, bold);

        using var hint = new SolidBrush(Muted);
        g.DrawString("← → 選擇 · Tab 切換標籤 · Enter 貼上 · Shift+Enter 純文字 · Ctrl+1–9 快速貼上 · Ctrl+P 常用 · Ctrl+Delete 刪除 · Esc 關閉",
                     small, hint, S(16), Height - S(24));
    }

    private void DrawChips(Graphics g, Font font)
    {
        chips.Clear();
        var x = search.Right + S(16);
        var y = S(14);
        var h = S(28);
        var entries = new List<(Filter? Filter, string Title, Color? Color)>
        {
            (new Filter.All(), "全部", null), (new Filter.Favorites(), "★ 常用", null), (new Filter.Text(), "文字", null),
            (new Filter.Image(), "圖片", null), (new Filter.File(), "檔案", null),
        };
        entries.AddRange(store.Tags.Select(t => ((Filter?)new Filter.Tag(t.Id), t.Name, (Color?)t.Color)));
        entries.Add((null, "＋", null));
        foreach (var (chipFilter, title, color) in entries)
        {
            var textSize = g.MeasureString(title, font);
            var width = textSize.Width + S(color == null ? 22 : 36);
            var rect = new RectangleF(x, y, width, h);
            var on = chipFilter != null && chipFilter == filter;
            using (var background = new SolidBrush(on ? Accent : Color.FromArgb(64, 64, 68)))
            using (var path = OverlayForm.RoundedRect(rect, h / 2))
                g.FillPath(background, path);
            var textX = rect.X + S(11);
            if (color is Color dot)
            {
                using var brush = new SolidBrush(dot);
                g.FillEllipse(brush, rect.X + S(11), rect.Y + (h - S(8)) / 2, S(8), S(8));
                textX += S(14);
            }
            g.DrawString(title, font, Brushes.White, textX, rect.Y + (h - textSize.Height) / 2);
            chips.Add(new Chip(chipFilter, title, color, rect));
            x += width + S(8);
        }
    }

    private void DrawCards(Graphics g, Font font, Font small, Font bold)
    {
        cardRects.Clear();
        var top = TopBarHeight + S(4);
        var height = Height - top - S(34);
        if (visible.Count == 0)
        {
            var message = store.Items.Count == 0 ? "還沒有紀錄。複製一些文字、圖片或檔案就會出現在這裡。"
                : filter is Filter.Favorites ? "還沒有常用的紀錄，按 Ctrl+P 或右鍵 →「加入常用」"
                : filter is Filter.Tag ? "這個標籤還沒有紀錄，右鍵卡片 →「標籤」就能加入"
                : "沒有符合的項目";
            using var brush = new SolidBrush(Muted);
            var size = g.MeasureString(message, font);
            g.DrawString(message, font, brush, (Width - size.Width) / 2, top + (height - size.Height) / 2);
            return;
        }
        for (var i = 0; i < visible.Count; i++)
        {
            var x = S(16) + i * (CardWidth + CardGap) - scroll;
            if (x > Width) break;
            if (x + CardWidth < 0) continue;
            var rect = new RectangleF(x, top, CardWidth, height);
            cardRects.Add((visible[i], rect));
            DrawCard(g, visible[i], rect, i, font, small, bold);
        }
    }

    private void DrawCard(Graphics g, ClipboardItem item, RectangleF rect, int index, Font font, Font small, Font bold)
    {
        var radius = S(10);
        using (var path = OverlayForm.RoundedRect(rect, radius))
        {
            using var background = new SolidBrush(CardColor);
            g.FillPath(background, path);
            var state = g.Save();
            g.SetClip(path);

            // Header: kind colour, source app, age.
            var header = new RectangleF(rect.X, rect.Y, rect.Width, S(30));
            using (var headerBrush = new SolidBrush(KindColor(item.Kind)))
                g.FillRectangle(headerBrush, header);
            var title = item.SourceAppName ?? item.Kind.Title();
            g.DrawString(Ellipsize(g, title, bold, rect.Width - S(90)), bold, Brushes.White, rect.X + S(10), rect.Y + S(7));
            var age = Age(item.CreatedAt);
            var ageSize = g.MeasureString(age, small);
            g.DrawString(age, small, Brushes.White, rect.Right - ageSize.Width - S(8), rect.Y + S(7));
            if (item.Pinned)
                g.DrawString("★", small, Brushes.Gold, rect.Right - ageSize.Width - S(24), rect.Y + S(7));

            var body = RectangleF.FromLTRB(rect.X + S(10), header.Bottom + S(8), rect.Right - S(10), rect.Bottom - S(28));
            DrawBody(g, item, body, font);

            // Footer: kind · size, tag dots, Ctrl+N badge.
            using var muted = new SolidBrush(Muted);
            var footer = $"{item.Kind.Title()} · {Footer(item)}";
            g.DrawString(footer, small, muted, rect.X + S(10), rect.Bottom - S(22));
            var dotX = rect.X + S(10) + g.MeasureString(footer, small).Width + S(4);
            foreach (var tagId in item.Tags.Take(3))
            {
                if (store.Tag(tagId) is not ClipboardTag tag) continue;
                using var dot = new SolidBrush(tag.Color);
                g.FillEllipse(dot, dotX, rect.Bottom - S(17), S(8), S(8));
                dotX += S(11);
            }
            if (index < 9)
            {
                var badge = $"Ctrl+{index + 1}";
                var size = g.MeasureString(badge, small);
                g.DrawString(badge, small, muted, rect.Right - size.Width - S(8), rect.Bottom - S(22));
            }
            g.Restore(state);
        }
        if (index == selected)
        {
            using var border = new Pen(Accent, S(3));
            using var outline = OverlayForm.RoundedRect(RectangleF.Inflate(rect, S(1.5f), S(1.5f)), radius + S(1.5f));
            g.DrawPath(border, outline);
        }
    }

    private void DrawBody(Graphics g, ClipboardItem item, RectangleF body, Font font)
    {
        switch (item.Kind)
        {
            case ClipboardKind.Image when store.Thumbnail(item) is Image thumb:
                var scale = Math.Min(body.Width / thumb.Width, body.Height / thumb.Height);
                var size = new SizeF(thumb.Width * scale, thumb.Height * scale);
                g.DrawImage(thumb, body.X + (body.Width - size.Width) / 2, body.Y + (body.Height - size.Height) / 2, size.Width, size.Height);
                break;
            case ClipboardKind.File:
                var paths = item.FilePaths ?? new();
                var lines = paths.Take(4).Select(p => "📄 " + Path.GetFileName(p)).ToList();
                if (paths.Count > 4) lines.Add($"還有 {paths.Count - 4} 個…");
                g.DrawString(string.Join("\n", lines), font, Brushes.White, body, new StringFormat { Trimming = StringTrimming.EllipsisCharacter });
                break;
            default:
                var text = item.Text ?? "";
                if (text.Length > 600) text = text[..600];
                g.DrawString(text, font, Brushes.White, body, new StringFormat { Trimming = StringTrimming.EllipsisWord });
                break;
        }
    }

    private static Color KindColor(ClipboardKind kind) => kind switch
    {
        ClipboardKind.Text => Color.FromArgb(10, 132, 255),
        ClipboardKind.RichText => Color.FromArgb(175, 82, 222),
        ClipboardKind.Image => Color.FromArgb(48, 176, 80),
        _ => Color.FromArgb(255, 149, 0),
    };

    private static string Footer(ClipboardItem item) => item.Kind switch
    {
        ClipboardKind.Text or ClipboardKind.RichText => $"{(item.Text ?? "").Length} 字",
        ClipboardKind.Image => item.ImageWidth is int w && item.ImageHeight is int h ? $"{w} × {h}" : "",
        _ => $"{item.FilePaths?.Count ?? 0} 個檔案",
    };

    internal static string Age(DateTimeOffset date)
    {
        var elapsed = DateTimeOffset.Now - date;
        if (elapsed.TotalMinutes < 1) return "剛剛";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes} 分鐘前";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours} 小時前";
        if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays} 天前";
        return date.ToLocalTime().ToString("M/d");
    }

    private static string Ellipsize(Graphics g, string text, Font font, float width)
    {
        if (g.MeasureString(text, font).Width <= width) return text;
        while (text.Length > 1 && g.MeasureString(text + "…", font).Width > width) text = text[..^1];
        return text + "…";
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}

/// A one-line text prompt for tag names.
internal sealed class TextPrompt : Form
{
    private readonly TextBox box;

    public string Value => box.Text;

    public TextPrompt(string title, string initial)
    {
        Text = title;
        Font = new Font("Microsoft JhengHei UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ClientSize = new Size(300, 90);
        box = new TextBox { Text = initial, Location = new Point(12, 12), Width = 276 };
        var ok = new Button { Text = "確定", DialogResult = DialogResult.OK, Location = new Point(132, 50), Width = 75 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(213, 50), Width = 75 };
        Controls.AddRange(new Control[] { box, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
