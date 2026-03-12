using Microsoft.EntityFrameworkCore;
using TransactionIngest.Models;

namespace TransactionIngest.Data;

/// <summary>Database context for transactions and audit logs.</summary>
public sealed class AppDbContext : DbContext
{
    /// <summary>The main transactions table.</summary>
    public DbSet<Transaction> Transactions => Set<Transaction>();

    /// <summary>Audit history for all transaction changes.</summary>
    public DbSet<TransactionAuditLog> AuditLogs => Set<TransactionAuditLog>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Transaction ──────────────────────────────────────────────────────
        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.HasKey(t => t.Id);

            // Prevent duplicates and keep lookups fast using the upstream ID.
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

            // Store status as a string for better readability in the DB.
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

            // Store change type as descriptive strings.
            entity.Property(al => al.ChangeType).HasConversion<string>();

            // Clean up logs if a transaction is deleted.
            entity.HasOne(al => al.TransactionRecord)
                  .WithMany(t => t.AuditLogs)
                  .HasForeignKey(al => al.TransactionFk)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(al => al.TransactionId)
                  .HasDatabaseName("IX_AuditLogs_TransactionId");
        });
    }
}
