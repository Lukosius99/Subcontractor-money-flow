using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PADS.MoneyFlow.Api.Models;

namespace PADS.MoneyFlow.Api.Services;

internal static class MonthlyFlowContentHasher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Compute(
        int year,
        int month,
        IEnumerable<MonthlyMoneyFlowRow> rows,
        IEnumerable<ImportWarning> warnings)
    {
        var fingerprint = new
        {
            year,
            month,
            rows = rows
                .OrderBy(row => row.SourceSheet, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.SourceRow)
                .ThenBy(row => row.ProjectCode, StringComparer.OrdinalIgnoreCase)
                .Select(row => new
                {
                    SourceSheet = Normalize(row.SourceSheet),
                    row.SourceRow,
                    ProjectCode = Normalize(row.ProjectCode),
                    ProjectName = Normalize(row.ProjectName),
                    ObjectNumber = Normalize(row.ObjectNumber),
                    SubcontractorName = Normalize(row.SubcontractorName),
                    CustomerName = Normalize(row.CustomerName),
                    RowType = Normalize(row.RowType),
                    ObjectName = Normalize(row.ObjectName),
                    row.AmountWithoutVat,
                    row.IndexedAmount,
                    Responsible = Normalize(row.Responsible),
                    Engineer = Normalize(row.Engineer)
                }),
            warnings = warnings
                .OrderBy(warning => warning.SourceRow)
                .ThenBy(warning => warning.Message, StringComparer.OrdinalIgnoreCase)
                .Select(warning => new
                {
                    warning.SourceRow,
                    Message = Normalize(warning.Message)
                })
        };

        var canonicalJson = JsonSerializer.Serialize(fingerprint, JsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
