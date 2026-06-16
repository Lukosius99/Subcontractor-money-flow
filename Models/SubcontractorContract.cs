namespace PADS.MoneyFlow.Api.Models;

public sealed class SubcontractorContract
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string ProjectCode { get; set; }
    public string? ProjectName { get; set; }
    public required string ObjectNumber { get; set; }
    public string? ObjectPrintCode { get; set; }
    public string? DepartmentCode { get; set; }
    public string? ObjectIndex { get; set; }
    public required string SubcontractorName { get; set; }
    public string? ObjectName { get; set; }
    public decimal ContractedAmount { get; set; }
    public string? ProjectStatus { get; set; }
    public bool IsActiveContractedProject { get; set; }
    public bool IsInLatestContractedImport { get; set; }
    public Guid? LastSeenContractImportBatchId { get; set; }
    public DateTimeOffset? LastSeenContractImportAt { get; set; }
    public DateTimeOffset? BecameInactiveAt { get; set; }
    public int? SourceRowCount { get; set; }
    public string? SourceRowsJson { get; set; }
    public string? Responsible { get; set; }
    public string? Engineer { get; set; }
    public required string SourceSystem { get; set; }
    public string? ExternalContractLineId { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DisplayFieldsUpdatedAt { get; set; }
}
