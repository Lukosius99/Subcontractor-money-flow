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
        var explicitName = SubcontractorNormalizer.CleanCustomerDisplayName(explicitClientName);
        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            return explicitName;
        }

        var legacyName = SubcontractorNormalizer.CleanCustomerDisplayName(legacySubcontractorName);
        if (!string.IsNullOrWhiteSpace(legacyName) && !LooksLikeDepartmentCode(legacyName))
        {
            return legacyName;
        }

        var projectCodeName = SubcontractorNormalizer.CleanCustomerDisplayName(projectCode);
        if (!string.IsNullOrWhiteSpace(projectCodeName) && !LooksLikeProjectOrObjectCode(projectCodeName))
        {
            return projectCodeName;
        }

        return null;
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
                // Deterministic winner when several aliases share a key: the
                // most recently seen one, with Id as a stable tie-breaker.
                group => SubcontractorNormalizer.NormalizeSubcontractorName(
                    group.OrderByDescending(alias => alias.LastSeenAt)
                        .ThenBy(alias => alias.Id)
                        .First()
                        .CanonicalName),
                StringComparer.Ordinal);
    }

    private static string SubcontractorMatchKey(
        string? subcontractorName,
        IReadOnlyDictionary<string, string> aliasMap)
    {
        var normalizedKey = SubcontractorNormalizer.NormalizeSubcontractorName(subcontractorName);
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
        return $"{NormalizeKeyPart(projectCode)}|{NormalizeKeyPart(objectNumber)}|{SubcontractorNormalizer.NormalizeSubcontractorName(subcontractorName)}";
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

    // A real per-object code (P####-##). Bare project codes and malformed
    // values are rejected, matching the frontend isValidClientObjectNumber so
    // project-level summary rows don't get double-counted.
    private static bool IsValidClientObjectNumber(string? value) =>
        Regex.IsMatch((value ?? string.Empty).Trim(), @"^P\d{4}-\d{2}$", RegexOptions.IgnoreCase);

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
