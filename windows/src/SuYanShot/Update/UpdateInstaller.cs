using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SuYanShot.Update;

internal sealed class UpdateInstallException(string message) : Exception(message);

/// Downloads a release's Windows exe, checks that it is this app signed with the same certificate, and swaps it in —
/// the Windows counterpart of the macOS UpdateInstaller. Checks, in order: the published SHA-256, the file's product name
/// and version, an intact Authenticode signature, and a signing certificate identical to the running exe's.
internal sealed class UpdateInstaller
{
    private readonly string installedExe;

    public UpdateInstaller(string? installedExe = null) => this.installedExe = installedExe ?? AppInstallation.RunningExe;

    /// Downloads into the install folder (same volume, so the swap is a rename) and returns the verified file.
    public async Task<string> PrepareAsync(ReleaseInfo release, CancellationToken token = default)
    {
        if (release.ExeUrl is not string exeUrl || release.ChecksumUrl is not string checksumUrl)
            throw new UpdateInstallException("這個版本沒有附上 Windows 版的檔案。");
        var download = installedExe + ".download";
        try
        {
            var expected = (await UpdateChecker.Client.GetStringAsync(checksumUrl, token)).Trim().Split(' ', '\t')[0].ToLowerInvariant();
            await using (var source = await UpdateChecker.Client.GetStreamAsync(exeUrl, token))
            await using (var target = File.Create(download))
                await source.CopyToAsync(target, token);
            Verify(download, release.Version, expected);
            return download;
        }
        catch
        {
            TryDelete(download);
            throw;
        }
    }

    internal void Verify(string candidate, string version, string expectedSha256)
    {
        string actual;
        using (var stream = File.OpenRead(candidate))
            actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (actual != expectedSha256) throw new UpdateInstallException("下載的檔案跟發佈的校驗碼不符，可能下載不完整或被改過，為了安全沒有安裝。");

        var info = FileVersionInfo.GetVersionInfo(candidate);
        if (info.ProductName != "拾光" || !string.Equals(Path.GetFileNameWithoutExtension(info.OriginalFilename), "Glean", StringComparison.OrdinalIgnoreCase))
            throw new UpdateInstallException($"下載的檔案不是拾光（{info.ProductName ?? "沒有產品名稱"}）。");
        var fileVersion = info.ProductVersion?.Split('+')[0] ?? "0";
        if (AppVersion.Components(fileVersion).SequenceEqual(AppVersion.Components(version)) == false)
            throw new UpdateInstallException($"下載的檔案版本是 {fileVersion}，跟發佈的 {version} 不一樣。");

        var installedThumbprint = SignerThumbprint(installedExe)
            ?? throw new UpdateInstallException("目前的拾光沒有用固定的憑證簽名，沒辦法確認新版是同一個來源。");
        if (!SignatureIntact(candidate) || SignerThumbprint(candidate) != installedThumbprint)
            throw new UpdateInstallException("新版沒有通過簽名檢查（不是同一張憑證簽的，或檔案被改過），為了安全沒有安裝。");
    }

    /// Renames the running exe to .old, puts the new one in its place, starts it, and returns; the caller then exits.
    /// The new process waits for this one to end, then deletes the .old file.
    public void Install(string preparedExe, bool launch = true)
    {
        var old = installedExe + ".old";
        TryDelete(old);
        File.Move(installedExe, old);
        try
        {
            File.Move(preparedExe, installedExe);
        }
        catch
        {
            File.Move(old, installedExe); // put the working version back
            throw;
        }
        if (launch) Process.Start(new ProcessStartInfo(installedExe, $"--after-update {Environment.ProcessId}") { UseShellExecute = false });
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // MARK: Authenticode

    internal static string? SignerThumbprint(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile is the documented way to read an Authenticode signer.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return certificate.Thumbprint;
        }
        catch (CryptographicException)
        {
            return null; // not signed
        }
    }

    /// True when the file's Authenticode signature matches its contents. The self-signed certificate is not trusted by
    /// Windows, so "untrusted root" counts as intact; a modified file reports a bad digest instead.
    internal static bool SignatureIntact(string path)
    {
        const uint CERT_E_UNTRUSTEDROOT = 0x800B0109, CERT_E_CHAINING = 0x800B010A;
        var file = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, filePointer, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,            // WTD_UI_NONE
                fdwRevocationChecks = 0,   // WTD_REVOKE_NONE
                dwUnionChoice = 1,         // WTD_CHOICE_FILE
                pFile = filePointer,
                dwStateAction = 0,
                dwProvFlags = 0x00000010,  // WTD_CACHE_ONLY_URL_RETRIEVAL: no network
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            var result = (uint)WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            return result is 0 or CERT_E_UNTRUSTEDROOT or CERT_E_CHAINING;
        }
        finally
        {
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
}
