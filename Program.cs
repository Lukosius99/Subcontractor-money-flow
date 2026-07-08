using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PADS.MoneyFlow.Api.Endpoints;
using PADS.MoneyFlow.Api.Persistence;
using PADS.MoneyFlow.Api.Services;

var builder = WebApplication.CreateBuilder(args);

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
//   1. MoneyFlow:DatabasePath in appsettings (.Production.json overrides base).
//   2. MONEY_FLOW_DB_PATH environment variable.
//   3. <app base directory>/data/monthly-money-flow.db
// Using AppContext.BaseDirectory (not Directory.GetCurrentDirectory) so that
// running under a Windows Service does not accidentally place the DB in
// C:\Windows\System32 when WorkingDirectory is not set.
var databasePath = builder.Configuration["MoneyFlow:DatabasePath"]
    ?? Environment.GetEnvironmentVariable("MONEY_FLOW_DB_PATH")
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

var app = builder.Build();

var apiKeyProvider = app.Services.GetRequiredService<ApiKeyProvider>();
if (string.IsNullOrWhiteSpace(apiKeyProvider.GetApiKey()))
{
    app.Logger.LogWarning(
        "MoneyFlow API key is not configured. Import endpoints will reject requests until " +
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

// Gate the PAD bulk-ingestion endpoints (POST /api/imports/*) and maintenance
// operations (POST /api/maintenance/*) behind the API key. Everything else —
// read-only GETs and the browser-driven manual link/assignment edits (which the
// frontend calls without a key) — stays open, matching the app's
// trusted-internal-network model.
app.Use(async (context, next) =>
{
    var requiresApiKey = (context.Request.Path.StartsWithSegments("/api/imports")
            || context.Request.Path.StartsWithSegments("/api/maintenance"))
        && HttpMethods.IsPost(context.Request.Method);

    if (requiresApiKey)
    {
        var apiKey = apiKeyProvider.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { error = "Import API key is not configured." });
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

app.MapSystemEndpoints();
app.MapMaintenanceEndpoints(databasePath);
app.MapImportEndpoints();
app.MapProjectEndpoints();
app.MapManualEditEndpoints();

app.MapFallbackToFile("index.html");

app.Run();
