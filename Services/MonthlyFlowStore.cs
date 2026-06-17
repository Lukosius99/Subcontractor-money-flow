using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PADS.MoneyFlow.Api.Dtos;
using PADS.MoneyFlow.Api.Models;
using PADS.MoneyFlow.Api.Persistence;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PADS.MoneyFlow.Api.Services;

public sealed class MonthlyFlowStore
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
        await EnsureMasterDataTablesAsync(db, cancellationToken);
        await SeedConfiguredSubcontractorAliasesAsync(db, cancellationToken);
        await BackfillSubcontractorIdentityAsync(db, cancellationToken);
    }

    public Task CleanupDuplicatePeriodsAsync(CancellationToken cancellationToken)
    {
        return InitializeAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<ImportBatch>> GetImportBatchesAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var batches = await db.ImportBatches
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return batches
            .OrderByDescending(batch => batch.ImportedAt)
            .ToList();
    }

    public async Task<IReadOnlyCollection<SubcontractorDiagnosticRow>> GetSubcontractorDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await EnsureMasterDataTablesAsync(db, cancellationToken);

        var subcontractors = await db.Subcontractors
            .AsNoTracking()
            .OrderBy(subcontractor => subcontractor.NormalizedKey)
            .ToListAsync(cancellationToken);
        var aliases = await db.SubcontractorAliases
            .AsNoTracking()
            .OrderBy(alias => alias.RawName)
            .ToListAsync(cancellationToken);

        return subcontractors
            .Select(subcontractor => new SubcontractorDiagnosticRow(
                subcontractor.NormalizedKey,
                subcontractor.CanonicalName,
                aliases
                    .Where(alias => alias.SubcontractorId == subcontractor.Id
                        || string.Equals(alias.NormalizedKey, subcontractor.NormalizedKey, StringComparison.Ordinal))
                    .Select(alias => alias.RawName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name)
                    .ToList()))
            .ToList();
    }

    public async Task<SaveImportResult> SaveImportAsync(
        ImportBatch batch,
        IReadOnlyCollection<MonthlyMoneyFlowRow> rows,
        IReadOnlyCollection<ImportWarning> warnings,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var existingBatch = await db.ImportBatches
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    existing => existing.ContentHash == batch.ContentHash,
                    cancellationToken);

            if (existingBatch is not null)
            {
                return new SaveImportResult(SaveImportStatus.Duplicate, existingBatch);
            }

            batch.RowsReceived = rows.Count;
            batch.WarningsCount = warnings.Count;
            batch.Status = "Imported";

            var now = DateTimeOffset.UtcNow;
            db.ImportBatches.Add(batch);
            TouchSubcontractorAliases(
                db,
                rows
                    .Where(row => !IsClientMonthlyValueRow(row))
                    .Select(row => row.SubcontractorName),
                now);

            foreach (var warning in warnings)
            {
                warning.ImportBatchId = batch.Id;
                db.ImportWarnings.Add(warning);
            }

            // Multiple source rows in one file can share the same logical RowKey
            // (same project + object + subcontractor for the month/sheet). The
            // stored model keeps a single row per logical key, so collapse those
            // duplicates here by summing amounts. This both preserves the
            // historical net total (previously multiple rows were summed at read
            // time) and prevents a UNIQUE constraint violation on
            // MonthlyFlowRows.RowKey when the same key is added twice in a batch.
            var collapsedRows = CollapseRowsByKey(rows);

            var rowKeys = collapsedRows.Select(row => row.RowKey).ToList();
            var existingRows = await db.MonthlyFlowRows
                .Where(row => rowKeys.Contains(row.RowKey))
                .ToDictionaryAsync(row => row.RowKey, StringComparer.OrdinalIgnoreCase, cancellationToken);

            foreach (var row in collapsedRows)
            {
                row.LastImportBatchId = batch.Id;
                row.UpdatedAt = now;

                if (!existingRows.TryGetValue(row.RowKey, out var existingRow))
                {
                    batch.RowsInserted++;
                    db.MonthlyFlowRows.Add(row);
                    continue;
                }

                if (RowsMatch(existingRow, row))
                {
                    batch.RowsSkipped++;
                    existingRow.LastImportBatchId = batch.Id;
                    existingRow.UpdatedAt = now;
                    continue;
                }

                batch.RowsUpdated++;
                CopyRowValues(existingRow, row, now);
            }

            ApplyMonthlyDisplayFieldsToContracts(db, rows, now);
            await db.SaveChangesAsync(cancellationToken);
            return new SaveImportResult(SaveImportStatus.Imported, batch);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyCollection<MonthlyMoneyFlowRow>> GetRowsByProjectCodeAsync(
        string projectCode,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.MonthlyFlowRows
            .AsNoTracking()
            .Where(row => row.ProjectCode.ToLower() == projectCode.ToLower())
            .OrderBy(row => row.Year)
            .ThenBy(row => row.Month)
            .ThenBy(row => row.SourceSheet)
            .ThenBy(row => row.SourceRow)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<MonthlyMoneyFlowRow>> GetRowsForProjectScopeAsync(
        string projectCode,
        string? objectNumber,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await EnsureMasterDataTablesAsync(db, cancellationToken);
        var rows = await db.MonthlyFlowRows
            .AsNoTracking()
            .OrderBy(row => row.Year)
            .ThenBy(row => row.Month)
            .ThenBy(row => row.ProjectCode)
            .ThenBy(row => row.SourceSheet)
            .ThenBy(row => row.SourceRow)
            .ToListAsync(cancellationToken);

        var assignmentParent = NormalizeKeyPart(ParseProjectObjectCode(projectCode).ParentProjectCode);
        var objectAssignments = await db.ManualObjectAssignments
            .AsNoTracking()
            .Where(assignment => assignment.ProjectCode == assignmentParent)
            .ToListAsync(cancellationToken);
        if (objectAssignments.Count > 0)
        {
            var aliasMap = await GetSubcontractorAliasMapAsync(db, cancellationToken);
            ApplyObjectAssignments(rows, objectAssignments, aliasMap);
        }

        return rows
            .Where(row => ProjectRowIsInScope(row.ProjectCode, row.ObjectNumber, projectCode, objectNumber))
            .ToList();
    }

    public async Task<ContractImportResponse> ImportContractsAsync(
        ContractImportRequest request,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var sourceSystem = NullIfWhiteSpace(request.SourceSystem) ?? "ManualTest";
        var rows = ValidateContractRows(request.Rows, warnings);
        var projectValueRows = ValidateProjectValueRows(request.ProjectValueRows, warnings);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            await EnsureMasterDataTablesAsync(db, cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var result = new ContractImportResponse
            {
                Imported = true,
                Warnings = warnings
            };

            var now = DateTimeOffset.UtcNow;
            var batch = RecordContractImportBatch(db, request, sourceSystem, result, now);
            var incomingProjectCodes = rows
                .Select(row => NormalizeKeyPart(row.ProjectCode))
                .Concat(projectValueRows.Select(row => NormalizeKeyPart(row.ProjectCode)))
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .ToHashSet(StringComparer.Ordinal);

            var projectsByCode = (await db.Projects.ToListAsync(cancellationToken))
                .ToDictionary(project => NormalizeKeyPart(project.ProjectCode), StringComparer.Ordinal);
            TouchSubcontractorAliases(db, rows.Select(row => row.SubcontractorName), now);
            var contractsByLogicalKey = (await db.SubcontractorContracts.ToListAsync(cancellationToken))
                .Where(contract => !string.IsNullOrWhiteSpace(contract.ObjectNumber))
                .GroupBy(contract => LogicalContractKey(contract.ProjectCode, contract.ObjectNumber, contract.SubcontractorName))
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
            var objectValuesByRowKey = (await db.ProjectObjectValues.ToListAsync(cancellationToken))
                .ToDictionary(value => value.RowKey, StringComparer.Ordinal);

            foreach (var row in rows)
            {
                UpsertProjectSnapshot(
                    db,
                    projectsByCode,
                    row.ProjectCode,
                    row.ProjectName,
                    row.Responsible,
                    row.Engineer,
                    row.ProjectStatus,
                    batch.Id,
                    now,
                    result);

                contractsByLogicalKey.TryGetValue(row.LogicalKey, out var existingLogicalContracts);
                var matchingContracts = (existingLogicalContracts ?? [])
                    .OrderByDescending(existing => existing.UpdatedAt)
                    .ThenByDescending(existing => existing.CreatedAt)
                    .ToList();
                var existingContract = matchingContracts.FirstOrDefault(existing => existing.RowKey == row.RowKey)
                    ?? matchingContracts.FirstOrDefault()
                    ?? contractsByLogicalKey
                        .SelectMany(group => group.Value)
                        .FirstOrDefault(existing => existing.RowKey == row.RowKey);

                if (matchingContracts.Count > 1)
                {
                    result.Warnings.Add(
                        $"Multiple existing contract rows match {row.ProjectCode} / {row.SubcontractorName} / {row.ObjectName}. Updated the latest row and left older test data unchanged.");
                }

                if (existingContract is null)
                {
                    var newContract = new SubcontractorContract
                    {
                        ProjectCode = row.ProjectCode,
                        ProjectName = row.ProjectName,
                        ObjectNumber = row.ObjectNumber,
                        ObjectPrintCode = row.ObjectPrintCode,
                        DepartmentCode = row.DepartmentCode,
                        ObjectIndex = row.ObjectIndex,
                        SubcontractorName = row.SubcontractorName,
                        ObjectName = row.ObjectName,
                        ContractedAmount = row.ContractedAmount,
                        ProjectStatus = row.ProjectStatus,
                        IsActiveContractedProject = true,
                        IsInLatestContractedImport = true,
                        LastSeenContractImportBatchId = batch.Id,
                        LastSeenContractImportAt = now,
                        BecameInactiveAt = null,
                        SourceRowCount = row.SourceRowCount,
                        SourceRowsJson = row.SourceRowsJson,
                        Responsible = row.Responsible,
                        Engineer = row.Engineer,
                        SourceSystem = sourceSystem,
                        ExternalContractLineId = row.ExternalContractLineId,
                        RowKey = row.RowKey,
                        CreatedAt = now,
                        UpdatedAt = now,
                        DisplayFieldsUpdatedAt = HasDisplayFields(row.ProjectName, row.ObjectName, row.Responsible, row.Engineer)
                            ? now
                            : null
                    };
                    db.SubcontractorContracts.Add(newContract);
                    contractsByLogicalKey[row.LogicalKey] = [newContract];
                    result.ContractsInserted++;
                    continue;
                }

                existingContract.ProjectCode = row.ProjectCode;
                existingContract.ObjectNumber = row.ObjectNumber;
                existingContract.SubcontractorName = row.SubcontractorName;
                existingContract.ContractedAmount = row.ContractedAmount;
                existingContract.ProjectStatus = row.ProjectStatus ?? existingContract.ProjectStatus;
                existingContract.IsActiveContractedProject = true;
                existingContract.IsInLatestContractedImport = true;
                existingContract.LastSeenContractImportBatchId = batch.Id;
                existingContract.LastSeenContractImportAt = now;
                existingContract.BecameInactiveAt = null;
                if (!string.IsNullOrWhiteSpace(row.ObjectPrintCode)) existingContract.ObjectPrintCode = row.ObjectPrintCode;
                if (!string.IsNullOrWhiteSpace(row.DepartmentCode)) existingContract.DepartmentCode = row.DepartmentCode;
                if (!string.IsNullOrWhiteSpace(row.ObjectIndex)) existingContract.ObjectIndex = row.ObjectIndex;
                if (row.SourceRowCount is not null) existingContract.SourceRowCount = row.SourceRowCount;
                if (!string.IsNullOrWhiteSpace(row.SourceRowsJson)) existingContract.SourceRowsJson = row.SourceRowsJson;
                existingContract.SourceSystem = sourceSystem;
                existingContract.ExternalContractLineId = row.ExternalContractLineId;
                if (ApplyProvidedDisplayFields(existingContract, row.ProjectName, row.ObjectName, row.Responsible, row.Engineer))
                {
                    existingContract.DisplayFieldsUpdatedAt = now;
                }
                if (existingContract.RowKey == row.RowKey
                    || !contractsByLogicalKey
                        .SelectMany(group => group.Value)
                        .Any(existing => existing.Id != existingContract.Id && existing.RowKey == row.RowKey))
                {
                    existingContract.RowKey = row.RowKey;
                }
                contractsByLogicalKey[row.LogicalKey] = matchingContracts
                    .Where(existing => existing.Id != existingContract.Id)
                    .Append(existingContract)
                    .ToList();
                existingContract.UpdatedAt = now;
                result.ContractsUpdated++;
            }

            foreach (var row in projectValueRows)
            {
                UpsertProjectSnapshot(
                    db,
                    projectsByCode,
                    row.ProjectCode,
                    projectName: null,
                    responsible: null,
                    engineer: null,
                    projectStatus: null,
                    batch.Id,
                    now,
                    result);

                if (!objectValuesByRowKey.TryGetValue(row.RowKey, out var objectValue))
                {
                    objectValue = new ProjectObjectValue
                    {
                        ProjectCode = row.ProjectCode,
                        ObjectNumber = row.ObjectNumber,
                        ObjectPrintCode = row.ObjectPrintCode,
                        DepartmentCode = row.DepartmentCode,
                        ObjectIndex = row.ObjectIndex,
                        ProjectValueAmount = row.ProjectValueAmount,
                        SourceRowCount = row.SourceRowCount,
                        SourceRowsJson = row.SourceRowsJson,
                        LastSeenContractImportBatchId = batch.Id,
                        CreatedAt = now,
                        UpdatedAt = now,
                        RowKey = row.RowKey
                    };
                    db.ProjectObjectValues.Add(objectValue);
                    objectValuesByRowKey[row.RowKey] = objectValue;
                    result.ProjectValuesInserted++;
                    continue;
                }

                objectValue.ProjectCode = row.ProjectCode;
                objectValue.ObjectNumber = row.ObjectNumber;
                if (!string.IsNullOrWhiteSpace(row.ObjectPrintCode)) objectValue.ObjectPrintCode = row.ObjectPrintCode;
                if (!string.IsNullOrWhiteSpace(row.DepartmentCode)) objectValue.DepartmentCode = row.DepartmentCode;
                if (!string.IsNullOrWhiteSpace(row.ObjectIndex)) objectValue.ObjectIndex = row.ObjectIndex;
                objectValue.ProjectValueAmount = row.ProjectValueAmount;
                if (row.SourceRowCount is not null) objectValue.SourceRowCount = row.SourceRowCount;
                if (!string.IsNullOrWhiteSpace(row.SourceRowsJson)) objectValue.SourceRowsJson = row.SourceRowsJson;
                objectValue.LastSeenContractImportBatchId = batch.Id;
                objectValue.UpdatedAt = now;
                result.ProjectValuesUpdated++;
            }

            if (rows.Count > 0 || projectValueRows.Count > 0)
            {
                MarkProjectsMissingFromLatestContractImportInactive(db, incomingProjectCodes, now);
            }

            result.Success = result.Imported;
            result.Inserted = result.ContractsInserted + result.ProjectValuesInserted;
            result.Updated = result.ContractsUpdated + result.ProjectValuesUpdated;
            result.ImportedRows = result.Inserted + result.Updated;
            batch.RowsInserted = result.Inserted;
            batch.RowsUpdated = result.Updated;
            batch.WarningsCount = (request.Warnings?.Count ?? 0) + result.Warnings.Count;

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static ImportBatch RecordContractImportBatch(
        MoneyFlowDbContext db,
        ContractImportRequest request,
        string sourceSystem,
        ContractImportResponse result,
        DateTimeOffset now)
    {
        var submittedRowCounts = (request.ContractRowCount ?? request.Rows?.Count ?? 0)
            + (request.ProjectValueRowCount ?? request.ProjectValueRows?.Count ?? 0);
        var aggregatedRowCount = request.AggregatedRowCount ?? submittedRowCounts;
        var batch = new ImportBatch
        {
            SourceFileName = NullIfWhiteSpace(request.SourceSystem) ?? "contracted-import",
            // ContentHash is uniquely indexed; contract imports are intentionally
            // append-only audit rows, so make each one unique.
            ContentHash = $"contracts:{Guid.NewGuid():N}",
            SchemaVersion = NullIfWhiteSpace(request.SchemaVersion),
            SourceSystem = sourceSystem,
            Year = 0,
            Month = 0,
            SheetName = NullIfWhiteSpace(request.SourceSheet)
                ?? (request.SourceSheets is { Count: > 0 } ? string.Join(", ", request.SourceSheets) : null),
            ExportedAt = request.ExportedAt,
            ImportedAt = now,
            RawRowCount = request.RawRowCount ?? aggregatedRowCount,
            AggregatedRowCount = aggregatedRowCount,
            SkippedBlankObjectPrintCodeCount = request.SkippedBlankObjectPrintCodeCount ?? 0,
            SkippedInvalidRowCount = request.SkippedInvalidRowCount ?? 0,
            RowsReceived = aggregatedRowCount,
            RowsInserted = result.ContractsInserted,
            RowsUpdated = result.ContractsUpdated,
            RowsSkipped = 0,
            WarningsCount = (request.Warnings?.Count ?? 0) + result.Warnings.Count,
            Status = "ContractsImported"
        };

        db.ImportBatches.Add(batch);
        return batch;
    }

    private static void MarkProjectsMissingFromLatestContractImportInactive(
        MoneyFlowDbContext db,
        HashSet<string> incomingProjectCodes,
        DateTimeOffset now)
    {
        foreach (var project in db.Projects.Local)
        {
            if (!project.IsActiveContractedProject)
            {
                continue;
            }

            if (incomingProjectCodes.Contains(NormalizeKeyPart(project.ProjectCode)))
            {
                continue;
            }

            project.IsActiveContractedProject = false;
            project.IsInLatestContractedImport = false;
            project.BecameInactiveAt ??= now;
            project.UpdatedAt = now;
        }

        foreach (var contract in db.SubcontractorContracts.Local)
        {
            if (!contract.IsActiveContractedProject)
            {
                continue;
            }

            if (incomingProjectCodes.Contains(NormalizeKeyPart(contract.ProjectCode)))
            {
                continue;
            }

            contract.IsActiveContractedProject = false;
            contract.IsInLatestContractedImport = false;
            contract.BecameInactiveAt ??= now;
            contract.UpdatedAt = now;
        }
    }

    private static void UpsertProjectSnapshot(
        MoneyFlowDbContext db,
        Dictionary<string, Project> projectsByCode,
        string projectCode,
        string? projectName,
        string? responsible,
        string? engineer,
        string? projectStatus,
        Guid batchId,
        DateTimeOffset now,
        ContractImportResponse result)
    {
        var key = NormalizeKeyPart(projectCode);
        projectsByCode.TryGetValue(key, out var project);

        if (project is null)
        {
            project = new Project
            {
                ProjectCode = projectCode,
                ProjectName = projectName,
                Responsible = responsible,
                Engineer = engineer,
                ProjectStatus = projectStatus,
                IsActiveContractedProject = true,
                IsInLatestContractedImport = true,
                LastSeenContractImportBatchId = batchId,
                LastSeenContractImportAt = now,
                BecameInactiveAt = null,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Projects.Add(project);
            projectsByCode[key] = project;
            result.ProjectsCreated++;
            return;
        }

        project.ProjectName = projectName ?? project.ProjectName;
        project.Responsible = responsible ?? project.Responsible;
        project.Engineer = engineer ?? project.Engineer;
        project.ProjectStatus = projectStatus ?? project.ProjectStatus;
        project.IsActiveContractedProject = true;
        project.IsInLatestContractedImport = true;
        project.LastSeenContractImportBatchId = batchId;
        project.LastSeenContractImportAt = now;
        project.BecameInactiveAt = null;
        project.UpdatedAt = now;
    }

    public async Task<ProjectDetailSnapshot> GetProjectDetailAsync(
        string projectCode,
        CancellationToken cancellationToken)
    {
        return await GetProjectDetailAsync(projectCode, null, cancellationToken);
    }

    public async Task<ProjectDetailSnapshot> GetProjectDetailAsync(
        string projectCode,
        string? objectNumber,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await EnsureMasterDataTablesAsync(db, cancellationToken);

        var rows = await db.MonthlyFlowRows
            .AsNoTracking()
            .OrderBy(row => row.Year)
            .ThenBy(row => row.Month)
            .ThenBy(row => row.ProjectCode)
            .ThenBy(row => row.SourceSheet)
            .ThenBy(row => row.SourceRow)
            .ToListAsync(cancellationToken);

        var contracts = await db.SubcontractorContracts
            .AsNoTracking()
            .OrderBy(contract => contract.ProjectCode)
            .ThenBy(contract => contract.SubcontractorName)
            .ThenBy(contract => contract.ObjectName)
            .ToListAsync(cancellationToken);

        var objectValues = await db.ProjectObjectValues
            .AsNoTracking()
            .OrderBy(value => value.ProjectCode)
            .ThenBy(value => value.ObjectNumber)
            .ToListAsync(cancellationToken);
        var aliasMap = await GetSubcontractorAliasMapAsync(db, cancellationToken);
        var manualLinks = await db.ManualContractLinks
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var assignmentParent = NormalizeKeyPart(ParseProjectObjectCode(projectCode).ParentProjectCode);
        var objectAssignments = await db.ManualObjectAssignments
            .AsNoTracking()
            .Where(assignment => assignment.ProjectCode == assignmentParent)
            .ToListAsync(cancellationToken);

        // Rewrite mis-numbered invoice rows to their corrected object BEFORE the
        // object-scope filter, so the moved rows appear under (and match within)
        // the target object even in object-scoped views.
        ApplyObjectAssignments(rows, objectAssignments, aliasMap);

        rows = rows
            .Where(row => ProjectRowIsInScope(row.ProjectCode, row.ObjectNumber, projectCode, objectNumber))
            .ToList();
        contracts = contracts
            .Where(contract => ProjectRowIsInScope(contract.ProjectCode, contract.ObjectNumber, projectCode, objectNumber))
            .ToList();
        objectValues = objectValues
            .Where(value => ProjectRowIsInScope(value.ProjectCode, value.ObjectNumber, projectCode, objectNumber))
            .ToList();

        var project = await db.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(
                existing => existing.ProjectCode.ToLower() == (objectNumber ?? projectCode).ToLower(),
                cancellationToken);

        return BuildProjectDetail(projectCode, project, contracts, objectValues, rows, aliasMap, manualLinks, objectAssignments);
    }

    /// <summary>
    /// Rewrites (in memory) the object number of invoice rows that a user has
    /// corrected via <see cref="ManualObjectAssignment"/>, so downstream scoping,
    /// matching and display all use the corrected object. Rows are AsNoTracking,
    /// so this never persists to the imported data.
    /// </summary>
    private static void ApplyObjectAssignments(
        List<MonthlyMoneyFlowRow> rows,
        IReadOnlyCollection<ManualObjectAssignment> assignments,
        IReadOnlyDictionary<string, string> aliasMap)
    {
        if (assignments.Count == 0)
        {
            return;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assignment in assignments)
        {
            map[$"{assignment.ProjectCode}|{assignment.SourceObjectNumber}|{assignment.SubcontractorKey}"] = assignment.TargetObjectNumber;
        }

        foreach (var row in rows)
        {
            if (IsClientMonthlyValueRow(row))
            {
                continue;
            }

            var key = $"{NormalizeKeyPart(ParentProjectCodeFor(row.ProjectCode, row.ObjectNumber))}"
                + $"|{NormalizeKeyPart(EffectiveObjectNumber(row.ProjectCode, row.ObjectNumber))}"
                + $"|{SubcontractorMatchKey(row.SubcontractorName, aliasMap)}";
            if (map.TryGetValue(key, out var target) && !string.IsNullOrWhiteSpace(target))
            {
                row.ObjectNumber = target;
            }
        }
    }

    public async Task<IReadOnlyCollection<string>> GetProjectCodesAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var projectCodes = await db.MonthlyFlowRows
            .AsNoTracking()
            .Where(row => row.ProjectCode != "")
            .Select(row => row.ProjectCode)
            .ToListAsync(cancellationToken);

        return projectCodes
            .Where(projectCode => !string.IsNullOrWhiteSpace(projectCode))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(projectCode => projectCode)
            .ToList();
    }

    public async Task<IReadOnlyCollection<ProjectSummary>> GetProjectSummariesAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await EnsureMasterDataTablesAsync(db, cancellationToken);

        var rows = await db.MonthlyFlowRows
            .AsNoTracking()
            .Where(row => row.ProjectCode != "")
            .Select(row => new
            {
                row.ProjectCode,
                row.ObjectNumber,
                row.SubcontractorName,
                row.CustomerName,
                row.RowType,
                row.SourceSheet,
                row.AmountWithoutVat,
                row.ProjectName,
                row.ObjectName,
                row.Responsible,
                row.Engineer
            })
            .ToListAsync(cancellationToken);

        var projects = await db.Projects
            .AsNoTracking()
            .Select(project => new
            {
                project.ProjectCode,
                project.ProjectName,
                project.Responsible,
                project.Engineer,
                project.ProjectStatus,
                project.IsActiveContractedProject,
                project.IsInLatestContractedImport,
                project.LastSeenContractImportAt,
                project.BecameInactiveAt
            })
            .ToListAsync(cancellationToken);

        var contracts = await db.SubcontractorContracts
            .AsNoTracking()
            .Select(contract => new
            {
                contract.ProjectCode,
                contract.ObjectNumber,
                contract.ObjectPrintCode,
                contract.DepartmentCode,
                contract.SubcontractorName,
                contract.ContractedAmount,
                contract.ProjectName,
                contract.ObjectName,
                contract.Responsible,
                contract.Engineer,
                contract.ProjectStatus,
                contract.IsActiveContractedProject,
                contract.IsInLatestContractedImport,
                contract.LastSeenContractImportAt,
                contract.BecameInactiveAt
            })
            .ToListAsync(cancellationToken);

        var objectValues = await db.ProjectObjectValues
            .AsNoTracking()
            .Select(value => new
            {
                value.ProjectCode,
                value.ObjectNumber,
                value.ObjectPrintCode,
                value.DepartmentCode,
                value.ObjectIndex,
                value.ProjectValueAmount
            })
            .ToListAsync(cancellationToken);

        var subcontractorInvoiceRows = rows
            .Where(row => !IsClientMonthlyValueProjection(row.RowType, row.SourceSheet))
            .ToList();

        var invoiceSummaries = subcontractorInvoiceRows
            .Where(row => !string.IsNullOrWhiteSpace(row.ProjectCode))
            .GroupBy(row => ParentProjectCodeFor(row.ProjectCode, row.ObjectNumber), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new
                {
                    AmountWithoutVat = group.Sum(row => row.AmountWithoutVat),
                    RowCount = group.Count(),
                    ObjectCount = group.Select(row => EffectiveObjectNumber(row.ProjectCode, row.ObjectNumber))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count(),
                    SubcontractorCount = group.Select(row => string.IsNullOrWhiteSpace(row.SubcontractorName)
                        ? "(Be subrangovo)"
                        : row.SubcontractorName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                    // Monthly objectName is the fallback project label when no
                    // contracted projectName exists.
                    ObjectName = group.Select(row => row.ObjectName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                    // Responsible/engineer come from monthly data (Fix 2).
                    Responsible = group.Select(row => row.Responsible).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
                    Engineer = group.Select(row => row.Engineer).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
                },
                StringComparer.OrdinalIgnoreCase);

        var contractSummaries = contracts
            .Where(contract => !string.IsNullOrWhiteSpace(contract.ProjectCode))
            .GroupBy(contract => ParentProjectCodeFor(contract.ProjectCode, contract.ObjectNumber), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new
                {
                    ContractedAmount = group.Sum(contract => contract.ContractedAmount),
                    ObjectCount = group.Select(contract => EffectiveObjectNumber(contract.ProjectCode, contract.ObjectNumber))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count(),
                    SubcontractorCount = group.Select(contract => contract.SubcontractorName)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count(),
                    // Contracted projectName is the preferred project label (Fix 1).
                    ProjectName = group.Select(contract => contract.ProjectName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                    Responsible = group.Select(contract => contract.Responsible).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
                    Engineer = group.Select(contract => contract.Engineer).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
                    ProjectStatus = group
                        .Where(contract => contract.IsInLatestContractedImport)
                        .Select(contract => contract.ProjectStatus)
                        .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
                        ?? group.Select(contract => contract.ProjectStatus).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
                    IsActiveContractedProject = group.Any(contract => contract.IsActiveContractedProject),
                    IsInLatestContractedImport = group.Any(contract => contract.IsInLatestContractedImport),
                    LastSeenContractImportAt = group
                        .Select(contract => contract.LastSeenContractImportAt)
                        .Where(value => value.HasValue)
                        .DefaultIfEmpty()
                        .Max(),
                    BecameInactiveAt = group
                        .Select(contract => contract.BecameInactiveAt)
                        .Where(value => value.HasValue)
                        .DefaultIfEmpty()
                        .Max()
                },
                StringComparer.OrdinalIgnoreCase);

        var projectRecordSummaries = projects
            .Where(project => !string.IsNullOrWhiteSpace(project.ProjectCode))
            .GroupBy(project => ParentProjectCodeFor(project.ProjectCode, null), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new
                {
                    ProjectName = group.Select(project => project.ProjectName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                    Responsible = group.Select(project => project.Responsible).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
                    Engineer = group.Select(project => project.Engineer).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
                    ProjectStatus = group
                        .Where(project => project.IsInLatestContractedImport)
                        .Select(project => project.ProjectStatus)
                        .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
                        ?? group.Select(project => project.ProjectStatus).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
                    IsActiveContractedProject = group.Any(project => project.IsActiveContractedProject),
                    IsInLatestContractedImport = group.Any(project => project.IsInLatestContractedImport),
                    LastSeenContractImportAt = group
                        .Select(project => project.LastSeenContractImportAt)
                        .Where(value => value.HasValue)
                        .DefaultIfEmpty()
                        .Max(),
                    BecameInactiveAt = group
                        .Select(project => project.BecameInactiveAt)
                        .Where(value => value.HasValue)
                        .DefaultIfEmpty()
                        .Max()
                },
                StringComparer.OrdinalIgnoreCase);

        var projectValueSummaries = objectValues
            .Where(value => !string.IsNullOrWhiteSpace(value.ProjectCode))
            .GroupBy(value => ParentProjectCodeFor(value.ProjectCode, value.ObjectNumber), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new
                {
                    ProjectValueAmount = group.Sum(value => value.ProjectValueAmount),
                    ObjectCount = group.Select(value => EffectiveObjectNumber(value.ProjectCode, value.ObjectNumber))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count()
                },
                StringComparer.OrdinalIgnoreCase);

        // Fallback display name for projects that carry only monthly data (e.g.
        // client-value-only projects with no contract): prefer the monthly
        // projectName, else the descriptive objectName. Built from ALL rows,
        // including client-value (SMD) rows that the subcontractor invoice
        // summary excludes, so these projects still show a name in the list.
        var monthlyNamesByProject = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.ProjectCode))
            .GroupBy(row => ParentProjectCodeFor(row.ProjectCode, row.ObjectNumber), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(r => r.ProjectName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                    ?? group.Select(r => r.ObjectName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                StringComparer.OrdinalIgnoreCase);

        var objectSummaries = rows
            .Select(row => EffectiveObjectNumber(row.ProjectCode, row.ObjectNumber))
            .Concat(contracts.Select(contract => EffectiveObjectNumber(contract.ProjectCode, contract.ObjectNumber)))
            .Concat(objectValues.Select(value => EffectiveObjectNumber(value.ProjectCode, value.ObjectNumber)))
            .Where(projectCode => !string.IsNullOrWhiteSpace(projectCode))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .GroupBy(projectCode => ParseProjectObjectCode(projectCode).ParentProjectCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(projectObjectCode =>
                    {
                        var parsed = ParseProjectObjectCode(projectObjectCode);
                        var objectRows = rows
                            .Where(row => string.Equals(EffectiveObjectNumber(row.ProjectCode, row.ObjectNumber), projectObjectCode, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        var objectSubcontractorRows = objectRows
                            .Where(row => !IsClientMonthlyValueProjection(row.RowType, row.SourceSheet))
                            .ToList();
                        var objectContracts = contracts
                            .Where(contract => string.Equals(EffectiveObjectNumber(contract.ProjectCode, contract.ObjectNumber), projectObjectCode, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        var objectProjectValues = objectValues
                            .Where(value => string.Equals(EffectiveObjectNumber(value.ProjectCode, value.ObjectNumber), projectObjectCode, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        var subcontractorInvoiced = objectSubcontractorRows.Sum(row => row.AmountWithoutVat);
                        var contracted = objectContracts.Sum(contract => contract.ContractedAmount);
                        var subcontractors = objectSubcontractorRows
                            .Select(row => string.IsNullOrWhiteSpace(row.SubcontractorName) ? "(Be subrangovo)" : row.SubcontractorName)
                            .Concat(objectContracts.Select(contract => contract.SubcontractorName))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count();
                        var status = contracted <= 0 && objectSubcontractorRows.Count == 0
                            ? "Pagal planą"
                            : StatusFor(contracted, contracted > 0 ? subcontractorInvoiced / contracted * 100 : 0);
                        var warningCount = objectSubcontractorRows.Count(row => string.IsNullOrWhiteSpace(row.ObjectNumber))
                            + (status == "Pagal planą" ? 0 : 1);

                        // Display fields: prefer contract source, fall back to monthly rows.
                        var objectName = objectContracts
                            .Select(c => c.ObjectName)
                            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                            ?? objectRows
                            .Select(r => r.ObjectName)
                            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

                        // Print code / department come only from contracted data.
                        var objectPrintCode = objectContracts
                            .Select(c => c.ObjectPrintCode)
                            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))
                            ?? objectProjectValues
                            .Select(v => v.ObjectPrintCode)
                            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
                        var departmentCode = objectContracts
                            .Select(c => c.DepartmentCode)
                            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))
                            ?? objectProjectValues
                            .Select(v => v.DepartmentCode)
                            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

                        var responsibles = objectRows.Select(r => r.Responsible)
                            .Concat(objectContracts.Select(c => c.Responsible))
                            .Where(v => !string.IsNullOrWhiteSpace(v))
                            .Select(v => v!)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(v => v)
                            .ToList();

                        var engineers = objectRows.Select(r => r.Engineer)
                            .Concat(objectContracts.Select(c => c.Engineer))
                            .Where(v => !string.IsNullOrWhiteSpace(v))
                            .Select(v => v!)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(v => v)
                            .ToList();

                        return new ProjectObjectSummary(
                            parsed.ObjectNumber,
                            parsed.ObjectCode,
                            contracted,
                            subcontractorInvoiced,
                            contracted - subcontractorInvoiced,
                            subcontractors,
                            warningCount,
                            status,
                            objectName,
                            responsibles,
                            engineers,
                            objectPrintCode,
                            departmentCode);
                    })
                    .OrderBy(summary => summary.ObjectNumber)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

        return invoiceSummaries.Keys
            .Concat(contractSummaries.Keys)
            .Concat(projectRecordSummaries.Keys)
            .Concat(projectValueSummaries.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(projectCode =>
            {
                invoiceSummaries.TryGetValue(projectCode, out var invoice);
                contractSummaries.TryGetValue(projectCode, out var contract);
                projectRecordSummaries.TryGetValue(projectCode, out var projectRecord);
                projectValueSummaries.TryGetValue(projectCode, out var projectValue);
                var invoiced = invoice?.AmountWithoutVat ?? 0;
                var contracted = contract?.ContractedAmount ?? 0;

                // Project name: prefer contracted projectName, fall back to the
                // monthly objectName, else null → frontend renders "—" (Fix 1/7).
                monthlyNamesByProject.TryGetValue(projectCode, out var monthlyName);
                var projectName = !string.IsNullOrWhiteSpace(contract?.ProjectName)
                    ? contract!.ProjectName
                    : (!string.IsNullOrWhiteSpace(projectRecord?.ProjectName) ? projectRecord!.ProjectName
                    : (!string.IsNullOrWhiteSpace(invoice?.ObjectName) ? invoice!.ObjectName
                    : (!string.IsNullOrWhiteSpace(monthlyName) ? monthlyName : null)));

                // Responsible/engineer: prefer monthly data, fall back to contracted (Fix 2).
                var responsible = !string.IsNullOrWhiteSpace(invoice?.Responsible)
                    ? invoice!.Responsible
                    : (!string.IsNullOrWhiteSpace(contract?.Responsible) ? contract!.Responsible
                    : (!string.IsNullOrWhiteSpace(projectRecord?.Responsible) ? projectRecord!.Responsible : null));
                var engineer = !string.IsNullOrWhiteSpace(invoice?.Engineer)
                    ? invoice!.Engineer
                    : (!string.IsNullOrWhiteSpace(contract?.Engineer) ? contract!.Engineer
                    : (!string.IsNullOrWhiteSpace(projectRecord?.Engineer) ? projectRecord!.Engineer : null));

                return new ProjectSummary(
                    projectCode,
                    Math.Max(Math.Max(invoice?.ObjectCount ?? 0, contract?.ObjectCount ?? 0), projectValue?.ObjectCount ?? 0),
                    invoiced,
                    invoice?.RowCount ?? 0,
                    Math.Max(invoice?.SubcontractorCount ?? 0, contract?.SubcontractorCount ?? 0),
                    contracted,
                    contracted - invoiced,
                    objectSummaries.TryGetValue(projectCode, out var objects)
                        ? objects.Sum(summary => summary.WarningsCount)
                        : 0,
                    // No subcontractor contract: "Missing contract" only when there
                    // are actually invoices without a contract; no contract AND no
                    // invoices is on track (e.g. client-value-only projects). Mirrors
                    // the frontend deriveStatus so the list matches the detail page.
                    contracted <= 0
                        ? (invoiced > 0 ? "Trūksta sutarties" : "Pagal planą")
                        : StatusFor(contracted, invoiced / contracted * 100),
                    projectName,
                    responsible,
                    engineer,
                    contract?.ProjectStatus ?? projectRecord?.ProjectStatus,
                    (contract?.IsActiveContractedProject ?? false) || (projectRecord?.IsActiveContractedProject ?? false),
                    (contract?.IsInLatestContractedImport ?? false) || (projectRecord?.IsInLatestContractedImport ?? false),
                    contract?.LastSeenContractImportAt ?? projectRecord?.LastSeenContractImportAt,
                    contract?.BecameInactiveAt ?? projectRecord?.BecameInactiveAt,
                    objectSummaries.TryGetValue(projectCode, out objects) ? objects : [],
                    projectValue?.ProjectValueAmount ?? 0);
            })
            .OrderBy(summary => summary.ProjectCode)
            .ToList();
    }

    public static string CreateRowKey(
        int year,
        int month,
        string? sourceSheet,
        int sourceRow,
        string projectCode,
        string? objectNumber,
        string? subcontractorName)
    {
        var sheetKey = NormalizeKeyPart(sourceSheet);
        if (!string.IsNullOrWhiteSpace(objectNumber))
        {
            var subcontractorKey = string.IsNullOrWhiteSpace(subcontractorName)
                ? "(Be subrangovo)"
                : subcontractorName;
            return $"{year:D4}-{month:D2}:{sheetKey}:{LogicalContractKey(projectCode, objectNumber, subcontractorKey)}";
        }

        return string.IsNullOrWhiteSpace(sheetKey)
            ? $"{year:D4}-{month:D2}:{sourceRow:D8}"
            : $"{year:D4}-{month:D2}:{sheetKey}:{sourceRow:D8}";
    }

    public static string CreateContractRowKey(
        string projectCode,
        string objectNumber,
        string subcontractorName,
        string? externalContractLineId)
    {
        return LogicalContractKey(projectCode, objectNumber, subcontractorName);
    }

    public static string CreateProjectObjectValueRowKey(string projectCode, string objectNumber)
    {
        return $"{NormalizeKeyPart(projectCode)}|{NormalizeKeyPart(objectNumber)}";
    }

    public static bool IsSmdSheet(string? sourceSheet)
    {
        return string.Equals(CleanText(sourceSheet), "SMD", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsClientMonthlyValueRow(MonthlyMoneyFlowRow row)
    {
        return IsClientMonthlyValueProjection(row.RowType, row.SourceSheet);
    }

    public static bool IsClientMonthlyValueProjection(string? rowType, string? sourceSheet)
    {
        return string.Equals(rowType, ClientMonthlyValueRowType, StringComparison.OrdinalIgnoreCase)
            || IsSmdSheet(sourceSheet);
    }

    public static string RowTypeFor(string? rowType, string? sourceSheet)
    {
        if (IsClientMonthlyValueProjection(rowType, sourceSheet))
        {
            return ClientMonthlyValueRowType;
        }

        return SubcontractorInvoiceRowType;
    }

    public static string ProjectCodeForSmdRow(string projectCode, string? objectNumber)
    {
        var parsedObject = ParseProjectObjectCode(objectNumber);
        if (!string.IsNullOrWhiteSpace(parsedObject.ParentProjectCode)
            && !string.Equals(parsedObject.ParentProjectCode, parsedObject.ObjectNumber, StringComparison.OrdinalIgnoreCase))
        {
            return parsedObject.ParentProjectCode;
        }

        var parsedProject = ParseProjectObjectCode(projectCode);
        return string.IsNullOrWhiteSpace(parsedProject.ParentProjectCode)
            ? projectCode
            : parsedProject.ParentProjectCode;
    }

    public static string? ResolveSmdClientName(string? explicitClientName, string? projectCode, string? legacySubcontractorName)
    {
        var explicitName = CleanCustomerDisplayName(explicitClientName);
        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            return explicitName;
        }

        var legacyName = CleanCustomerDisplayName(legacySubcontractorName);
        if (!string.IsNullOrWhiteSpace(legacyName) && !LooksLikeDepartmentCode(legacyName))
        {
            return legacyName;
        }

        var projectCodeName = CleanCustomerDisplayName(projectCode);
        if (!string.IsNullOrWhiteSpace(projectCodeName) && !LooksLikeProjectOrObjectCode(projectCodeName))
        {
            return projectCodeName;
        }

        return null;
    }

    private static ProjectDetailSnapshot BuildProjectDetail(
        string projectCode,
        Project? project,
        IReadOnlyCollection<SubcontractorContract> contracts,
        IReadOnlyCollection<ProjectObjectValue> objectValues,
        IReadOnlyCollection<MonthlyMoneyFlowRow> rows,
        IReadOnlyDictionary<string, string> subcontractorAliasMap,
        IReadOnlyCollection<ManualContractLink> manualLinks,
        IReadOnlyCollection<ManualObjectAssignment> objectAssignments)
    {
        // Manual links rewrite the invoice-side subcontractor key (never the
        // contract side) within one project+object scope, so a wrongly entered
        // monthly name lands on the contracted identity.
        var manualLinkMap = manualLinks
            .GroupBy(
                link => $"{NormalizeKeyPart(link.ProjectCode)}|{NormalizeKeyPart(link.ObjectNumber)}|{link.SourceSubcontractorKey}",
                StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().TargetSubcontractorKey, StringComparer.Ordinal);

        string InvoiceSubcontractorKey(string normalizedProjectCode, string normalizedObjectNumber, string? subcontractorName)
        {
            var key = SubcontractorMatchKey(subcontractorName, subcontractorAliasMap);
            return manualLinkMap.TryGetValue($"{normalizedProjectCode}|{normalizedObjectNumber}|{key}", out var targetKey)
                ? targetKey
                : key;
        }

        var monthKeys = rows
            .Select(row => new YearMonth(row.Year, row.Month))
            .Distinct()
            .OrderBy(month => month.Year)
            .ThenBy(month => month.Month)
            .ToList();

        var smdRows = rows
            .Where(IsClientMonthlyValueRow)
            .ToList();
        var subcontractorRows = rows
            .Where(row => !IsClientMonthlyValueRow(row))
            .ToList();

        var invoiceGroups = subcontractorRows
            .Where(row => !string.IsNullOrWhiteSpace(row.ObjectNumber))
            .GroupBy(row =>
            {
                var normalizedProjectCode = NormalizeKeyPart(ParentProjectCodeFor(row.ProjectCode, row.ObjectNumber));
                var normalizedObjectNumber = NormalizeKeyPart(row.ObjectNumber);
                return new InvoiceMatchKey(
                    normalizedProjectCode,
                    normalizedObjectNumber,
                    InvoiceSubcontractorKey(normalizedProjectCode, normalizedObjectNumber, row.SubcontractorName ?? "(Be subrangovo)"),
                    HasObjectNumber: true);
            })
            .ToDictionary(group => group.Key, group => group.ToList());

        var detailRows = new List<ProjectContractDetailRow>();
        var matchedInvoiceKeys = new HashSet<InvoiceMatchKey>();

        foreach (var contractGroup in contracts.GroupBy(contract => new InvoiceMatchKey(
            NormalizeKeyPart(ParentProjectCodeFor(contract.ProjectCode, contract.ObjectNumber)),
            NormalizeKeyPart(contract.ObjectNumber),
            SubcontractorMatchKey(contract.SubcontractorName, subcontractorAliasMap),
            HasObjectNumber: !string.IsNullOrWhiteSpace(contract.ObjectNumber))))
        {
            var contract = contractGroup
                .OrderByDescending(item => item.UpdatedAt)
                .ThenByDescending(item => item.CreatedAt)
                .First();
            var key = new InvoiceMatchKey(
                NormalizeKeyPart(ParentProjectCodeFor(contract.ProjectCode, contract.ObjectNumber)),
                NormalizeKeyPart(contract.ObjectNumber),
                SubcontractorMatchKey(contract.SubcontractorName, subcontractorAliasMap),
                HasObjectNumber: !string.IsNullOrWhiteSpace(contract.ObjectNumber));
            invoiceGroups.TryGetValue(key, out var matchingRows);
            matchedInvoiceKeys.Add(key);
            var rowLinks = manualLinks
                .Where(link => string.Equals(NormalizeKeyPart(link.ProjectCode), key.ProjectCode, StringComparison.Ordinal)
                    && string.Equals(NormalizeKeyPart(link.ObjectNumber), key.ObjectNumber, StringComparison.Ordinal)
                    && string.Equals(link.TargetSubcontractorKey, key.SubcontractorName, StringComparison.Ordinal))
                .OrderBy(link => link.SourceSubcontractorName, StringComparer.OrdinalIgnoreCase)
                .Select(link => new ManualContractLinkInfo(link.Id, link.SourceSubcontractorName, link.TargetSubcontractorName))
                .ToList();
            detailRows.Add(CreateDetailRow(
                EffectiveObjectNumber(contract.ProjectCode, contract.ObjectNumber),
                contract.ObjectNumber,
                contract.ObjectPrintCode,
                contract.DepartmentCode,
                CanonicalSubcontractorName(contract.SubcontractorName),
                contract.ObjectName,
                contract.ContractedAmount,
                matchingRows ?? [],
                monthKeys,
                false,
                contract.RowKey,
                null,
                rowLinks));
        }

        var unmatchedInvoiceGroups = invoiceGroups
            .Where(group => !matchedInvoiceKeys.Contains(group.Key))
            .Concat(subcontractorRows
                .Where(row => string.IsNullOrWhiteSpace(row.ObjectNumber))
                .GroupBy(row => new InvoiceMatchKey(
                    NormalizeKeyPart(ParentProjectCodeFor(row.ProjectCode, row.ObjectNumber)),
                    "",
                    SubcontractorMatchKey(row.SubcontractorName ?? "(Be subrangovo)", subcontractorAliasMap),
                    HasObjectNumber: false))
                .Select(group => new KeyValuePair<InvoiceMatchKey, List<MonthlyMoneyFlowRow>>(group.Key, group.ToList())));

        foreach (var group in unmatchedInvoiceGroups)
        {
            var first = group.Value.First();
            detailRows.Add(CreateDetailRow(
                EffectiveObjectNumber(first.ProjectCode, first.ObjectNumber),
                first.ObjectNumber,
                null,
                null,
                string.IsNullOrWhiteSpace(first.SubcontractorName) ? "(Be subrangovo)" : CanonicalSubcontractorName(first.SubcontractorName),
                first.ObjectName ?? "",
                0,
                group.Value,
                monthKeys,
                true,
                null,
                string.IsNullOrWhiteSpace(first.ObjectNumber) ? "Trūksta objekto numerio" : null,
                []));
        }

        var objectAssignmentInfos = objectAssignments
            .Select(assignment => new ObjectAssignmentInfo(
                assignment.Id,
                assignment.SourceObjectNumber,
                assignment.TargetObjectNumber,
                assignment.SubcontractorName))
            .ToList();

        // Same fallback as the project list: client-value-only projects have no
        // Project record name, so fall back to a contract name, then the monthly
        // projectName, then the descriptive objectName from any row.
        var detailProjectName = project?.ProjectName;
        if (string.IsNullOrWhiteSpace(detailProjectName))
        {
            detailProjectName = contracts.Select(c => c.ProjectName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                ?? rows.Select(r => r.ProjectName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                ?? rows.Select(r => r.ObjectName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        }

        return new ProjectDetailSnapshot(
            projectCode,
            detailProjectName,
            project?.Responsible,
            project?.Engineer,
            contracts.Count,
            objectValues
                .Select(value => new ProjectObjectValueDetailRow(
                    value.ProjectCode,
                    value.ObjectNumber,
                    value.ObjectPrintCode,
                    value.DepartmentCode,
                    value.ObjectIndex,
                    value.ProjectValueAmount))
                .OrderBy(value => value.ObjectNumber)
                .ToList(),
            BuildSmdCustomerRows(smdRows, monthKeys),
            detailRows
                .OrderBy(row => row.IsImportedOnly)
                .ThenBy(row => row.ProjectObjectNumber)
                .ThenBy(row => row.SubcontractorName)
                .ThenBy(row => row.ObjectName)
                .ToList(),
            objectAssignmentInfos);
    }

    private static ProjectContractDetailRow CreateDetailRow(
        string projectObjectNumber,
        string? objectNumber,
        string? objectPrintCode,
        string? departmentCode,
        string subcontractorName,
        string? objectName,
        decimal contracted,
        IReadOnlyCollection<MonthlyMoneyFlowRow> rows,
        IReadOnlyCollection<YearMonth> monthKeys,
        bool isImportedOnly,
        string? rowKey,
        string? warning,
        IReadOnlyCollection<ManualContractLinkInfo> links)
    {
        var monthly = monthKeys
            .Select(month => new MonthlyAmount(
                month.Year,
                month.Month,
                rows
                    .Where(row => row.Year == month.Year && row.Month == month.Month)
                    .Sum(row => row.AmountWithoutVat)))
            .ToList();

        var invoiced = rows.Sum(row => row.AmountWithoutVat);
        var usage = contracted > 0 ? invoiced / contracted * 100 : 0;
        return new ProjectContractDetailRow(
            projectObjectNumber,
            objectNumber,
            objectPrintCode,
            departmentCode,
            subcontractorName,
            objectName,
            contracted,
            invoiced,
            contracted - invoiced,
            usage,
            StatusFor(contracted, usage),
            monthly,
            isImportedOnly,
            rowKey,
            warning,
            links);
    }

    private static IReadOnlyCollection<SmdCustomerInvoiceRow> BuildSmdCustomerRows(
        IReadOnlyCollection<MonthlyMoneyFlowRow> rows,
        IReadOnlyCollection<YearMonth> monthKeys)
    {
        var monthlyRows = rows
            .GroupBy(row => new
            {
                ObjectNumber = EffectiveObjectNumber(row.ProjectCode, row.ObjectNumber),
                CustomerKey = NormalizeClientName(ResolveSmdClientName(row.CustomerName, row.ProjectCode, row.SubcontractorName)),
                row.Year,
                row.Month
            })
            .Select(group =>
            {
                var first = group.First();
                var customerNames = group
                    .Select(row => ResolveSmdClientName(row.CustomerName, row.ProjectCode, row.SubcontractorName))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .ToList();
                return new
                {
                    group.Key.ObjectNumber,
                    CustomerName = CanonicalClientDisplayName(customerNames),
                    group.Key.Year,
                    group.Key.Month,
                    ObjectName = first.ObjectName,
                    ClientMonthlyAmount = group.Sum(row => row.AmountWithoutVat),
                    SourceSheet = group.Select(row => row.SourceSheet).FirstOrDefault(sheet => !string.IsNullOrWhiteSpace(sheet)) ?? "SMD",
                    SourceRows = group.Select(row => row.SourceRow).Distinct().OrderBy(row => row).ToList()
                };
            })
            .OrderBy(row => row.ObjectNumber)
            .ThenBy(row => row.CustomerName)
            .ThenBy(row => row.Year)
            .ThenBy(row => row.Month)
            .ToList();

        var totals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SmdCustomerInvoiceRow>();
        foreach (var row in monthlyRows)
        {
            var totalKey = $"{NormalizeKeyPart(row.ObjectNumber)}|{NormalizeClientName(row.CustomerName)}";
            totals.TryGetValue(totalKey, out var previousTotal);
            var totalYtd = previousTotal + row.ClientMonthlyAmount;
            totals[totalKey] = totalYtd;
            result.Add(new SmdCustomerInvoiceRow(
                row.Year,
                row.Month,
                row.ObjectNumber,
                row.CustomerName,
                row.ObjectName,
                row.ClientMonthlyAmount,
                totalYtd,
                row.SourceSheet,
                row.SourceRows));
        }

        return result;
    }

    private static string StatusFor(decimal contracted, decimal usage)
    {
        if (contracted <= 0)
        {
            return "Trūksta sutarties";
        }

        // Match the frontend deriveStatus: ≤100% used is on track, only going
        // over the contracted amount is a problem. No "near limit"/"at limit"
        // gradations, so the list and the detail page agree.
        return usage > 100 ? "Viršyta riba" : "Pagal planą";
    }

    private static List<ValidatedContractRow> ValidateContractRows(
        List<ContractImportRowRequest>? rows,
        List<string> warnings)
    {
        var validatedRows = new List<ValidatedContractRow>();
        if (rows is null)
        {
            return validatedRows;
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var rowName = $"rows[{index}]";
            var projectCode = CleanText(row.ProjectCode);
            var objectNumber = CleanText(row.ObjectNumber);
            var subcontractorName = CleanText(row.SubcontractorName);
            var objectName = CleanText(row.ObjectName);
            var projectStatus = CleanText(row.ProjectStatus);

            if (projectCode is null)
            {
                warnings.Add($"{rowName}.projectCode is required. Row was skipped.");
                continue;
            }

            if (objectNumber is null)
            {
                warnings.Add($"{rowName}.objectNumber is required. Row was skipped.");
                continue;
            }

            if (subcontractorName is null)
            {
                warnings.Add($"{rowName}.subcontractorName is required. Row was skipped.");
                continue;
            }

            if (row.ContractedAmount is null)
            {
                warnings.Add($"{rowName}.contractedAmount is required. Row was skipped.");
                continue;
            }

            var externalContractLineId = NullIfWhiteSpace(row.ExternalContractLineId);
            var logicalKey = LogicalContractKey(projectCode, objectNumber, subcontractorName);
            var sourceRowsJson = row.SourceRows is { Count: > 0 }
                ? System.Text.Json.JsonSerializer.Serialize(row.SourceRows)
                : null;
            validatedRows.Add(new ValidatedContractRow(
                projectCode,
                CleanText(row.ProjectName),
                objectNumber,
                CleanText(row.ObjectPrintCode),
                CleanText(row.DepartmentCode),
                CleanText(row.ObjectIndex),
                subcontractorName,
                objectName,
                row.ContractedAmount.Value,
                projectStatus,
                row.SourceRowCount,
                sourceRowsJson,
                CleanText(row.Responsible),
                CleanText(row.Engineer),
                externalContractLineId,
                logicalKey,
                CreateContractRowKey(projectCode, objectNumber, subcontractorName, externalContractLineId)));
        }

        return validatedRows;
    }

    private static List<ValidatedProjectValueRow> ValidateProjectValueRows(
        List<ProjectValueRowRequest>? rows,
        List<string> warnings)
    {
        var validatedRows = new List<ValidatedProjectValueRow>();
        if (rows is null)
        {
            return validatedRows;
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var rowName = $"projectValueRows[{index}]";
            var projectCode = CleanText(row.ProjectCode);
            var objectNumber = CleanText(row.ObjectNumber);

            if (projectCode is null)
            {
                warnings.Add($"{rowName}.projectCode is required. Row was skipped.");
                continue;
            }

            if (objectNumber is null)
            {
                warnings.Add($"{rowName}.objectNumber is required. Row was skipped.");
                continue;
            }

            if (row.ProjectValueAmount is null)
            {
                warnings.Add($"{rowName}.projectValueAmount is required. Row was skipped.");
                continue;
            }

            var sourceRowsJson = row.SourceRows is { Count: > 0 }
                ? System.Text.Json.JsonSerializer.Serialize(row.SourceRows)
                : null;
            validatedRows.Add(new ValidatedProjectValueRow(
                projectCode,
                objectNumber,
                CleanText(row.ObjectPrintCode),
                CleanText(row.DepartmentCode),
                CleanText(row.ObjectIndex),
                row.ProjectValueAmount.Value,
                row.SourceRowCount,
                sourceRowsJson,
                CreateProjectObjectValueRowKey(projectCode, objectNumber)));
        }

        return validatedRows;
    }

    private static async Task EnsureMasterDataTablesAsync(
        MoneyFlowDbContext db,
        CancellationToken cancellationToken)
    {
        await EnsureImportTablesAsync(db, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS Projects (
                Id TEXT NOT NULL CONSTRAINT PK_Projects PRIMARY KEY,
                ProjectCode TEXT NOT NULL,
                ProjectName TEXT NULL,
                Responsible TEXT NULL,
                Engineer TEXT NULL,
                ProjectStatus TEXT NULL,
                IsActiveContractedProject INTEGER NOT NULL DEFAULT 0,
                IsInLatestContractedImport INTEGER NOT NULL DEFAULT 0,
                LastSeenContractImportBatchId TEXT NULL,
                LastSeenContractImportAt TEXT NULL,
                BecameInactiveAt TEXT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                DisplayFieldsUpdatedAt TEXT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Projects_ProjectCode
            ON Projects (ProjectCode);
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS SubcontractorContracts (
                Id TEXT NOT NULL CONSTRAINT PK_SubcontractorContracts PRIMARY KEY,
                ProjectCode TEXT NOT NULL,
                ProjectName TEXT NULL,
                ObjectNumber TEXT NOT NULL DEFAULT '',
                ObjectPrintCode TEXT NULL,
                DepartmentCode TEXT NULL,
                ObjectIndex TEXT NULL,
                SubcontractorName TEXT NOT NULL,
                ObjectName TEXT NULL,
                ContractedAmount decimal(18,2) NOT NULL,
                ProjectStatus TEXT NULL,
                IsActiveContractedProject INTEGER NOT NULL DEFAULT 0,
                IsInLatestContractedImport INTEGER NOT NULL DEFAULT 0,
                LastSeenContractImportBatchId TEXT NULL,
                LastSeenContractImportAt TEXT NULL,
                BecameInactiveAt TEXT NULL,
                SourceRowCount INTEGER NULL,
                SourceRowsJson TEXT NULL,
                Responsible TEXT NULL,
                Engineer TEXT NULL,
                SourceSystem TEXT NOT NULL,
                ExternalContractLineId TEXT NULL,
                RowKey TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                DisplayFieldsUpdatedAt TEXT NULL
            );
            """, cancellationToken);

        await AddColumnIfMissingAsync(
            db,
            "SubcontractorContracts",
            "ObjectNumber",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);

        await AddColumnIfMissingAsync(
            db,
            "SubcontractorContracts",
            "DisplayFieldsUpdatedAt",
            "TEXT NULL",
            cancellationToken);

        await AddColumnIfMissingAsync(
            db,
            "SubcontractorContracts",
            "ObjectPrintCode",
            "TEXT NULL",
            cancellationToken);

        await AddColumnIfMissingAsync(
            db,
            "SubcontractorContracts",
            "DepartmentCode",
            "TEXT NULL",
            cancellationToken);

        await AddColumnIfMissingAsync(
            db,
            "SubcontractorContracts",
            "ObjectIndex",
            "TEXT NULL",
            cancellationToken);

        await AddColumnIfMissingAsync(
            db,
            "SubcontractorContracts",
            "SourceRowCount",
            "INTEGER NULL",
            cancellationToken);

        await AddColumnIfMissingAsync(
            db,
            "SubcontractorContracts",
            "SourceRowsJson",
            "TEXT NULL",
            cancellationToken);

        await AddContractSnapshotColumnsAsync(db, "Projects", cancellationToken);
        await AddContractSnapshotColumnsAsync(db, "SubcontractorContracts", cancellationToken);
        await BootstrapExistingContractSnapshotStateAsync(db, cancellationToken);

        await AddColumnIfMissingAsync(
            db,
            "ImportBatches",
            "SourceSystem",
            "TEXT NULL",
            cancellationToken);

        await AddColumnIfMissingAsync(
            db,
            "ImportBatches",
            "RawRowCount",
            "INTEGER NOT NULL DEFAULT 0",
            cancellationToken);

        await AddColumnIfMissingAsync(
            db,
            "ImportBatches",
            "AggregatedRowCount",
            "INTEGER NOT NULL DEFAULT 0",
            cancellationToken);

        await AddColumnIfMissingAsync(
            db,
            "ImportBatches",
            "SkippedBlankObjectPrintCodeCount",
            "INTEGER NOT NULL DEFAULT 0",
            cancellationToken);

        await AddColumnIfMissingAsync(
            db,
            "ImportBatches",
            "SkippedInvalidRowCount",
            "INTEGER NOT NULL DEFAULT 0",
            cancellationToken);

        await AddColumnIfMissingAsync(
            db,
            "MonthlyFlowRows",
            "ProjectName",
            "TEXT NULL",
            cancellationToken);

        await AddColumnIfMissingAsync(
            db,
            "MonthlyFlowRows",
            "ObjectNumber",
            "TEXT NULL",
            cancellationToken);

        await AddColumnIfMissingAsync(
            db,
            "MonthlyFlowRows",
            "SourceSheet",
            "TEXT NULL",
            cancellationToken);

        await AddColumnIfMissingAsync(
            db,
            "MonthlyFlowRows",
            "CustomerName",
            "TEXT NULL",
            cancellationToken);

        await AddColumnIfMissingAsync(
            db,
            "MonthlyFlowRows",
            "RowType",
            "TEXT NOT NULL DEFAULT 'SubcontractorInvoice'",
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS ProjectObjectValues (
                Id TEXT NOT NULL CONSTRAINT PK_ProjectObjectValues PRIMARY KEY,
                ProjectCode TEXT NOT NULL,
                ObjectNumber TEXT NOT NULL,
                ObjectPrintCode TEXT NULL,
                DepartmentCode TEXT NULL,
                ObjectIndex TEXT NULL,
                ProjectValueAmount decimal(18,2) NOT NULL,
                SourceRowCount INTEGER NULL,
                SourceRowsJson TEXT NULL,
                LastSeenContractImportBatchId TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                RowKey TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS IX_ProjectObjectValues_ProjectCode
            ON ProjectObjectValues (ProjectCode);
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ProjectObjectValues_RowKey
            ON ProjectObjectValues (RowKey);
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS Subcontractors (
                Id TEXT NOT NULL CONSTRAINT PK_Subcontractors PRIMARY KEY,
                CanonicalName TEXT NOT NULL,
                LegalForm TEXT NULL,
                BaseName TEXT NOT NULL,
                NormalizedKey TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                LastSeenAt TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Subcontractors_NormalizedKey
            ON Subcontractors (NormalizedKey);
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS SubcontractorAliases (
                Id TEXT NOT NULL CONSTRAINT PK_SubcontractorAliases PRIMARY KEY,
                SubcontractorId TEXT NULL,
                RawName TEXT NOT NULL,
                NormalizedKey TEXT NOT NULL,
                CanonicalName TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                LastSeenAt TEXT NOT NULL,
                CONSTRAINT FK_SubcontractorAliases_Subcontractors_SubcontractorId
                    FOREIGN KEY (SubcontractorId) REFERENCES Subcontractors (Id) ON DELETE SET NULL
            );
            """, cancellationToken);

        await AddColumnIfMissingAsync(
            db,
            "SubcontractorAliases",
            "SubcontractorId",
            "TEXT NULL",
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS IX_SubcontractorAliases_NormalizedKey
            ON SubcontractorAliases (NormalizedKey);
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS IX_SubcontractorAliases_SubcontractorId
            ON SubcontractorAliases (SubcontractorId);
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS IX_SubcontractorContracts_ProjectCode
            ON SubcontractorContracts (ProjectCode);
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS IX_SubcontractorContracts_RowKey
            ON SubcontractorContracts (RowKey);
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS ManualContractLinks (
                Id TEXT NOT NULL CONSTRAINT PK_ManualContractLinks PRIMARY KEY,
                ProjectCode TEXT NOT NULL,
                ObjectNumber TEXT NOT NULL,
                SourceSubcontractorKey TEXT NOT NULL,
                TargetSubcontractorKey TEXT NOT NULL,
                SourceSubcontractorName TEXT NOT NULL,
                TargetSubcontractorName TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ManualContractLinks_Scope_SourceKey
            ON ManualContractLinks (ProjectCode, ObjectNumber, SourceSubcontractorKey);
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS ManualObjectAssignments (
                Id TEXT NOT NULL CONSTRAINT PK_ManualObjectAssignments PRIMARY KEY,
                ProjectCode TEXT NOT NULL,
                SourceObjectNumber TEXT NOT NULL,
                SubcontractorKey TEXT NOT NULL,
                SubcontractorName TEXT NOT NULL,
                TargetObjectNumber TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ManualObjectAssignments_Scope_SourceKey
            ON ManualObjectAssignments (ProjectCode, SourceObjectNumber, SubcontractorKey);
            """, cancellationToken);
    }

    private static async Task EnsureImportTablesAsync(
        MoneyFlowDbContext db,
        CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS ImportBatches (
                Id TEXT NOT NULL CONSTRAINT PK_ImportBatches PRIMARY KEY,
                SourceFileName TEXT NOT NULL,
                ContentHash TEXT NOT NULL,
                SchemaVersion TEXT NULL,
                SourceSystem TEXT NULL,
                Year INTEGER NOT NULL,
                Month INTEGER NOT NULL,
                SheetName TEXT NULL,
                ExportedAt TEXT NULL,
                RawRowCount INTEGER NOT NULL DEFAULT 0,
                AggregatedRowCount INTEGER NOT NULL DEFAULT 0,
                SkippedBlankObjectPrintCodeCount INTEGER NOT NULL DEFAULT 0,
                SkippedInvalidRowCount INTEGER NOT NULL DEFAULT 0,
                ImportedAt TEXT NOT NULL,
                RowsReceived INTEGER NOT NULL,
                RowsInserted INTEGER NOT NULL,
                RowsUpdated INTEGER NOT NULL,
                RowsSkipped INTEGER NOT NULL,
                WarningsCount INTEGER NOT NULL,
                Status TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ImportBatches_ContentHash
            ON ImportBatches (ContentHash);
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS MonthlyFlowRows (
                Id TEXT NOT NULL CONSTRAINT PK_MonthlyFlowRows PRIMARY KEY,
                Year INTEGER NOT NULL,
                Month INTEGER NOT NULL,
                ProjectCode TEXT NOT NULL,
                ProjectName TEXT NULL,
                ObjectNumber TEXT NULL,
                SubcontractorName TEXT NULL,
                CustomerName TEXT NULL,
                RowType TEXT NOT NULL DEFAULT 'SubcontractorInvoice',
                ObjectName TEXT NULL,
                AmountWithoutVat decimal(18,2) NOT NULL,
                IndexedAmount decimal(18,2) NULL,
                Responsible TEXT NULL,
                Engineer TEXT NULL,
                SourceSheet TEXT NULL,
                SourceRow INTEGER NOT NULL,
                RowKey TEXT NOT NULL,
                LastImportBatchId TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                CONSTRAINT FK_MonthlyFlowRows_ImportBatches_LastImportBatchId
                    FOREIGN KEY (LastImportBatchId) REFERENCES ImportBatches (Id) ON DELETE RESTRICT
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS IX_MonthlyFlowRows_RowKey
            ON MonthlyFlowRows (RowKey);
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS IX_MonthlyFlowRows_ProjectCode
            ON MonthlyFlowRows (ProjectCode);
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS ImportWarnings (
                Id TEXT NOT NULL CONSTRAINT PK_ImportWarnings PRIMARY KEY,
                ImportBatchId TEXT NOT NULL,
                SourceRow INTEGER NULL,
                Message TEXT NOT NULL,
                CONSTRAINT FK_ImportWarnings_ImportBatches_ImportBatchId
                    FOREIGN KEY (ImportBatchId) REFERENCES ImportBatches (Id) ON DELETE CASCADE
            );
            """, cancellationToken);
    }

    private static async Task AddContractSnapshotColumnsAsync(
        MoneyFlowDbContext db,
        string tableName,
        CancellationToken cancellationToken)
    {
        await AddColumnIfMissingAsync(db, tableName, "ProjectStatus", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(db, tableName, "IsActiveContractedProject", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, tableName, "IsInLatestContractedImport", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, tableName, "LastSeenContractImportBatchId", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(db, tableName, "LastSeenContractImportAt", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(db, tableName, "BecameInactiveAt", "TEXT NULL", cancellationToken);
    }

    private static async Task BootstrapExistingContractSnapshotStateAsync(
        MoneyFlowDbContext db,
        CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE SubcontractorContracts
            SET IsActiveContractedProject = 1,
                IsInLatestContractedImport = 1
            WHERE LastSeenContractImportAt IS NULL
              AND BecameInactiveAt IS NULL
              AND IsActiveContractedProject = 0
              AND IsInLatestContractedImport = 0;
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            UPDATE Projects
            SET IsActiveContractedProject = 1,
                IsInLatestContractedImport = 1
            WHERE LastSeenContractImportAt IS NULL
              AND BecameInactiveAt IS NULL
              AND IsActiveContractedProject = 0
              AND IsInLatestContractedImport = 0
              AND EXISTS (
                  SELECT 1
                  FROM SubcontractorContracts c
                  WHERE UPPER(c.ProjectCode) = UPPER(Projects.ProjectCode)
              );
            """, cancellationToken);
    }

    // Tables whose schema this code may ALTER. An ALTER TABLE cannot bind its
    // identifiers as parameters, so the table name is validated against this
    // allowlist before it is ever interpolated into DDL — even though all current
    // callers pass hardcoded literals.
    private static readonly HashSet<string> AllowedSchemaTables = new(StringComparer.Ordinal)
    {
        "SubcontractorContracts",
        "ImportBatches",
        "MonthlyFlowRows",
        "SubcontractorAliases",
        "Projects",
    };

    private static async Task AddColumnIfMissingAsync(
        MoneyFlowDbContext db,
        string tableName,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        if (!AllowedSchemaTables.Contains(tableName))
        {
            throw new ArgumentException($"Unknown schema table '{tableName}'.", nameof(tableName));
        }

        // pragma_table_info accepts a bound parameter, so SqlQuery (interpolated →
        // parameterized) reads the existing columns with no raw-SQL interpolation.
        var existingColumns = await db.Database
            .SqlQuery<string>($"SELECT name AS Value FROM pragma_table_info({tableName})")
            .ToListAsync(cancellationToken);

        if (existingColumns.Any(name => string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        // tableName is allowlisted above; columnName/definition are hardcoded
        // internal literals. Identifiers cannot be parameterized in DDL.
        var sql = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static void ApplyMonthlyDisplayFieldsToContracts(
        MoneyFlowDbContext db,
        IReadOnlyCollection<MonthlyMoneyFlowRow> rows,
        DateTimeOffset updatedAt)
    {
        var projectRows = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.ProjectName))
            .GroupBy(row => NormalizeKeyPart(row.ProjectCode))
            .Select(group => group.First());

        foreach (var row in projectRows)
        {
            var project = db.Projects.Local.FirstOrDefault(existing => NormalizeKeyPart(existing.ProjectCode) == NormalizeKeyPart(row.ProjectCode))
                ?? db.Projects.FirstOrDefault(existing => existing.ProjectCode.ToLower() == row.ProjectCode.ToLower());

            if (project is not null && string.IsNullOrWhiteSpace(project.ProjectName))
            {
                project.ProjectName = row.ProjectName;
                project.UpdatedAt = updatedAt;
            }
        }

        var rowsByContractKey = rows
            .Where(row => !IsClientMonthlyValueRow(row))
            .Where(row => !string.IsNullOrWhiteSpace(row.ObjectNumber))
            .GroupBy(row => LogicalContractKey(
                row.ProjectCode,
                row.ObjectNumber!,
                string.IsNullOrWhiteSpace(row.SubcontractorName) ? "(Be subrangovo)" : row.SubcontractorName!))
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        if (rowsByContractKey.Count == 0)
        {
            return;
        }

        foreach (var contract in db.SubcontractorContracts)
        {
            var key = LogicalContractKey(contract.ProjectCode, contract.ObjectNumber, contract.SubcontractorName);
            if (!rowsByContractKey.TryGetValue(key, out var row))
            {
                continue;
            }

            if (ApplyMissingDisplayFields(contract, row.ProjectName, row.ObjectName, row.Responsible, row.Engineer))
            {
                contract.DisplayFieldsUpdatedAt = updatedAt;
                contract.UpdatedAt = updatedAt;
            }
        }
    }

    private static void TouchSubcontractorAliases(
        MoneyFlowDbContext db,
        IEnumerable<string?> rawNames,
        DateTimeOffset now)
    {
        var names = rawNames
            .Select(CleanSubcontractorDisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
        {
            return;
        }

        var identities = names
            .Select(NormalizeSubcontractorIdentity)
            .Where(identity => !string.IsNullOrWhiteSpace(identity.NormalizedKey))
            .ToList();
        var keys = identities
            .Select(identity => identity.NormalizedKey)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var subcontractorsByKey = db.Subcontractors
            .Where(subcontractor => keys.Contains(subcontractor.NormalizedKey))
            .ToList()
            .ToDictionary(subcontractor => subcontractor.NormalizedKey, StringComparer.Ordinal);
        var aliasesByRawKey = db.SubcontractorAliases
            .Where(alias => keys.Contains(alias.NormalizedKey))
            .ToList()
            .GroupBy(alias => $"{alias.NormalizedKey}|{NormalizeKeyPart(alias.RawName)}", StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var identity in identities)
        {
            if (!subcontractorsByKey.TryGetValue(identity.NormalizedKey, out var subcontractor))
            {
                subcontractor = new Subcontractor
                {
                    CanonicalName = identity.CanonicalDisplayName,
                    LegalForm = string.IsNullOrWhiteSpace(identity.LegalForm) ? null : identity.LegalForm,
                    BaseName = identity.BaseName,
                    NormalizedKey = identity.NormalizedKey,
                    CreatedAt = now,
                    LastSeenAt = now
                };
                db.Subcontractors.Add(subcontractor);
                subcontractorsByKey[identity.NormalizedKey] = subcontractor;
            }
            else
            {
                subcontractor.LastSeenAt = now;
                if (IsCleanerCanonicalName(identity.CanonicalDisplayName, subcontractor.CanonicalName))
                {
                    subcontractor.CanonicalName = identity.CanonicalDisplayName;
                }
            }

            var aliasKey = $"{identity.NormalizedKey}|{NormalizeKeyPart(identity.RawName)}";
            if (aliasesByRawKey.TryGetValue(aliasKey, out var alias))
            {
                alias.RawName = identity.RawName;
                alias.CanonicalName = subcontractor.CanonicalName;
                alias.SubcontractorId = subcontractor.Id;
                alias.LastSeenAt = now;
                continue;
            }

            alias = new SubcontractorAlias
            {
                RawName = identity.RawName,
                NormalizedKey = identity.NormalizedKey,
                CanonicalName = subcontractor.CanonicalName,
                SubcontractorId = subcontractor.Id,
                CreatedAt = now,
                LastSeenAt = now
            };
            db.SubcontractorAliases.Add(alias);
            aliasesByRawKey[aliasKey] = alias;
        }
    }

    private async Task SeedConfiguredSubcontractorAliasesAsync(
        MoneyFlowDbContext db,
        CancellationToken cancellationToken)
    {
        if (_configuredAliases.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var changed = false;
        foreach (var configured in _configuredAliases)
        {
            var rawName = CleanSubcontractorDisplayName(configured.RawName);
            var canonicalName = CleanSubcontractorDisplayName(configured.CanonicalName);
            if (string.IsNullOrWhiteSpace(rawName) || string.IsNullOrWhiteSpace(canonicalName))
            {
                continue;
            }

            var normalizedKey = NormalizeSubcontractorName(rawName);
            var alias = await db.SubcontractorAliases
                .FirstOrDefaultAsync(existing => existing.NormalizedKey == normalizedKey, cancellationToken);
            if (alias is null)
            {
                db.SubcontractorAliases.Add(new SubcontractorAlias
                {
                    RawName = rawName,
                    NormalizedKey = normalizedKey,
                    CanonicalName = canonicalName,
                    CreatedAt = now,
                    LastSeenAt = now
                });
                changed = true;
                continue;
            }

            if (!string.Equals(alias.CanonicalName, canonicalName, StringComparison.Ordinal)
                || !string.Equals(alias.RawName, rawName, StringComparison.Ordinal))
            {
                alias.RawName = rawName;
                alias.CanonicalName = canonicalName;
                alias.LastSeenAt = now;
                changed = true;
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static async Task BackfillSubcontractorIdentityAsync(
        MoneyFlowDbContext db,
        CancellationToken cancellationToken)
    {
        var names = await db.SubcontractorContracts
            .AsNoTracking()
            .Select(contract => contract.SubcontractorName)
            .Concat(db.MonthlyFlowRows
                .AsNoTracking()
                .Where(row => row.RowType != ClientMonthlyValueRowType
                    && row.SourceSheet != "SMD")
                .Select(row => row.SubcontractorName))
            .ToListAsync(cancellationToken);

        TouchSubcontractorAliases(db, names, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ManualContractLinkResult> CreateManualContractLinkAsync(
        string projectCode,
        string? targetContractRowKey,
        string? sourceSubcontractorName,
        string? sourceObjectNumber,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(targetContractRowKey))
            {
                return new ManualContractLinkResult(false, "Nepasirinkta tikslinė sutarties eilutė.", null);
            }

            var contract = await db.SubcontractorContracts
                .AsNoTracking()
                .FirstOrDefaultAsync(existing => existing.RowKey == targetContractRowKey, cancellationToken);
            if (contract is null)
            {
                return new ManualContractLinkResult(false, "Tikslinė sutarties eilutė nerasta.", null);
            }

            var requestedParent = ParseProjectObjectCode(projectCode).ParentProjectCode;
            var contractParent = ParentProjectCodeFor(contract.ProjectCode, contract.ObjectNumber);
            if (!string.Equals(NormalizeKeyPart(requestedParent), NormalizeKeyPart(contractParent), StringComparison.Ordinal))
            {
                return new ManualContractLinkResult(false, "Tikslinė sutartis priklauso kitam projektui.", null);
            }

            // Rows may only be connected within one object scope: an invoice row
            // on P1677-05 must never merge into a contract on P1677-06.
            if (string.IsNullOrWhiteSpace(sourceObjectNumber))
            {
                return new ManualContractLinkResult(false, "Reikalingas pradinis objekto numeris.", null);
            }

            var contractObjectScope = NormalizeKeyPart(EffectiveObjectNumber(contract.ProjectCode, contract.ObjectNumber));
            var sourceObjectScope = NormalizeKeyPart(EffectiveObjectNumber(sourceObjectNumber, sourceObjectNumber));
            if (!string.Equals(sourceObjectScope, contractObjectScope, StringComparison.Ordinal))
            {
                return new ManualContractLinkResult(
                    false,
                    $"Rows belong to different objects ({sourceObjectScope} vs {contractObjectScope}). Only rows on the same object can be connected.",
                    null);
            }

            // Resolve both sides exactly like read-time matching does (alias map
            // included) so the stored keys always hit the same dictionary slots.
            var aliasMap = await GetSubcontractorAliasMapAsync(db, cancellationToken);
            var sourceIdentity = NormalizeSubcontractorIdentity(sourceSubcontractorName);
            var sourceKey = SubcontractorMatchKey(sourceSubcontractorName, aliasMap);
            var targetKey = SubcontractorMatchKey(contract.SubcontractorName, aliasMap);
            if (string.IsNullOrWhiteSpace(sourceIdentity.BaseName) || string.IsNullOrWhiteSpace(sourceKey))
            {
                return new ManualContractLinkResult(false, "Reikalingas pradinis subrangovo pavadinimas.", null);
            }

            if (string.Equals(sourceKey, targetKey, StringComparison.Ordinal))
            {
                return new ManualContractLinkResult(false, "Šios eilutės jau atitinka pagal pavadinimą; nėra ką susieti.", null);
            }

            var scopeProjectCode = NormalizeKeyPart(contractParent);
            var scopeObjectNumber = NormalizeKeyPart(contract.ObjectNumber);
            var link = await db.ManualContractLinks
                .FirstOrDefaultAsync(
                    existing => existing.ProjectCode == scopeProjectCode
                        && existing.ObjectNumber == scopeObjectNumber
                        && existing.SourceSubcontractorKey == sourceKey,
                    cancellationToken);
            if (link is null)
            {
                link = new ManualContractLink
                {
                    ProjectCode = scopeProjectCode,
                    ObjectNumber = scopeObjectNumber,
                    SourceSubcontractorKey = sourceKey,
                    TargetSubcontractorKey = targetKey,
                    SourceSubcontractorName = sourceIdentity.CanonicalDisplayName,
                    TargetSubcontractorName = CanonicalSubcontractorName(contract.SubcontractorName)
                };
                db.ManualContractLinks.Add(link);
            }
            else
            {
                link.TargetSubcontractorKey = targetKey;
                link.SourceSubcontractorName = sourceIdentity.CanonicalDisplayName;
                link.TargetSubcontractorName = CanonicalSubcontractorName(contract.SubcontractorName);
            }

            await db.SaveChangesAsync(cancellationToken);
            return new ManualContractLinkResult(true, null, link);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteManualContractLinkAsync(
        string projectCode,
        Guid linkId,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var requestedParent = NormalizeKeyPart(ParseProjectObjectCode(projectCode).ParentProjectCode);
            var link = await db.ManualContractLinks
                .FirstOrDefaultAsync(
                    existing => existing.Id == linkId && existing.ProjectCode == requestedParent,
                    cancellationToken);
            if (link is null)
            {
                return false;
            }

            db.ManualContractLinks.Remove(link);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ObjectAssignmentResult> CreateObjectAssignmentAsync(
        string projectCode,
        string? subcontractorName,
        string? sourceObjectNumber,
        string? targetObjectNumber,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            await EnsureMasterDataTablesAsync(db, cancellationToken);

            if (string.IsNullOrWhiteSpace(sourceObjectNumber))
            {
                return new ObjectAssignmentResult(false, "Reikalingas pradinis objekto numeris.", null);
            }

            if (string.IsNullOrWhiteSpace(targetObjectNumber))
            {
                return new ObjectAssignmentResult(false, "Reikalingas naujas objekto numeris.", null);
            }

            // Resolve the subcontractor key exactly like read-time matching so the
            // stored key hits the same dictionary slot in ApplyObjectAssignments.
            var aliasMap = await GetSubcontractorAliasMapAsync(db, cancellationToken);
            var identity = NormalizeSubcontractorIdentity(subcontractorName);
            var subcontractorKey = SubcontractorMatchKey(subcontractorName, aliasMap);
            if (string.IsNullOrWhiteSpace(identity.BaseName) || string.IsNullOrWhiteSpace(subcontractorKey))
            {
                return new ObjectAssignmentResult(false, "Reikalingas subrangovo pavadinimas.", null);
            }

            var parent = NormalizeKeyPart(ParseProjectObjectCode(projectCode).ParentProjectCode);
            var source = NormalizeKeyPart(sourceObjectNumber);
            var target = NormalizeKeyPart(targetObjectNumber);
            if (string.Equals(source, target, StringComparison.Ordinal))
            {
                return new ObjectAssignmentResult(false, "Naujas objektas sutampa su esamu.", null);
            }

            var assignment = await db.ManualObjectAssignments
                .FirstOrDefaultAsync(
                    existing => existing.ProjectCode == parent
                        && existing.SourceObjectNumber == source
                        && existing.SubcontractorKey == subcontractorKey,
                    cancellationToken);
            if (assignment is null)
            {
                assignment = new ManualObjectAssignment
                {
                    ProjectCode = parent,
                    SourceObjectNumber = source,
                    SubcontractorKey = subcontractorKey,
                    SubcontractorName = identity.CanonicalDisplayName,
                    TargetObjectNumber = target
                };
                db.ManualObjectAssignments.Add(assignment);
            }
            else
            {
                assignment.TargetObjectNumber = target;
                assignment.SubcontractorName = identity.CanonicalDisplayName;
            }

            await db.SaveChangesAsync(cancellationToken);
            return new ObjectAssignmentResult(true, null, assignment);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteObjectAssignmentAsync(
        string projectCode,
        Guid assignmentId,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var parent = NormalizeKeyPart(ParseProjectObjectCode(projectCode).ParentProjectCode);
            var assignment = await db.ManualObjectAssignments
                .FirstOrDefaultAsync(
                    existing => existing.Id == assignmentId && existing.ProjectCode == parent,
                    cancellationToken);
            if (assignment is null)
            {
                return false;
            }

            db.ManualObjectAssignments.Remove(assignment);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>> GetSubcontractorAliasMapAsync(
        MoneyFlowDbContext db,
        CancellationToken cancellationToken)
    {
        var aliases = await db.SubcontractorAliases
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias.NormalizedKey)
                && !string.IsNullOrWhiteSpace(alias.CanonicalName))
            .GroupBy(alias => alias.NormalizedKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => NormalizeSubcontractorName(group.First().CanonicalName),
                StringComparer.Ordinal);
    }

    private static string SubcontractorMatchKey(
        string? subcontractorName,
        IReadOnlyDictionary<string, string> aliasMap)
    {
        var normalizedKey = NormalizeSubcontractorName(subcontractorName);
        return aliasMap.TryGetValue(normalizedKey, out var canonicalKey)
            ? canonicalKey
            : normalizedKey;
    }

    private static bool IsCleanerCanonicalName(string candidate, string current)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(current))
        {
            return true;
        }

        var candidateScore = (candidate.Contains('"') ? 10 : 0) + candidate.Length;
        var currentScore = (current.Contains('"') ? 10 : 0) + current.Length;
        return candidateScore < currentScore;
    }

    private static bool ApplyMissingDisplayFields(
        SubcontractorContract contract,
        string? projectName,
        string? objectName,
        string? responsible,
        string? engineer)
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(contract.ProjectName) && !string.IsNullOrWhiteSpace(projectName))
        {
            contract.ProjectName = projectName;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(contract.ObjectName) && !string.IsNullOrWhiteSpace(objectName))
        {
            contract.ObjectName = objectName;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(contract.Responsible) && !string.IsNullOrWhiteSpace(responsible))
        {
            contract.Responsible = responsible;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(contract.Engineer) && !string.IsNullOrWhiteSpace(engineer))
        {
            contract.Engineer = engineer;
            changed = true;
        }

        return changed;
    }

    private static bool ApplyProvidedDisplayFields(
        SubcontractorContract contract,
        string? projectName,
        string? objectName,
        string? responsible,
        string? engineer)
    {
        var changed = false;
        if (!string.IsNullOrWhiteSpace(projectName) && !string.Equals(contract.ProjectName, projectName, StringComparison.Ordinal))
        {
            contract.ProjectName = projectName;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(objectName) && !string.Equals(contract.ObjectName, objectName, StringComparison.Ordinal))
        {
            contract.ObjectName = objectName;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(responsible) && !string.Equals(contract.Responsible, responsible, StringComparison.Ordinal))
        {
            contract.Responsible = responsible;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(engineer) && !string.Equals(contract.Engineer, engineer, StringComparison.Ordinal))
        {
            contract.Engineer = engineer;
            changed = true;
        }

        return changed;
    }

    private static bool HasDisplayFields(params string?[] values)
    {
        return values.Any(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string NormalizeKeyPart(string? value)
    {
        return CollapseWhitespace(value).ToUpperInvariant();
    }

    private static bool LooksLikeProjectOrObjectCode(string? value)
    {
        var text = NormalizeKeyPart(value);
        return Regex.IsMatch(text, @"^P\d{3,6}(-[A-Z0-9]+)?$");
    }

    private static bool LooksLikeDepartmentCode(string? value)
    {
        var text = NormalizeKeyPart(value);
        if (LooksLikeProjectOrObjectCode(text))
        {
            return false;
        }

        return Regex.IsMatch(text, @"^[A-Z]{2,6}\d{0,3}$");
    }

    public static string NormalizeClientName(string? value)
    {
        var text = CleanCustomerDisplayName(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        foreach (var quote in SubcontractorQuoteChars)
        {
            text = text.Replace(quote.ToString(), " ");
        }

        text = Regex.Replace(text, @"[()[\]{}]", " ");
        text = Regex.Replace(text, @"\s*[,.]\s*", " ");
        text = CollapseWhitespace(text).ToUpperInvariant();
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 0 && ClientLegalFormTokens.Contains(tokens[0]))
        {
            tokens.RemoveAt(0);
        }

        while (tokens.Count > 0 && ClientLegalFormTokens.Contains(tokens[^1]))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        return string.Join(' ', tokens);
    }

    private static string CanonicalClientDisplayName(IReadOnlyCollection<string> rawNames)
    {
        var candidates = rawNames
            .Select(CleanClientDisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ClientDisplayScore)
            .ThenBy(name => name.Length)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return candidates.Count == 0 ? "(Be užsakovo)" : candidates[0];
    }

    private static string? CleanClientDisplayName(string? value)
    {
        var text = CleanCustomerDisplayName(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var quote in SubcontractorQuoteChars)
        {
            text = text.Replace(quote.ToString(), " ");
        }

        text = Regex.Replace(text, @"\s*[,.]\s*", " ");
        text = CollapseWhitespace(text);
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 0 && ClientLegalFormTokens.Contains(tokens[0]))
        {
            tokens.RemoveAt(0);
        }

        while (tokens.Count > 0 && ClientLegalFormTokens.Contains(tokens[^1]))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        return ToClientDisplayCase(tokens.Count == 0 ? text : string.Join(' ', tokens));
    }

    private static string ToClientDisplayCase(string value)
    {
        if (!value.Any(char.IsLetter) || value.Any(char.IsLower))
        {
            return value;
        }

        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value.ToLower(CultureInfo.CurrentCulture));
    }

    private static int ClientDisplayScore(string name)
    {
        var score = 0;
        if (name.Contains('"') || name.Contains('„') || name.Contains('“') || name.Contains('”'))
        {
            score += 10;
        }

        var firstToken = NormalizeKeyPart(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(firstToken) && ClientLegalFormTokens.Contains(firstToken))
        {
            score += 2;
        }

        return score;
    }

    // Lithuanian legal-form phrases (multi-word). Checked longest-first so the
    // longer phrase wins before its shorter subset (e.g. "uždaroji akcinė
    // bendrovė" before "akcinė bendrovė").
    private static readonly string[] SubcontractorLegalFormPhrases =
    {
        "uždaroji akcinė bendrovė",
        "akcinė bendrovė",
        "mažoji bendrija",
        "individuali įmonė",
    };

    // Single-token Lithuanian legal forms (already lowercased).
    private static readonly HashSet<string> SubcontractorLegalFormTokens = new(StringComparer.Ordinal)
    {
        "uab", "ab", "mb", "všį", "iį", "vį", "ūb", "tūb", "kb",
    };

    private static readonly IReadOnlyCollection<LegalFormPattern> SubcontractorLegalForms =
    [
        new("SP Z O O", ["SP", "Z", "O", "O"]),
        new("VŠĮ", ["VŠĮ"]),
        new("VŠĮ", ["VSI"]),
        new("UAB", ["UAB"]),
        new("MB", ["MB"]),
        new("AB", ["AB"]),
        new("IĮ", ["IĮ"]),
        new("II", ["II"]),
        new("VĮ", ["VĮ"]),
        new("SIA", ["SIA"]),
        new("AS", ["AS"]),
        new("GMBH", ["GMBH"]),
        new("OU", ["OU"]),
        new("OÜ", ["OÜ"]),
    ];

    private static readonly HashSet<string> ClientLegalFormTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "AB", "UAB", "MB", "VŠĮ", "VšĮ", "IĮ", "VĮ"
    };

    private static readonly char[] SubcontractorQuoteChars =
    {
        '"', '\'', '„', '“', '”', '«', '»', // " ' „ “ ” « »
    };

    public static SubcontractorNameIdentity NormalizeSubcontractorIdentity(string? value)
    {
        var rawName = CollapseWhitespace(value);
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return new SubcontractorNameIdentity("", "(Be subrangovo)", "", "", "");
        }

        var text = rawName;
        var pipeIndex = text.IndexOf('|');
        if (pipeIndex >= 0)
        {
            text = pipeIndex == 0 ? text[(pipeIndex + 1)..] : text[..pipeIndex];
        }

        foreach (var quote in SubcontractorQuoteChars)
        {
            text = text.Replace(quote.ToString(), " ");
        }

        text = Regex.Replace(text, @"\bsp\.?\s*z\.?\s*o\.?\s*o\.?\b", " SP Z O O ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"[;,()[\]{}]", " ");
        text = Regex.Replace(text, @"\s*\.\s*", ".");
        text = CollapseWhitespace(text);
        if (text.Length == 0)
        {
            return new SubcontractorNameIdentity(rawName, "(Be subrangovo)", "", "", "");
        }

        var tokens = text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => NormalizeCompanyToken(token).ToUpperInvariant())
            .ToList();

        var legalForm = "";
        if (TryRemoveLegalForm(tokens, fromStart: true, out var startLegalForm))
        {
            legalForm = startLegalForm;
        }
        else if (TryRemoveLegalForm(tokens, fromStart: false, out var endLegalForm))
        {
            legalForm = endLegalForm;
        }

        var baseName = CollapseWhitespace(string.Join(' ', tokens));
        var normalizedKey = string.IsNullOrWhiteSpace(baseName)
            ? legalForm
            : $"{legalForm}|{baseName}";
        var canonicalName = CanonicalSubcontractorDisplayName(legalForm, baseName, rawName);

        return new SubcontractorNameIdentity(
            rawName,
            canonicalName,
            legalForm,
            baseName,
            normalizedKey);
    }

    /// <summary>
    /// Builds a deterministic matching key for a subcontractor company name.
    /// The key is LEGALFORM|BASENAME; no substring, fuzzy, or similarity matching
    /// is used.
    /// </summary>
    public static string NormalizeSubcontractorName(string? value)
    {
        return NormalizeSubcontractorIdentity(value).NormalizedKey;
    }

    public static string CanonicalSubcontractorName(string? value)
    {
        return NormalizeSubcontractorIdentity(value).CanonicalDisplayName;
    }

    private static bool TryRemoveLegalForm(List<string> tokens, bool fromStart, out string legalForm)
    {
        foreach (var candidate in SubcontractorLegalForms.OrderByDescending(form => form.Tokens.Length))
        {
            if (fromStart)
            {
                if (tokens.Count < candidate.Tokens.Length
                    || !candidate.Tokens.SequenceEqual(tokens.Take(candidate.Tokens.Length), StringComparer.Ordinal))
                {
                    continue;
                }

                tokens.RemoveRange(0, candidate.Tokens.Length);
                legalForm = candidate.Key;
                return true;
            }

            if (tokens.Count < candidate.Tokens.Length
                || !candidate.Tokens.SequenceEqual(tokens.Skip(tokens.Count - candidate.Tokens.Length), StringComparer.Ordinal))
            {
                continue;
            }

            tokens.RemoveRange(tokens.Count - candidate.Tokens.Length, candidate.Tokens.Length);
            legalForm = candidate.Key;
            return true;
        }

        legalForm = "";
        return false;
    }

    private static string CanonicalSubcontractorDisplayName(string legalForm, string baseName, string rawName)
    {
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return CleanSubcontractorDisplayName(rawName) ?? "(Be subrangovo)";
        }

        var displayBase = string.Join(' ', baseName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(DisplayCompanyToken));

        return string.IsNullOrWhiteSpace(legalForm)
            ? displayBase
            : $"{legalForm} {displayBase}";
    }

    private static string DisplayCompanyToken(string token)
    {
        if (Regex.IsMatch(token, @"^\d+[A-Z0-9]*\.LT$", RegexOptions.IgnoreCase))
        {
            var prefix = token[..^3].ToLowerInvariant();
            return $"{prefix}.LT";
        }

        if (token.Length <= 3 && token.All(char.IsLetter))
        {
            return token;
        }

        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(token.ToLower(CultureInfo.CurrentCulture));
    }

    private static string NormalizeCompanyToken(string token)
    {
        if (!token.Contains('.')
            && Regex.IsMatch(token, @"^(?=.*\d)[a-z0-9]+lt$", RegexOptions.IgnoreCase))
        {
            return $"{token[..^2]}.lt";
        }

        return token;
    }

    public static string? CleanSubcontractorDisplayName(string? value)
    {
        var text = CollapseWhitespace(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var pipeIndex = text.IndexOf('|');
        if (pipeIndex >= 0)
        {
            text = pipeIndex == 0 ? text[(pipeIndex + 1)..] : text[..pipeIndex];
        }

        text = CollapseWhitespace(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text.Trim();
    }

    public static string? CleanCustomerDisplayName(string? value)
    {
        var text = CollapseWhitespace(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var pipeIndex = text.IndexOf('|');
        if (pipeIndex >= 0)
        {
            text = pipeIndex == 0 ? "" : text[..pipeIndex];
        }

        text = CollapseWhitespace(text);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool TokensStartWith(List<string> tokens, string[] prefix)
    {
        if (tokens.Count < prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (!string.Equals(tokens[i], prefix[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TokensEndWith(List<string> tokens, string[] suffix)
    {
        if (tokens.Count < suffix.Length)
        {
            return false;
        }

        var offset = tokens.Count - suffix.Length;
        for (var i = 0; i < suffix.Length; i++)
        {
            if (!string.Equals(tokens[offset + i], suffix[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? CleanText(string? value)
    {
        var collapsed = CollapseWhitespace(value);
        return string.IsNullOrWhiteSpace(collapsed) ? null : collapsed;
    }

    private static string CollapseWhitespace(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? ""
            : Regex.Replace(value.Trim(), @"\s+", " ");
    }

    private static string LogicalContractKey(string projectCode, string objectNumber, string subcontractorName)
    {
        return $"{NormalizeKeyPart(projectCode)}|{NormalizeKeyPart(objectNumber)}|{NormalizeSubcontractorName(subcontractorName)}";
    }

    public static ProjectObjectCode ParseProjectObjectCode(string? value)
    {
        var cleaned = CleanText(value) ?? "";
        var dashIndex = cleaned.LastIndexOf('-');
        if (dashIndex <= 0 || dashIndex == cleaned.Length - 1)
        {
            return new ProjectObjectCode(cleaned, cleaned, "");
        }

        return new ProjectObjectCode(
            cleaned[..dashIndex],
            cleaned,
            cleaned[(dashIndex + 1)..]);
    }

    private static string EffectiveObjectNumber(string projectCode, string? objectNumber)
    {
        var cleanObjectNumber = CleanText(objectNumber);
        if (!string.IsNullOrWhiteSpace(cleanObjectNumber))
        {
            var parsedObjectNumber = ParseProjectObjectCode(cleanObjectNumber);
            var parsedProjectCode = ParseProjectObjectCode(projectCode);
            if (!string.Equals(parsedObjectNumber.ParentProjectCode, parsedObjectNumber.ObjectNumber, StringComparison.OrdinalIgnoreCase)
                && string.Equals(parsedObjectNumber.ParentProjectCode, parsedProjectCode.ParentProjectCode, StringComparison.OrdinalIgnoreCase))
            {
                return cleanObjectNumber;
            }
        }

        return CleanText(projectCode) ?? cleanObjectNumber ?? "";
    }

    private static string ParentProjectCodeFor(string projectCode, string? objectNumber)
    {
        return ParseProjectObjectCode(EffectiveObjectNumber(projectCode, objectNumber)).ParentProjectCode;
    }

    private static bool ProjectRowIsInScope(
        string projectCode,
        string? objectNumber,
        string requestedProjectCode,
        string? requestedObjectNumber)
    {
        var effectiveObjectNumber = EffectiveObjectNumber(projectCode, objectNumber);
        if (!string.IsNullOrWhiteSpace(requestedObjectNumber))
        {
            return string.Equals(effectiveObjectNumber, requestedObjectNumber, StringComparison.OrdinalIgnoreCase);
        }

        var requested = ParseProjectObjectCode(requestedProjectCode);
        if (!string.Equals(requested.ParentProjectCode, requested.ObjectNumber, StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(effectiveObjectNumber, requested.ObjectNumber, StringComparison.OrdinalIgnoreCase);
        }

        var actual = ParseProjectObjectCode(effectiveObjectNumber);
        return string.Equals(actual.ParentProjectCode, requested.ParentProjectCode, StringComparison.OrdinalIgnoreCase);
    }

    private static List<MonthlyMoneyFlowRow> CollapseRowsByKey(IReadOnlyCollection<MonthlyMoneyFlowRow> rows)
    {
        var byKey = new Dictionary<string, MonthlyMoneyFlowRow>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var row in rows)
        {
            if (!byKey.TryGetValue(row.RowKey, out var existing))
            {
                byKey[row.RowKey] = row;
                order.Add(row.RowKey);
                continue;
            }

            // Same logical key within this file: sum the money values so the
            // stored single row reflects the combined total for the month.
            existing.AmountWithoutVat += row.AmountWithoutVat;
            if (row.IndexedAmount.HasValue)
            {
                existing.IndexedAmount = (existing.IndexedAmount ?? 0m) + row.IndexedAmount.Value;
            }

            // Keep the first non-empty descriptive value (the keys are equal, so
            // these are expected to match; this just avoids losing data when one
            // duplicate row left a field blank).
            existing.ProjectName ??= row.ProjectName;
            existing.ObjectName ??= row.ObjectName;
            existing.CustomerName ??= row.CustomerName;
            existing.RowType = RowTypeFor(row.RowType, row.SourceSheet);
            existing.Responsible ??= row.Responsible;
            existing.Engineer ??= row.Engineer;
            existing.SourceSheet ??= row.SourceSheet;
            existing.ObjectNumber ??= row.ObjectNumber;
        }

        return order.Select(key => byKey[key]).ToList();
    }

    private static bool RowsMatch(MonthlyMoneyFlowRow existingRow, MonthlyMoneyFlowRow importedRow)
    {
        return string.Equals(existingRow.ProjectCode, importedRow.ProjectCode, StringComparison.Ordinal)
            && string.Equals(existingRow.ProjectName, importedRow.ProjectName, StringComparison.Ordinal)
            && string.Equals(existingRow.ObjectNumber, importedRow.ObjectNumber, StringComparison.Ordinal)
            && string.Equals(existingRow.SubcontractorName, importedRow.SubcontractorName, StringComparison.Ordinal)
            && string.Equals(existingRow.CustomerName, importedRow.CustomerName, StringComparison.Ordinal)
            && string.Equals(RowTypeFor(existingRow.RowType, existingRow.SourceSheet), RowTypeFor(importedRow.RowType, importedRow.SourceSheet), StringComparison.Ordinal)
            && string.Equals(existingRow.ObjectName, importedRow.ObjectName, StringComparison.Ordinal)
            && existingRow.AmountWithoutVat == importedRow.AmountWithoutVat
            && existingRow.IndexedAmount == importedRow.IndexedAmount
            && string.Equals(existingRow.Responsible, importedRow.Responsible, StringComparison.Ordinal)
            && string.Equals(existingRow.Engineer, importedRow.Engineer, StringComparison.Ordinal)
            && string.Equals(existingRow.SourceSheet, importedRow.SourceSheet, StringComparison.Ordinal);
    }

    private static void CopyRowValues(
        MonthlyMoneyFlowRow existingRow,
        MonthlyMoneyFlowRow importedRow,
        DateTimeOffset updatedAt)
    {
        existingRow.Year = importedRow.Year;
        existingRow.Month = importedRow.Month;
        existingRow.ProjectCode = importedRow.ProjectCode;
        existingRow.ProjectName = importedRow.ProjectName;
        existingRow.ObjectNumber = importedRow.ObjectNumber;
        existingRow.SubcontractorName = importedRow.SubcontractorName;
        existingRow.CustomerName = importedRow.CustomerName;
        existingRow.RowType = RowTypeFor(importedRow.RowType, importedRow.SourceSheet);
        existingRow.ObjectName = importedRow.ObjectName;
        existingRow.AmountWithoutVat = importedRow.AmountWithoutVat;
        existingRow.IndexedAmount = importedRow.IndexedAmount;
        existingRow.Responsible = importedRow.Responsible;
        existingRow.Engineer = importedRow.Engineer;
        existingRow.SourceSheet = importedRow.SourceSheet;
        existingRow.SourceRow = importedRow.SourceRow;
        existingRow.LastImportBatchId = importedRow.LastImportBatchId;
        existingRow.UpdatedAt = updatedAt;
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
    decimal ProjectValue);

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
    string? DepartmentCode = null);

public sealed record ProjectObjectCode(string ParentProjectCode, string ObjectNumber, string ObjectCode);

public sealed record ConfiguredSubcontractorAlias(string? RawName, string? CanonicalName);

public sealed record SubcontractorNameIdentity(
    string RawName,
    string CanonicalDisplayName,
    string LegalForm,
    string BaseName,
    string NormalizedKey);

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
    IReadOnlyCollection<ManualContractLinkInfo> Links);

public sealed record ManualContractLinkInfo(Guid Id, string SourceName, string TargetName);

public sealed record ManualContractLinkResult(bool Success, string? Error, ManualContractLink? Link);

public sealed record ObjectAssignmentResult(bool Success, string? Error, ManualObjectAssignment? Assignment);

public sealed record MonthlyAmount(int Year, int Month, decimal AmountWithoutVat);

public sealed record YearMonth(int Year, int Month);

file sealed record InvoiceMatchKey(string ProjectCode, string ObjectNumber, string SubcontractorName, bool HasObjectNumber);

public sealed record LegalFormPattern(string Key, string[] Tokens);

sealed record ValidatedContractRow(
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

sealed record ValidatedProjectValueRow(
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
