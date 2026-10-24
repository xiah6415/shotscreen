using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SuYanShot.ClipboardHistory;

/// Password login for phone sharing, as on macOS. The URL carries no secret: a device enters the password once and gets
/// a long-lived session cookie. Only hashes are stored — PBKDF2-SHA256 for the password, SHA-256 for session tokens —
/// so reading the file doesn't let anyone in. Kept in %APPDATA%\SuYanShot\phone-share-auth.json.
internal sealed class PhoneShareAuth
{
    internal sealed record Device(Guid Id, string Name, string TokenHash, DateTimeOffset CreatedAt)
    {
        public DateTimeOffset LastSeen { get; set; }
    }

    private sealed record StoredPassword(string Salt, string Hash, int Iterations);

    private sealed class FileContents
    {
        public StoredPassword? Password { get; set; }
        public List<Device> Devices { get; set; } = new();
    }

    public abstract record LoginResult
    {
        public sealed record Success(string Token) : LoginResult;
        public sealed record WrongPassword(int Remaining) : LoginResult;
        public sealed record LockedOut(int Seconds) : LoginResult;
    }

    public const int MinimumPasswordLength = 6;
    public const string CookieName = "glean_session";
    private const int Iterations = 200_000;
    private const int MaxFailures = 5;
    private static readonly TimeSpan Lockout = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string path;
    private readonly FileContents contents;
    /// Failed logins per source address, for a short lockout after repeated wrong passwords.
    private readonly Dictionary<string, (int Count, DateTimeOffset? LockedUntil)> failures = new();

    /// Raised after a password or device change.
    public event Action? Changed;

    public PhoneShareAuth(string? path = null)
    {
        this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SuYanShot", "phone-share-auth.json");
        try
        {
            contents = File.Exists(this.path) ? JsonSerializer.Deserialize<FileContents>(File.ReadAllText(this.path), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            contents = new();
        }
    }

    public bool HasPassword => contents.Password != null;
    public IReadOnlyList<Device> Devices => contents.Devices;

    /// Sets or changes the password. Changing it signs every device out.
    public void SetPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        contents.Password = new StoredPassword(Convert.ToBase64String(salt), Convert.ToBase64String(Derive(password, salt, Iterations)), Iterations);
        contents.Devices.Clear();
        Save();
    }

    public bool CheckPassword(string password)
    {
        if (contents.Password is not StoredPassword stored) return false;
        try
        {
            var expected = Convert.FromBase64String(stored.Hash);
            return CryptographicOperations.FixedTimeEquals(Derive(password, Convert.FromBase64String(stored.Salt), stored.Iterations), expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public LoginResult Login(string password, string deviceName, string fromAddress)
    {
        var now = DateTimeOffset.Now;
        if (failures.TryGetValue(fromAddress, out var entry) && entry.LockedUntil is DateTimeOffset locked && locked > now)
            return new LoginResult.LockedOut((int)Math.Ceiling((locked - now).TotalSeconds));
        if (!CheckPassword(password))
        {
            entry.Count++;
            if (entry.Count >= MaxFailures) entry = (0, now + Lockout);
            failures[fromAddress] = entry;
            return entry.LockedUntil is DateTimeOffset until
                ? new LoginResult.LockedOut((int)Math.Ceiling((until - now).TotalSeconds))
                : new LoginResult.WrongPassword(MaxFailures - entry.Count);
        }
        failures.Remove(fromAddress);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var name = deviceName.Trim();
        contents.Devices.Add(new Device(Guid.NewGuid(), name.Length == 0 ? "未命名裝置" : name[..Math.Min(40, name.Length)], Sha256(token), now) { LastSeen = now });
        Save();
        return new LoginResult.Success(token);
    }

    /// The signed-in device for a session token, refreshing its last-seen time (saved at most once a minute).
    public Device? DeviceForToken(string token)
    {
        var hash = Encoding.UTF8.GetBytes(Sha256(token));
        var device = contents.Devices.FirstOrDefault(d => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(d.TokenHash), hash));
        if (device == null) return null;
        if (DateTimeOffset.Now - device.LastSeen > TimeSpan.FromMinutes(1))
        {
            device.LastSeen = DateTimeOffset.Now;
            Save();
        }
        return device;
    }

    public void Revoke(Guid id)
    {
        contents.Devices.RemoveAll(d => d.Id == id);
        Save();
    }

    public void RevokeAll()
    {
        contents.Devices.Clear();
        Save();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(contents, Json));
        File.Move(temp, path, overwrite: true);
        Changed?.Invoke();
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    internal static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
