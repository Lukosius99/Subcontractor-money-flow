namespace PADS.MoneyFlow.Api.Dtos;

public sealed class MonthlyFlowImportResponse
{
    public bool Imported { get; set; }
    public string? Reason { get; set; }
    public string? SourceFileName { get; set; }
    public string? ContentHash { get; set; }
    public DateTimeOffset? ImportedAt { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public int RowsImported { get; set; }
    public int RowsReceived { get; set; }
    public int RowsInserted { get; set; }
    public int RowsUpdated { get; set; }
    public int RowsSkipped { get; set; }
    public int WarningsImported { get; set; }
    public int WarningsCount { get; set; }
}

public sealed class MonthlyFlowImportErrorResponse
{
    public bool Imported { get; set; }
    public required string Error { get; set; }
}

public sealed class ImportBatchResponse
{
    public required string SourceFileName { get; set; }
    public required string ContentHash { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public int RowsImported { get; set; }
    public int RowsReceived { get; set; }
    public int RowsInserted { get; set; }
    public int RowsUpdated { get; set; }
    public int RowsSkipped { get; set; }
    public int WarningsImported { get; set; }
    public int WarningsCount { get; set; }
    public required string Status { get; set; }
}
