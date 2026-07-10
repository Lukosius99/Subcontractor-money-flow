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

    public async Task<IReadOnlyCollection<MonthlyMoneyFlowRow>> GetRowsForProjectScopeAsync(
        string projectCode,
        string? objectNumber,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.MonthlyFlowRows
            .AsNoTracking()
            .Where(row => !row.IsExcludedFromTotals)
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

    public async Task<ProjectDetailSnapshot> GetProjectDetailAsync(
        string projectCode,
        string? objectNumber,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var rows = await db.MonthlyFlowRows
            .AsNoTracking()
            .Where(row => !row.IsExcludedFromTotals)
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

        // Object-scoped views pass object codes that don't exist in Projects
        // (it stores parent codes only), so fall back to the parent project to
        // keep Responsible/Engineer populated on object-level pages.
        var projectLookup = (objectNumber ?? projectCode).ToLower();
        var project = await db.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(
                existing => existing.ProjectCode.ToLower() == projectLookup,
                cancellationToken);
        if (project is null)
        {
            var parentLookup = ParseProjectObjectCode(projectCode).ParentProjectCode.ToLower();
            project = await db.Projects
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    existing => existing.ProjectCode.ToLower() == parentLookup,
                    cancellationToken);
        }

        return BuildProjectDetail(projectCode, project, contracts, objectValues, rows, aliasMap, manualLinks, objectAssignments);
    }

    /// <summary>
    /// Rewrites (in memory) the object number of invoice rows that a user has
    /// corrected via <see cref="ManualObjectAssignment"/>, so downstream scoping,
    /// matching and display all use the corrected object. Rows are AsNoTracking,
    /// so this never persists to the imported data.
    /// </summary>
    public async Task<IReadOnlyCollection<ProjectSummary>> GetProjectSummariesAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var rows = await db.MonthlyFlowRows
            .AsNoTracking()
            .Where(row => row.ProjectCode != "" && !row.IsExcludedFromTotals)
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

        // Client (SMD) invoiced per project: the complement of the subcontractor
        // invoice rows, so the list can show project-value remaining (Užsakovas
        // side) the same way the detail page does. Only rows on a real object
        // (P####-##) count — bare-parent rows are project-level totals that
        // duplicate the per-object values, so the detail page excludes them too
        // (mirrors the frontend isValidClientObjectNumber filter).
        var clientInvoiceSummaries = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.ProjectCode))
            .Where(row => IsClientMonthlyValueProjection(row.RowType, row.SourceSheet))
            .Where(row => IsValidClientObjectNumber(EffectiveObjectNumber(row.ProjectCode, row.ObjectNumber)))
            .GroupBy(row => ParentProjectCodeFor(row.ProjectCode, row.ObjectNumber), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(row => row.AmountWithoutVat),
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
                        var clientInvoiced = objectRows
                            .Where(row => IsClientMonthlyValueProjection(row.RowType, row.SourceSheet))
                            .Where(row => IsValidClientObjectNumber(EffectiveObjectNumber(row.ProjectCode, row.ObjectNumber)))
                            .Sum(row => row.AmountWithoutVat);
                        var projectValueAmount = objectProjectValues.Sum(value => value.ProjectValueAmount);
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
                            departmentCode,
                            projectValueAmount,
                            clientInvoiced);
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
                clientInvoiceSummaries.TryGetValue(projectCode, out var clientInvoiced);
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
                    : !string.IsNullOrWhiteSpace(contract?.Responsible)
                        ? contract!.Responsible
                        : !string.IsNullOrWhiteSpace(projectRecord?.Responsible) ? projectRecord!.Responsible : null;
                var engineer = !string.IsNullOrWhiteSpace(invoice?.Engineer)
                    ? invoice!.Engineer
                    : !string.IsNullOrWhiteSpace(contract?.Engineer)
                        ? contract!.Engineer
                        : !string.IsNullOrWhiteSpace(projectRecord?.Engineer) ? projectRecord!.Engineer : null;

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
                    projectValue?.ProjectValueAmount ?? 0,
                    clientInvoiced);
            })
            .OrderBy(summary => summary.ProjectCode)
            .ToList();
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
                SubcontractorNormalizer.CanonicalSubcontractorName(contract.SubcontractorName),
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
                string.IsNullOrWhiteSpace(first.SubcontractorName) ? "(Be subrangovo)" : SubcontractorNormalizer.CanonicalSubcontractorName(first.SubcontractorName),
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
            links,
            rows.Select(row => new MonthlyRowDetail(
                row.Id,
                row.Year,
                row.Month,
                row.ProjectCode,
                row.ObjectNumber,
                row.SubcontractorName,
                row.CustomerName,
                row.ObjectName,
                row.AmountWithoutVat,
                row.SourceSheet,
                row.SourceRow,
                row.Responsible,
                row.Engineer)).ToList());
    }

    private static IReadOnlyCollection<SmdCustomerInvoiceRow> BuildSmdCustomerRows(
        IReadOnlyCollection<MonthlyMoneyFlowRow> rows,
        IReadOnlyCollection<YearMonth> monthKeys)
    {
        var monthlyRows = rows
            .GroupBy(row => new
            {
                ObjectNumber = EffectiveObjectNumber(row.ProjectCode, row.ObjectNumber),
                CustomerKey = SubcontractorNormalizer.NormalizeClientName(ResolveSmdClientName(row.CustomerName, row.ProjectCode, row.SubcontractorName)),
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
                    CustomerName = SubcontractorNormalizer.CanonicalClientDisplayName(customerNames),
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
            var totalKey = $"{NormalizeKeyPart(row.ObjectNumber)}|{SubcontractorNormalizer.NormalizeClientName(row.CustomerName)}";
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

}
