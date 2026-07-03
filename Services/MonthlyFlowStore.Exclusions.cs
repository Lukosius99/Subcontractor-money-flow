using Microsoft.EntityFrameworkCore;
using PADS.MoneyFlow.Api.Models;
using PADS.MoneyFlow.Api.Persistence;

namespace PADS.MoneyFlow.Api.Services;

public sealed partial class MonthlyFlowStore
{
    private const string ManualExclusionSource = "Manual LAN edit";

    public async Task<IReadOnlyCollection<MonthlyMoneyFlowRow>> GetIgnoredRowsForProjectScopeAsync(
        string projectCode,
        string? objectNumber,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await SchemaInitializer.EnsureMasterDataTablesAsync(db, cancellationToken);

        var rows = await db.MonthlyFlowRows
            .AsNoTracking()
            .Where(row => row.IsExcludedFromTotals)
            .ToListAsync(cancellationToken);

        var parentCode = NormalizeKeyPart(ParseProjectObjectCode(projectCode).ParentProjectCode);
        var assignments = await db.ManualObjectAssignments
            .AsNoTracking()
            .Where(assignment => assignment.ProjectCode == parentCode)
            .ToListAsync(cancellationToken);
        if (assignments.Count > 0)
        {
            var aliasMap = await GetSubcontractorAliasMapAsync(db, cancellationToken);
            ApplyObjectAssignments(rows, assignments, aliasMap);
        }

        return rows
            .Where(row => ProjectRowIsInScope(row.ProjectCode, row.ObjectNumber, projectCode, objectNumber))
            .OrderByDescending(row => row.ExcludedAt)
            .ThenByDescending(row => row.Year)
            .ThenByDescending(row => row.Month)
            .ToList();
    }

    public Task<ServiceResult<MonthlyMoneyFlowRow>> ExcludeMonthlyRowAsync(
        string projectCode,
        string? objectNumber,
        Guid rowId,
        string? reason,
        CancellationToken cancellationToken) =>
        SetMonthlyRowExclusionAsync(projectCode, objectNumber, rowId, true, reason, cancellationToken);

    public Task<ServiceResult<MonthlyMoneyFlowRow>> RestoreMonthlyRowAsync(
        string projectCode,
        string? objectNumber,
        Guid rowId,
        CancellationToken cancellationToken) =>
        SetMonthlyRowExclusionAsync(projectCode, objectNumber, rowId, false, null, cancellationToken);

    private async Task<ServiceResult<MonthlyMoneyFlowRow>> SetMonthlyRowExclusionAsync(
        string projectCode,
        string? objectNumber,
        Guid rowId,
        bool excluded,
        string? reason,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            await SchemaInitializer.EnsureMasterDataTablesAsync(db, cancellationToken);
            var scopedRow = await db.MonthlyFlowRows
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == rowId, cancellationToken);
            if (scopedRow is not null)
            {
                var parentCode = NormalizeKeyPart(ParseProjectObjectCode(projectCode).ParentProjectCode);
                var assignments = await db.ManualObjectAssignments
                    .AsNoTracking()
                    .Where(assignment => assignment.ProjectCode == parentCode)
                    .ToListAsync(cancellationToken);
                if (assignments.Count > 0)
                {
                    var aliasMap = await GetSubcontractorAliasMapAsync(db, cancellationToken);
                    ApplyObjectAssignments([scopedRow], assignments, aliasMap);
                }
            }

            if (scopedRow is null || !ProjectRowIsInScope(scopedRow.ProjectCode, scopedRow.ObjectNumber, projectCode, objectNumber))
            {
                return ServiceResult<MonthlyMoneyFlowRow>.Failure("Monthly row was not found in this project scope.");
            }

            var row = await db.MonthlyFlowRows.FirstAsync(candidate => candidate.Id == rowId, cancellationToken);

            var cleanReason = NullIfWhiteSpace(reason)?.Trim();
            if (cleanReason?.Length > 500)
            {
                cleanReason = cleanReason[..500];
            }

            row.IsExcludedFromTotals = excluded;
            row.ExcludedAt = excluded ? DateTimeOffset.UtcNow : null;
            row.ExcludedReason = excluded ? cleanReason : null;
            row.ExcludedBy = excluded ? ManualExclusionSource : null;
            await db.SaveChangesAsync(cancellationToken);
            return ServiceResult<MonthlyMoneyFlowRow>.Success(row);
        }
        finally
        {
            _lock.Release();
        }
    }
}
