using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PADS.MoneyFlow.Api.Dtos;
using PADS.MoneyFlow.Api.Models;
using PADS.MoneyFlow.Api.Persistence;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PADS.MoneyFlow.Api.Services;

public sealed partial class MonthlyFlowStore
{
    public const string SubcontractorInvoiceRowType = "SubcontractorInvoice";
    public const string ClientMonthlyValueRowType = "ClientMonthlyValue";

    private readonly IDbContextFactory<MoneyFlowDbContext> _dbContextFactory;
    private readonly IReadOnlyCollection<ConfiguredSubcontractorAlias> _configuredAliases;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public MonthlyFlowStore(
        IDbContextFactory<MoneyFlowDbContext> dbContextFactory,
        IConfiguration configuration)
    {
        _dbContextFactory = dbContextFactory;
        _configuredAliases = configuration
            .GetSection("MoneyFlow:SubcontractorAliases")
            .Get<List<ConfiguredSubcontractorAlias>>() ?? [];
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        // Write-Ahead Logging lets dashboard reads proceed concurrently with an
        // import write instead of blocking on it. The setting is persisted in the
        // database header, so running it on each startup is idempotent.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        await SchemaInitializer.EnsureMasterDataTablesAsync(db, cancellationToken);
        await SeedConfiguredSubcontractorAliasesAsync(db, cancellationToken);
        await BackfillSubcontractorIdentityAsync(db, cancellationToken);
    }

}

public sealed record SaveImportResult(SaveImportStatus Status, ImportBatch? ExistingBatch);

public sealed record ProjectSummary(
    string ProjectCode,
    int ObjectCount,
    decimal AmountWithoutVat,
    int RowCount,
    int SubcontractorCount,
    decimal ContractedAmount,
    decimal Remaining,
    int WarningsCount,
    string Status,
    string? ProjectName,
    string? Responsible,
    string? Engineer,
    string? ProjectStatus,
    bool IsActiveContractedProject,
    bool IsInLatestContractedImport,
    DateTimeOffset? LastSeenContractImportAt,
    DateTimeOffset? BecameInactiveAt,
    IReadOnlyCollection<ProjectObjectSummary> Objects,
    decimal ProjectValue,
    decimal ClientInvoiced);

public sealed record ProjectObjectSummary(
    string ObjectNumber,
    string ObjectCode,
    decimal ContractedAmount,
    decimal AmountWithoutVat,
    decimal Remaining,
    int SubcontractorCount,
    int WarningsCount,
    string Status,
    string? ObjectName,
    IReadOnlyList<string> Responsibles,
    IReadOnlyList<string> Engineers,
    string? ObjectPrintCode = null,
    string? DepartmentCode = null,
    decimal ProjectValue = 0,
    decimal ClientInvoiced = 0);

public sealed record ProjectObjectCode(string ParentProjectCode, string ObjectNumber, string ObjectCode);

public sealed record ConfiguredSubcontractorAlias(string? RawName, string? CanonicalName);

public sealed record SubcontractorDiagnosticRow(
    string NormalizedKey,
    string CanonicalName,
    IReadOnlyCollection<string> RawNames);

public sealed record ProjectDetailSnapshot(
    string ProjectCode,
    string? ProjectName,
    string? Responsible,
    string? Engineer,
    int ContractCount,
    IReadOnlyCollection<ProjectObjectValueDetailRow> ProjectObjectValues,
    IReadOnlyCollection<SmdCustomerInvoiceRow> SmdCustomerRows,
    IReadOnlyCollection<ProjectContractDetailRow> ContractRows,
    IReadOnlyCollection<ObjectAssignmentInfo> ObjectAssignments);

public sealed record ObjectAssignmentInfo(
    Guid Id,
    string SourceObjectNumber,
    string TargetObjectNumber,
    string SubcontractorName);

public sealed record ProjectObjectValueDetailRow(
    string ProjectCode,
    string ObjectNumber,
    string? ObjectPrintCode,
    string? DepartmentCode,
    string? ObjectIndex,
    decimal ProjectValueAmount);

public sealed record SmdCustomerInvoiceRow(
    int Year,
    int Month,
    string ObjectNumber,
    string CustomerName,
    string? ObjectName,
    decimal ClientMonthlyAmount,
    decimal TotalYtdAmount,
    string SourceSheet,
    IReadOnlyCollection<int> SourceRows);

public sealed record ProjectContractDetailRow(
    string ProjectObjectNumber,
    string? ObjectNumber,
    string? ObjectPrintCode,
    string? DepartmentCode,
    string SubcontractorName,
    string? ObjectName,
    decimal Contracted,
    decimal Invoiced,
    decimal Remaining,
    decimal UsagePercent,
    string Status,
    IReadOnlyCollection<MonthlyAmount> Monthly,
    bool IsImportedOnly,
    string? RowKey,
    string? Warning,
    IReadOnlyCollection<ManualContractLinkInfo> Links,
    IReadOnlyCollection<MonthlyRowDetail> SourceRows);

public sealed record MonthlyRowDetail(
    Guid Id,
    int Year,
    int Month,
    string ProjectCode,
    string? ObjectNumber,
    string? SubcontractorName,
    string? CustomerName,
    string? ObjectName,
    decimal AmountWithoutVat,
    string? SourceSheet,
    int SourceRow,
    string? Responsible,
    string? Engineer);

public sealed record ManualContractLinkInfo(Guid Id, string SourceName, string TargetName);

public sealed record ManualContractLinkResult(bool Success, string? Error, ManualContractLink? Link);

public sealed record ObjectAssignmentResult(bool Success, string? Error, ManualObjectAssignment? Assignment);

public sealed record MonthlyAmount(int Year, int Month, decimal AmountWithoutVat);

public sealed record YearMonth(int Year, int Month);

internal sealed record InvoiceMatchKey(string ProjectCode, string ObjectNumber, string SubcontractorName, bool HasObjectNumber);

internal sealed record ValidatedContractRow(
    string ProjectCode,
    string? ProjectName,
    string ObjectNumber,
    string? ObjectPrintCode,
    string? DepartmentCode,
    string? ObjectIndex,
    string SubcontractorName,
    string? ObjectName,
    decimal ContractedAmount,
    string? ProjectStatus,
    int? SourceRowCount,
    string? SourceRowsJson,
    string? Responsible,
    string? Engineer,
    string? ExternalContractLineId,
    string LogicalKey,
    string RowKey);

internal sealed record ValidatedProjectValueRow(
    string ProjectCode,
    string ObjectNumber,
    string? ObjectPrintCode,
    string? DepartmentCode,
    string? ObjectIndex,
    decimal ProjectValueAmount,
    int? SourceRowCount,
    string? SourceRowsJson,
    string RowKey);

public enum SaveImportStatus
{
    Imported,
    Duplicate
}
