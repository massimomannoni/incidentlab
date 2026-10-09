using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IncidentLab.Gateway;

public sealed record GatewayConfig
{
    public string PublicHost { get; init; } = "";
    public string OriginKey { get; init; } = "";
    public string Username { get; init; } = "";
    public string PasswordSalt { get; init; } = "";
    public string PasswordHash { get; init; } = "";
    public string Access { get; init; } = "viewer";
    public string MasterUsername { get; init; } = "";
    public string MasterPasswordSalt { get; init; } = "";
    public string MasterPasswordHash { get; init; } = "";
    public string MasterAdmissionKey { get; init; } = "";
    public string MonitorUrl { get; init; } = "http://monitor:8010";
    public const int PasswordIterations = 600_000;
    public const int RequestsPerMinute = 600;
    public const int ConcurrentRequests = 8;
    public const int MaximumResponseBytes = 2 * 1024 * 1024;
    public static readonly TimeSpan SessionDuration = TimeSpan.FromHours(1);

    public static GatewayConfig FromEnvironment()
    {
        string Get(string key, string fallback = "") => Environment.GetEnvironmentVariable(key) ?? fallback;
        return new() { PublicHost = Get("PUBLIC_HOST"), OriginKey = Get("PUBLIC_ORIGIN_KEY"), Username = Get("PUBLIC_USERNAME"),
            PasswordSalt = Get("PUBLIC_PASSWORD_SALT"), PasswordHash = Get("PUBLIC_PASSWORD_HASH"), Access = Get("PUBLIC_ACCESS", "viewer"),
            MonitorUrl = Get("PUBLIC_MONITOR_URL", "http://monitor:8010"), MasterUsername = Get("PUBLIC_MASTER_USERNAME"),
            MasterPasswordSalt = Get("PUBLIC_MASTER_PASSWORD_SALT"), MasterPasswordHash = Get("PUBLIC_MASTER_PASSWORD_HASH"),
            MasterAdmissionKey = Get("PUBLIC_MASTER_ADMISSION_KEY") };
    }
    public void Validate()
    {
        if (MasterAdmissionKey.Length > 0 && (MasterUsername.Length == 0 || !Regex.IsMatch(MasterAdmissionKey, "^[a-f0-9]{64}$")))
            throw new ArgumentException("Invalid master admission configuration (values withheld)");
        if (!Regex.IsMatch(PublicHost, @"^[a-z0-9][a-z0-9.-]*[a-z0-9]$", RegexOptions.CultureInvariant) || PublicHost.Length > 253 ||
            !Regex.IsMatch(OriginKey, "^[a-f0-9]{64}$") || !Regex.IsMatch(Username, "^[a-zA-Z0-9_-]{3,64}$") ||
            !Regex.IsMatch(PasswordSalt, "^[a-f0-9]{32}$") || !Regex.IsMatch(PasswordHash, "^[a-f0-9]{64}$") || Access != "viewer")
            throw new ArgumentException("Invalid public gateway configuration (values withheld)");
        if (MasterUsername.Length + MasterPasswordSalt.Length + MasterPasswordHash.Length > 0 &&
            (!Regex.IsMatch(MasterUsername, "^[a-zA-Z0-9_-]{3,64}$") || !Regex.IsMatch(MasterPasswordSalt, "^[a-f0-9]{32}$") ||
             !Regex.IsMatch(MasterPasswordHash, "^[a-f0-9]{64}$") || string.Equals(Username, MasterUsername, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Invalid personal master configuration (values withheld)");
        if (!Uri.TryCreate(MonitorUrl, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.UserInfo.Length > 0 || u.AbsolutePath != "/" || u.Query.Length > 0 || u.Fragment.Length > 0)
            throw new ArgumentException("Invalid fixed monitor endpoint");
    }
    public bool VerifyPassword(string username, string password)
        => Authenticate(username, password) is not null;

    public string? Authenticate(string username, string password)
    {
        var calculated = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromHexString(PasswordSalt), PasswordIterations, HashAlgorithmName.SHA256, 32);
        var master = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromHexString(MasterPasswordSalt.Length > 0 ? MasterPasswordSalt : PasswordSalt), PasswordIterations, HashAlgorithmName.SHA256, 32);
        // Always perform both expensive hashes and comparisons, including unknown users.
        var viewerOk = Equal(username, Username) & CryptographicOperations.FixedTimeEquals(calculated, Convert.FromHexString(PasswordHash));
        var masterOk = Equal(username, MasterUsername) & CryptographicOperations.FixedTimeEquals(master,
            Convert.FromHexString(MasterPasswordHash.Length > 0 ? MasterPasswordHash : PasswordHash)) & (MasterUsername.Length > 0);
        return masterOk ? "master" : viewerOk ? "viewer" : null;
    }
    public static bool Equal(string left, string right) => CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(left)), SHA256.HashData(Encoding.UTF8.GetBytes(right)));
}
