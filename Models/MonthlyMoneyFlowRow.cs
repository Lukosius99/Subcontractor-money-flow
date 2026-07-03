namespace PADS.MoneyFlow.Api.Models;

public sealed class MonthlyMoneyFlowRow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Year { get; set; }
    public int Month { get; set; }
    public required string ProjectCode { get; set; }
    public string? ProjectName { get; set; }
    public string? ObjectNumber { get; set; }
    public string? SubcontractorName { get; set; }
    public string? CustomerName { get; set; }
    public string RowType { get; set; } = "SubcontractorInvoice";
    public string? ObjectName { get; set; }
    public decimal AmountWithoutVat { get; set; }
    public decimal? IndexedAmount { get; set; }
    public string? Responsible { get; set; }
    public string? Engineer { get; set; }
    public string? SourceSheet { get; set; }
    public int SourceRow { get; set; }
    public required string RowKey { get; set; }
    public Guid LastImportBatchId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsExcludedFromTotals { get; set; }
    public DateTimeOffset? ExcludedAt { get; set; }
    public string? ExcludedReason { get; set; }
    public string? ExcludedBy { get; set; }
}
