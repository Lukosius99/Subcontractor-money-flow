using Microsoft.EntityFrameworkCore;

namespace PADS.MoneyFlow.Api.Persistence;

// Startup schema creation and forward-compatible column patching for the SQLite
// database. Kept separate from MonthlyFlowStore so the query/import logic is not
// interleaved with raw DDL. All statements are idempotent (CREATE ... IF NOT
// EXISTS / ADD COLUMN guarded by AddColumnIfMissingAsync), so running this on
// every startup — and defensively before reads — is safe.
internal static class SchemaInitializer
{
    public static async Task EnsureMasterDataTablesAsync(
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

        await AddColumnIfMissingAsync(db, "MonthlyFlowRows", "IsExcludedFromTotals", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, "MonthlyFlowRows", "ExcludedAt", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(db, "MonthlyFlowRows", "ExcludedReason", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(db, "MonthlyFlowRows", "ExcludedBy", "TEXT NULL", cancellationToken);

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
                IsExcludedFromTotals INTEGER NOT NULL DEFAULT 0,
                ExcludedAt TEXT NULL,
                ExcludedReason TEXT NULL,
                ExcludedBy TEXT NULL,
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
}
