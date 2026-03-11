using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TransactionIngest.Models;

/// <summary>
/// A card transaction as stored in the local database.
/// TransactionId is the upstream business key used for all upserts and lookups.
/// </summary>
public sealed class Transaction
{
    /// <summary>Auto-increment surrogate PK — internal to this database.</summary>
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Upstream transaction identifier (e.g. "T-1001").
    /// Stable across re-deliveries; used as the upsert key.
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string TransactionId { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 hash of the raw card number.
    /// We never store the PAN itself — the hash is enough for change detection.
    /// </summary>
    [Required]
    [MaxLength(64)]
    public string CardNumberHash { get; set; } = string.Empty;

    /// <summary>Last four digits of the card — kept for display and reconciliation.</summary>
    [Required]
    [MaxLength(4)]
    public string CardLast4 { get; set; } = string.Empty;

    /// <summary>Store or terminal location (e.g. "STO-01").</summary>
    [Required]
    [MaxLength(20)]
    public string LocationCode { get; set; } = string.Empty;

    /// <summary>Product or service purchased.</summary>
    [Required]
    [MaxLength(20)]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>Transaction amount — precision matches the API contract: decimal(18,2).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    /// <summary>When the transaction happened at the point of sale (always UTC).</summary>
    public DateTime TransactionTime { get; set; }

    /// <summary>Current lifecycle state. Starts as Active; can become Revoked or Finalized.</summary>
    public TransactionStatus Status { get; set; } = TransactionStatus.Active;

    /// <summary>When this row was first created in our database.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When this row was last modified.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>All audit entries related to this transaction.</summary>
    public ICollection<TransactionAuditLog> AuditLogs { get; set; } = [];
}
