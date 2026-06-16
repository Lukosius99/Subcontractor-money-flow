namespace PADS.MoneyFlow.Api.Models;

public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string ProjectCode { get; set; }
    public string? ProjectName { get; set; }
    public string? Responsible { get; set; }
    public string? Engineer { get; set; }
    public string? ProjectStatus { get; set; }
    public bool IsActiveContractedProject { get; set; }
    public bool IsInLatestContractedImport { get; set; }
    public Guid? LastSeenContractImportBatchId { get; set; }
    public DateTimeOffset? LastSeenContractImportAt { get; set; }
    public DateTimeOffset? BecameInactiveAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
