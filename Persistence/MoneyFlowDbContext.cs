using Microsoft.EntityFrameworkCore;
using PADS.MoneyFlow.Api.Models;

namespace PADS.MoneyFlow.Api.Persistence;

public sealed class MoneyFlowDbContext : DbContext
{
    public MoneyFlowDbContext(DbContextOptions<MoneyFlowDbContext> options)
        : base(options)
    {
    }

    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<MonthlyMoneyFlowRow> MonthlyFlowRows => Set<MonthlyMoneyFlowRow>();
    public DbSet<ImportWarning> ImportWarnings => Set<ImportWarning>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<SubcontractorContract> SubcontractorContracts => Set<SubcontractorContract>();
    public DbSet<ProjectObjectValue> ProjectObjectValues => Set<ProjectObjectValue>();
    public DbSet<Subcontractor> Subcontractors => Set<Subcontractor>();
    public DbSet<SubcontractorAlias> SubcontractorAliases => Set<SubcontractorAlias>();
    public DbSet<ManualContractLink> ManualContractLinks => Set<ManualContractLink>();
    public DbSet<ManualObjectAssignment> ManualObjectAssignments => Set<ManualObjectAssignment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ImportBatch>(entity =>
        {
            entity.ToTable("ImportBatches");
            entity.HasKey(batch => batch.Id);
            entity.HasIndex(batch => batch.ContentHash).IsUnique();
            entity.Property(batch => batch.SourceFileName).IsRequired();
            entity.Property(batch => batch.ContentHash).IsRequired();
            entity.Property(batch => batch.Status).IsRequired();
        });

        modelBuilder.Entity<MonthlyMoneyFlowRow>(entity =>
        {
            entity.ToTable("MonthlyFlowRows");
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => row.RowKey).IsUnique();
            entity.HasIndex(row => row.ProjectCode);
            entity.Property(row => row.ProjectCode).IsRequired();
            entity.Property(row => row.RowKey).IsRequired();
            entity.Property(row => row.SourceSheet);
            entity.Property(row => row.RowType).IsRequired();
            entity.Property(row => row.AmountWithoutVat).HasColumnType("decimal(18,2)");
            entity.Property(row => row.IndexedAmount).HasColumnType("decimal(18,2)");
            entity.Property(row => row.IsExcludedFromTotals).HasDefaultValue(false);
            entity.HasOne<ImportBatch>()
                .WithMany()
                .HasForeignKey(row => row.LastImportBatchId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ImportWarning>(entity =>
        {
            entity.ToTable("ImportWarnings");
            entity.HasKey(warning => warning.Id);
            entity.Property(warning => warning.Message).IsRequired();
            entity.HasOne<ImportBatch>()
                .WithMany()
                .HasForeignKey(warning => warning.ImportBatchId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Project>(entity =>
        {
            entity.ToTable("Projects");
            entity.HasKey(project => project.Id);
            entity.HasIndex(project => project.ProjectCode).IsUnique();
            entity.Property(project => project.ProjectCode).IsRequired();
            entity.Property(project => project.IsActiveContractedProject).HasDefaultValue(false);
            entity.Property(project => project.IsInLatestContractedImport).HasDefaultValue(false);
        });

        modelBuilder.Entity<SubcontractorContract>(entity =>
        {
            entity.ToTable("SubcontractorContracts");
            entity.HasKey(contract => contract.Id);
            entity.HasIndex(contract => contract.ProjectCode);
            entity.HasIndex(contract => contract.RowKey).IsUnique();
            entity.Property(contract => contract.ProjectCode).IsRequired();
            entity.Property(contract => contract.ObjectNumber).IsRequired();
            entity.Property(contract => contract.SubcontractorName).IsRequired();
            entity.Property(contract => contract.SourceSystem).IsRequired();
            entity.Property(contract => contract.RowKey).IsRequired();
            entity.Property(contract => contract.ContractedAmount).HasColumnType("decimal(18,2)");
            entity.Property(contract => contract.IsActiveContractedProject).HasDefaultValue(false);
            entity.Property(contract => contract.IsInLatestContractedImport).HasDefaultValue(false);
        });

        modelBuilder.Entity<ProjectObjectValue>(entity =>
        {
            entity.ToTable("ProjectObjectValues");
            entity.HasKey(value => value.Id);
            entity.HasIndex(value => value.RowKey).IsUnique();
            entity.HasIndex(value => value.ProjectCode);
            entity.Property(value => value.ProjectCode).IsRequired();
            entity.Property(value => value.ObjectNumber).IsRequired();
            entity.Property(value => value.RowKey).IsRequired();
            entity.Property(value => value.ProjectValueAmount).HasColumnType("decimal(18,2)");
        });

        modelBuilder.Entity<Subcontractor>(entity =>
        {
            entity.ToTable("Subcontractors");
            entity.HasKey(subcontractor => subcontractor.Id);
            entity.HasIndex(subcontractor => subcontractor.NormalizedKey).IsUnique();
            entity.Property(subcontractor => subcontractor.CanonicalName).IsRequired();
            entity.Property(subcontractor => subcontractor.BaseName).IsRequired();
            entity.Property(subcontractor => subcontractor.NormalizedKey).IsRequired();
        });

        modelBuilder.Entity<SubcontractorAlias>(entity =>
        {
            entity.ToTable("SubcontractorAliases");
            entity.HasKey(alias => alias.Id);
            entity.HasIndex(alias => alias.NormalizedKey);
            entity.HasIndex(alias => alias.SubcontractorId);
            entity.Property(alias => alias.RawName).IsRequired();
            entity.Property(alias => alias.NormalizedKey).IsRequired();
            entity.Property(alias => alias.CanonicalName).IsRequired();
            entity.HasOne<Subcontractor>()
                .WithMany()
                .HasForeignKey(alias => alias.SubcontractorId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ManualContractLink>(entity =>
        {
            entity.ToTable("ManualContractLinks");
            entity.HasKey(link => link.Id);
            entity.HasIndex(link => new { link.ProjectCode, link.ObjectNumber, link.SourceSubcontractorKey }).IsUnique();
            entity.Property(link => link.ProjectCode).IsRequired();
            entity.Property(link => link.ObjectNumber).IsRequired();
            entity.Property(link => link.SourceSubcontractorKey).IsRequired();
            entity.Property(link => link.TargetSubcontractorKey).IsRequired();
            entity.Property(link => link.SourceSubcontractorName).IsRequired();
            entity.Property(link => link.TargetSubcontractorName).IsRequired();
        });

        modelBuilder.Entity<ManualObjectAssignment>(entity =>
        {
            entity.ToTable("ManualObjectAssignments");
            entity.HasKey(assignment => assignment.Id);
            entity.HasIndex(assignment => new { assignment.ProjectCode, assignment.SourceObjectNumber, assignment.SubcontractorKey }).IsUnique();
            entity.Property(assignment => assignment.ProjectCode).IsRequired();
            entity.Property(assignment => assignment.SourceObjectNumber).IsRequired();
            entity.Property(assignment => assignment.SubcontractorKey).IsRequired();
            entity.Property(assignment => assignment.SubcontractorName).IsRequired();
            entity.Property(assignment => assignment.TargetObjectNumber).IsRequired();
        });
    }
}
