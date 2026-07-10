using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using PADS.MoneyFlow.Api.Persistence;
using PADS.MoneyFlow.Api.Services;

namespace PADS.MoneyFlow.Api.Endpoints;

internal static class MaintenanceEndpoints
{
    private const int BackupsToKeep = 30;
    private const int RestoreBackupsToKeep = 10;
    private const long MaxRestoreBytes = 10 * 1024 * 1024;

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
                var backupsDirectory = GetBackupsDirectory(databasePath);
                Directory.CreateDirectory(backupsDirectory);

                var baseName = $"monthly-money-flow-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
                backupPath = GetAvailablePath(backupsDirectory, baseName, ".db");

                await SqliteDatabaseVerifier.CreateConsistentBackupAsync(
                    databasePath,
                    backupPath,
                    cancellationToken);

                var prunedCount = PruneBackups(
                    backupsDirectory,
                    "monthly-money-flow-backup-*.db",
                    BackupsToKeep,
                    logger);
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
                TryDelete(backupPath, logger);
                logger.LogError(exception, "Database backup failed.");

                return Results.Problem(
                    title: "Database backup failed",
                    detail: "The backup failed unexpectedly. Check the server logs for details.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // The service owns the live DB and performs the restore itself. No Windows
        // administrator token or service restart is required. The maintenance gate
        // drains current API work and rejects new DB requests for the short restore
        // window. A consistent rollback copy is always created first.
        app.MapPost("/api/maintenance/db-restore", async (
            HttpRequest request,
            DatabaseMaintenanceGate maintenanceGate,
            BackupPassphraseProvider passphraseProvider,
            IDbContextFactory<MoneyFlowDbContext> dbFactory,
            MonthlyFlowStore store,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            string? uploadedPath = null;
            string? decryptedPath = null;
            string? rollbackPath = null;
            try
            {
                if (!request.HasFormContentType)
                {
                    return Results.BadRequest(new { error = "multipart/form-data with a backup file is required." });
                }

                var form = await request.ReadFormAsync(cancellationToken);
                var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
                if (file is null || file.Length <= 0)
                {
                    return Results.BadRequest(new { error = "A non-empty backup file is required." });
                }
                if (file.Length > MaxRestoreBytes)
                {
                    return Results.BadRequest(new { error = "The backup file exceeds the 10 MB limit." });
                }

                var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (extension is not ".mfbackup" and not ".db")
                {
                    return Results.BadRequest(new { error = "Only .mfbackup and standalone .db files are supported." });
                }

                var databaseDirectory = Path.GetDirectoryName(databasePath) ?? AppContext.BaseDirectory;
                var workingDirectory = Path.Combine(databaseDirectory, ".restore-temp");
                Directory.CreateDirectory(workingDirectory);
                uploadedPath = Path.Combine(workingDirectory, $"upload-{Guid.NewGuid():N}{extension}");
                await using (var output = new FileStream(
                    uploadedPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true))
                {
                    await file.CopyToAsync(output, cancellationToken);
                }

                var restoreSource = uploadedPath;
                if (extension == ".mfbackup")
                {
                    var passphrase = passphraseProvider.GetPassphrase();
                    if (string.IsNullOrWhiteSpace(passphrase))
                    {
                        return Results.Json(
                            new { error = "Backup passphrase is not configured on the server." },
                            statusCode: StatusCodes.Status503ServiceUnavailable);
                    }

                    decryptedPath = Path.Combine(workingDirectory, $"decrypted-{Guid.NewGuid():N}.db");
                    try
                    {
                        await BackupFileCrypto.DecryptAsync(
                            uploadedPath,
                            decryptedPath,
                            passphrase,
                            cancellationToken);
                    }
                    finally
                    {
                        passphrase = null;
                    }
                    restoreSource = decryptedPath;
                }

                try
                {
                    await SqliteDatabaseVerifier.VerifyIntegrityAsync(restoreSource, cancellationToken);
                }
                catch (Exception exception) when (exception is InvalidDataException or SqliteException)
                {
                    logger.LogWarning(exception, "Rejected a backup that is not a valid SQLite database.");
                    return Results.BadRequest(new { error = "The uploaded backup is not a valid SQLite database." });
                }

                var maintenanceLease = await maintenanceGate.TryBeginMaintenanceAsync(cancellationToken);
                if (maintenanceLease is null)
                {
                    return Results.Conflict(new { error = "Another database restore is already in progress." });
                }

                await using (maintenanceLease)
                {
                    // Once the live DB operation begins, finish or roll back even if
                    // the HTTP client disconnects.
                    var operationToken = CancellationToken.None;
                    var backupsDirectory = GetBackupsDirectory(databasePath);
                    Directory.CreateDirectory(backupsDirectory);
                    rollbackPath = GetAvailablePath(
                        backupsDirectory,
                        $"pre-restore-{DateTime.Now:yyyyMMdd-HHmmss}",
                        ".db");

                    await SqliteDatabaseVerifier.CreateConsistentBackupAsync(
                        databasePath,
                        rollbackPath,
                        operationToken);

                    try
                    {
                        await SqliteDatabaseVerifier.RestoreConsistentBackupAsync(
                            restoreSource,
                            databasePath,
                            operationToken);
                        await store.InitializeAsync(operationToken);
                        await SqliteDatabaseVerifier.VerifyReadinessAsync(dbFactory, operationToken);
                    }
                    catch (Exception restoreException)
                    {
                        logger.LogError(restoreException, "Database restore failed; applying rollback copy.");
                        try
                        {
                            await SqliteDatabaseVerifier.RestoreConsistentBackupAsync(
                                rollbackPath,
                                databasePath,
                                operationToken);
                            await store.InitializeAsync(operationToken);
                            await SqliteDatabaseVerifier.VerifyReadinessAsync(dbFactory, operationToken);
                        }
                        catch (Exception rollbackException)
                        {
                            logger.LogCritical(
                                rollbackException,
                                "Database rollback failed after an unsuccessful online restore.");
                            throw new AggregateException(
                                "Database restore and automatic rollback both failed.",
                                restoreException,
                                rollbackException);
                        }

                        throw;
                    }

                    var prunedCount = PruneBackups(
                        backupsDirectory,
                        "pre-restore-*.db",
                        RestoreBackupsToKeep,
                        logger);
                    logger.LogInformation(
                        "Database restored online from {FileName}; rollback copy {RollbackPath}; pruned {PrunedCount} old restore copies.",
                        Path.GetFileName(file.FileName), rollbackPath, prunedCount);

                    return Results.Ok(new
                    {
                        restored = true,
                        fileName = Path.GetFileName(file.FileName),
                        rollbackFileName = Path.GetFileName(rollbackPath),
                        serviceRestarted = false,
                        prunedCount
                    });
                }
            }
            catch (InvalidDataException exception)
            {
                logger.LogWarning(exception, "Rejected invalid database restore input.");
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (ArgumentException exception)
            {
                logger.LogWarning(exception, "Rejected invalid database restore request.");
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Results.StatusCode(499);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Database restore failed.");
                return Results.Problem(
                    title: "Database restore failed",
                    detail: "The restore failed and the previous database was retained or restored. Check the server logs.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
            finally
            {
                TryDelete(decryptedPath, logger);
                TryDelete(uploadedPath, logger);
            }
        });
    }

    private static string GetBackupsDirectory(string databasePath) => Path.Combine(
        Path.GetDirectoryName(databasePath) ?? AppContext.BaseDirectory,
        "Backups");

    private static string GetAvailablePath(
        string directory,
        string baseName,
        string extension)
    {
        var path = Path.Combine(directory, baseName + extension);
        for (var attempt = 2; File.Exists(path); attempt++)
        {
            path = Path.Combine(directory, $"{baseName}-{attempt}{extension}");
        }
        return path;
    }

    private static int PruneBackups(
        string directory,
        string searchPattern,
        int copiesToKeep,
        ILogger logger)
    {
        var prunedCount = 0;
        var oldBackups = new DirectoryInfo(directory)
            .GetFiles(searchPattern)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(copiesToKeep);
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
                logger.LogWarning(exception, "Could not prune old backup {BackupPath}.", oldBackup.FullName);
            }
        }
        return prunedCount;
    }

    private static void TryDelete(string? path, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not remove temporary database file {DatabasePath}.", path);
        }
    }
}
