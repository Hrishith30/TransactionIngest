using Microsoft.EntityFrameworkCore;
using TransactionIngest.Models;

namespace TransactionIngest.Data;

/// <summary>
/// EF Core DbContext for the transaction ingestion database.
/// Uses Fluent API to configure column types, constraints, and indexes
/// that can't be expressed cleanly with data annotations alone.
/// </summary>
public sealed class AppDbContext : DbContext
{
    /// <summary>The main transactions table.</summary>
    public DbSet<Transaction> Transactions => Set<Transaction>();

    /// <summary>Append-only audit log — one row per field change or lifecycle event.</summary>
    public DbSet<TransactionAuditLog> AuditLogs => Set<TransactionAuditLog>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Transaction ──────────────────────────────────────────────────────
        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.HasKey(t => t.Id);

            // Unique constraint on the upstream key — prevents duplicate rows
            // and makes upsert lookups fast.
            entity.HasIndex(t => t.TransactionId)
                  .IsUnique()
                  .HasDatabaseName("IX_Transactions_TransactionId");

            entity.Property(t => t.TransactionId).IsRequired();
            entity.Property(t => t.CardNumberHash).HasMaxLength(64).IsRequired();
            entity.Property(t => t.CardLast4).HasMaxLength(4).IsRequired();
            entity.Property(t => t.LocationCode).HasMaxLength(20).IsRequired();
            entity.Property(t => t.ProductName).HasMaxLength(20).IsRequired();

            // Explicit precision for money — matches the spec.
            entity.Property(t => t.Amount).HasColumnType("decimal(18,2)");

            // Store status as a string ("Active" not "0") for readability in the DB.
            entity.Property(t => t.Status).HasConversion<string>();
        });

        // ── TransactionAuditLog ──────────────────────────────────────────────
        modelBuilder.Entity<TransactionAuditLog>(entity =>
        {
            entity.HasKey(al => al.Id);

            entity.Property(al => al.TransactionId).IsRequired();
            entity.Property(al => al.FieldName).HasMaxLength(50);
            entity.Property(al => al.OldValue).HasMaxLength(256);
            entity.Property(al => al.NewValue).HasMaxLength(256);

            // Store change type as a string ("Insert" not "0") for readability.
            entity.Property(al => al.ChangeType).HasConversion<string>();

            // Cascade delete keeps audit rows in sync if a transaction is removed.
            entity.HasOne(al => al.TransactionRecord)
                  .WithMany(t => t.AuditLogs)
                  .HasForeignKey(al => al.TransactionFk)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(al => al.TransactionId)
                  .HasDatabaseName("IX_AuditLogs_TransactionId");
        });
    }
}
