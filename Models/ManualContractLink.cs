namespace PADS.MoneyFlow.Api.Models;

/// <summary>
/// User-created link that routes monthly invoice rows entered under a wrong
/// subcontractor name (e.g. "UAB Voltarena") to the contracted subcontractor
/// (e.g. "MB Voltarena") for one project object scope. The contract name is
/// the authoritative identity; the link only affects read-time matching and is
/// ignored once source files use the contracted name.
/// </summary>
public sealed class ManualContractLink
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Normalized parent project code, e.g. "P1578".</summary>
    public required string ProjectCode { get; set; }

    /// <summary>Normalized object scope, e.g. "P1578-01".</summary>
    public required string ObjectNumber { get; set; }

    /// <summary>Normalized key of the wrong monthly name, e.g. "UAB|VOLTARENA".</summary>
    public required string SourceSubcontractorKey { get; set; }

    /// <summary>Normalized key of the contracted name, e.g. "MB|VOLTARENA".</summary>
    public required string TargetSubcontractorKey { get; set; }

    public required string SourceSubcontractorName { get; set; }
    public required string TargetSubcontractorName { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
