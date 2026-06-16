namespace PADS.MoneyFlow.Api.Models;

public sealed class ImportBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string SourceFileName { get; set; }
    public string ContentHash { get; set; } = "";
    public string? SchemaVersion { get; set; }
    public string? SourceSystem { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public string? SheetName { get; set; }
    public DateTimeOffset? ExportedAt { get; set; }
    public int RawRowCount { get; set; }
    public int AggregatedRowCount { get; set; }
    public int SkippedBlankObjectPrintCodeCount { get; set; }
    public int SkippedInvalidRowCount { get; set; }
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;
    public int RowsReceived { get; set; }
    public int RowsInserted { get; set; }
    public int RowsUpdated { get; set; }
    public int RowsSkipped { get; set; }
    public int WarningsCount { get; set; }
    public string Status { get; set; } = "Imported";
}
