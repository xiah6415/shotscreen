using System.Media;

namespace SuYanShot.UI;

/// A short native sound played only after a successful capture, as on macOS (which plays "Pop").
internal static class CompletionSound
{
    // Windows' own short click; missing on trimmed-down installs, where the system "Asterisk" sound stands in.
    private static readonly Lazy<SoundPlayer?> Player = new(() =>
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Navigation Start.wav");
        if (!File.Exists(path)) return null;
        try
        {
            var player = new SoundPlayer(path);
            player.Load();
            return player;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            return null;
        }
    });

    public static void Screenshot()
    {
        try
        {
            if (Player.Value is SoundPlayer player) player.Play(); // asynchronous; a new Play replaces the previous one
            else SystemSounds.Asterisk.Play();
        }
        catch (InvalidOperationException)
        {
            // No sound device or a broken file: a capture never fails because of its sound.
        }
    }
}
