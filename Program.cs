using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PADS.MoneyFlow.Api.Endpoints;
using PADS.MoneyFlow.Api.Persistence;
using PADS.MoneyFlow.Api.Services;

// Offline DB backup used by server scripts. It must run before the web host is
// constructed so a backup operation never starts the app, never runs schema
// bootstrap code and never writes to the live database.
var backupDatabaseIndex = Array.FindIndex(
    args,
    argument => string.Equals(argument, "--backup-db", StringComparison.OrdinalIgnoreCase));
if (backupDatabaseIndex >= 0)
{
    if (backupDatabaseIndex + 2 >= args.Length)
    {
        Console.Error.WriteLine("Usage: PADS.MoneyFlow.Api --backup-db <source-database-path> <backup-database-path>");
        Environment.ExitCode = 2;
        return;
    }

    try
    {
        await SqliteDatabaseVerifier.CreateConsistentBackupAsync(
            args[backupDatabaseIndex + 1],
            args[backupDatabaseIndex + 2],
            CancellationToken.None);
        Console.WriteLine("Database backup created and verified.");
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Database backup failed: {exception.Message}");
        Environment.ExitCode = 1;
    }

    return;
}

var cryptoOperation = args.FirstOrDefault(argument =>
    string.Equals(argument, "--encrypt-db", StringComparison.OrdinalIgnoreCase)
    || string.Equals(argument, "--decrypt-db", StringComparison.OrdinalIgnoreCase));
if (cryptoOperation is not null)
{
    var operationIndex = Array.IndexOf(args, cryptoOperation);
    if (operationIndex + 2 >= args.Length)
    {
        Console.Error.WriteLine($"Usage: PADS.MoneyFlow.Api {cryptoOperation} <input-path> <output-path>");
        Environment.ExitCode = 2;
        return;
    }

    var passphrase = Environment.GetEnvironmentVariable("MONEY_FLOW_BACKUP_PASSPHRASE");
    if (string.IsNullOrWhiteSpace(passphrase))
    {
        Console.Error.WriteLine("MONEY_FLOW_BACKUP_PASSPHRASE is not configured.");
        Environment.ExitCode = 2;
        return;
    }

    try
    {
        if (string.Equals(cryptoOperation, "--encrypt-db", StringComparison.OrdinalIgnoreCase))
        {
            await BackupFileCrypto.EncryptAsync(
                args[operationIndex + 1],
                args[operationIndex + 2],
                passphrase,
                CancellationToken.None);
        }
        else
        {
            await BackupFileCrypto.DecryptAsync(
                args[operationIndex + 1],
                args[operationIndex + 2],
                passphrase,
                CancellationToken.None);
        }

        Console.WriteLine("Backup cryptographic operation passed.");
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Backup cryptographic operation failed: {exception.Message}");
        Environment.ExitCode = 1;
    }

    return;
}

// Lightweight offline DB validation used by deploy and restore scripts. It runs
// before the web host is constructed, never mutates the supplied database and
// communicates success/failure through the process exit code.
var validateDatabaseIndex = Array.FindIndex(
    args,
    argument => string.Equals(argument, "--validate-db", StringComparison.OrdinalIgnoreCase));
if (validateDatabaseIndex >= 0)
{
    if (validateDatabaseIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("Usage: PADS.MoneyFlow.Api --validate-db <database-path>");
        Environment.ExitCode = 2;
        return;
    }

    try
    {
        await SqliteDatabaseVerifier.VerifyIntegrityAsync(
            args[validateDatabaseIndex + 1],
            CancellationToken.None);
        Console.WriteLine("Database integrity check passed.");
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Database integrity check failed: {exception.Message}");
        Environment.ExitCode = 1;
    }

    return;
}

var builder = WebApplication.CreateBuilder(args);

if (OperatingSystem.IsWindows()
    && Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
{
    ConfigureWindowsEventLog(builder);
}

// Allow hosting as a Windows Service. No-op when running interactively from a
// console (e.g. `dotnet run`), so the dev workflow is unchanged. The name
// matches the service registered by Deploy-MoneyFlow.ps1.
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "MoneyFlow";
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

// Cap request bodies so an oversized POST can't exhaust memory. The largest
// real import is well under 1 MB; 10 MB is a generous ceiling.
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 10 * 1024 * 1024;
});

// Resolve the SQLite database path. Order of precedence:
//   1. MONEY_FLOW_DB_PATH environment variable (explicit operational override).
//   2. MoneyFlow:DatabasePath from the normal ASP.NET Core configuration chain.
//   3. <app base directory>/data/monthly-money-flow.db
// Using AppContext.BaseDirectory (not Directory.GetCurrentDirectory) so that
// running under a Windows Service does not accidentally place the DB in
// C:\Windows\System32 when WorkingDirectory is not set.
var databasePath = Environment.GetEnvironmentVariable("MONEY_FLOW_DB_PATH")
    ?? builder.Configuration["MoneyFlow:DatabasePath"]
    ?? Path.Combine(AppContext.BaseDirectory, "data", "monthly-money-flow.db");
var databaseDirectory = Path.GetDirectoryName(databasePath);
if (!string.IsNullOrWhiteSpace(databaseDirectory))
{
    Directory.CreateDirectory(databaseDirectory);
}

builder.Services.AddDbContextFactory<MoneyFlowDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath};Default Timeout=30"));
builder.Services.AddSingleton<MonthlyFlowStore>();
builder.Services.AddSingleton<MonthlyFlowImportService>();
builder.Services.AddSingleton<ApiKeyProvider>();
builder.Services.AddSingleton<BackupPassphraseProvider>();
builder.Services.AddSingleton<DatabaseMaintenanceGate>();

var app = builder.Build();

var apiKeyProvider = app.Services.GetRequiredService<ApiKeyProvider>();
if (string.IsNullOrWhiteSpace(apiKeyProvider.GetApiKey()))
{
    app.Logger.LogWarning(
        "MoneyFlow API key is not configured. State-changing API endpoints will reject requests until " +
        "Set-MoneyFlowApiKey.ps1 is run.");
}

await app.Services.GetRequiredService<MonthlyFlowStore>()
    .InitializeAsync(CancellationToken.None);

app.Use(async (context, next) =>
{
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; " +
        "form-action 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
        "font-src 'self'; img-src 'self' data:; connect-src 'self'";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // HTML documents must always revalidate: they reference the ?v=-fingerprinted
    // css/js, so a stale cached .html pins old asset versions and old markup
    // (e.g. one project URL showing pre-update UI while another shows current).
    // The fingerprinted assets themselves stay cacheable.
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        }
    }
});

// Everyone on the trusted internal network may use the read-only UI and GET/HEAD
// API endpoints without signing in. Every state-changing API request is gated by
// the API key, including browser-driven corrections. This default-deny rule also
// protects future POST/PUT/PATCH/DELETE endpoints automatically.
app.Use(async (context, next) =>
{
    var isReadOnlyMethod = HttpMethods.IsGet(context.Request.Method)
        || HttpMethods.IsHead(context.Request.Method)
        || HttpMethods.IsOptions(context.Request.Method);
    var requiresApiKey = context.Request.Path.StartsWithSegments("/api")
        && !isReadOnlyMethod;

    if (requiresApiKey)
    {
        var apiKey = apiKeyProvider.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { error = "Mutation API key is not configured." });
            return;
        }

        // Fixed-time comparison so response timing doesn't reveal how much of
        // a guessed key matched.
        var provided = context.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (provided is null
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(provided),
                Encoding.UTF8.GetBytes(apiKey)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid API key." });
            return;
        }
    }

    await next();
});

var databaseMaintenanceGate = app.Services.GetRequiredService<DatabaseMaintenanceGate>();
app.Use(async (context, next) =>
{
    var isRestoreRequest = string.Equals(
        context.Request.Path.Value,
        "/api/maintenance/db-restore",
        StringComparison.OrdinalIgnoreCase);
    var usesDatabase = context.Request.Path.StartsWithSegments("/api")
        || context.Request.Path.Equals("/ready");

    if (!usesDatabase || isRestoreRequest)
    {
        await next();
        return;
    }

    using var requestLease = databaseMaintenanceGate.TryEnterRequest();
    if (requestLease is null)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "1";
        await context.Response.WriteAsJsonAsync(new { error = "Database maintenance is in progress." });
        return;
    }

    await next();
});

app.MapSystemEndpoints();
app.MapMaintenanceEndpoints(databasePath);
app.MapImportEndpoints();
app.MapProjectEndpoints();
app.MapManualEditEndpoints();

// API callers must receive a real JSON 404. Without this catch-all, the SPA
// fallback returns index.html with HTTP 200 for mistyped /api routes.
app.Map("/api/{**path}", () => Results.NotFound(new { error = "API endpoint not found." }));

app.MapFallbackToFile("index.html");

app.Run();

static void ConfigureWindowsEventLog(WebApplicationBuilder builder)
{
    if (OperatingSystem.IsWindows())
    {
        var settings = new Microsoft.Extensions.Logging.EventLog.EventLogSettings
        {
            SourceName = "MoneyFlow"
        };
        builder.Logging.AddEventLog(settings);
    }
}
