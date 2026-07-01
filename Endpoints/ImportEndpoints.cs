using PADS.MoneyFlow.Api.Dtos;
using PADS.MoneyFlow.Api.Services;

namespace PADS.MoneyFlow.Api.Endpoints;

internal static class ImportEndpoints
{
    public static void MapImportEndpoints(this WebApplication app)
    {
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
                    detail: "The import failed unexpectedly. Check the server logs for details.",
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
    }
}
