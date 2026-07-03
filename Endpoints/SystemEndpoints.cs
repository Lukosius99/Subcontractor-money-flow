using PADS.MoneyFlow.Api.Services;

namespace PADS.MoneyFlow.Api.Endpoints;

internal static class SystemEndpoints
{
    public static void MapSystemEndpoints(this WebApplication app)
    {
        // Lightweight liveness probe — confirms the service is up without touching the
        // database or the real API. Intentionally open (no API key required).
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        if (!app.Environment.IsProduction())
        {
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
        else
        {
            // Reserve the API route so the SPA fallback cannot turn it into a
            // misleading 200 response containing index.html in Production.
            app.MapGet("/api/diagnostics/subcontractors", () => Results.NotFound());
        }
    }
}
