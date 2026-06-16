namespace PADS.MoneyFlow.Api.Models;

/// <summary>
/// User-created correction that moves a subcontractor's monthly invoice rows
/// from a wrong/empty object number to the correct one for a project, so the
/// rows land in the right object scope and can then be linked to a contract.
/// Like <see cref="ManualContractLink"/>, this only affects read-time matching
/// and is harmless once source files use the correct object number.
/// </summary>
public sealed class ManualObjectAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Normalized parent project code, e.g. "P1585".</summary>
    public required string ProjectCode { get; set; }

    /// <summary>Normalized wrong/original object number as displayed, e.g. "P1585-0" (may equal the parent when the source row had no object).</summary>
    public required string SourceObjectNumber { get; set; }

    /// <summary>Normalized subcontractor match key, scopes the move to one subcontractor's invoices.</summary>
    public required string SubcontractorKey { get; set; }

    /// <summary>Display name of the subcontractor (for the undo UI).</summary>
    public required string SubcontractorName { get; set; }

    /// <summary>Normalized corrected object number, e.g. "P1585-01".</summary>
    public required string TargetObjectNumber { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
