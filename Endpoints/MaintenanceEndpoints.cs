using PADS.MoneyFlow.Api.Persistence;

namespace PADS.MoneyFlow.Api.Endpoints;

internal static class MaintenanceEndpoints
{
    private const int BackupsToKeep = 30;

    public static void MapMaintenanceEndpoints(this WebApplication app, string databasePath)
    {
        // Creates a transactionally consistent snapshot of the live database via
        // SQLite's backup API, so callers never need to stop the service (or hold
        // administrator rights) to take a safe backup. Gated by the same API key
        // as the import endpoints (see Program.cs).
        app.MapPost("/api/maintenance/db-backup", async (
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            string? backupPath = null;
            try
            {
                var backupsDirectory = Path.Combine(
                    Path.GetDirectoryName(databasePath) ?? AppContext.BaseDirectory,
                    "Backups");
                Directory.CreateDirectory(backupsDirectory);

                var baseName = $"monthly-money-flow-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
                backupPath = Path.Combine(backupsDirectory, baseName + ".db");
                for (var attempt = 2; File.Exists(backupPath); attempt++)
                {
                    backupPath = Path.Combine(backupsDirectory, $"{baseName}-{attempt}.db");
                }

                await SqliteDatabaseVerifier.CreateConsistentBackupAsync(
                    databasePath,
                    backupPath,
                    cancellationToken);

                var prunedCount = 0;
                var oldBackups = new DirectoryInfo(backupsDirectory)
                    .GetFiles("monthly-money-flow-backup-*.db")
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .Skip(BackupsToKeep);
                foreach (var oldBackup in oldBackups)
                {
                    try
                    {
                        foreach (var suffix in new[] { "-wal", "-shm" })
                        {
                            var sidecar = new FileInfo(oldBackup.FullName + suffix);
                            if (sidecar.Exists)
                            {
                                sidecar.Delete();
                            }
                        }

                        oldBackup.Delete();
                        prunedCount++;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // An old copy owned by another account (e.g. created manually
                        // by an administrator) may resist deletion; skip it rather
                        // than fail the backup.
                        logger.LogWarning(exception, "Could not prune old backup {Backup}.", oldBackup.FullName);
                    }
                }

                var sizeBytes = new FileInfo(backupPath).Length;
                logger.LogInformation(
                    "Database backup created at {BackupPath} ({SizeBytes} bytes), pruned {PrunedCount} old copies.",
                    backupPath, sizeBytes, prunedCount);

                return Results.Ok(new
                {
                    backedUp = true,
                    fileName = Path.GetFileName(backupPath),
                    fullPath = backupPath,
                    sizeBytes,
                    prunedCount
                });
            }
            catch (Exception exception)
            {
                if (backupPath is not null)
                {
                    try
                    {
                        File.Delete(backupPath);
                    }
                    catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
                    {
                        logger.LogWarning(cleanupException, "Could not remove failed backup {BackupPath}.", backupPath);
                    }
                }
                logger.LogError(exception, "Database backup failed.");

                return Results.Problem(
                    title: "Database backup failed",
                    detail: "The backup failed unexpectedly. Check the server logs for details.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }
}
