namespace TransactionIngest.Services;

/// <summary>Source for transaction snapshots.</summary>
public interface ITransactionApiClient
{
    /// <summary>Get transactions from the last 24 hours.</summary>
    Task<IReadOnlyList<TransactionDto>> FetchLast24HoursAsync(CancellationToken ct = default);
}
