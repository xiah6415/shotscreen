using SuYanShot.App;

namespace SuYanShot;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One instance only: a second launch would fight the first over the global hotkeys.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\SuYanShot.SingleInstance", out var isFirst);
        if (!isFirst) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplication());
    }
}
