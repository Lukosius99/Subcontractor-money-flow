using System.Text.Json;

namespace PADS.MoneyFlow.Api.Dtos;

public sealed class ContractImportRequest
{
    public string? SchemaVersion { get; set; }
    public string? SourceSystem { get; set; }
    public string? SourceSheet { get; set; }
    public List<string>? SourceSheets { get; set; }
    public DateTimeOffset? ExportedAt { get; set; }
    public int? RawRowCount { get; set; }
    public int? AggregatedRowCount { get; set; }
    public int? ContractRowCount { get; set; }
    public int? ProjectValueRowCount { get; set; }
    public int? SkippedBlankObjectPrintCodeCount { get; set; }
    public int? SkippedInvalidRowCount { get; set; }
    public List<ContractImportRowRequest>? Rows { get; set; }
    public List<ProjectValueRowRequest>? ProjectValueRows { get; set; }
    public List<JsonElement>? Warnings { get; set; }
}

public sealed class ContractImportRowRequest
{
    public string? RowType { get; set; }
    public string? ProjectCode { get; set; }
    public string? ProjectName { get; set; }
    public string? ObjectNumber { get; set; }
    public string? ObjectPrintCode { get; set; }
    public string? DepartmentCode { get; set; }
    public string? ObjectIndex { get; set; }
    public string? SubcontractorName { get; set; }
    public string? ObjectName { get; set; }
    public decimal? ContractedAmount { get; set; }
    public string? ProjectStatus { get; set; }
    public int? SourceRowCount { get; set; }
    public List<int>? SourceRows { get; set; }
    public string? Responsible { get; set; }
    public string? Engineer { get; set; }
    public string? ExternalContractLineId { get; set; }
}

public sealed class ProjectValueRowRequest
{
    public string? RowType { get; set; }
    public string? ProjectCode { get; set; }
    public string? ObjectNumber { get; set; }
    public string? ObjectPrintCode { get; set; }
    public string? DepartmentCode { get; set; }
    public string? ObjectIndex { get; set; }
    public decimal? ProjectValueAmount { get; set; }
    public int? SourceRowCount { get; set; }
    public List<int>? SourceRows { get; set; }
}

public sealed class ContractImportResponse
{
    // Legacy fields (kept for backward compatibility with existing PAD/tests).
    public bool Imported { get; set; }
    public int ProjectsCreated { get; set; }
    public int ContractsInserted { get; set; }
    public int ContractsUpdated { get; set; }
    public int ProjectValuesInserted { get; set; }
    public int ProjectValuesUpdated { get; set; }

    // Newer, more descriptive aliases.
    public bool Success { get; set; }
    public int ImportedRows { get; set; }
    public int Inserted { get; set; }
    public int Updated { get; set; }

    public List<string> Warnings { get; set; } = [];
}

public sealed class ContractImportErrorResponse
{
    public bool Imported { get; set; }
    public required string Error { get; set; }
}
