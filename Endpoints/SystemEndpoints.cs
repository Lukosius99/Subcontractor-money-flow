using PADS.MoneyFlow.Api.Services;

namespace PADS.MoneyFlow.Api.Endpoints;

internal static class SystemEndpoints
{
    public static void MapSystemEndpoints(this WebApplication app)
    {
        // Lightweight liveness probe — confirms the service is up without touching the
        // database or the real API. Intentionally open (no API key required).
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

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
    }
}
