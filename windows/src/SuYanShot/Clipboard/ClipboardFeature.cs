using SuYanShot.App;
using SuYanShot.UI;

namespace SuYanShot.ClipboardHistory;

/// Clipboard history: records copies, shows the panel, pastes the chosen entry into the previous app, and shares it with a phone.
internal sealed class ClipboardFeature : IDisposable
{
    private readonly AppSettings settings;
    private readonly ClipboardMonitor monitor = new();
    private ClipboardPanel? panel;
    private PhoneShareWindow? phoneShareWindow;

    public ClipboardStore Store { get; }
    public PhoneShare PhoneShare { get; }

    public ClipboardFeature(AppSettings settings)
    {
        this.settings = settings;
        Store = new ClipboardStore(limit: settings.ClipboardLimit);
        PhoneShare = new PhoneShare(Store, item => monitor.Ignore(Paster.Write(item, Store)));
        monitor.Captured += snapshot => Store.Add(snapshot);
        if (settings.ClipboardEnabled) monitor.Start();
    }

    public bool IsEnabled
    {
        get => settings.ClipboardEnabled;
        set
        {
            settings.ClipboardEnabled = value;
            settings.Save();
            if (value) monitor.Start(); else monitor.Stop();
        }
    }

    public int Limit
    {
        get => Store.Limit;
        set
        {
            Store.Limit = value;
            settings.ClipboardLimit = value;
            settings.Save();
        }
    }

    public void TogglePanel()
    {
        if (panel is { IsDisposed: false })
        {
            panel.Close();
            return;
        }
        panel = new ClipboardPanel(Store, Paste);
        panel.FormClosed += (_, _) => panel = null;
        panel.ShowPanel();
    }

    public void ShowPhoneShare()
    {
        if (phoneShareWindow is { IsDisposed: false })
        {
            phoneShareWindow.Activate();
            return;
        }
        phoneShareWindow = new PhoneShareWindow(PhoneShare);
        phoneShareWindow.FormClosed += (_, _) => phoneShareWindow = null;
        phoneShareWindow.Show();
        phoneShareWindow.Activate();
    }

    private void Paste(ClipboardItem item, bool plainText, IntPtr previousWindow)
    {
        monitor.Ignore(Paster.Write(item, Store, plainText));
        Store.Bump(item.Id);
        // Give the panel a moment to disappear before the previous app takes focus back.
        var timer = new System.Windows.Forms.Timer { Interval = 60 };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            Paster.PasteInto(previousWindow);
        };
        timer.Start();
    }

    public void Dispose()
    {
        PhoneShare.Stop();
        monitor.Dispose();
    }
}
