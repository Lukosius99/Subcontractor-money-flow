using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace PADS.MoneyFlow.Api.Persistence;

internal static class SqliteDatabaseVerifier
{
    public static async Task CreateConsistentBackupAsync(
        string sourceDatabasePath,
        string backupDatabasePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceDatabasePath))
        {
            throw new ArgumentException("Source database path is required.", nameof(sourceDatabasePath));
        }

        if (string.IsNullOrWhiteSpace(backupDatabasePath))
        {
            throw new ArgumentException("Backup database path is required.", nameof(backupDatabasePath));
        }

        var sourceFullPath = Path.GetFullPath(sourceDatabasePath);
        var backupFullPath = Path.GetFullPath(backupDatabasePath);
        if (string.Equals(sourceFullPath, backupFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Source and backup database paths must be different.", nameof(backupDatabasePath));
        }

        var sourceFile = new FileInfo(sourceFullPath);
        if (!sourceFile.Exists || sourceFile.Length == 0)
        {
            throw new InvalidDataException($"SQLite source database does not exist or is empty: {sourceFullPath}");
        }

        var backupDirectory = Path.GetDirectoryName(backupFullPath);
        if (!string.IsNullOrWhiteSpace(backupDirectory))
        {
            Directory.CreateDirectory(backupDirectory);
        }

        if (File.Exists(backupFullPath))
        {
            throw new IOException($"Backup target already exists: {backupFullPath}");
        }

        try
        {
            var sourceConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = sourceFullPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            var backupConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = backupFullPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            await using var sourceConnection = new SqliteConnection(sourceConnectionString);
            await using var backupConnection = new SqliteConnection(backupConnectionString);
            await sourceConnection.OpenAsync(cancellationToken);
            await backupConnection.OpenAsync(cancellationToken);

            sourceConnection.BackupDatabase(backupConnection);
            await VerifyIntegrityAsync(backupFullPath, cancellationToken);
        }
        catch
        {
            try
            {
                File.Delete(backupFullPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Keep the original backup failure as the visible error.
            }

            throw;
        }
    }

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
