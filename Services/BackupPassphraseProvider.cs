namespace PADS.MoneyFlow.Api.Services;

internal sealed class BackupPassphraseProvider(IConfiguration configuration, IWebHostEnvironment environment)
{
    private readonly string _passphraseFilePath = configuration["MoneyFlow:BackupPassphraseFilePath"]
        ?? (environment.IsProduction()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PADS", "MoneyFlow", "Configuration", "backup-passphrase.txt")
            : Path.Combine(AppContext.BaseDirectory, "data", "backup-passphrase.txt"));

    public string? GetPassphrase()
    {
        try
        {
            if (File.Exists(_passphraseFilePath))
            {
                var fileValue = File.ReadAllText(_passphraseFilePath).Trim();
                if (!string.IsNullOrWhiteSpace(fileValue))
                {
                    return fileValue;
                }
            }
        }
        catch (IOException)
        {
            // A setter replaces the file atomically. Fall back to the environment
            // if a request arrives during that small replacement window.
        }
        catch (UnauthorizedAccessException)
        {
            // Fail closed below when neither source can be read.
        }

        var environmentValue = Environment.GetEnvironmentVariable("MONEY_FLOW_BACKUP_PASSPHRASE");
        return !string.IsNullOrWhiteSpace(environmentValue) ? environmentValue : null;
    }
}
