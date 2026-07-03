using System.Globalization;
using System.Text.Json;
using PADS.MoneyFlow.Api.Dtos;
using PADS.MoneyFlow.Api.Models;

namespace PADS.MoneyFlow.Api.Services;

public sealed class MonthlyFlowImportService
{
    private static readonly HashSet<string> SupportedSchemaVersions = ["1.3", "1.4"];

    private readonly MonthlyFlowStore _store;

    public MonthlyFlowImportService(MonthlyFlowStore store)
    {
        _store = store;
    }

    public async Task<ServiceResult<MonthlyFlowImportResponse>> ImportAsync(
        string? sourceFileName,
        string json,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceFileName))
        {
            return ServiceResult<MonthlyFlowImportResponse>.Failure(
                "sourceFileName is required. Provide ?sourceFileName=... or X-Source-File-Name.");
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return ServiceResult<MonthlyFlowImportResponse>.Failure("Request body must contain JSON.");
        }

        var normalizedSourceFileName = sourceFileName.Trim();
        var parseResult = Parse(json);
        if (!parseResult.IsSuccess)
        {
            return ServiceResult<MonthlyFlowImportResponse>.Failure(parseResult.Error!);
        }

        var import = parseResult.Value;
        var batch = new ImportBatch
        {
            SourceFileName = normalizedSourceFileName,
            ContentHash = "",
            SchemaVersion = import.SchemaVersion,
            Year = import.Year,
            Month = import.Month,
            SheetName = import.SheetName,
            ExportedAt = import.ExportedAt,
            RowsReceived = import.Rows.Count,
            WarningsCount = import.Warnings.Count
        };

        var rows = import.Rows.Select(row =>
        {
            var isSmd = MonthlyFlowStore.IsSmdSheet(row.SourceSheet);
            var clientName = isSmd
                ? MonthlyFlowStore.ResolveSmdClientName(row.CustomerName, row.ProjectCode, row.SubcontractorName)
                : null;
            var cleanName = isSmd
                ? clientName
                : SubcontractorNormalizer.CleanSubcontractorDisplayName(row.SubcontractorName);
            var projectCode = isSmd
                ? MonthlyFlowStore.ProjectCodeForSmdRow(row.ProjectCode, row.ObjectNumber)
                : row.ProjectCode;
            return new MonthlyMoneyFlowRow
            {
                Year = batch.Year,
                Month = batch.Month,
                SourceRow = row.SourceRow,
                ProjectCode = projectCode,
                ProjectName = row.ProjectName,
                ObjectNumber = row.ObjectNumber,
                SubcontractorName = isSmd ? null : cleanName,
                CustomerName = clientName,
                RowType = isSmd ? MonthlyFlowStore.ClientMonthlyValueRowType : MonthlyFlowStore.SubcontractorInvoiceRowType,
                ObjectName = row.ObjectName,
                AmountWithoutVat = row.AmountWithoutVat,
                IndexedAmount = row.IndexedAmount,
                Responsible = row.Responsible,
                Engineer = row.Engineer,
                SourceSheet = row.SourceSheet,
                RowKey = MonthlyFlowStore.CreateRowKey(
                    batch.Year,
                    batch.Month,
                    row.SourceSheet,
                    row.SourceRow,
                    projectCode,
                    row.ObjectNumber,
                    cleanName),
                LastImportBatchId = batch.Id
            };
        }).ToList();

        var warnings = import.Warnings
            .Select(warning => new ImportWarning
            {
                ImportBatchId = batch.Id,
                SourceRow = warning.SourceRow,
                Message = warning.Message
            })
            .ToList();

        batch.ContentHash = MonthlyFlowContentHasher.Compute(batch.Year, batch.Month, rows, warnings);

        var saveResult = await _store.SaveImportAsync(batch, rows, warnings, cancellationToken);
        if (saveResult.Status == SaveImportStatus.Duplicate && saveResult.ExistingBatch is not null)
        {
            return DuplicateResponse(saveResult.ExistingBatch, batch.ContentHash);
        }

        return ServiceResult<MonthlyFlowImportResponse>.Success(new MonthlyFlowImportResponse
        {
            Imported = true,
            SourceFileName = batch.SourceFileName,
            ContentHash = batch.ContentHash,
            ImportedAt = batch.ImportedAt,
            Year = batch.Year,
            Month = batch.Month,
            RowsImported = batch.RowsInserted + batch.RowsUpdated + batch.RowsSkipped,
            RowsReceived = batch.RowsReceived,
            RowsInserted = batch.RowsInserted,
            RowsUpdated = batch.RowsUpdated,
            RowsSkipped = batch.RowsSkipped,
            WarningsImported = batch.WarningsCount,
            WarningsCount = batch.WarningsCount
        });
    }

    private static ServiceResult<MonthlyFlowImportResponse> DuplicateResponse(
        ImportBatch existingBatch,
        string contentHash)
    {
        return ServiceResult<MonthlyFlowImportResponse>.Success(new MonthlyFlowImportResponse
        {
            Imported = false,
            Reason = "Duplicate import",
            SourceFileName = existingBatch.SourceFileName,
            ContentHash = contentHash,
            ImportedAt = existingBatch.ImportedAt,
            Year = existingBatch.Year,
            Month = existingBatch.Month,
            RowsImported = 0,
            RowsReceived = 0,
            RowsInserted = 0,
            RowsUpdated = 0,
            RowsSkipped = 0,
            WarningsImported = 0,
            WarningsCount = 0
        });
    }

    private static ServiceResult<ParsedImport> Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            return ServiceResult<ParsedImport>.Failure($"Invalid JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ServiceResult<ParsedImport>.Failure("Request JSON must be an object.");
            }

            var schemaVersion = ReadString(root, "schemaVersion");
            if (schemaVersion is null || !SupportedSchemaVersions.Contains(schemaVersion))
            {
                return ServiceResult<ParsedImport>.Failure("schemaVersion must be one of: '1.3', '1.4'.");
            }

            if (!TryReadInt(root, "year", out var year))
            {
                return ServiceResult<ParsedImport>.Failure("year is required and must be a number.");
            }

            if (year < 2000 || year > 2100)
            {
                return ServiceResult<ParsedImport>.Failure("year must be between 2000 and 2100.");
            }

            if (!TryReadInt(root, "month", out var month))
            {
                return ServiceResult<ParsedImport>.Failure("month is required and must be a number.");
            }

            if (month < 1 || month > 12)
            {
                return ServiceResult<ParsedImport>.Failure("month must be between 1 and 12.");
            }

            if (!root.TryGetProperty("rows", out var rowsElement) || rowsElement.ValueKind != JsonValueKind.Array)
            {
                return ServiceResult<ParsedImport>.Failure("rows array is required.");
            }

            var warnings = new List<ParsedWarning>();
            var rows = ParseRows(rowsElement, schemaVersion, warnings);
            AddSubmittedWarnings(root, warnings);

            DateTimeOffset? exportedAt = null;
            if (root.TryGetProperty("exportedAt", out var exportedAtElement)
                && exportedAtElement.ValueKind != JsonValueKind.Null
                && !TryReadDateTimeOffset(exportedAtElement, out exportedAt))
            {
                warnings.Add(new ParsedWarning(null, "exportedAt must be an ISO date/time. Value was ignored."));
            }

            return ServiceResult<ParsedImport>.Success(new ParsedImport(
                schemaVersion,
                year,
                month,
                ReadString(root, "sheetName"),
                exportedAt,
                rows,
                warnings));
        }
    }

    private static List<ParsedRow> ParseRows(
        JsonElement rowsElement,
        string schemaVersion,
        List<ParsedWarning> warnings)
    {
        var rows = new List<ParsedRow>();
        var index = 0;
        string? currentSmdClientName = null;

        foreach (var rowElement in rowsElement.EnumerateArray())
        {
            var rowName = $"rows[{index}]";
            index++;

            if (rowElement.ValueKind != JsonValueKind.Object)
            {
                warnings.Add(new ParsedWarning(null, $"{rowName} must be an object. Row was skipped."));
                continue;
            }

            if (!TryReadPositiveInt(rowElement, "sourceRow", out var sourceRow))
            {
                warnings.Add(new ParsedWarning(null, $"{rowName}.sourceRow must be a positive number. Row was skipped."));
                continue;
            }

            var sourceSheet = ReadString(rowElement, "sourceSheet");
            var isSmd = MonthlyFlowStore.IsSmdSheet(sourceSheet);
            if (schemaVersion == "1.4" && string.IsNullOrWhiteSpace(sourceSheet))
            {
                warnings.Add(new ParsedWarning(sourceRow, $"{rowName}.sourceSheet is required for schemaVersion 1.4. Row was skipped."));
                continue;
            }

            var explicitCustomerName = ReadFirstString(rowElement, "clientName", "customerName", "uzsakovas", "uzsakovasName");
            var rawSubcontractorName = ReadString(rowElement, "subcontractorName");
            if (isSmd)
            {
                var rowClientName = MonthlyFlowStore.ResolveSmdClientName(explicitCustomerName, null, rawSubcontractorName);
                if (!string.IsNullOrWhiteSpace(rowClientName))
                {
                    currentSmdClientName = rowClientName;
                }
            }

            var projectCode = ReadString(rowElement, "projectCode");
            if (string.IsNullOrWhiteSpace(projectCode))
            {
                warnings.Add(new ParsedWarning(sourceRow, $"{rowName}.projectCode is required. Row was skipped."));
                continue;
            }

            if (!TryReadDecimal(rowElement, "amountWithoutVat", out var amountWithoutVat))
            {
                if (isSmd)
                {
                    continue;
                }

                warnings.Add(new ParsedWarning(sourceRow, $"{rowName}.amountWithoutVat is required and must be a number. Row was skipped."));
                continue;
            }

            decimal? indexedAmount = null;
            if (rowElement.TryGetProperty("indexedAmount", out var indexedAmountElement)
                && indexedAmountElement.ValueKind != JsonValueKind.Null)
            {
                if (TryReadDecimal(indexedAmountElement, out var parsedIndexedAmount))
                {
                    indexedAmount = parsedIndexedAmount;
                }
                else
                {
                    warnings.Add(new ParsedWarning(sourceRow, $"{rowName}.indexedAmount must be a number. Value was ignored."));
                }
            }

            rows.Add(new ParsedRow(
                sourceRow,
                projectCode.Trim(),
                ReadString(rowElement, "projectName"),
                isSmd ? currentSmdClientName : explicitCustomerName,
                rawSubcontractorName,
                ReadString(rowElement, "objectNumber"),
                ReadString(rowElement, "objectName"),
                amountWithoutVat,
                indexedAmount,
                ReadString(rowElement, "responsible"),
                ReadString(rowElement, "engineer"),
                sourceSheet));
        }

        return rows;
    }

    private static void AddSubmittedWarnings(JsonElement root, List<ParsedWarning> warnings)
    {
        if (!root.TryGetProperty("warnings", out var warningsElement) || warningsElement.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (warningsElement.ValueKind != JsonValueKind.Array)
        {
            warnings.Add(new ParsedWarning(null, "warnings must be an array. Submitted warnings were ignored."));
            return;
        }

        var index = 0;
        foreach (var warningElement in warningsElement.EnumerateArray())
        {
            var warningName = $"warnings[{index}]";
            index++;

            if (warningElement.ValueKind != JsonValueKind.Object)
            {
                warnings.Add(new ParsedWarning(null, $"{warningName} must be an object. Submitted warning was ignored."));
                continue;
            }

            var message = ReadString(warningElement, "message");
            if (string.IsNullOrWhiteSpace(message))
            {
                warnings.Add(new ParsedWarning(ReadNullableInt(warningElement, "sourceRow"), $"{warningName}.message is required. Submitted warning was ignored."));
                continue;
            }

            warnings.Add(new ParsedWarning(ReadNullableInt(warningElement, "sourceRow"), message.Trim()));
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? NullIfWhiteSpace(value.GetString()) : null;
    }

    private static string? ReadFirstString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = ReadString(element, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static int? ReadNullableInt(JsonElement element, string propertyName)
    {
        return TryReadInt(element, propertyName, out var value) ? value : null;
    }

    private static bool TryReadPositiveInt(JsonElement element, string propertyName, out int value)
    {
        return TryReadInt(element, propertyName, out value) && value > 0;
    }

    private static bool TryReadInt(JsonElement element, string propertyName, out int value)
    {
        value = default;
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }

    private static bool TryReadDecimal(JsonElement element, string propertyName, out decimal value)
    {
        value = default;
        return element.TryGetProperty(propertyName, out var property) && TryReadDecimal(property, out value);
    }

    private static bool TryReadDecimal(JsonElement element, out decimal value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value);
    }

    private static bool TryReadDateTimeOffset(JsonElement element, out DateTimeOffset? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        // Invariant culture + AssumeUniversal so the same export parses
        // identically on a dev laptop and on the Windows service regardless of
        // host locale; offset-less timestamps are treated as UTC.
        if (!DateTimeOffset.TryParse(
                element.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed record ParsedImport(
        string SchemaVersion,
        int Year,
        int Month,
        string? SheetName,
        DateTimeOffset? ExportedAt,
        List<ParsedRow> Rows,
        List<ParsedWarning> Warnings);

    private sealed record ParsedRow(
        int SourceRow,
        string ProjectCode,
        string? ProjectName,
        string? CustomerName,
        string? SubcontractorName,
        string? ObjectNumber,
        string? ObjectName,
        decimal AmountWithoutVat,
        decimal? IndexedAmount,
        string? Responsible,
        string? Engineer,
        string? SourceSheet);

    private sealed record ParsedWarning(int? SourceRow, string Message);
}
