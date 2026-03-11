using System.Text.Json.Serialization;

namespace TransactionIngest.Services;

/// <summary>
/// Mirrors the JSON shape returned by the upstream transactions API.
/// Property names match the camelCase API contract exactly.
/// </summary>
public sealed class TransactionDto
{
    [JsonPropertyName("transactionId")]
    public string TransactionId { get; set; } = string.Empty;

    /// <summary>Full PAN as returned by the API. Never written to the database.</summary>
    [JsonPropertyName("cardNumber")]
    public string CardNumber { get; set; } = string.Empty;

    [JsonPropertyName("locationCode")]
    public string LocationCode { get; set; } = string.Empty;

    [JsonPropertyName("productName")]
    public string ProductName { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    /// <summary>When the transaction occurred at the POS (UTC).</summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }
}
