using System.ComponentModel.DataAnnotations;

namespace TransactionIngest.Models;

/// <summary>
/// One row per change to a transaction — inserted on every Insert, Update, Revoke, or Finalize event.
/// Never updated after creation; this table is the authoritative history of what changed and when.
/// </summary>
public sealed class TransactionAuditLog
{
    /// <summary>Auto-increment surrogate PK.</summary>
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The upstream TransactionId this entry relates to.
    /// Stored as a string (not just a FK) so the audit trail is readable even if the
    /// parent transaction row is later purged.
    /// </summary>
    [Required]
    public int TransactionId { get; set; }

    /// <summary>UTC timestamp of the ingestion run that produced this entry.</summary>
    public DateTime ChangedAt { get; set; }

    /// <summary>What kind of change this was — Insert, Update, Revoked, or Finalized.</summary>
    public ChangeType ChangeType { get; set; }

    /// <summary>
    /// Which field changed. Only populated for Update entries.
    /// Null for Insert / Revoked / Finalized, where no single field diff applies.
    /// </summary>
    [MaxLength(50)]
    public string? FieldName { get; set; }

    /// <summary>The field's value before the change. Null for Insert entries.</summary>
    [MaxLength(256)]
    public string? OldValue { get; set; }

    /// <summary>The field's value after the change. Null for Revoked / Finalized entries.</summary>
    [MaxLength(256)]
    public string? NewValue { get; set; }

    /// <summary>FK to the parent Transaction row.</summary>
    public int TransactionFk { get; set; }

    /// <summary>Navigation property back to the parent transaction.</summary>
    public Transaction? TransactionRecord { get; set; }
}
