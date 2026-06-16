namespace PADS.MoneyFlow.Api.Models;

public sealed class ProjectObjectValue
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string ProjectCode { get; set; }
    public required string ObjectNumber { get; set; }
    public string? ObjectPrintCode { get; set; }
    public string? DepartmentCode { get; set; }
    public string? ObjectIndex { get; set; }
    public decimal ProjectValueAmount { get; set; }
    public int? SourceRowCount { get; set; }
    public string? SourceRowsJson { get; set; }
    public Guid LastSeenContractImportBatchId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public required string RowKey { get; set; }
}
