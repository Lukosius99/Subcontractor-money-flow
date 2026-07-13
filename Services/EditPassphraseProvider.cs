using System.Security.Cryptography;
using System.Text;

namespace PADS.MoneyFlow.Api.Services;

internal sealed class EditPassphraseProvider(IConfiguration configuration, IWebHostEnvironment environment)
{
    private const string HashPrefix = "PBKDF2-SHA256";
    private const int MinimumIterations = 100_000;
    private const int MaximumIterations = 1_000_000;
    private const int ExpectedHashSize = 32;
    private readonly string? _configuredPassphrase = configuration["MoneyFlow:EditPassphrase"];
    private readonly string? _environmentPassphrase =
        Environment.GetEnvironmentVariable("MONEY_FLOW_EDIT_PASSPHRASE");
    private readonly string _passphraseFilePath = configuration["MoneyFlow:EditPassphraseFilePath"]
        ?? (environment.IsProduction()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PADS", "MoneyFlow", "Configuration", "edit-passphrase.txt")
            : Path.Combine(AppContext.BaseDirectory, "data", "edit-passphrase.txt"));

    public bool IsConfigured() => GetFileHash() is not null
        || !string.IsNullOrWhiteSpace(_configuredPassphrase)
        || !string.IsNullOrWhiteSpace(_environmentPassphrase);

    public bool Verify(string? providedPassphrase)
    {
        if (string.IsNullOrWhiteSpace(providedPassphrase))
        {
            return false;
        }

        var fileHash = GetFileHash();
        if (fileHash is not null)
        {
            return VerifyHash(providedPassphrase, fileHash);
        }

        var configured = !string.IsNullOrWhiteSpace(_configuredPassphrase)
            ? _configuredPassphrase
            : _environmentPassphrase;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(providedPassphrase),
            Encoding.UTF8.GetBytes(configured));
    }

    private string? GetFileHash()
    {
        try
        {
            if (!File.Exists(_passphraseFilePath))
            {
                return null;
            }

            var value = File.ReadAllText(_passphraseFilePath).Trim();
            return !string.IsNullOrWhiteSpace(value) ? value : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool VerifyHash(string passphrase, string encodedHash)
    {
        var parts = encodedHash.Split('$');
        if (parts.Length != 4
            || !string.Equals(parts[0], HashPrefix, StringComparison.Ordinal)
            || !int.TryParse(parts[1], out var iterations)
            || iterations is < MinimumIterations or > MaximumIterations)
        {
            return false;
        }

        byte[] salt;
        byte[] expectedHash;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expectedHash = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length < 16 || expectedHash.Length != ExpectedHashSize)
        {
            return false;
        }

        var actualHash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expectedHash.Length);
        try
        {
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualHash);
        }
    }
}
