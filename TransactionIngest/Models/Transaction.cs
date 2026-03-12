using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TransactionIngest.Models;

/// <summary>A transaction record in our database.</summary>
public sealed class Transaction
{
    /// <summary>Internal database ID.</summary>
    [Key]
    public int Id { get; set; }

    /// <summary>ID from the upstream system used for matching.</summary>
    [Required]
    public int TransactionId { get; set; }

    /// <summary>Hashed card number for change detection without storing raw data.</summary>
    [Required]
    [MaxLength(64)]
    public string CardNumberHash { get; set; } = string.Empty;

    /// <summary>Last 4 digits for display/reconciliation.</summary>
    [Required]
    [MaxLength(4)]
    public string CardLast4 { get; set; } = string.Empty;

    /// <summary>Where the transaction happened.</summary>
    [Required]
    [MaxLength(20)]
    public string LocationCode { get; set; } = string.Empty;

    /// <summary>Item or service purchased.</summary>
    [Required]
    [MaxLength(20)]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>Amount of the transaction.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    /// <summary>When the sale happened (UTC).</summary>
    public DateTime TransactionTime { get; set; }

    /// <summary>Current state in our system.</summary>
    public TransactionStatus Status { get; set; } = TransactionStatus.Active;

    /// <summary>When this row was first created in our database.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When this row was last modified.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>All audit entries related to this transaction.</summary>
    public ICollection<TransactionAuditLog> AuditLogs { get; set; } = [];
}
