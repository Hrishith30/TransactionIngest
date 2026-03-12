using System.Text.Json.Serialization;

namespace TransactionIngest.Services;

/// <summary>Data transfer object for API responses.</summary>
public sealed class TransactionDto
{
    [JsonPropertyName("transactionId")]
    public string TransactionId { get; set; } = string.Empty;

    /// <summary>Full PAN (hashed before storage).</summary>
    [JsonPropertyName("cardNumber")]
    public string CardNumber { get; set; } = string.Empty;

    [JsonPropertyName("locationCode")]
    public string LocationCode { get; set; } = string.Empty;

    [JsonPropertyName("productName")]
    public string ProductName { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    /// <summary>Transaction time (UTC).</summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }
}
