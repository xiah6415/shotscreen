using System.Diagnostics;
using SuYanShot.App;
using SuYanShot.Update;

namespace SuYanShot;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Started by the updater: wait for the old version to quit, then remove it.
        if (args is ["--after-update", var pid] && int.TryParse(pid, out var oldProcess))
        {
            try { Process.GetProcessById(oldProcess).WaitForExit(15_000); }
            catch (ArgumentException) { } // already gone
            AppInstallation.CleanUpAfterUpdate();
        }
        else if (AppInstallation.OfferInstall())
        {
            return; // the installed copy takes over
        }

        // One instance only: a second launch would fight the first over the global hotkeys.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\SuYanShot.SingleInstance", out var isFirst);
        if (!isFirst) return;

        Application.Run(new TrayApplication());
    }
}
