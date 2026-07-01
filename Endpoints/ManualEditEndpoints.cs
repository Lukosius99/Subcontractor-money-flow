using PADS.MoneyFlow.Api.Dtos;
using PADS.MoneyFlow.Api.Services;

namespace PADS.MoneyFlow.Api.Endpoints;

internal static class ManualEditEndpoints
{
    public static void MapManualEditEndpoints(this WebApplication app)
    {
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
