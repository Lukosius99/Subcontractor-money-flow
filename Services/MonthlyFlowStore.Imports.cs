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
            await SchemaInitializer.EnsureMasterDataTablesAsync(db, cancellationToken);
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
            .Select(SubcontractorNormalizer.CleanSubcontractorDisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
        {
            return;
        }

        var identities = names
            .Select(SubcontractorNormalizer.NormalizeSubcontractorIdentity)
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
            var rawName = SubcontractorNormalizer.CleanSubcontractorDisplayName(configured.RawName);
            var canonicalName = SubcontractorNormalizer.CleanSubcontractorDisplayName(configured.CanonicalName);
            if (string.IsNullOrWhiteSpace(rawName) || string.IsNullOrWhiteSpace(canonicalName))
            {
                continue;
            }

            var normalizedKey = SubcontractorNormalizer.NormalizeSubcontractorName(rawName);
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

}
