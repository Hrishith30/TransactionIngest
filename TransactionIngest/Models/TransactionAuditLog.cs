using System.ComponentModel.DataAnnotations;

namespace TransactionIngest.Models;

/// <summary>Tracks every change to a transaction for audit purposes.</summary>
public sealed class TransactionAuditLog
{
    /// <summary>Local audit row ID.</summary>
    [Key]
    public int Id { get; set; }

    /// <summary>The upstream ID this log relates to.</summary>
    [Required]
    public int TransactionId { get; set; }

    /// <summary>When the change was processed.</summary>
    public DateTime ChangedAt { get; set; }

    /// <summary>What kind of change this was — Insert, Update, Revoked, or Finalized.</summary>
    public ChangeType ChangeType { get; set; }

    /// <summary>Name of the changed field (for updates).</summary>
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
