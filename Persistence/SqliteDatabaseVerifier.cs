using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace PADS.MoneyFlow.Api.Persistence;

internal static class SqliteDatabaseVerifier
{
    public static async Task VerifyIntegrityAsync(string databasePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Database path is required.", nameof(databasePath));
        }

        var fullPath = Path.GetFullPath(databasePath);
        var file = new FileInfo(fullPath);
        if (!file.Exists || file.Length == 0)
        {
            throw new InvalidDataException($"SQLite database does not exist or is empty: {fullPath}");
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(Convert.ToString(result), "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"SQLite integrity check failed: {result ?? "no result"}");
        }
    }

    public static async Task VerifyReadinessAsync(
        IDbContextFactory<MoneyFlowDbContext> dbFactory,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check(1);";

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(Convert.ToString(result), "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"SQLite readiness check failed: {result ?? "no result"}");
        }
    }
}
