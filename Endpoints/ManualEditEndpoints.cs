using PADS.MoneyFlow.Api.Dtos;
using PADS.MoneyFlow.Api.Services;

namespace PADS.MoneyFlow.Api.Endpoints;

internal static class ManualEditEndpoints
{
    public static void MapManualEditEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectCode}/ignored-rows", async (
            string projectCode,
            HttpRequest request,
            MonthlyFlowStore store,
            CancellationToken cancellationToken) =>
        {
            var objectNumber = request.Query["objectNumber"].FirstOrDefault();
            var rows = await store.GetIgnoredRowsForProjectScopeAsync(projectCode, objectNumber, cancellationToken);
            return Results.Ok(new
            {
                projectCode,
                objectNumber,
                rows = rows.Select(row => new
                {
                    row.Id,
                    row.ProjectCode,
                    row.ProjectName,
                    row.ObjectNumber,
                    row.ObjectName,
                    row.SubcontractorName,
                    row.CustomerName,
                    row.Year,
                    row.Month,
                    row.SourceSheet,
                    row.SourceRow,
                    row.AmountWithoutVat,
                    row.ExcludedAt,
                    row.ExcludedReason,
                    row.ExcludedBy
                })
            });
        });

        app.MapPost("/api/projects/{projectCode}/monthly-flow/{rowId:guid}/exclude", async (
            string projectCode,
            Guid rowId,
            HttpRequest request,
            ExcludeMonthlyRowRequest? excludeRequest,
            MonthlyFlowStore store,
            CancellationToken cancellationToken) =>
        {
            var objectNumber = request.Query["objectNumber"].FirstOrDefault();
            var result = await store.ExcludeMonthlyRowAsync(
                projectCode, objectNumber, rowId, excludeRequest?.Reason, cancellationToken);
            return result.IsSuccess
                ? Results.Ok(new { excluded = true, rowId = result.Value.Id })
                : Results.NotFound(new { excluded = false, error = result.Error });
        });

        app.MapPost("/api/projects/{projectCode}/monthly-flow/{rowId:guid}/restore", async (
            string projectCode,
            Guid rowId,
            HttpRequest request,
            MonthlyFlowStore store,
            CancellationToken cancellationToken) =>
        {
            var objectNumber = request.Query["objectNumber"].FirstOrDefault();
            var result = await store.RestoreMonthlyRowAsync(projectCode, objectNumber, rowId, cancellationToken);
            return result.IsSuccess
                ? Results.Ok(new { restored = true, rowId = result.Value.Id })
                : Results.NotFound(new { restored = false, error = result.Error });
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
    }
}
