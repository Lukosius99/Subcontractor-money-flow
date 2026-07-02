namespace PADS.MoneyFlow.Api.Services;

internal sealed class ApiKeyProvider(IConfiguration configuration, IWebHostEnvironment environment)
{
    private readonly string? _configuredKey = configuration["MoneyFlow:ApiKey"];
    private readonly string? _environmentKey = Environment.GetEnvironmentVariable("MONEY_FLOW_API_KEY");
    private readonly string _keyFilePath = configuration["MoneyFlow:ApiKeyFilePath"]
        ?? (environment.IsProduction()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PADS", "MoneyFlow", "Configuration", "api-key.txt")
            : Path.Combine(AppContext.BaseDirectory, "data", "api-key.txt"));

    public string? GetApiKey()
    {
        // The file is checked for every import request so a permitted operator can
        // replace the key without restarting or reconfiguring the Windows service.
        try
        {
            if (File.Exists(_keyFilePath))
            {
                var fileKey = File.ReadAllText(_keyFilePath).Trim();
                if (!string.IsNullOrWhiteSpace(fileKey))
                {
                    return fileKey;
                }
            }
        }
        catch (IOException)
        {
            // A setter uses an atomic replacement. A request arriving during that
            // tiny window falls back to the existing configuration sources.
        }
        catch (UnauthorizedAccessException)
        {
            // Treat an unreadable key like a missing key; imports fail closed below.
        }

        return !string.IsNullOrWhiteSpace(_configuredKey) ? _configuredKey : _environmentKey;
    }
}
