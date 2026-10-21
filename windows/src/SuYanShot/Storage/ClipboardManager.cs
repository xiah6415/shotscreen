namespace SuYanShot.Storage;

internal static class ClipboardManager
{
    /// Puts the image on the clipboard as a bitmap plus PNG, so apps that keep transparency or prefer PNG get it.
    public static void Copy(Bitmap image)
    {
        var data = new DataObject();
        data.SetImage(image);
        var png = new MemoryStream();
        image.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        data.SetData("PNG", false, png);
        // The clipboard can be briefly locked by another process; retry a few times before giving up.
        Clipboard.SetDataObject(data, copy: true, retryTimes: 5, retryDelay: 100);
    }
}
