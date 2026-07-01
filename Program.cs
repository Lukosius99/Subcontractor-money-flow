using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PADS.MoneyFlow.Api.Endpoints;
using PADS.MoneyFlow.Api.Persistence;
using PADS.MoneyFlow.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Allow hosting as a Windows Service. No-op when running interactively from a
// console (e.g. `dotnet run`), so the dev workflow is unchanged.
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "PADS Monthly Money Flow";
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

// API key for write endpoints (imports + deletes). Resolved from, in order:
//   1. MoneyFlow:ApiKey in appsettings / MoneyFlow__ApiKey env var.
//   2. MONEY_FLOW_API_KEY environment variable.
// Leave unset and the check is disabled (warned at startup) so the app keeps
// working until the key is configured; once set, write endpoints require it.
var apiKey = builder.Configuration["MoneyFlow:ApiKey"]
    ?? Environment.GetEnvironmentVariable("MONEY_FLOW_API_KEY");

var app = builder.Build();

if (string.IsNullOrWhiteSpace(apiKey))
{
    app.Logger.LogWarning(
        "MoneyFlow API key is not configured. Import and delete endpoints are UNPROTECTED. " +
        "Set the MONEY_FLOW_API_KEY environment variable to require a key.");
}

await app.Services.GetRequiredService<MonthlyFlowStore>()
    .CleanupDuplicatePeriodsAsync(CancellationToken.None);

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

// Gate the PAD bulk-ingestion endpoints (POST /api/imports/*) behind the API key.
// Everything else — read-only GETs and the browser-driven manual link/assignment
// edits (which the frontend calls without a key) — stays open, matching the
// app's trusted-internal-network model.
app.Use(async (context, next) =>
{
    var isImport = context.Request.Path.StartsWithSegments("/api/imports")
        && HttpMethods.IsPost(context.Request.Method);

    if (isImport && !string.IsNullOrWhiteSpace(apiKey))
    {
        var provided = context.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (!string.Equals(provided, apiKey, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid API key." });
            return;
        }
    }

    await next();
});

app.MapSystemEndpoints();
app.MapImportEndpoints();
app.MapProjectEndpoints();
app.MapManualEditEndpoints();

app.MapFallbackToFile("index.html");

app.Run();
