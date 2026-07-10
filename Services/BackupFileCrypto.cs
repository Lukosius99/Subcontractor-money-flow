using System.Security.Cryptography;
using System.Text;

namespace PADS.MoneyFlow.Api.Services;

internal static class BackupFileCrypto
{
    private static readonly byte[] Magic = "MFDBK001"u8.ToArray();
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 310_000;

    public static async Task EncryptAsync(
        string inputPath,
        string outputPath,
        string passphrase,
        CancellationToken cancellationToken)
    {
        ValidateArguments(inputPath, outputPath, passphrase);
        var plaintext = await File.ReadAllBytesAsync(inputPath, cancellationToken);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var ciphertext = new byte[plaintext.Length];
        var key = DeriveKey(passphrase, salt);

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Magic);

            await using var output = new FileStream(
                outputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);
            await output.WriteAsync(Magic, cancellationToken);
            await output.WriteAsync(salt, cancellationToken);
            await output.WriteAsync(nonce, cancellationToken);
            await output.WriteAsync(tag, cancellationToken);
            await output.WriteAsync(ciphertext, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static async Task DecryptAsync(
        string inputPath,
        string outputPath,
        string passphrase,
        CancellationToken cancellationToken)
    {
        ValidateArguments(inputPath, outputPath, passphrase);
        var payload = await File.ReadAllBytesAsync(inputPath, cancellationToken);
        var headerSize = Magic.Length + SaltSize + NonceSize + TagSize;
        if (payload.Length <= headerSize || !payload.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("The file is not a supported encrypted MoneyFlow backup.");
        }

        var offset = Magic.Length;
        var salt = payload.AsSpan(offset, SaltSize).ToArray();
        offset += SaltSize;
        var nonce = payload.AsSpan(offset, NonceSize).ToArray();
        offset += NonceSize;
        var tag = payload.AsSpan(offset, TagSize).ToArray();
        offset += TagSize;
        var ciphertext = payload.AsSpan(offset).ToArray();
        var plaintext = new byte[ciphertext.Length];
        var key = DeriveKey(passphrase, salt);

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Magic);
            await using var output = new FileStream(
                outputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);
            await output.WriteAsync(plaintext, cancellationToken);
        }
        catch (AuthenticationTagMismatchException exception)
        {
            throw new InvalidDataException("Backup decryption failed. The passphrase is wrong or the file is damaged.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            KeySize);

    private static void ValidateArguments(string inputPath, string outputPath, string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < 20)
        {
            throw new ArgumentException("Backup passphrase must contain at least 20 characters.", nameof(passphrase));
        }

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Backup input file was not found.", inputPath);
        }

        if (string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Input and output paths must be different.", nameof(outputPath));
        }
    }
}
