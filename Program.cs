using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PADS.MoneyFlow.Api.Dtos;
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

// Lightweight liveness probe — confirms the service is up without touching the
// database or the real API. Intentionally open (no API key required).
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/imports/monthly-flow", async (
    HttpRequest request,
    MonthlyFlowImportService importService,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    try
    {
        string? sourceFileName;
        string json;

        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(cancellationToken);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null)
            {
                return Results.BadRequest(new { error = "Uploaded JSON file is required in multipart form data." });
            }

            sourceFileName = file.FileName;
            await using var stream = file.OpenReadStream();
            using var reader = new StreamReader(stream);
            json = await reader.ReadToEndAsync(cancellationToken);
        }
        else
        {
            sourceFileName = request.Query["sourceFileName"].FirstOrDefault()
                ?? request.Headers["X-Source-File-Name"].FirstOrDefault();

            using var reader = new StreamReader(request.Body);
            json = await reader.ReadToEndAsync(cancellationToken);
        }

        var result = await importService.ImportAsync(sourceFileName, json, cancellationToken);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.BadRequest(new MonthlyFlowImportErrorResponse
            {
                Imported = false,
                Error = result.Error!
            });
    }
    catch (Exception exception)
    {
        logger.LogError(exception, "Monthly flow import failed.");

        return Results.Problem(
            title: "Monthly flow import failed",
            detail: exception.Message,
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/imports/contracts", async (
    ContractImportRequest? importRequest,
    MonthlyFlowStore store,
    CancellationToken cancellationToken) =>
{
    if (importRequest is null)
    {
        return Results.BadRequest(new ContractImportErrorResponse
        {
            Imported = false,
            Error = "Request JSON must be an object."
        });
    }

    if (importRequest.Rows is null && importRequest.ProjectValueRows is null)
    {
        return Results.BadRequest(new ContractImportErrorResponse
        {
            Imported = false,
            Error = "rows or projectValueRows array is required."
        });
    }

    var result = await store.ImportContractsAsync(importRequest, cancellationToken);
    return Results.Ok(result);
});

app.MapGet("/api/imports/monthly-flow/status", async (
    MonthlyFlowStore store,
    CancellationToken cancellationToken) =>
{
    var imports = (await store.GetImportBatchesAsync(cancellationToken))
        .Select(batch => new ImportBatchResponse
        {
            SourceFileName = batch.SourceFileName,
            ContentHash = batch.ContentHash,
            ImportedAt = batch.ImportedAt,
            Year = batch.Year,
            Month = batch.Month,
            RowsImported = batch.RowsInserted + batch.RowsUpdated + batch.RowsSkipped,
            RowsReceived = batch.RowsReceived,
            RowsInserted = batch.RowsInserted,
            RowsUpdated = batch.RowsUpdated,
            RowsSkipped = batch.RowsSkipped,
            WarningsImported = batch.WarningsCount,
            WarningsCount = batch.WarningsCount,
            Status = batch.Status
        })
        .ToList();

    return Results.Ok(new
    {
        latestImport = imports.FirstOrDefault(),
        imports
    });
});

app.MapGet("/api/diagnostics/subcontractors", async (
    MonthlyFlowStore store,
    CancellationToken cancellationToken) =>
{
    var rows = await store.GetSubcontractorDiagnosticsAsync(cancellationToken);
    return Results.Ok(new
    {
        count = rows.Count,
        rows
    });
});

app.MapGet("/api/projects", async (
    MonthlyFlowStore store,
    CancellationToken cancellationToken) =>
{
    var projects = await store.GetProjectSummariesAsync(cancellationToken);
    static bool IsMainActiveProject(ProjectSummary project) =>
        project.IsActiveContractedProject
        && project.IsInLatestContractedImport
        && (string.IsNullOrWhiteSpace(project.ProjectStatus)
            || string.Equals(project.ProjectStatus, "Statybos", StringComparison.OrdinalIgnoreCase));

    var activeProjects = projects
        .Where(IsMainActiveProject)
        .ToList();
    var inactiveProjects = projects
        .Where(project => !IsMainActiveProject(project))
        .ToList();
    var projectCodes = activeProjects.Select(project => project.ProjectCode).ToList();

    static object ProjectPayload(ProjectSummary project) => new
    {
        projectCode = project.ProjectCode,
        projectName = project.ProjectName,
        responsible = project.Responsible,
        engineer = project.Engineer,
        projectStatus = project.ProjectStatus,
        isActiveContractedProject = project.IsActiveContractedProject,
        isInLatestContractedImport = project.IsInLatestContractedImport,
        lastSeenContractImportAt = project.LastSeenContractImportAt,
        becameInactiveAt = project.BecameInactiveAt,
        objectCount = project.ObjectCount,
        amountWithoutVat = project.AmountWithoutVat,
        contractedAmount = project.ContractedAmount,
        projectValue = project.ProjectValue,
        clientInvoiced = project.ClientInvoiced,
        remaining = project.Remaining,
        rowCount = project.RowCount,
        subcontractorCount = project.SubcontractorCount,
        warningsCount = project.WarningsCount,
        status = project.Status,
        objects = project.Objects.Select(projectObject => new
        {
            objectNumber = projectObject.ObjectNumber,
            objectCode = projectObject.ObjectCode,
            objectPrintCode = projectObject.ObjectPrintCode,
            departmentCode = projectObject.DepartmentCode,
            contractedAmount = projectObject.ContractedAmount,
            amountWithoutVat = projectObject.AmountWithoutVat,
            remaining = projectObject.Remaining,
            subcontractorCount = projectObject.SubcontractorCount,
            warningsCount = projectObject.WarningsCount,
            status = projectObject.Status,
            objectName = projectObject.ObjectName,
            responsibles = projectObject.Responsibles,
            engineers = projectObject.Engineers
        })
    };

    return Results.Ok(new
    {
        projectCodes,
        inactiveProjectCodes = inactiveProjects.Select(project => project.ProjectCode).ToList(),
        allProjectCodes = projects.Select(project => project.ProjectCode).ToList(),
        projects = activeProjects.Select(ProjectPayload),
        activeProjects = activeProjects.Select(ProjectPayload),
        inactiveProjects = inactiveProjects.Select(ProjectPayload)
    });
});

app.MapGet("/api/projects/{projectCode}/monthly-flow", async (
    string projectCode,
    HttpResponse response,
    HttpRequest request,
    MonthlyFlowStore store,
    CancellationToken cancellationToken) =>
{
    response.Headers.CacheControl = "no-store";
    response.Headers.Pragma = "no-cache";
    response.Headers.Expires = "0";

    var selectedObjectNumber = request.Query["objectNumber"].FirstOrDefault();
    var summaries = await store.GetProjectSummariesAsync(cancellationToken);
    var parentProjectCode = MonthlyFlowStore.ParseProjectObjectCode(projectCode).ParentProjectCode;
    var parentSummary = summaries.FirstOrDefault(summary =>
        string.Equals(summary.ProjectCode, parentProjectCode, StringComparison.OrdinalIgnoreCase));
    var rows = await store.GetRowsForProjectScopeAsync(projectCode, selectedObjectNumber, cancellationToken);
    var detail = await store.GetProjectDetailAsync(projectCode, selectedObjectNumber, cancellationToken);
    var subcontractorRows = rows
        .Where(row => !MonthlyFlowStore.IsClientMonthlyValueRow(row))
        .ToList();

    var groupedRows = rows
        .GroupBy(row => new { row.Year, row.Month })
        .OrderBy(group => group.Key.Year)
        .ThenBy(group => group.Key.Month)
        .Select(group => new
        {
            year = group.Key.Year,
            month = group.Key.Month,
            rows = group
                .OrderBy(row => row.SourceSheet)
                .ThenBy(row => row.SourceRow)
        });

    var totalsByMonth = subcontractorRows
        .GroupBy(row => new { row.Year, row.Month })
        .OrderBy(group => group.Key.Year)
        .ThenBy(group => group.Key.Month)
        .Select(group => new
        {
            year = group.Key.Year,
            month = group.Key.Month,
            amountWithoutVat = group.Sum(row => row.AmountWithoutVat)
        });

    var totalsBySubcontractor = subcontractorRows
        .GroupBy(row => string.IsNullOrWhiteSpace(row.SubcontractorName)
            ? "(Be subrangovo)"
            : row.SubcontractorName)
        .OrderByDescending(group => group.Sum(row => row.AmountWithoutVat))
        .Select(group => new
        {
            subcontractorName = group.Key,
            amountWithoutVat = group.Sum(row => row.AmountWithoutVat)
        });

    return Results.Ok(new
    {
        projectCode,
        parentProjectCode,
        selectedObjectNumber,
        isAllObjects = string.IsNullOrWhiteSpace(selectedObjectNumber)
            && string.Equals(projectCode, parentProjectCode, StringComparison.OrdinalIgnoreCase),
        objects = parentSummary?.Objects.Select(projectObject => new
        {
            objectNumber = projectObject.ObjectNumber,
            objectCode = projectObject.ObjectCode,
            objectPrintCode = projectObject.ObjectPrintCode,
            departmentCode = projectObject.DepartmentCode,
            contractedAmount = projectObject.ContractedAmount,
            amountWithoutVat = projectObject.AmountWithoutVat,
            remaining = projectObject.Remaining,
            subcontractorCount = projectObject.SubcontractorCount,
            warningsCount = projectObject.WarningsCount,
            status = projectObject.Status
        }) ?? [],
        projectName = detail.ProjectName,
        responsible = detail.Responsible,
        engineer = detail.Engineer,
        contractCount = detail.ContractCount,
        totals = new
        {
            amountWithoutVat = subcontractorRows.Sum(row => row.AmountWithoutVat),
            byMonth = totalsByMonth,
            bySubcontractor = totalsBySubcontractor
        },
        groups = groupedRows,
        contractRows = detail.ContractRows,
        projectObjectValues = detail.ProjectObjectValues,
        smdCustomerRows = detail.SmdCustomerRows,
        objectAssignments = detail.ObjectAssignments
    });
});

app.MapPost("/api/projects/{projectCode}/contract-links", async (
    string projectCode,
    ManualContractLinkRequest? linkRequest,
    MonthlyFlowStore store,
    CancellationToken cancellationToken) =>
{
    if (linkRequest is null)
    {
        return Results.BadRequest(new { linked = false, error = "Užklausos JSON turi būti objektas." });
    }

    var result = await store.CreateManualContractLinkAsync(
        projectCode,
        linkRequest.TargetContractRowKey,
        linkRequest.SourceSubcontractorName,
        linkRequest.SourceObjectNumber,
        cancellationToken);

    return result.Success
        ? Results.Ok(new
        {
            linked = true,
            id = result.Link!.Id,
            projectCode = result.Link.ProjectCode,
            objectNumber = result.Link.ObjectNumber,
            sourceName = result.Link.SourceSubcontractorName,
            targetName = result.Link.TargetSubcontractorName
        })
        : Results.BadRequest(new { linked = false, error = result.Error });
});

app.MapDelete("/api/projects/{projectCode}/contract-links/{linkId:guid}", async (
    string projectCode,
    Guid linkId,
    MonthlyFlowStore store,
    CancellationToken cancellationToken) =>
{
    var deleted = await store.DeleteManualContractLinkAsync(projectCode, linkId, cancellationToken);
    return deleted ? Results.NoContent() : Results.NotFound();
});

app.MapPost("/api/projects/{projectCode}/object-assignments", async (
    string projectCode,
    ObjectAssignmentRequest? assignmentRequest,
    MonthlyFlowStore store,
    CancellationToken cancellationToken) =>
{
    if (assignmentRequest is null)
    {
        return Results.BadRequest(new { assigned = false, error = "Užklausos JSON turi būti objektas." });
    }

    var result = await store.CreateObjectAssignmentAsync(
        projectCode,
        assignmentRequest.SubcontractorName,
        assignmentRequest.SourceObjectNumber,
        assignmentRequest.TargetObjectNumber,
        cancellationToken);

    return result.Success
        ? Results.Ok(new
        {
            assigned = true,
            id = result.Assignment!.Id,
            projectCode = result.Assignment.ProjectCode,
            sourceObjectNumber = result.Assignment.SourceObjectNumber,
            targetObjectNumber = result.Assignment.TargetObjectNumber,
            subcontractorName = result.Assignment.SubcontractorName
        })
        : Results.BadRequest(new { assigned = false, error = result.Error });
});

app.MapDelete("/api/projects/{projectCode}/object-assignments/{assignmentId:guid}", async (
    string projectCode,
    Guid assignmentId,
    MonthlyFlowStore store,
    CancellationToken cancellationToken) =>
{
    var deleted = await store.DeleteObjectAssignmentAsync(projectCode, assignmentId, cancellationToken);
    return deleted ? Results.NoContent() : Results.NotFound();
});

app.MapFallbackToFile("index.html");

app.Run();
